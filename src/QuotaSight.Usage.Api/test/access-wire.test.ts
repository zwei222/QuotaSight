/**
 * JWT verification through the real Worker entry point.
 *
 * Outbound fetch is intercepted: only the configured Access certs URL may
 * succeed, and it returns a key generated in this process. No request leaves
 * the machine.
 */
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import {
  createHarness,
  json,
  resetData,
  scalar,
  snapshotBody,
  type Harness,
} from './harness';
import {
  ASSERTION_HEADER,
  AUD,
  READ_B_ID,
  READ_ID,
  TEAM_DOMAIN,
  WRITE_A2_ID,
  WRITE_B_ID,
  WRITE_ID,
  generateSyntheticKey,
  jwksJson,
  signAccessJwt,
  tamperSignature,
  teamDomain,
  unsignedToken,
  type SyntheticKey,
} from './jwt-fixture';

const UNAUTHORIZED = { error: { code: 'unauthorized', message: 'Credentials are required.' } };

interface FetchLog {
  certs: number;
  other: string[];
}

type JwksMode = 'ok' | 'http-500' | 'timeout' | 'invalid';

function accessBindings(overrides: Record<string, string> = {}): Record<string, string> {
  return {
    ACCESS_TEAM_DOMAIN: TEAM_DOMAIN,
    ACCESS_AUD: AUD,
    ACCESS_PRINCIPALS: JSON.stringify([
      { id: WRITE_ID, ownerId: 'owner-a', collectorId: 'col-a1', scopes: ['usage:write'] },
      { id: WRITE_A2_ID, ownerId: 'owner-a', collectorId: 'col-a2', scopes: ['usage:write'] },
      { id: WRITE_B_ID, ownerId: 'owner-b', collectorId: 'col-b1', scopes: ['usage:write'] },
      { id: READ_ID, ownerId: 'owner-a', collectorId: null, scopes: ['usage:read'] },
      { id: READ_B_ID, ownerId: 'owner-b', collectorId: null, scopes: ['usage:read'] },
    ]),
    ...overrides,
  };
}

function outbound(mode: JwksMode, jwksBody: string, log: FetchLog) {
  return async (request: Request): Promise<Response> => {
    const url = new URL(request.url);
    const certs = url.origin === TEAM_DOMAIN && url.pathname === '/cdn-cgi/access/certs';
    if (!certs) {
      log.other.push(url.href);
      return new Response('outbound denied', { status: 500 });
    }
    log.certs += 1;
    if (mode === 'http-500') return new Response('unavailable', { status: 500 });
    if (mode === 'invalid') return new Response('{', { status: 200, headers: { 'content-type': 'application/json' } });
    if (mode === 'timeout') {
      await new Promise<void>((resolve, reject) => {
        const timer = setTimeout(resolve, 6_500);
        const abort = () => {
          clearTimeout(timer);
          reject(Object.assign(new Error('timeout'), { name: 'TimeoutError' }));
        };
        if (request.signal.aborted) {
          abort();
          return;
        }
        request.signal.addEventListener('abort', abort, { once: true });
      });
      return new Response('late', { status: 504 });
    }
    return new Response(jwksBody, { status: 200, headers: { 'content-type': 'application/json' } });
  };
}

async function open(mode: JwksMode, jwksBody: string, bindings = accessBindings()): Promise<{ h: Harness; log: FetchLog }> {
  const log: FetchLog = { certs: 0, other: [] };
  const h = await createHarness('src/index.ts', {
    bindings,
    outbound: outbound(mode, jwksBody, log),
  });
  return { h, log };
}

let key: SyntheticKey;
let jwksBody: string;
let issuedAt: number;
let ok: Harness;
let down: Harness;
let slow: Harness;
let bad: Harness;
let invalidConfig: Harness;
let okLog: FetchLog;

async function token(input: Partial<Parameters<typeof signAccessJwt>[0]> = {}): Promise<string> {
  return signAccessJwt({
    kid: key.kid,
    privateKey: key.privateKey,
    issuer: TEAM_DOMAIN,
    audience: AUD,
    issuedAt,
    expirationTime: issuedAt + 3_600,
    ...input,
  });
}

function assertion(jwt: string): Record<string, string> {
  return { [ASSERTION_HEADER]: jwt };
}

