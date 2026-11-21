/**
 * Worker entry point.
 *
 * The app is built once per isolate so the Access JWKS cache survives requests.
 * A missing or invalid configuration installs the deny-all authenticator (503
 * `auth_unconfigured`) and does not fetch a key set. The JWKS URL is derived
 * only from the configured team domain, never from a token claim.
 *
 * That same path emits one fixed token, `access_config_invalid`, once per
 * isolate so an operator can see why every request was refused. It carries no
 * secret, team domain, audience, principal or other configuration value, and
 * `wrangler.toml` keeps observability disabled, so it is visible only in
 * `wrangler dev` / `wrangler tail` and never retained by a log product.
 */
import { createRemoteJWKSet } from 'jose';
import { createAccessAuthenticator } from './access-jwt';
import { createApp, type AppEnv } from './app';
import { UnconfiguredAuthenticator } from './auth';

type WorkerEnv = AppEnv['Bindings'];

/** One fixed line per isolate: no secret, domain, audience or principal value. */
const ACCESS_CONFIG_INVALID = 'access_config_invalid';

let app: ReturnType<typeof createApp> | undefined;

function getApp(env: WorkerEnv): ReturnType<typeof createApp> {
  if (app !== undefined) return app;
  const authenticator = createAccessAuthenticator(env, (config) =>
    createRemoteJWKSet(new URL(`${config.issuer}/cdn-cgi/access/certs`)),
  );
  // `app` is memoised above, so this branch runs once per isolate even though
  // every request passes through here.
  if (authenticator instanceof UnconfiguredAuthenticator) {
    console.error(ACCESS_CONFIG_INVALID);
  }
  app = createApp({ authenticator });
  return app;
}

export default {
  async fetch(request: Request, env: WorkerEnv, ctx: ExecutionContext): Promise<Response> {
    return getApp(env).fetch(request, env, ctx);
  },
};
