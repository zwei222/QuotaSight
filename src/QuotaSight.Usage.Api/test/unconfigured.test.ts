/**
 * An unconfigured deployment is not a credential problem.
 *
 * The Worker entry installs a deny-all authenticator when its Access
 * configuration is missing or invalid. A client cannot repair that, and a 401
 * would tell it the opposite ("your credential is wrong"), so the whole API
 * answers 503 `auth_unconfigured`: the retryable class, distinguishable from
 * `401 unauthorized` and `503 auth_unavailable`.
 *
 * The isolate also emits exactly one fixed token, `access_config_invalid`, so an
 * operator can see the cause without any secret, team domain or configuration
 * value ever reaching a log. `wrangler.toml` keeps observability disabled, so
 * that line only shows up in `wrangler dev` / `wrangler tail`.
 *
 * The entry point is imported directly here (no Miniflare) so the per-isolate
 * console output can be observed in this process.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';

const CONFIG_TOKEN = 'access_config_invalid';
const UNABLE = 'Authentication is not configured for this deployment.';

/** A fresh module instance, i.e. a fresh isolate with its own memoised app. */
async function freshWorker(): Promise<typeof import('../src/index').default> {
  vi.resetModules();
  const module = await import('../src/index');
  return module.default;
}

async function call(
  worker: typeof import('../src/index').default,
  path: string,
  bindings: Record<string, string> = {},
): Promise<Response> {
  return worker.fetch(
    new Request(`https://usage.test${path}`),
    bindings as never,
    {} as never,
  );
}

interface ErrorBody {
  readonly error: { readonly code: string; readonly message: string; readonly details?: unknown };
}

async function bodyOf(response: Response): Promise<ErrorBody> {
  return (await response.json()) as ErrorBody;
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('a deployment with no Access configuration', () => {
  it('answers 503 auth_unconfigured on both routes instead of 401 unauthorized', async () => {
    const worker = await freshWorker();

    const read = await call(worker, '/v1/usage/daily?from=2026-11-01&to=2026-11-02');
    const write = await call(worker, '/v1/collectors/col-a1/snapshots/1');

    expect(read.status).toBe(503);
    expect(write.status).toBe(503);
    for (const response of [read, write]) {
      expect(await bodyOf(response)).toEqual({ error: { code: 'auth_unconfigured', message: UNABLE } });
    }
  });

  it('logs one fixed token per isolate and never a configuration value', async () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const worker = await freshWorker();
    const bindings = {
      // A partial map is still invalid; none of these values may be logged.
      ACCESS_TEAM_DOMAIN: 'https://example.cloudflareaccess.com',
      ACCESS_AUD: 'synthetic-aud-tag',
      ACCESS_PRINCIPALS: '[{"id":"synthetic-principal.access"}]',
    };

    const first = await call(worker, '/v1/usage/daily?from=2026-11-01&to=2026-11-02', bindings);
    const second = await call(worker, '/v1/collectors/col-a1/snapshots/1', bindings);
    const third = await call(worker, '/v1/usage/daily?from=2026-11-01&to=2026-11-02');

    expect(first.status).toBe(503);
    expect(second.status).toBe(503);
    expect(third.status).toBe(503);

    // Exactly one call, with exactly one argument, and that argument is the token.
    expect(spy).toHaveBeenCalledTimes(1);
    const [args] = spy.mock.calls as unknown as [unknown[]];
    expect(args).toEqual([CONFIG_TOKEN]);
    const logged = args.map((arg) => String(arg)).join(' ');
    for (const value of Object.values(bindings)) {
      expect(logged).not.toContain(value);
    }
    expect(logged).not.toContain('cloudflareaccess');
  });

  it('keeps the retryable 5xx class and carries no detail a client could branch on', async () => {
    const worker = await freshWorker();

    const response = await call(worker, '/v1/usage/daily?from=2026-11-01&to=2026-11-02');
    const body = await bodyOf(response);

    // 503 is what every client of this API retries: the Collector retries 5xx with
    // the same revision and body, and the GUI classifies 503 as transient.
    expect(response.status).toBe(503);
    expect(body.error.code).toBe('auth_unconfigured');
    expect(body.error.details).toBeUndefined();
    expect(Object.keys(body.error).sort()).toEqual(['code', 'message']);
    expect(body.error.message).toBe(UNABLE);
  });

  it('treats an unconfigured and an invalid configuration identically', async () => {
    const empty = vi.spyOn(console, 'error').mockImplementation(() => {});
    const worker = await freshWorker();
    const missing = await call(worker, '/v1/usage/daily?from=2026-11-01&to=2026-11-02');
    expect(missing.status).toBe(503);
    expect(empty).toHaveBeenCalledTimes(1);

    empty.mockRestore();
    const invalidSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const invalid = await freshWorker();
    const response = await call(invalid, '/v1/usage/daily?from=2026-11-01&to=2026-11-02', {
      ACCESS_TEAM_DOMAIN: 'not-a-team-domain',
      ACCESS_AUD: 'short',
      ACCESS_PRINCIPALS: '[]',
    });
    expect(response.status).toBe(503);
    expect((await bodyOf(response)).error.code).toBe('auth_unconfigured');
    expect(invalidSpy).toHaveBeenCalledTimes(1);
  });
});
