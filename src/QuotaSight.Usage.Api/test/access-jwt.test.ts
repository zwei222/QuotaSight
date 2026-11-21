/**
 * Access service-token verification. Tokens and keys are generated at runtime.
 * Ordinary failures are null (the app turns that into one fixed 401). JWKS
 * availability failures are a reason-free 503. Nothing here is logged.
 */
import { errors, generateKeyPair, type CryptoKey } from 'jose';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import { AccessJwtAuthenticator } from '../src/access-jwt';
import type { Principal } from '../src/auth';
import { ApiError } from '../src/errors';
import {
  ASSERTION_HEADER,
  AUD,
  AUD_OTHER,
  FIXED_NOW,
  FIXED_NOW_SECONDS,
  READ_ID,
  TEAM_DOMAIN,
  WRITE_ID,
  generateSyntheticKey,
  signAccessJwt,
  signConfusedHmac,
  tamperSignature,
  teamDomain,
  unsignedToken,
  type SyntheticKey,
} from './jwt-fixture';

const DAY = 24 * 60 * 60;

function principals(): Map<string, Principal> {
  return new Map<string, Principal>([
    [WRITE_ID, { ownerId: 'owner-a', collectorId: 'col-a1', scopes: ['usage:write'] }],
    [READ_ID, { ownerId: 'owner-a', collectorId: null, scopes: ['usage:read'] }],
  ]);
}

function authenticator(key: SyntheticKey, overrides: Partial<ConstructorParameters<typeof AccessJwtAuthenticator>[0]> = {}) {
  return new AccessJwtAuthenticator({
    issuer: TEAM_DOMAIN,
    audiences: [AUD],
    principals: principals(),
    keys: key.keys,
    maxTokenAgeSeconds: DAY,
    now: () => FIXED_NOW,
    ...overrides,
  });
}

function request(headers: Record<string, string> = {}): Request {
  return new Request('https://usage.test/v1/collectors/col-a1/snapshots/1', { method: 'PUT', headers });
}

function withToken(token: string, extra: Record<string, string> = {}): Request {
  return request({ [ASSERTION_HEADER]: token, ...extra });
}

let key: SyntheticKey;
let otherPrivateKey: CryptoKey;

beforeAll(async () => {
  key = await generateSyntheticKey('synthetic-key');
  otherPrivateKey = (await generateKeyPair('RS256', { modulusLength: 2048 })).privateKey;
});

async function sign(input: Partial<Parameters<typeof signAccessJwt>[0]> = {}) {
  return signAccessJwt({ kid: key.kid, privateKey: key.privateKey, ...input });
}

