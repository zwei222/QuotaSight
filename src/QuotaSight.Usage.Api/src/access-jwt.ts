/**
 * Verifies a Cloudflare Access service-token JWT and maps `common_name` to a
 * server-side principal. Token claims never grant scopes.
 *
 * Signature, expiry, claim and identity failures return null. The application
 * turns that into one fixed 401. JWKS availability failures, and anything else
 * the verifier throws, become a reason-free 503 so a 500 cannot echo internals.
 */
import { errors, jwtVerify, type JWTVerifyGetKey } from 'jose';
import type { Authenticator, Principal } from './auth';
import { UnconfiguredAuthenticator } from './auth';
import { parseAccessConfig, type AccessConfig, type AccessEnv } from './access-config';
import { ApiError } from './errors';

const ASSERTION_HEADER = 'Cf-Access-Jwt-Assertion';
const MAX_ASSERTION_BYTES = 8 * 1024;
const COMMON_NAME_PATTERN = /^[A-Za-z0-9._-]{1,128}$/;
const CLOCK_TOLERANCE_SECONDS = 60;
const UNAVAILABLE = 'Authentication is temporarily unavailable.';

export interface AccessJwtOptions {
  readonly issuer: string;
  readonly audiences: readonly string[];
  readonly principals: ReadonlyMap<string, Principal>;
  readonly keys: JWTVerifyGetKey;
  readonly maxTokenAgeSeconds: number;
  readonly now?: () => Date;
}

function isTokenRejection(err: unknown): boolean {
  return (
    err instanceof errors.JWTExpired ||
    err instanceof errors.JWTClaimValidationFailed ||
    err instanceof errors.JWTInvalid ||
    err instanceof errors.JWSInvalid ||
    err instanceof errors.JWSSignatureVerificationFailed ||
    err instanceof errors.JOSEAlgNotAllowed ||
    err instanceof errors.JOSENotSupported
  );
}

function unavailable(): ApiError {
  return new ApiError(503, 'auth_unavailable', UNAVAILABLE);
}

export class AccessJwtAuthenticator implements Authenticator {
  constructor(private readonly options: AccessJwtOptions) {}

  async authenticate(request: Request): Promise<Principal | null> {
    const assertion = request.headers.get(ASSERTION_HEADER);
    if (assertion === null || assertion.length === 0) return null;
    if (new TextEncoder().encode(assertion).byteLength > MAX_ASSERTION_BYTES) return null;

    let commonName: unknown;
    try {
      const verified = await jwtVerify(assertion, this.options.keys, {
        algorithms: ['RS256'],
        issuer: this.options.issuer,
        audience: [...this.options.audiences],
        requiredClaims: ['exp', 'iat'],
        clockTolerance: CLOCK_TOLERANCE_SECONDS,
        maxTokenAge: this.options.maxTokenAgeSeconds,
        currentDate: this.options.now?.() ?? new Date(),
      });
      commonName = verified.payload.common_name;
    } catch (err) {
      if (isTokenRejection(err)) return null;
      throw unavailable();
    }

    if (typeof commonName !== 'string' || !COMMON_NAME_PATTERN.test(commonName)) return null;
    const stored = this.options.principals.get(commonName);
    if (stored === undefined) return null;
    return {
      ownerId: stored.ownerId,
      collectorId: stored.collectorId,
      scopes: [...stored.scopes],
    };
  }
}

/**
 * Builds the request authenticator from Worker bindings. Invalid configuration
 * does not resolve keys and denies every request.
 */
export function createAccessAuthenticator(
  env: AccessEnv,
  resolveKeys: (config: AccessConfig) => JWTVerifyGetKey,
  now?: () => Date,
): Authenticator {
  const config = parseAccessConfig(env);
  if (config === null) return new UnconfiguredAuthenticator();
  return new AccessJwtAuthenticator({
    issuer: config.issuer,
    audiences: config.audiences,
    principals: config.principals,
    maxTokenAgeSeconds: config.maxTokenAgeSeconds,
    keys: resolveKeys(config),
    now,
  });
}
