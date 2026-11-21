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

  it('does not present Workers Paid as a prerequisite in the package README', async () => {
    const readme = await readFile(join(packageRoot, 'README.md'), 'utf8');
    expect(readme).toContain('Workers Free');
    expect(readme).toContain('CPU measurement gate');
    expect(readme).not.toMatch(/needs Workers \*\*Paid\*\*/);
    expect(readme).not.toContain('Deployment prerequisite');
    expect(readme).not.toContain('count toward that CPU time');
  });

  it('states the same free-tier premise in the architecture and security documents', async () => {
    const repoRootPath = resolve(packageRoot, '..', '..');
    for (const name of ['architecture.md', 'security.md']) {
      const doc = await readFile(join(repoRootPath, 'docs', name), 'utf8');
      expect(doc).toContain('Workers Free');
      expect(doc).toContain('CPU');
      expect(doc).not.toMatch(/Workers\s+Paid/);
    }
  });

  it('records in the package README what a real deployment settled', async () => {
    const readme = await readFile(join(packageRoot, 'README.md'), 'utf8');
    // Class names and prose wrap; compare the claims, not the line breaks.
    const flat = readme.replace(/\s+/g, ' ');
    expect(flat).toContain('Observed in one account on 2026-11-22');
    expect(flat).toContain('HTTP 403');
    expect(flat).toContain('HTTP 401');
    expect(flat).toContain('302');
    expect(flat).toContain('checks 1 to 4');
    // The old blanket "nothing has been observed" claim is gone; only the
    // narrower gaps stay listed.
    expect(readme).not.toContain('no rejection has been observed against a real Access');
    expect(flat).toContain('strict service token authentication off');
  });

  it('drops the unobserved-Access claim from the architecture document', async () => {
    const architecture = await readFile(join(packageRoot, '..', '..', 'docs', 'architecture.md'), 'utf8');
    expect(architecture).not.toContain(
      '実環境の Cloudflare Access が不正なサービス トークンに対して 403 や 302 を返すかは未検証です。',
    );
    expect(architecture).toContain('2026-11-22');
    expect(architecture).toContain('403');
    expect(architecture).toContain('401');
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
 * three free plans as the premise, what does and does not consume the free CPU
 * budget, the measurement gate that decides the deployment, the fallback that is
 * documented but deliberately not applied, the ordering that keeps Access in
 * front of the hostname, the two D1 behaviours that only a remote deployment can
 * prove, and the rule that no real account value may appear in it.
 */
describe('deployment runbook', () => {
  const repoRoot = resolve(packageRoot, '..', '..');
  const runbook = join(repoRoot, 'docs', 'deployment.md');

  async function readRunbook(): Promise<string> {
    return readFile(runbook, 'utf8');
  }

  /** Line breaks in the runbook are prose, not contract: compare on one line. */
  const flatten = (text: string): string => text.replace(/\s+/g, ' ');

  async function readRunbookFlat(): Promise<string> {
    return flatten(await readRunbook());
  }

  it('makes the three free plans the premise and drops the Paid prerequisite', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('Workers Free, D1 Free and Zero Trust Free');
    expect(doc).toMatch(/No Workers Paid\s+subscription\s+is\s+required\s+or\s+requested/);
    expect(doc).not.toMatch(/Workers\s+\*\*Paid\*\*/);
    expect(doc).not.toContain('plan prerequisite');
    expect(doc).not.toContain('not a tuning choice');
    expect(doc).not.toContain('do not deploy this slice to a Free account');
  });

  it('separates what consumes the Free CPU budget from what does not', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('Waiting on a D1 query does not count toward CPU time');
    expect(doc).toContain(
      'query execution and result serialization run within the Workers platform CPU and memory limits',
    );
    expect(doc).toContain('RS256');
    expect(doc).toContain('JSON.parse');
    expect(doc).toContain('JSON.stringify');
    expect(doc).toContain('10-20 ms');
    expect(doc).toContain('conditionally feasible');
    expect(doc).toContain('no measurement of this implementation');
  });

  it('records the Free limits the deployment has to stay inside', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('100,000 requests per day');
    expect(doc).toContain('10 ms of CPU time');
    expect(doc).toContain('50 subrequests');
    expect(doc).toContain('50 queries per invocation');
    expect(doc).toContain('counts as one query');
    expect(doc).toContain('100,000 rows written per day');
    expect(doc).toContain('5,000,000 rows read per day');
    expect(doc).toContain('5 GB');
    expect(doc).toContain('00:00 UTC');
    expect(doc).toContain('50 service tokens');
    expect(doc).toContain('does not consume a seat');
    expect(doc).toContain('200,000 events per day');
    expect(doc).toContain('Workers Logs keeps 3 days');
    expect(doc).toContain('Access logs are kept 24 hours');
  });

  it('refuses every action that would start billing', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('Do not upgrade the account to Workers Paid');
    expect(doc).toContain('payment details');
    expect(doc).toContain('nothing is charged');
    expect(doc).toContain('queried past its limit fails and recovers at 00:00 UTC');
  });

  it('gates the deployment on measured CPU instead of on a plan', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('## 6. CPU measurement gate');
    expect(doc).toContain('Exceeded CPU Time Limits');
    expect(doc).toContain('Error 1102');
    expect(doc).toContain('P99');
    expect(doc).toContain('8 ms');
    expect(doc).toContain('rollover');
    expect(doc).toMatch(/a P99 above 10 ms can be\s+recorded without a single error/);
    expect(doc).toContain('62 days');
    expect(doc).toContain('63 statements');
    expect(doc).toContain('32 KiB');
    expect(doc).toContain('JWKS cache miss');
  });

  it('records a local estimate in §6 without letting it stand in for the gate', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('### Local estimate before the gate');
    // The shapes the gate measures, with the numbers actually observed.
    expect(doc).toContain('62 days, 32.7 KiB body, 63 statements');
    expect(doc).toContain('0.49 ms');
    expect(doc).toContain('1.65 ms');
    expect(doc).toContain('3.79 ms');
    expect(doc).toContain('1.22 ms');
    expect(doc).toContain('2.79 ms');
    expect(doc).toContain('5.35 ms');
    // Where that median goes, so a later edit cannot describe a different cost.
    expect(doc).toContain('date validation ~28%');
    expect(doc).toContain('RS256 verification ~23%');
    // The estimate is a baseline for comparison, never the decision.
    expect(doc).toContain('not a substitute for the gate');
    expect(doc).toContain('about 0.5 ms');
    expect(doc).toContain('2-3x');
    expect(doc).toContain('above 5 ms');
    expect(doc).toContain('still unmeasured');
  });

  it('keeps the fallback design documented and explicitly not applied', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('### 6.5 If the gate fails: fallback options (not applied)');
    expect(doc).toContain('Delegate the signature check to Access');
    expect(doc).toContain('Move the aggregation into SQL');
    expect(doc).toContain('Splitting the batch does not reduce CPU');
    expect(doc).toMatch(/documented and not\s+implemented here/);
    expect(doc).toContain('the deployment is called off');
    expect(doc).toContain('does not fit the Free plans');
  });

  it('turns strict service token authentication on before the smoke test', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('Zero Trust → Access controls → Access settings → Manage service tokens');
    expect(doc).toContain('on (recommended)');
    expect(doc).toContain('2026-10-05');
  });

  it('bounds an authentication investigation by the Free log retention', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('### 3.7 Investigating an authentication failure');
    expect(doc).toContain('only the last 24 hours');
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

  it('records the remote proof of RETURNING and of batch() rollback as done', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('RETURNING');
    expect(doc).toContain('changedDays');
    expect(doc).toContain('ignoredReductions');
    expect(doc).toContain('remote D1');
    expect(doc).toContain('stale_revision');
    expect(doc).toContain('DELETE FROM usage_day');
    // The two behaviours the prose used to demand as a future proof are now
    // recorded as proven against remote D1, in one account, on one date, and the
    // runbook still says a new deployment has to re-run the same four checks.
    const flat = await readRunbookFlat();
    expect(flat).toContain('Observed on 2026-11-22');
    expect(flat).toMatch(/all four checks matched/);
    expect(flat).toContain('re-runs the same four checks');
  });

  it('records the Access status observed for each rejected credential', async () => {
    const flat = await readRunbookFlat();
    expect(flat).toContain('No credential at all → HTTP 403');
    expect(flat).toContain('wrong secret → HTTP 401');
    expect(flat).toContain('A revoked secret');
    expect(flat).toContain('No `302` was observed');
    // The other half of the old open question stays open, as a limitation.
    expect(flat).toContain('strict service token authentication off');
    expect(flat).toContain('token from another application');
  });

  it('records the end-to-end result of a valid write and read credential', async () => {
    const flat = await readRunbookFlat();
    expect(flat).toContain('forbidden_scope');
    expect(flat).toContain('lastReceivedAt');
    expect(flat).toMatch(/about 0\.09 s/);
    expect(flat).toContain('a read token cannot `PUT`');
  });

  it('documents registering a real collector row and swapping the principals', async () => {
    const flat = await readRunbookFlat();
    expect(flat).toContain('INSERT INTO collectors (id, owner_id, active_generation, created_at)');
    expect(flat).toContain("strftime('%Y-%m-%dT%H:%M:%SZ','now')");
    expect(flat).toContain('`ownerId` must be the principal');
    // One Client ID, one subject: the map is rejected as a whole otherwise.
    expect(flat).toContain('one Client ID cannot appear as two subjects');
    expect(flat).toContain('a **swap**, not an addition');
  });

  it('documents the deploy order and the uncommitted deployment configuration', async () => {
    const flat = await readRunbookFlat();
    expect(flat).toContain('No targets deployed');
    expect(flat).toContain('.git/info/exclude');
    expect(flat).toContain('database_id');
    expect(flat).toContain('custom_domain = true');
    // `-c` is how the file is selected, so wrangler.toml stays untouched.
    expect(flat).toContain('`-c`');
    expect(flat).toContain('without a route and without a workers.dev subdomain');
  });

  it('documents the wrangler login callback and the logout that follows', async () => {
    const flat = await readRunbookFlat();
    expect(flat).toContain('localhost:8976');
    expect(flat).toContain('wrangler logout');
    expect(flat).toContain('the browser is on another machine');
  });

  it('recommends a one-year token duration and records the observed maximum', async () => {
    const flat = await readRunbookFlat();
    expect(flat).toContain('10 years');
    expect(flat).toContain('one year');
    expect(flat).toContain('the default is still unconfirmed');
  });

  it('warns that the policy action selector can reset to Allow', async () => {
    const flat = await readRunbookFlat();
    expect(flat).toContain('Any Access Service Token');
    expect(flat).toContain('resets to `Allow`');
    expect(flat).toContain('create the token first');
  });

  it('records rotating a secret instead of displaying the stored one', async () => {
    const flat = await readRunbookFlat();
    expect(flat).toContain('rotate again after the test');
    expect(flat).toMatch(/immediate revocation|invalidates immediately/);
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

  it('carries only the still-unverified list as a checklist to run in a real account', async () => {
    const doc = await readRunbook();
    expect(doc).toContain('Verify in a real environment');
    expect(doc).toContain('### 8.1 Observed in one real account (2026-11-22)');
    expect(doc).toContain('### 8.2 Still unverified');
    const items = doc.match(/^- \[ \] /gm) ?? [];
    expect(items).toHaveLength(10);
    expect(doc).toContain('cpuTime');
    expect(doc).toContain('[observability]');
    // The two behaviours that moved out of the checklist are still named, as
    // observed facts rather than as work an operator owes.
    const observed = doc.slice(doc.indexOf('### 8.1'), doc.indexOf('### 8.2'));
    expect(observed).toContain('RETURNING');
    expect(observed).toContain('rollback scope');
    expect(observed).toContain('403');
    expect(observed).toContain('401');
  });

  it('records the first real CPU measurement with its sample sizes and conditions', async () => {
    const flat = await readRunbookFlat();
    const start = flat.indexOf('### 6.0 First real measurement (2026-11-22)');
    expect(start).toBeGreaterThan(-1);
    const section = flat.slice(start, flat.indexOf('### 6.1 Pass condition'));
    // Conditions first: one account, the free plans, one region, one day.
    expect(section).toContain('one account');
    expect(section).toContain('Workers Free');
    expect(section).toContain('Tokyo / NRT');
    expect(section).toContain('2026-11-22');
    // Every reading keeps the sample size it came from.
    expect(section).toContain('7 invocations');
    expect(section).toContain('8.91 ms');
    expect(section).toContain('22.53 ms');
    expect(section).toContain('P50 98 ms');
    expect(section).toContain('40 consecutive invocations');
    expect(section).toContain('1.87 ms');
    expect(section).toContain('8 invocations');
    expect(section).toContain('3.84 ms');
    expect(section).toContain('53 invocations');
    expect(section).toContain('4.87 ms');
    expect(section).toContain('13.53 ms');
    expect(section).toContain('14.93 ms');
  });

  it('leaves the CPU gate undecided and claims neither a pass nor a failure', async () => {
    const flat = await readRunbookFlat();
    const section = flat.slice(
      flat.indexOf('### 6.0 First real measurement (2026-11-22)'),
      flat.indexOf('### 6.1 Pass condition'),
    );
    expect(section).toContain('stays undecided');
    expect(section).toContain('1102');
    expect(section).toMatch(/Neither a pass\s+nor a failure is recorded here/);
    // The cold-start explanation is named as a guess, not as an observation.
    expect(section).toContain('a guess, not a verified cause');
    // The local estimate was optimistic, and by how much it was.
    expect(section).toContain('optimistic');
    expect(section).toContain('about 8x');
    expect(section).toContain('about 10x');
    expect(section).toContain('2-3x');
    // No verdict may appear elsewhere either.
    expect(flat).not.toContain('the gate passed');
    expect(flat).not.toContain('passed the gate');
    expect(flat).not.toContain('the gate failed on');
  });

  it('separates the measured 62-day PUT from the unmeasured 5000-row read', async () => {
    const doc = await readRunbook();
    const unverified = doc.slice(doc.indexOf('### 8.2 Still unverified'));
    expect(unverified).toContain('5000-row read');
    // The write half is reported as measured, in §8.1, not as work still owed.
    expect(unverified).toMatch(/62-day, 32 KiB `PUT` half of this item was measured/);
    expect(unverified).toContain('the read half was not');
    // The gate itself is the other open item, with the sample that was too small.
    expect(unverified).toMatch(/53 invocations/);
  });

  it('records the CPU readings of that run in §8.1 as observations', async () => {
    const flat = await readRunbookFlat();
    const observed = flat.slice(
      flat.indexOf('### 8.1 Observed in one real account'),
      flat.indexOf('### 8.2 Still unverified'),
    );
    expect(observed).toContain('CPU Time per execution');
    expect(observed).toContain('1.87 ms');
    expect(observed).toContain('3.84 ms');
    expect(observed).toContain('14.93 ms');
    expect(observed).toContain('not the gate');
    // The read path at the documented bound was not exercised, and §8.1 says so.
    expect(observed).toContain('5000-row bound');
  });

  it('records the remote migrations without claiming they prove transactionality', async () => {
    const flat = await readRunbookFlat();
    expect(flat).toContain('`0001`–`0003`');
    expect(flat).toContain('`d1_migrations`');
    expect(flat).toContain('two indexes and four triggers');
    expect(flat).toContain('not a proof that each migration file runs in one transaction');
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
