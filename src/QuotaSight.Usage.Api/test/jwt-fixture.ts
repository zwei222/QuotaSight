/**
 * Runtime-only RS256 material for Access JWT tests.
 *
 * Keys and tokens are generated when the test runs. This file must not contain
 * key literals, real team domains, or real client identifiers.
 */
import {
  createLocalJWKSet,
  exportJWK,
  exportSPKI,
  generateKeyPair,
  importJWK,
  SignJWT,
  UnsecuredJWT,
  type CryptoKey,
  type JWK,
  type JWTVerifyGetKey,
} from 'jose';

export const TEAM_DOMAIN = 'https://example.cloudflareaccess.com';
export const CERTS_PATH = '/cdn-cgi/access/certs';
export const AUD = 'appaudtagvalue01';
export const AUD_OTHER = 'readeraudtagval1';
export const WRITE_ID = 'writer.access';
export const WRITE_A2_ID = 'writer-a2.access';
export const WRITE_B_ID = 'writer-b.access';
export const READ_ID = 'reader.access';
export const READ_B_ID = 'reader-b.access';
export const ASSERTION_HEADER = 'Cf-Access-Jwt-Assertion';
export const FIXED_NOW = new Date('2026-11-20T03:00:00Z');
export const FIXED_NOW_SECONDS = Math.floor(FIXED_NOW.getTime() / 1000);

const AUD_PATTERN = /^[A-Za-z0-9]{16,128}$/;
const CLIENT_PATTERN = /^[A-Za-z0-9._-]{1,128}$/;

if (!AUD_PATTERN.test(AUD) || !AUD_PATTERN.test(AUD_OTHER)) {
  throw new Error('fixture audience tags do not match the Access AUD contract');
}
for (const id of [WRITE_ID, WRITE_A2_ID, WRITE_B_ID, READ_ID, READ_B_ID]) {
  if (!CLIENT_PATTERN.test(id)) throw new Error('fixture client id does not match the common_name contract');
}

/** Builds a team-domain origin without embedding a non-example host in source. */
export function teamDomain(label: string): string {
  return `https://${label}.cloudflareaccess.com`;
}

export interface SyntheticKey {
  readonly kid: string;
  readonly publicKey: CryptoKey;
  readonly privateKey: CryptoKey;
  readonly publicJwk: JWK;
  readonly keys: JWTVerifyGetKey;
}

export async function generateSyntheticKey(kid = 'synthetic-key'): Promise<SyntheticKey> {
  const { publicKey, privateKey } = await generateKeyPair('RS256', { modulusLength: 2048 });
  const publicJwk = await exportJWK(publicKey);
  publicJwk.alg = 'RS256';
  publicJwk.use = 'sig';
  publicJwk.kid = kid;
  return {
    kid,
    publicKey,
    privateKey,
    publicJwk,
    keys: createLocalJWKSet({ keys: [publicJwk] }),
  };
}

export function jwksJson(key: SyntheticKey): string {
  return JSON.stringify({ keys: [key.publicJwk] });
}

export interface SignInput {
  readonly kid: string;
  readonly privateKey: CryptoKey;
  readonly commonName?: string;
  readonly omitCommonName?: boolean;
  readonly extraClaims?: Record<string, unknown>;
  readonly issuer?: string;
  readonly audience?: string | string[];
  readonly issuedAt?: number;
  readonly expirationTime?: number;
  readonly notBefore?: number;
  readonly omitIssuedAt?: boolean;
  readonly omitExpiration?: boolean;
  readonly header?: Record<string, unknown>;
}

export async function signAccessJwt(input: SignInput): Promise<string> {
  const claims: Record<string, unknown> = { ...(input.extraClaims ?? {}) };
  if (!input.omitCommonName && claims.common_name === undefined) {
    claims.common_name = input.commonName ?? WRITE_ID;
  }
  const jwt = new SignJWT(claims).setProtectedHeader({
    alg: 'RS256',
    kid: input.kid,
    ...(input.header ?? {}),
  });
  jwt.setIssuer(input.issuer ?? TEAM_DOMAIN);
  jwt.setAudience(input.audience ?? AUD);
  if (!input.omitIssuedAt) jwt.setIssuedAt(input.issuedAt ?? FIXED_NOW_SECONDS);
  if (!input.omitExpiration) jwt.setExpirationTime(input.expirationTime ?? FIXED_NOW_SECONDS + 3_600);
  if (input.notBefore !== undefined) jwt.setNotBefore(input.notBefore);
  return jwt.sign(input.privateKey);
}

export function unsignedToken(): string {
  return new UnsecuredJWT({ common_name: WRITE_ID })
    .setIssuer(TEAM_DOMAIN)
    .setAudience(AUD)
    .setIssuedAt(FIXED_NOW_SECONDS)
    .setExpirationTime(FIXED_NOW_SECONDS + 3_600)
    .encode();
}

/** Signs with HS256 using the RSA public key as the HMAC secret. */
export async function signConfusedHmac(key: SyntheticKey): Promise<string> {
  const spki = await exportSPKI(key.publicKey);
  const secret = new TextEncoder().encode(spki);
  const hmac = await importJWK({ kty: 'oct', k: Buffer.from(secret).toString('base64url') }, 'HS256');
  return new SignJWT({ common_name: WRITE_ID })
    .setProtectedHeader({ alg: 'HS256', kid: key.kid })
    .setIssuer(TEAM_DOMAIN)
    .setAudience(AUD)
    .setIssuedAt(FIXED_NOW_SECONDS)
    .setExpirationTime(FIXED_NOW_SECONDS + 3_600)
    .sign(hmac);
}

export function tamperSignature(token: string): string {
  const parts = token.split('.');
  const signature = parts[2];
  if (parts.length !== 3 || signature === undefined || signature.length === 0) {
    throw new Error('expected a signed compact JWT');
  }
  const bytes = Buffer.from(signature, 'base64url');
  bytes[0] = (bytes[0] ?? 0) ^ 0xff;
  parts[2] = bytes.toString('base64url');
  return parts.join('.');
}
