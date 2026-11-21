/**
 * Worker entry point.
 *
 * The app is built once per isolate so the Access JWKS cache survives requests.
 * A missing or invalid configuration installs the deny-all authenticator and
 * does not fetch a key set. The JWKS URL is derived only from the configured
 * team domain, never from a token claim.
 */
import { createRemoteJWKSet } from 'jose';
import { createAccessAuthenticator } from './access-jwt';
import { createApp, type AppEnv } from './app';

type WorkerEnv = AppEnv['Bindings'];

let app: ReturnType<typeof createApp> | undefined;

function getApp(env: WorkerEnv): ReturnType<typeof createApp> {
  if (app !== undefined) return app;
  app = createApp({
    authenticator: createAccessAuthenticator(env, (config) =>
      createRemoteJWKSet(new URL(`${config.issuer}/cdn-cgi/access/certs`)),
    ),
  });
  return app;
}

export default {
  async fetch(request: Request, env: WorkerEnv, ctx: ExecutionContext): Promise<Response> {
    return getApp(env).fetch(request, env, ctx);
  },
};
