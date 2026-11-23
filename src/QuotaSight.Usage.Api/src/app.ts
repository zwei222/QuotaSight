/**
 * HTTP surface for the first vertical slice.
 *
 * Order of checks is deliberate: authenticate, authorise the scope, check the
 * collector binding of the credential, bound and parse the request, then resolve
 * the collector from server-side state. The collector id in the path or query is
 * only ever a lookup key; ownership, scope binding and the active generation are
 * all enforced against the database, and every lookup is owner-filtered so an
 * out-of-scope id is indistinguishable from one that does not exist.
 */
import { Hono } from 'hono';
import type { ContentfulStatusCode } from 'hono/utils/http-status';
import { hasScope, type Authenticator, type Principal } from './auth';
import { readBoundedBodyText } from './body';
import { ApiError, errorBody } from './errors';
import { parseQuota } from './quota';
import {
  MAX_BREAKDOWN_QUERY_SPAN_DAYS,
  canonicalSnapshotJson,
  isJsonMediaType,
  parseCollectorId,
  parseDailyQuery,
  parseQuotaReadQuery,
  parseRevision,
  parseSnapshotBody,
  sha256Hex,
  sumTokensSafely,
} from './schema';
import {
  applyQuota,
  applySnapshot,
  applySnapshotV2,
  loadOwnedCollector,
  readBreakdown,
  readCollectorFreshness,
  readDaily,
  readQuotaV2,
} from './store';

export interface AppOptions {
  authenticator: Authenticator;
  now?: () => Date;
}

export type AppEnv = {
  Bindings: {
    DB: D1Database;
    ACCESS_TEAM_DOMAIN?: string;
    ACCESS_AUD?: string;
    ACCESS_PRINCIPALS?: string;
    ACCESS_MAX_TOKEN_AGE_SECONDS?: string;
  };
  Variables: { principal: Principal };
};