beforeAll(async () => {
  key = await generateSyntheticKey('synthetic-key');
  jwksBody = jwksJson(key);
  issuedAt = Math.floor(Date.now() / 1000);
  const opened = await Promise.all([
    open('ok', jwksBody),
    open('http-500', jwksBody),
    open('timeout', jwksBody),
    open('invalid', jwksBody),
    open('ok', jwksBody, accessBindings({ ACCESS_AUD: 'short' })),
  ]);
  ok = opened[0].h;
  okLog = opened[0].log;
  down = opened[1].h;
  slow = opened[2].h;
  bad = opened[3].h;
  invalidConfig = opened[4].h;
}, 120_000);

afterAll(async () => {
  await Promise.all([ok, down, slow, bad, invalidConfig].map((h) => h?.dispose()));
});

beforeEach(async () => {
  await Promise.all([ok, down, slow, bad, invalidConfig].map((h) => resetData(h.db)));
});

async function stored(h: Harness): Promise<number> {
  return (
    (await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')) +
    (await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day'))
  );
}

describe('Access JWT through src/index.ts', () => {
  it('fetches the configured certs url once and then uses the cache', async () => {
    const localLog: FetchLog = { certs: 0, other: [] };
    const local = await createHarness('src/index.ts', {
      bindings: accessBindings(),
      outbound: outbound('ok', jwksBody, localLog),
    });
    try {
      await resetData(local.db);
      const write = await token();
      const first = await local.putSnapshot({ revision: 1, body: snapshotBody(), token: null, headers: assertion(write) });
      const second = await local.putSnapshot({ revision: 2, body: snapshotBody(), token: null, headers: assertion(write) });
      expect(first.status).toBe(200);
      expect(second.status).toBe(200);
      expect(localLog.certs).toBe(1);
      expect(localLog.other).toEqual([]);
    } finally {
      await local.dispose();
    }
  }, 60_000);

  it('does not follow a jku in the token', async () => {
    const write = await token({ header: { jku: 'https://evil.example/jwks' } });
    const before = okLog.other.length;
    const response = await ok.putSnapshot({ revision: 1, body: snapshotBody(), token: null, headers: assertion(write) });
    expect(response.status).toBe(200);
    expect(okLog.other.slice(before)).toEqual([]);
  });

  it('enforces collector binding, owner filter and scope with the mapped principal', async () => {
    const writeA = await token();
    const writeA2 = await token({ commonName: WRITE_A2_ID });
    const writeB = await token({ commonName: WRITE_B_ID });
    const readA = await token({ commonName: READ_ID });
    const readB = await token({ commonName: READ_B_ID });

    expect((await ok.putSnapshot({ revision: 1, body: snapshotBody(), token: null, headers: assertion(writeA) })).status).toBe(200);
    expect(
      (
        await ok.putSnapshot({
          revision: 1,
          body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 5 }] }),
          collectorId: 'col-a2',
          token: null,
          headers: assertion(writeA2),
        })
      ).status,
    ).toBe(200);
    expect(
      (
        await ok.putSnapshot({
          revision: 1,
          body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 7 }] }),
          collectorId: 'col-b1',
          token: null,
          headers: assertion(writeB),
        })
      ).status,
    ).toBe(200);

    const wrongCollector = await ok.putSnapshot({
      revision: 2,
      body: snapshotBody(),
      collectorId: 'col-a2',
      token: null,
      headers: assertion(writeA),
    });
    expect(wrongCollector.status).toBe(403);
    expect((await json(wrongCollector)).error.code).toBe('forbidden_collector');

    const otherOwner = await ok.putSnapshot({
      revision: 2,
      body: snapshotBody(),
      collectorId: 'col-b1',
      token: null,
      headers: assertion(writeA),
    });
    expect(otherOwner.status).toBe(403);
    expect((await json(otherOwner)).error.code).toBe('forbidden_collector');

    const readOnWrite = await ok.putSnapshot({ revision: 2, body: snapshotBody(), token: null, headers: assertion(readA) });
    expect(readOnWrite.status).toBe(403);
    expect((await json(readOnWrite)).error.code).toBe('forbidden_scope');

    const writeOnRead = await ok.getDaily('?from=2026-11-01&to=2026-11-30', null, assertion(writeA));
    expect(writeOnRead.status).toBe(403);
    expect((await json(writeOnRead)).error.code).toBe('forbidden_scope');

    const escalated = await token({ extraClaims: { scope: 'usage:read', scopes: ['usage:read'] } });
    const stillWrite = await ok.getDaily('?from=2026-11-01&to=2026-11-30', null, assertion(escalated));
    expect(stillWrite.status).toBe(403);
    expect((await json(stillWrite)).error.code).toBe('forbidden_scope');

    const ownerA = await ok.getDaily('?from=2026-11-01&to=2026-11-30', null, assertion(readA));
    expect(ownerA.status).toBe(200);
    expect((await json(ownerA)).days).toEqual([
      { date: '2026-11-18', collectorId: 'col-a1', totalTokens: 100 },
      { date: '2026-11-18', collectorId: 'col-a2', totalTokens: 5 },
    ]);

    const ownerB = await ok.getDaily('?from=2026-11-01&to=2026-11-30', null, assertion(readB));
    expect((await json(ownerB)).days).toEqual([{ date: '2026-11-18', collectorId: 'col-b1', totalTokens: 7 }]);

    const probe = await ok.getDaily('?from=2026-11-01&to=2026-11-30&collector=col-a1', null, assertion(readB));
    expect(probe.status).toBe(404);
    expect((await json(probe)).error.code).toBe('collector_not_found');
  });

  it('ignores a valid JWT presented on any header other than the Access assertion', async () => {
    const write = await token();
    const response = await ok.putSnapshot({
      revision: 1,
      body: snapshotBody(),
      token: null,
      headers: {
        authorization: `Bearer ${write}`,
        cookie: `CF_Authorization=${write}`,
        'Cf-Access-Authenticated-User-Email': 'owner-a@example.com',
      },
    });
    expect(response.status).toBe(401);
    expect(await json(response)).toEqual(UNAUTHORIZED);
    expect(await stored(ok)).toBe(0);
  });

  it('returns the same 401 body for every authentication failure', async () => {
    const write = await token();
    const cases = [
      ok.putSnapshot({ revision: 1, body: snapshotBody(), token: null }),
      ok.putSnapshot({ revision: 1, body: snapshotBody(), token: null, headers: { [ASSERTION_HEADER]: '' } }),
      ok.putSnapshot({ revision: 1, body: snapshotBody(), token: null, headers: assertion(tamperSignature(write)) }),
      ok.putSnapshot({
        revision: 1,
        body: snapshotBody(),
        token: null,
        headers: assertion(await token({ issuer: teamDomain('foreign') })),
      }),
      ok.putSnapshot({
        revision: 1,
        body: snapshotBody(),
        token: null,
        headers: assertion(await token({ commonName: 'unknown.access' })),
      }),
      ok.putSnapshot({ revision: 1, body: snapshotBody(), token: null, headers: assertion(unsignedToken()) }),
    ];
    const bodies = [];
    for (const pending of cases) {
      const response = await pending;
      expect(response.status).toBe(401);
      bodies.push(await json(response));
    }
    for (const body of bodies) expect(body).toEqual(UNAUTHORIZED);
    expect(await stored(ok)).toBe(0);
  });

  it('does not write when the JWKS responds 500', async () => {
    const write = await token();
    const response = await down.putSnapshot({ revision: 1, body: snapshotBody(), token: null, headers: assertion(write) });
    expect(response.status).toBe(503);
    const body = await json(response);
    expect(body.error.code).toBe('auth_unavailable');
    expect(body.error.details).toBeUndefined();
    expect(JSON.stringify(body)).not.toContain(write);
    expect(JSON.stringify(body)).not.toContain(WRITE_ID);
    expect(await stored(down)).toBe(0);
  });

  it('does not write when the JWKS times out', async () => {
    const write = await token();
    const response = await slow.putSnapshot({ revision: 1, body: snapshotBody(), token: null, headers: assertion(write) });
    expect(response.status).toBe(503);
    const body = await json(response);
    expect(body.error.code).toBe('auth_unavailable');
    expect(body.error.details).toBeUndefined();
    expect(JSON.stringify(body)).not.toContain(write);
    expect(await stored(slow)).toBe(0);
  }, 20_000);

  it('does not write when the JWKS document is not a key set', async () => {
    const write = await token();
    const response = await bad.putSnapshot({ revision: 1, body: snapshotBody(), token: null, headers: assertion(write) });
    expect(response.status).toBe(503);
    expect((await json(response)).error.code).toBe('auth_unavailable');
    expect(await stored(bad)).toBe(0);
  });

  it('answers 503 auth_unconfigured when configuration is present but invalid', async () => {
    const write = await token();
    const response = await invalidConfig.putSnapshot({
      revision: 1,
      body: snapshotBody(),
      token: null,
      headers: assertion(write),
    });
    // Not a credential problem: the deployment itself is misconfigured, so the
    // retryable 503 class answers and no configuration value is echoed.
    expect(response.status).toBe(503);
    expect(await json(response)).toEqual({
      error: {
        code: 'auth_unconfigured',
        message: 'Authentication is not configured for this deployment.',
      },
    });
    expect(await stored(invalidConfig)).toBe(0);
  });
});
