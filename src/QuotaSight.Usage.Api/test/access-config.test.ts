/**
 * Access configuration is fail-closed: one invalid value discards the whole
 * map and the resulting authenticator denies every request.
 */
import { beforeAll, describe, expect, it } from 'vitest';
import { createAccessAuthenticator } from '../src/access-jwt';
import { parseAccessConfig, type AccessEnv } from '../src/access-config';
import {
  AUD,
  AUD_OTHER,
  FIXED_NOW,
  READ_ID,
  TEAM_DOMAIN,
  WRITE_ID,
  generateSyntheticKey,
  signAccessJwt,
  teamDomain,
  type SyntheticKey,
} from './jwt-fixture';

const DAY = 24 * 60 * 60;

function writeEntry(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: WRITE_ID,
    ownerId: 'owner-a',
    collectorId: 'col-a1',
    scopes: ['usage:write'],
    ...overrides,
  };
}

function readEntry(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: READ_ID,
    ownerId: 'owner-a',
    collectorId: null,
    scopes: ['usage:read'],
    ...overrides,
  };
}

function principalsJson(entries: readonly unknown[]): string {
  return JSON.stringify(entries);
}

function validEnv(overrides: Partial<Record<keyof AccessEnv, string | undefined>> = {}): AccessEnv {
  return {
    ACCESS_TEAM_DOMAIN: TEAM_DOMAIN,
    ACCESS_AUD: AUD,
    ACCESS_PRINCIPALS: principalsJson([writeEntry(), readEntry()]),
    ...overrides,
  };
}

let key: SyntheticKey;
let writeToken: string;

beforeAll(async () => {
  key = await generateSyntheticKey();
  writeToken = await signAccessJwt({ kid: key.kid, privateKey: key.privateKey });
});

async function assertDeniesAll(env: AccessEnv): Promise<void> {
  expect(parseAccessConfig(env)).toBeNull();
  let resolved = false;
  const authenticator = createAccessAuthenticator(env, () => {
    resolved = true;
    return key.keys;
  }, () => FIXED_NOW);
  const request = new Request('https://usage.test/v1/collectors/col-a1/snapshots/1', {
    method: 'PUT',
    headers: { 'Cf-Access-Jwt-Assertion': writeToken },
  });
  expect(await authenticator.authenticate(request)).toBeNull();
  expect(resolved).toBe(false);
}

