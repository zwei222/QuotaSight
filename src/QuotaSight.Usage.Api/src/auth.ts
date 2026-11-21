/**
 * Authenticated principals are resolved server-side from the request; the
 * collector id in the URL is never trusted on its own.
 *
 * The runtime entry point installs Access service-token verification when the
 * Worker environment contains a valid configuration, and `UnconfiguredAuthenticator`
 * otherwise. There is no environment-variable bearer token, no hardcoded secret
 * and no fallback identity in this package.
 */

import { ApiError } from './errors';

export type Scope = 'usage:write' | 'usage:read';

export interface Principal {
  readonly ownerId: string;
  /** Collector binding of the credential. `null` means "not collector-bound". */
  readonly collectorId: string | null;
  readonly scopes: readonly Scope[];
}

export interface Authenticator {
  /**
   * Resolves the caller, or `null` when the request is not authenticated (the app
   * answers one fixed 401). A failure of the service itself — an unavailable key
   * set, or a deployment with no Access configuration — is thrown as an ApiError
   * with its own 503 code instead, so it is never mistaken for a bad credential.
   */
  authenticate(request: Request): Promise<Principal | null>;
}

/**
 * Default for an unconfigured deployment: the whole API is unavailable, not the
 * caller's credential.
 *
 * A 401 would tell a client its credential is wrong, and no client can repair a
 * missing deployment configuration. This answers 503 `auth_unconfigured` instead:
 * the retryable class, fixed body, no details and no configuration value.
 */
export class UnconfiguredAuthenticator implements Authenticator {
  async authenticate(): Promise<never> {
    throw new ApiError(
      503,
      'auth_unconfigured',
      'Authentication is not configured for this deployment.',
    );
  }
}

export function hasScope(principal: Principal, scope: Scope): boolean {
  return principal.scopes.includes(scope);
}
