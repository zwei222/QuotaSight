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
import {
  canonicalSnapshotJson,
  isJsonMediaType,
  parseCollectorId,
  parseDailyQuery,
  parseRevision,
  parseSnapshot,
  sha256Hex,
  sumTokensSafely,
} from './schema';
import { applySnapshot, loadOwnedCollector, readDaily } from './store';

export interface AppOptions {
  authenticator: Authenticator;
  now?: () => Date;
}

export type AppEnv = {
  Bindings: { DB: D1Database };
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
    const snapshot = parseSnapshot(raw);

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
    const receipt = await applySnapshot(c.env.DB, collector, snapshot, revision, digest, now().toISOString());

    return c.json({
      status: receipt.status,
      collectorId: receipt.collectorId,
      generation: receipt.generation,
      revision: receipt.revision,
      receivedAt: receipt.receivedAt,
      changedDays: receipt.changedDays,
    });
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

    return c.json({ from: query.from, to: query.to, days, totalTokens });
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