describe('parseAccessConfig', () => {
  it('accepts a writer and a reader and defaults the token age', () => {
    const config = parseAccessConfig(validEnv());
    expect(config).not.toBeNull();
    expect(config?.issuer).toBe(TEAM_DOMAIN);
    expect(config?.audiences).toEqual([AUD]);
    expect(config?.maxTokenAgeSeconds).toBe(DAY);
    expect(config?.principals.get(WRITE_ID)).toEqual({
      ownerId: 'owner-a',
      collectorId: 'col-a1',
      scopes: ['usage:write'],
    });
    expect(config?.principals.get(READ_ID)).toEqual({
      ownerId: 'owner-a',
      collectorId: null,
      scopes: ['usage:read'],
    });
    expect(config?.principals.get('__proto__')).toBeUndefined();
    expect(config?.principals.get('constructor')).toBeUndefined();
  });

  it('accepts several exact audience tags and an in-range token age', () => {
    const config = parseAccessConfig(
      validEnv({
        ACCESS_AUD: `${AUD},${AUD_OTHER}`,
        ACCESS_MAX_TOKEN_AGE_SECONDS: '3600',
      }),
    );
    expect(config?.audiences).toEqual([AUD, AUD_OTHER]);
    expect(config?.maxTokenAgeSeconds).toBe(3_600);
  });

  it('accepts the token-age bounds and stores a prototype-shaped id on the Map', () => {
    expect(parseAccessConfig(validEnv({ ACCESS_MAX_TOKEN_AGE_SECONDS: '1' }))?.maxTokenAgeSeconds).toBe(1);
    expect(parseAccessConfig(validEnv({ ACCESS_MAX_TOKEN_AGE_SECONDS: '604800' }))?.maxTokenAgeSeconds).toBe(
      604_800,
    );
    const config = parseAccessConfig(
      validEnv({
        ACCESS_PRINCIPALS: principalsJson([writeEntry({ id: '__proto__' })]),
      }),
    );
    expect(config?.principals.get('__proto__')).toEqual({
      ownerId: 'owner-a',
      collectorId: 'col-a1',
      scopes: ['usage:write'],
    });
    expect(Object.prototype).not.toHaveProperty('ownerId');
  });

  it.each([
    ['missing team domain', validEnv({ ACCESS_TEAM_DOMAIN: undefined })],
    ['empty team domain', validEnv({ ACCESS_TEAM_DOMAIN: '' })],
    ['http team domain', validEnv({ ACCESS_TEAM_DOMAIN: 'http://example.cloudflareaccess.com' })],
    ['trailing slash', validEnv({ ACCESS_TEAM_DOMAIN: `${TEAM_DOMAIN}/` })],
    ['certs path', validEnv({ ACCESS_TEAM_DOMAIN: `${TEAM_DOMAIN}/cdn-cgi/access/certs` })],
    ['port', validEnv({ ACCESS_TEAM_DOMAIN: `${TEAM_DOMAIN}:443` })],
    ['uppercase host', validEnv({ ACCESS_TEAM_DOMAIN: teamDomain('Example') })],
    ['missing audience', validEnv({ ACCESS_AUD: undefined })],
    ['empty audience', validEnv({ ACCESS_AUD: '' })],
    ['short audience', validEnv({ ACCESS_AUD: 'shortaud' })],
    ['audience with a hyphen', validEnv({ ACCESS_AUD: 'appaudtagvalue0-' })],
    ['audience too long', validEnv({ ACCESS_AUD: 'a'.repeat(129) })],
    ['empty audience segment', validEnv({ ACCESS_AUD: `${AUD},` })],
    ['spaced audience segment', validEnv({ ACCESS_AUD: `${AUD}, ${AUD_OTHER}` })],
    ['one invalid audience', validEnv({ ACCESS_AUD: `${AUD},short` })],
    ['missing principals', validEnv({ ACCESS_PRINCIPALS: undefined })],
    ['invalid principals json', validEnv({ ACCESS_PRINCIPALS: '{' })],
    ['principals object', validEnv({ ACCESS_PRINCIPALS: JSON.stringify({ id: WRITE_ID }) })],
    ['empty principals', validEnv({ ACCESS_PRINCIPALS: '[]' })],
    ['duplicate ids', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry(), writeEntry({ collectorId: 'col-a2' })]) })],
    ['unknown key', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry({ note: 'x' })]) })],
    ['prototype key', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry({ ['__proto__']: { admin: true } })]) })],
    ['unknown scope', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry({ scopes: ['usage:admin'] })]) })],
    ['both scopes', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry({ scopes: ['usage:write', 'usage:read'] })]) })],
    ['write without collector', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry({ collectorId: null })]) })],
    ['write missing collector', validEnv({ ACCESS_PRINCIPALS: principalsJson([(() => {
      const entry = writeEntry();
      delete entry.collectorId;
      return entry;
    })()]) })],
    ['read bound to a collector', validEnv({ ACCESS_PRINCIPALS: principalsJson([readEntry({ collectorId: 'col-a1' })]) })],
    ['read with write scope', validEnv({ ACCESS_PRINCIPALS: principalsJson([readEntry({ scopes: ['usage:write'] })]) })],
    ['empty scopes', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry({ scopes: [] })]) })],
    ['duplicate scope entry', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry({ scopes: ['usage:write', 'usage:write'] })]) })],
    ['scope string', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry({ scopes: 'usage:write' })]) })],
    ['bad owner id', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry({ ownerId: 'Owner-A' })]) })],
    ['short owner id', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry({ ownerId: 'ab' })]) })],
    ['bad collector id', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry({ collectorId: 'COL' })]) })],
    ['bad client id', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry({ id: 'writer access' })]) })],
    ['one bad entry discards a good one', validEnv({ ACCESS_PRINCIPALS: principalsJson([writeEntry(), readEntry({ collectorId: 'col-a1' })]) })],
    ['token age zero', validEnv({ ACCESS_MAX_TOKEN_AGE_SECONDS: '0' })],
    ['token age above seven days', validEnv({ ACCESS_MAX_TOKEN_AGE_SECONDS: '604801' })],
    ['token age negative', validEnv({ ACCESS_MAX_TOKEN_AGE_SECONDS: '-1' })],
    ['token age fraction', validEnv({ ACCESS_MAX_TOKEN_AGE_SECONDS: '1.5' })],
    ['token age unit suffix', validEnv({ ACCESS_MAX_TOKEN_AGE_SECONDS: '24h' })],
    ['token age leading zero', validEnv({ ACCESS_MAX_TOKEN_AGE_SECONDS: '01' })],
    ['token age blank', validEnv({ ACCESS_MAX_TOKEN_AGE_SECONDS: '' })],
    ['token age padded', validEnv({ ACCESS_MAX_TOKEN_AGE_SECONDS: ' 120' })],
    ['completely unconfigured', {}],
  ] as const)('%s denies every request', async (_name, env) => {
    await assertDeniesAll(env);
  });

  it('rejects a non-string binding instead of coercing it', async () => {
    await assertDeniesAll({ ...validEnv(), ACCESS_AUD: 1 as never });
  });
});
