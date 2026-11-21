/**
 * Deployment configuration for Cloudflare Access service-token verification.
 *
 * The function is pure and fail-closed: any missing or invalid value discards
 * the whole configuration. Callers must treat `null` as deny-all.
 */
import type { Principal } from './auth';
import { parseCollectorId } from './schema';

export interface AccessEnv {
  readonly ACCESS_TEAM_DOMAIN?: unknown;
  readonly ACCESS_AUD?: unknown;
  readonly ACCESS_PRINCIPALS?: unknown;
  readonly ACCESS_MAX_TOKEN_AGE_SECONDS?: unknown;
}

export interface AccessConfig {
  readonly issuer: string;
  readonly audiences: readonly string[];
  readonly principals: ReadonlyMap<string, Principal>;
  readonly maxTokenAgeSeconds: number;
}

const TEAM_DOMAIN_PATTERN = /^https:\/\/[a-z0-9-]+\.cloudflareaccess\.com$/;
const AUDIENCE_PATTERN = /^[A-Za-z0-9]{16,128}$/;
const CLIENT_ID_PATTERN = /^[A-Za-z0-9._-]{1,128}$/;
const TOKEN_AGE_PATTERN = /^(?:0|[1-9]\d*)$/;
const DEFAULT_MAX_TOKEN_AGE_SECONDS = 24 * 60 * 60;
const MIN_MAX_TOKEN_AGE_SECONDS = 1;
const MAX_MAX_TOKEN_AGE_SECONDS = 604_800;
const PRINCIPAL_KEYS = ['collectorId', 'id', 'ownerId', 'scopes'] as const;

function slugOrNull(raw: unknown): string | null {
  if (typeof raw !== 'string') return null;
  try {
    return parseCollectorId(raw);
  } catch {
    return null;
  }
}

function parseAudiences(raw: string): string[] | null {
  const parts = raw.split(',');
  if (parts.length === 0 || parts.some((part) => !AUDIENCE_PATTERN.test(part))) return null;
  return parts;
}

function parseMaxTokenAge(raw: unknown): number | null {
  if (raw === undefined) return DEFAULT_MAX_TOKEN_AGE_SECONDS;
  if (typeof raw !== 'string' || !TOKEN_AGE_PATTERN.test(raw)) return null;
  const value = Number(raw);
  if (!Number.isSafeInteger(value) || value < MIN_MAX_TOKEN_AGE_SECONDS || value > MAX_MAX_TOKEN_AGE_SECONDS) {
    return null;
  }
  return value;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return false;
  const proto = Object.getPrototypeOf(value);
  return proto === Object.prototype || proto === null;
}

function parsePrincipal(value: unknown): { id: string; principal: Principal } | null {
  if (!isPlainObject(value)) return null;
  if (Object.keys(value).length !== PRINCIPAL_KEYS.length) return null;
  for (const key of PRINCIPAL_KEYS) {
    if (!Object.hasOwn(value, key)) return null;
  }

  const id = value.id;
  if (typeof id !== 'string' || !CLIENT_ID_PATTERN.test(id)) return null;
  const ownerId = slugOrNull(value.ownerId);
  if (ownerId === null) return null;
  if (!Array.isArray(value.scopes) || value.scopes.length !== 1) return null;
  const scope = value.scopes[0];
  if (scope !== 'usage:write' && scope !== 'usage:read') return null;

  if (scope === 'usage:write') {
    if (typeof value.collectorId !== 'string') return null;
    const collectorId = slugOrNull(value.collectorId);
    if (collectorId === null) return null;
    return { id, principal: { ownerId, collectorId, scopes: ['usage:write'] } };
  }
  if (value.collectorId !== null) return null;
  return { id, principal: { ownerId, collectorId: null, scopes: ['usage:read'] } };
}

export function parseAccessConfig(env: AccessEnv): AccessConfig | null {
  const team = env.ACCESS_TEAM_DOMAIN;
  const audience = env.ACCESS_AUD;
  const principalsRaw = env.ACCESS_PRINCIPALS;
  if (typeof team !== 'string' || !TEAM_DOMAIN_PATTERN.test(team)) return null;
  if (typeof audience !== 'string') return null;
  const audiences = parseAudiences(audience);
  if (audiences === null) return null;
  if (typeof principalsRaw !== 'string') return null;
  const maxTokenAgeSeconds = parseMaxTokenAge(env.ACCESS_MAX_TOKEN_AGE_SECONDS);
  if (maxTokenAgeSeconds === null) return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(principalsRaw);
  } catch {
    return null;
  }
  if (!Array.isArray(parsed) || parsed.length === 0) return null;

  const principals = new Map<string, Principal>();
  for (const entry of parsed) {
    const principal = parsePrincipal(entry);
    if (principal === null || principals.has(principal.id)) return null;
    principals.set(principal.id, principal.principal);
  }
  return { issuer: team, audiences, principals, maxTokenAgeSeconds };
}