export function createApp(options: AppOptions) {
  const now = options.now ?? (() => new Date());
  const app = new Hono<AppEnv>();

  app.use('/v1/*', async (c, next) => {
    const principal = await options.authenticator.authenticate(c.req.raw);
    if (principal === null) {
      throw new ApiError(401, 'unauthorized', 'Credentials are required.');
    }
    c.set('principal', principal);
    await next();
  });

  app.put('/v1/collectors/:collectorId/snapshots/:revision', async (c) => {
    const principal = c.get('principal');
    if (!hasScope(principal, 'usage:write')) {
      throw new ApiError(403, 'forbidden_scope', 'This credential cannot write usage snapshots.');
    }

    const collectorId = parseCollectorId(c.req.param('collectorId'));
    const revision = parseRevision(c.req.param('revision'));

    // The binding is checked before anything is read from the body or resolved
    // from the database: a credential may only ever address its own collector,
    // so an id it is not bound to is answered identically whether or not it
    // exists.
    if (principal.collectorId !== collectorId) {
      throw new ApiError(403, 'forbidden_collector', 'This credential cannot write to that collector.');
    }

    if (!isJsonMediaType(c.req.header('content-type'))) {
      throw new ApiError(415, 'unsupported_media_type', 'Content-Type must be application/json.');
    }

    const text = await readBoundedBodyText(c.req.raw);

    let raw: unknown;
    try {
      raw = JSON.parse(text);
    } catch {
      throw new ApiError(400, 'invalid_json', 'The request body must be valid JSON.');
    }

    // `schemaVersion` picks the wire contract. Both versions go through the same
    // checks in the same order; only the write and the response shape differ, so
    // a v1 collector sees exactly the bytes it saw before v2 existed.
    const snapshot = parseSnapshotBody(raw);

    const collector = await loadOwnedCollector(c.env.DB, collectorId, principal.ownerId);
    if (collector === null) {
      throw new ApiError(404, 'collector_not_found', 'No such collector.');
    }
    if (snapshot.generation !== collector.activeGeneration) {
      throw new ApiError(
        409,
        'generation_inactive',
        'The snapshot generation is not the active generation of this collector.',
        { generation: snapshot.generation, activeGeneration: collector.activeGeneration },
      );
    }

    const digest = await sha256Hex(canonicalSnapshotJson(snapshot));

    if (snapshot.schemaVersion === 2) {
      const receipt = await applySnapshotV2(
        c.env.DB,
        collector,
        snapshot,
        revision,
        digest,
        now().toISOString(),
      );

      return c.json({
        status: receipt.status,
        collectorId: receipt.collectorId,
        generation: receipt.generation,
        revision: receipt.revision,
        receivedAt: receipt.receivedAt,
        changedDays: receipt.changedDays,
        ignoredReductions: receipt.ignoredReductions,
        changedParts: receipt.changedParts,
        ignoredPartReductions: receipt.ignoredPartReductions,
      });
    }

    const receipt = await applySnapshot(c.env.DB, collector, snapshot, revision, digest, now().toISOString());

    return c.json({
      status: receipt.status,
      collectorId: receipt.collectorId,
      generation: receipt.generation,
      revision: receipt.revision,
      receivedAt: receipt.receivedAt,
      changedDays: receipt.changedDays,
      ignoredReductions: receipt.ignoredReductions,
    });
  });

  app.put('/v1/collectors/:collectorId/quota', async (c) => {
    const principal = c.get('principal');
    if (!hasScope(principal, 'usage:write')) throw new ApiError(403, 'forbidden_scope', 'This credential cannot write usage snapshots.');
    const collectorId = parseCollectorId(c.req.param('collectorId'));
    if (principal.collectorId !== collectorId) throw new ApiError(403, 'forbidden_collector', 'This credential cannot write to that collector.');
    if (!isJsonMediaType(c.req.header('content-type'))) throw new ApiError(415, 'unsupported_media_type', 'Content-Type must be application/json.');
    const text = await readBoundedBodyText(c.req.raw, 4 * 1024);
    let raw: unknown;
    try { raw = JSON.parse(text); } catch { throw new ApiError(400, 'invalid_json', 'The request body must be valid JSON.'); }
    const snapshot = parseQuota(raw, now());
    const collector = await loadOwnedCollector(c.env.DB, collectorId, principal.ownerId);
    if (collector === null) throw new ApiError(404, 'collector_not_found', 'No such collector.');
    if (snapshot.generation !== collector.activeGeneration) throw new ApiError(409, 'generation_inactive', 'The snapshot generation is not active.');
    // One transaction: the retention row per provider and the newest snapshot.
    // The route's body, bounds, authentication and response are unchanged; only
    // what is stored alongside the snapshot is new.
    await applyQuota(c.env.DB, collector, snapshot);
    return c.json({ status: 'accepted' });
  });

  app.get('/v1/quota/latest', async (c) => {
    const principal = c.get('principal');
    if (!hasScope(principal, 'usage:read') || principal.collectorId !== null) throw new ApiError(403, 'forbidden_scope', 'This credential cannot read owner-wide usage.');

    // The path is shared: without the selector this is exactly the v1 body a
    // deployed client already parses, and `?schema=2` adds the last successful
    // value next to each group's current status.
    if (parseQuotaReadQuery(new URL(c.req.url)) === 2) {
      return c.json(await readQuotaV2(c.env.DB, principal.ownerId));
    }

    const rows = await c.env.DB.prepare(`SELECT q.collected_at, q.payload_json FROM quota_snapshot q
      JOIN collectors c ON c.id = q.collector_id AND c.owner_id = q.owner_id AND c.active_generation = q.generation
      WHERE q.owner_id = ? ORDER BY q.collected_at DESC LIMIT 65`).bind(principal.ownerId).all<{ collected_at: string; payload_json: string }>();
    if (rows.results.length > 64) throw new ApiError(422, 'result_too_large', 'The quota response exceeds its row limit.', { reason: 'too_many_rows', limitRows: 64 });
    const snapshots = rows.results.map(row => {
      const payload = JSON.parse(row.payload_json) as { providers: unknown[] };
      return { collectedAt: row.collected_at, providers: payload.providers };
    });
    const responseBody = { snapshots };
    if (new TextEncoder().encode(JSON.stringify(responseBody)).byteLength > 256 * 1024) throw new ApiError(422, 'result_too_large', 'The quota response exceeds its byte limit.', { reason: 'response_too_large', limitBytes: 256 * 1024 });
    return c.json(responseBody);
  });

  app.get('/v1/usage/daily', async (c) => {
    const principal = c.get('principal');
    if (!hasScope(principal, 'usage:read')) {
      throw new ApiError(403, 'forbidden_scope', 'This credential cannot read usage.');
    }
    // `usage:read` covers every collector of the owner. A credential that is
    // bound to a single collector must not be able to widen its own scope, so
    // it cannot exercise the read scope at all.
    if (principal.collectorId !== null) {
      throw new ApiError(
        403,
        'forbidden_scope',
        'A collector-bound credential cannot read owner-wide usage.',
      );
    }

    const query = parseDailyQuery(new URL(c.req.url));
    if (query.collectorId !== null) {
      const collector = await loadOwnedCollector(c.env.DB, query.collectorId, principal.ownerId);
      if (collector === null) {
        throw new ApiError(404, 'collector_not_found', 'No such collector.');
      }
    }

    const days = await readDaily(c.env.DB, principal.ownerId, query);
    const totalTokens = sumTokensSafely(days.map((day) => day.totalTokens));
    // Reception time per covered collector. A daily total on its own says
    // nothing about how old it is, so the caller gets the newest accepted
    // receipt of each collector's active generation (or `null` when the
    // collector has not reported in that generation yet).
    const collectors = await readCollectorFreshness(c.env.DB, principal.ownerId, query);

    return c.json({ from: query.from, to: query.to, days, totalTokens, collectors });
  });

  app.get('/v1/usage/breakdown', async (c) => {
    const principal = c.get('principal');
    if (!hasScope(principal, 'usage:read')) {
      throw new ApiError(403, 'forbidden_scope', 'This credential cannot read usage.');
    }
    // Same rule as the daily read: `usage:read` is an owner-wide scope, so a
    // credential bound to one collector cannot exercise it at all.
    if (principal.collectorId !== null) {
      throw new ApiError(
        403,
        'forbidden_scope',
        'A collector-bound credential cannot read owner-wide usage.',
      );
    }

    // The breakdown is the expensive read: it aggregates every key of every day
    // in the range, so it accepts a narrower window than the daily read.
    const query = parseDailyQuery(new URL(c.req.url), MAX_BREAKDOWN_QUERY_SPAN_DAYS);
    if (query.collectorId !== null) {
      const collector = await loadOwnedCollector(c.env.DB, query.collectorId, principal.ownerId);
      if (collector === null) {
        throw new ApiError(404, 'collector_not_found', 'No such collector.');
      }
    }

    const breakdown = await readBreakdown(c.env.DB, principal.ownerId, query);
    // Freshness is reported for the same reason as on the daily read: a
    // breakdown with no age next to it would look authoritative.
    const collectors = await readCollectorFreshness(c.env.DB, principal.ownerId, query);

    return c.json({
      from: query.from,
      to: query.to,
      parts: breakdown.parts,
      coverage: breakdown.coverage,
      collectors,
    });
  });

  app.notFound((c) => c.json({ error: { code: 'not_found', message: 'No such route.' } }, 404));

  app.onError((err, c) => {
    if (err instanceof ApiError) {
      return c.json(errorBody(err), err.status as ContentfulStatusCode);
    }
    return c.json({ error: { code: 'internal_error', message: 'Internal error.' } }, 500);
  });

  return app;
}