describe('AccessJwtAuthenticator', () => {
  it('accepts a writer and a reader', async () => {
    const auth = authenticator(key);
    const writer = await auth.authenticate(withToken(await sign()));
    expect(writer).toEqual({ ownerId: 'owner-a', collectorId: 'col-a1', scopes: ['usage:write'] });
    const reader = await auth.authenticate(withToken(await sign({ commonName: READ_ID })));
    expect(reader).toEqual({ ownerId: 'owner-a', collectorId: null, scopes: ['usage:read'] });
  });

  it('accepts audience as a string or an array, including a second configured tag', async () => {
    const auth = authenticator(key, { audiences: [AUD, AUD_OTHER] });
    expect(await auth.authenticate(withToken(await sign({ audience: AUD })))).not.toBeNull();
    expect(await auth.authenticate(withToken(await sign({ audience: [AUD] })))).not.toBeNull();
    expect(await auth.authenticate(withToken(await sign({ audience: AUD_OTHER })))).not.toBeNull();
    expect(await auth.authenticate(withToken(await sign({ audience: [AUD_OTHER, 'not-the-tag'] })))).not.toBeNull();
  });

  it('does not grant scopes carried inside the token', async () => {
    const auth = authenticator(key);
    const principal = await auth.authenticate(
      withToken(
        await sign({
          extraClaims: { scope: 'usage:read', scopes: ['usage:read', 'usage:write'] },
        }),
      ),
    );
    expect(principal?.scopes).toEqual(['usage:write']);
  });

  it.each([
    ['missing header', async () => request()],
    ['empty header', async () => request({ [ASSERTION_HEADER]: '' })],
    ['malformed header', async () => request({ [ASSERTION_HEADER]: 'not-a-jwt' })],
    ['header over 8 KiB', async () => request({ [ASSERTION_HEADER]: 'a'.repeat(8 * 1024 + 1) })],
    ['bearer prefix', async () => request({ [ASSERTION_HEADER]: `Bearer ${await sign()}` })],
    ['tampered signature', async () => withToken(tamperSignature(await sign()))],
    ['signed by another key', async () => withToken(await sign({ privateKey: otherPrivateKey }))],
    ['alg none', async () => withToken(unsignedToken())],
    ['hmac confusion', async () => withToken(await signConfusedHmac(key))],
    ['wrong issuer', async () => withToken(await sign({ issuer: teamDomain('foreign') }))],
    ['wrong audience', async () => withToken(await sign({ audience: AUD_OTHER }))],
    ['partial audience', async () => withToken(await sign({ audience: `${AUD}x` }))],
    ['audience prefix', async () => withToken(await sign({ audience: AUD.slice(0, -1) }))],
    ['expired beyond tolerance', async () => withToken(await sign({ expirationTime: FIXED_NOW_SECONDS - 60 }))],
    ['nbf beyond tolerance', async () => withToken(await sign({ notBefore: FIXED_NOW_SECONDS + 61 }))],
    ['missing iat', async () => withToken(await sign({ omitIssuedAt: true }))],
    ['missing exp', async () => withToken(await sign({ omitExpiration: true }))],
    [
      'older than maxTokenAge plus tolerance',
      async () =>
        withToken(
          await sign({
            issuedAt: FIXED_NOW_SECONDS - (120 + 61),
            expirationTime: FIXED_NOW_SECONDS + 60,
          }),
        ),
    ],
    ['missing common_name', async () => withToken(await sign({ omitCommonName: true }))],
    ['numeric common_name', async () => withToken(await sign({ omitCommonName: true, extraClaims: { common_name: 1 } }))],
    ['array common_name', async () => withToken(await sign({ omitCommonName: true, extraClaims: { common_name: [WRITE_ID] } }))],
    ['malformed common_name', async () => withToken(await sign({ commonName: 'writer access' }))],
    ['unregistered common_name', async () => withToken(await sign({ commonName: 'unknown.access' }))],
    ['unregistered __proto__', async () => withToken(await sign({ commonName: '__proto__' }))],
    ['unregistered constructor', async () => withToken(await sign({ commonName: 'constructor' }))],
  ] as const)('returns null for %s', async (_name, build) => {
    const auth = authenticator(key, { maxTokenAgeSeconds: 120 });
    expect(await auth.authenticate(await build())).toBeNull();
  });

  it('accepts a token inside the exp and maxTokenAge tolerance', async () => {
    const auth = authenticator(key, { maxTokenAgeSeconds: 120 });
    expect(
      await auth.authenticate(withToken(await sign({ expirationTime: FIXED_NOW_SECONDS - 59 }))),
    ).not.toBeNull();
    expect(
      await auth.authenticate(withToken(await sign({ notBefore: FIXED_NOW_SECONDS + 60 }))),
    ).not.toBeNull();
    expect(
      await auth.authenticate(
        withToken(
          await sign({
            issuedAt: FIXED_NOW_SECONDS - (120 + 60),
            expirationTime: FIXED_NOW_SECONDS + 60,
          }),
        ),
      ),
    ).not.toBeNull();
  });

  it('ignores Authorization, cookies and the email header', async () => {
    const token = await sign();
    const auth = authenticator(key);
    expect(await auth.authenticate(request({ authorization: `Bearer ${token}` }))).toBeNull();
    expect(await auth.authenticate(request({ cookie: `CF_Authorization=${token}` }))).toBeNull();
    expect(await auth.authenticate(request({ 'Cf-Access-Authenticated-User-Email': 'owner-a@example.com' }))).toBeNull();
    const principal = await auth.authenticate(
      withToken(token, {
        authorization: `Bearer ${await sign({ commonName: READ_ID })}`,
        cookie: `CF_Authorization=${await sign({ commonName: READ_ID })}`,
        'Cf-Access-Authenticated-User-Email': 'someone@example.com',
      }),
    );
    expect(principal).toEqual({ ownerId: 'owner-a', collectorId: 'col-a1', scopes: ['usage:write'] });
  });

  it('reports an unknown kid as auth unavailable', async () => {
    const auth = authenticator(key);
    const token = await sign({ header: { kid: 'missing-key' } });
    await expect(auth.authenticate(withToken(token))).rejects.toMatchObject({
      name: 'ApiError',
      status: 503,
      code: 'auth_unavailable',
      details: undefined,
    });
  });

  it.each([
    ['invalid jwks', () => new errors.JWKSInvalid('malformed set')],
    ['jwks timeout', () => new errors.JWKSTimeout()],
  ] as const)('reports %s as auth unavailable without the token', async (_name, buildError) => {
    const token = await sign();
    const auth = authenticator(key, {
      keys: async () => {
        throw buildError();
      },
    });
    const error = await auth.authenticate(withToken(token)).then(
      () => {
        throw new Error('expected auth_unavailable');
      },
      (err: unknown) => err,
    );
    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 503, code: 'auth_unavailable', details: undefined });
    expect((error as ApiError).message).not.toContain(token);
    expect((error as ApiError).message).not.toContain(WRITE_ID);
  });

  it('turns an unexpected verifier failure into 503 without echoing it', async () => {
    const token = await sign();
    const secret = `boom-${token}`;
    const auth = authenticator(key, {
      keys: async () => {
        throw new Error(secret);
      },
    });
    const logs = await captureConsole(async () => {
      const error = await auth.authenticate(withToken(token)).then(
        () => {
          throw new Error('expected a mapped failure');
        },
        (err: unknown) => err,
      );
      expect(error).toBeInstanceOf(ApiError);
      expect((error as ApiError).status).toBe(503);
      expect((error as ApiError).message).not.toContain(token);
      expect((error as ApiError).message).not.toContain(secret);
    });
    expect(logs).not.toContain(token);
    expect(logs).not.toContain(secret);
    expect(logs).not.toContain(WRITE_ID);
  });

  it('does not log tokens, client ids or key material', async () => {
    const token = await sign();
    const auth = authenticator(key);
    const logs = await captureConsole(async () => {
      expect(await auth.authenticate(withToken(token))).not.toBeNull();
      expect(await auth.authenticate(withToken(tamperSignature(token)))).toBeNull();
      await auth.authenticate(withToken(await sign({ header: { kid: 'missing-key' } }))).catch((err: unknown) => {
        expect(err).toBeInstanceOf(ApiError);
      });
    });
    expect(logs).not.toContain(token);
    expect(logs).not.toContain(WRITE_ID);
    expect(logs).not.toContain(ASSERTION_HEADER);
    const modulus = key.publicJwk.n;
    expect(modulus).toBeTruthy();
    expect(logs).not.toContain(modulus as string);
  });
});

const CONSOLE_METHODS = ['log', 'info', 'warn', 'error', 'debug'] as const;

function dump(value: unknown, seen = new Set<unknown>()): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') return String(value);
  if (typeof value !== 'object' && typeof value !== 'function') return '';
  if (seen.has(value)) return '';
  seen.add(value);
  const parts: string[] = [];
  if (value instanceof Error) parts.push(value.name, value.message, value.stack ?? '');
  for (const keyName of Object.keys(value)) {
    parts.push(keyName, dump((value as Record<string, unknown>)[keyName], seen));
  }
  return parts.join('\n');
}

async function captureConsole(run: () => Promise<void>): Promise<string> {
  const chunks: string[] = [];
  const spies = CONSOLE_METHODS.map((method) =>
    vi.spyOn(console, method).mockImplementation((...args: unknown[]) => {
      chunks.push(args.map((arg) => dump(arg)).join(' '));
    }),
  );
  try {
    await run();
  } finally {
    for (const spy of spies) spy.mockRestore();
  }
  return chunks.join('\n');
}
