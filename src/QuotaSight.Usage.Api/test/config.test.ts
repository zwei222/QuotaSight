import { readFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

/**
 * Local-only package guarantees. There is no remote exposure in this slice: no
 * account binding, no route, no workers.dev subdomain and no preview URL. These
 * are asserted against the real configuration file so a later edit cannot
 * quietly publish the Worker.
 */
const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

async function readConfig(): Promise<string> {
  return readFile(join(packageRoot, 'wrangler.toml'), 'utf8');
}

describe('wrangler configuration', () => {
  it('disables the workers.dev subdomain', async () => {
    expect(await readConfig()).toMatch(/^workers_dev\s*=\s*false$/m);
  });

  it('disables preview urls', async () => {
    expect(await readConfig()).toMatch(/^preview_urls\s*=\s*false$/m);
  });

  it('declares no remote exposure', async () => {
    const config = await readConfig();
    expect(config).not.toMatch(/^\s*account_id\s*=/m);
    expect(config).not.toMatch(/^\s*route\s*=/m);
    expect(config).not.toMatch(/^\s*routes\s*=/m);
    expect(config).not.toMatch(/\bremote\s*=\s*true/);
    expect(config).not.toMatch(/\bzone_id\s*=/);
  });

  it('binds only the local D1 database', async () => {
    const config = await readConfig();
    expect(config).toMatch(/^\[\[d1_databases\]\]$/m);
    expect(config).toMatch(/^binding\s*=\s*"DB"$/m);
  });
});
