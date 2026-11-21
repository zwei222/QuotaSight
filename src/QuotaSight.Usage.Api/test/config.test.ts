import { execFile } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { describe, expect, it } from 'vitest';

/**
 * Local-only package guarantees. There is no remote exposure in this slice: no
 * account binding, no route, no workers.dev subdomain and no preview URL. These
 * are asserted against the real configuration file so a later edit cannot
 * quietly publish the Worker.
 */
const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const execFileAsync = promisify(execFile);

async function readConfig(): Promise<string> {
  return readFile(join(packageRoot, 'wrangler.toml'), 'utf8');
}

async function packageFiles(): Promise<string[]> {
  const { stdout } = await execFileAsync('git', ['ls-files', '-co', '--exclude-standard', '-z'], {
    cwd: packageRoot,
  });
  return stdout.split('\0').filter((name) => name.length > 0);
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

  it('does not publish Access configuration', async () => {
    const config = await readConfig();
    expect(config).not.toMatch(/^\[vars\]\s*$/m);
    expect(config).not.toMatch(/\bACCESS_/);
  });
});

describe('repository hygiene', () => {
  it('ignores local secret files and keeps only a placeholder example', async () => {
    const ignore = await readFile(join(packageRoot, '.gitignore'), 'utf8');
    expect(ignore).toMatch(/^\.dev\.vars\*$/m);
    expect(ignore).toMatch(/^!\.dev\.vars\.example$/m);
    expect(ignore).toMatch(/^\.env\*$/m);
    const example = await readFile(join(packageRoot, '.dev.vars.example'), 'utf8');
    expect(example).toContain('https://example.cloudflareaccess.com');
    expect(example).toContain('<aud-tag>');
    expect(example).toContain('<client-id>.access');
  });

  it('documents Access verification, fail-closed behavior and the bypass assumptions', async () => {
    const readme = await readFile(join(packageRoot, 'README.md'), 'utf8');
    expect(readme).toContain('ACCESS_TEAM_DOMAIN');
    expect(readme).toContain('ACCESS_AUD');
    expect(readme).toContain('ACCESS_PRINCIPALS');
    expect(readme).toContain('ACCESS_MAX_TOKEN_AGE_SECONDS');
    expect(readme).toContain('Cf-Access-Jwt-Assertion');
    expect(readme).toContain('auth_unavailable');
    expect(readme).toContain('workers_dev');
    expect(readme).toContain('preview_urls');
    expect(readme).toMatch(/Service Binding/);
    expect(readme).not.toContain('Real authentication and credential issuance');
  });

  it('has no real-looking team domain, hex secret or embedded key', async () => {
    const domain = /[A-Za-z0-9-]+\.cloudflareaccess\.com/g;
    const hex = /(?<![0-9A-Fa-f])[0-9A-Fa-f]{32,64}(?![0-9A-Fa-f])/g;
    const base64 = /[A-Za-z0-9+/_-]{80,}={0,2}/g;
    const violations: string[] = [];
    for (const name of await packageFiles()) {
      const text = await readFile(join(packageRoot, name), 'utf8');
      for (const match of text.matchAll(domain)) {
        if (match[0] !== 'example.cloudflareaccess.com') violations.push(`${name}: domain ${match[0]}`);
      }
      if (name === 'package-lock.json') continue;
      for (const match of text.matchAll(hex)) violations.push(`${name}: hex ${match[0]}`);
      for (const match of text.matchAll(base64)) violations.push(`${name}: base64 ${match[0].slice(0, 24)}…`);
    }
    expect(violations).toEqual([]);
  });
});
