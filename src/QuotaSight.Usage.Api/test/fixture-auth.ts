/**
 * TEST-ONLY fixture identities. These are not credentials: the authenticator
 * below exists solely to give the local tests a scoped caller. The production
 * entry point (`src/index.ts`) uses the deny-all authenticator instead.
 */
import type { Authenticator, Principal } from '../src/auth';

const tokens: Record<string, Principal> = {
  'write-a1': { ownerId: 'owner-a', collectorId: 'col-a1', scopes: ['usage:write'] },
  'write-a2': { ownerId: 'owner-a', collectorId: 'col-a2', scopes: ['usage:write'] },
  'write-b1': { ownerId: 'owner-b', collectorId: 'col-b1', scopes: ['usage:write'] },
  'read-a1': { ownerId: 'owner-a', collectorId: null, scopes: ['usage:read'] },
  'read-b1': { ownerId: 'owner-b', collectorId: null, scopes: ['usage:read'] },
  // Collector-bound identities that also carry a read scope: the API must not
  // let a single-collector credential read owner-wide usage.
  'read-bound-a1': { ownerId: 'owner-a', collectorId: 'col-a1', scopes: ['usage:read'] },
  'write-read-a1': { ownerId: 'owner-a', collectorId: 'col-a1', scopes: ['usage:write', 'usage:read'] },
};

export const fixtureAuthenticator: Authenticator = {
  async authenticate(request: Request): Promise<Principal | null> {
    const header = (request.headers.get('authorization') ?? '').trim();
    const match = /^Bearer ([A-Za-z0-9._-]+)$/.exec(header);
    if (match === null) return null;
    return tokens[match[1] as string] ?? null;
  },
};
