/**
 * Authenticated principals are resolved server-side from the request; the
 * collector id in the URL is never trusted on its own.
 *
 * The runtime entry point installs Access service-token verification when the
 * Worker environment contains a valid configuration, and `UnconfiguredAuthenticator`
 * otherwise. There is no environment-variable bearer token, no hardcoded secret
 * and no fallback identity in this package.
 */

export type Scope = 'usage:write' | 'usage:read';

export interface Principal {
  readonly ownerId: string;
  /** Collector binding of the credential. `null` means "not collector-bound". */
  readonly collectorId: string | null;
  readonly scopes: readonly Scope[];
}

export interface Authenticator {
  authenticate(request: Request): Promise<Principal | null>;
}

/** Default for an unconfigured deployment: deny everything. */
export class UnconfiguredAuthenticator implements Authenticator {
  async authenticate(): Promise<null> {
    return null;
  }
}

export function hasScope(principal: Principal, scope: Scope): boolean {
  return principal.scopes.includes(scope);
}
