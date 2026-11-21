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
    expect(readme).toContain('auth_unconfigured');
    expect(readme).toContain('access_config_invalid');
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

/**
 * The deployment runbook is the one document an operator copies commands out of,
 * so the promises it makes are pinned here rather than left to prose review: the
 * plan prerequisite, the ordering that keeps Access in front of the hostname,
 * the two D1 behaviours that only a remote deployment can prove, and the rule
 * that no real account value may appear in it.
 */
describe('deployment runbook', () => {
  const repoRoot = resolve(packageRoot, '..', '..');
  const runbook = join(repoRoot, 'docs', 'deployment.md');

  async function readRunbook(): Promise<string> {
    return readFile(runbook, 'utf8');
  }

  it('treats Workers Paid as a prerequisite and says why', async () => {
    const doc = await readRunbook();
    expect(doc).toMatch(/Workers\s+\*\*Paid\*\*/);
    expect(doc).toContain('10 ms');
    expect(doc).toContain('D1 query execution and result serialization');
    expect(doc).toContain('not a code defect');
  });

  it('creates the Access application before the hostname exists', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('Create the Access application before you attach the Custom Domain.');
    expect(doc).toContain('Service Auth');
  });

  it('pins the policy constraints that keep Access in front of every path', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('Never add a `Bypass` policy to the application.');
    expect(doc).toContain('Do not enable Managed OAuth on these applications.');
    expect(doc).toContain('strict service token authentication');
    expect(doc).toContain('401');
    expect(doc).toContain('403');
    expect(doc).toContain('302');
    expect(doc).toContain('AUD changes');
  });

  it('requires a remote proof for RETURNING and for batch() rollback', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('RETURNING');
    expect(doc).toContain('changedDays');
    expect(doc).toContain('ignoredReductions');
    expect(doc).toContain('remote D1');
    expect(doc).toContain('stale_revision');
    expect(doc).toContain('DELETE FROM usage_day');
  });

  it('documents secret rotation that leaves the Client ID unchanged', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('Client ID does not change');
    expect(doc).toContain('grace period');
    expect(doc).toContain('30 days');
    expect(doc).toContain('LoadCredential');
    expect(doc).toContain('systemctl restart');
    expect(doc).toContain('one week');
  });

  it('keeps credentials out of command arguments and shell history', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('-H @');
    expect(doc).toContain('--config');
    expect(doc).toContain('wrangler secret put');
    expect(doc).toContain('shell history');
  });

  it('carries the unverified list as a checklist to run in a real account', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('Verify in a real environment');
    const items = doc.match(/^- \[ \] /gm) ?? [];
    expect(items).toHaveLength(9);
  });

  it('has no real host, team domain, hex id or access client id', async () => {
    const doc = await readRunbook();
    const violations: string[] = [];
    for (const match of doc.matchAll(/[A-Za-z0-9-]+\.cloudflareaccess\.com/g)) {
      if (match[0] !== 'example.cloudflareaccess.com') violations.push(`domain ${match[0]}`);
    }
    for (const match of doc.matchAll(/(?<![0-9A-Fa-f])[0-9A-Fa-f]{32,64}(?![0-9A-Fa-f])/g)) {
      violations.push(`hex ${match[0]}`);
    }
    for (const match of doc.matchAll(/[0-9A-Za-z]{16,}\.access\b/g)) {
      violations.push(`client id ${match[0]}`);
    }
    expect(violations).toEqual([]);
    // No absolute path from a machine that ran the deployment, and no
    // profile-relative path either: the runbook is published with the code.
    expect(doc).not.toMatch(/\/(?:home|Users|root)\//);
  });
});
