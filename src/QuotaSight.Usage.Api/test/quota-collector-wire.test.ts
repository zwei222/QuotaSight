import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { createHarness, FIXTURE, json, resetData, type Harness } from './harness';

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = resolve(packageRoot, '../..');
const BUILD_HINT = 'dotnet publish src/QuotaSight.Collector/QuotaSight.Collector.csproj -c Release';

function collectorBinary(): string {
  const candidates = [
    process.env['QUOTASIGHT_COLLECTOR_BIN'],
    join(repositoryRoot, 'src/QuotaSight.Collector/bin/Release/net10.0/QuotaSight.Collector'),
    join(repositoryRoot, 'src/QuotaSight.Collector/bin/Debug/net10.0/QuotaSight.Collector'),
  ];
  const found = candidates.find(candidate => typeof candidate === 'string' && candidate.length > 0 && existsSync(candidate));
  if (found === undefined) throw new Error(`The real QuotaSight.Collector quota-preview binary was not found. Build it with: ${BUILD_HINT}`);
  return found;
}

// Deliberately synthetic TokScale usage; all private values must stay off the quota wire.
const syntheticUsage = JSON.stringify([
  { provider: 'Claude', account: { id: 'claude-account-secret', label: 'private account', is_active: true }, email: 'private@example.test', plan: 'private plan', freshness: { fetched_at: 1760000000, stale: false }, metrics: [
    { label: 'Session', remaining_percent: 0, resets_at: '2026-12-01T00:00:00Z', remaining_label: 'private remaining text' },
    { label: 'Weekly', remaining_percent: 100, resets_at: null },
  ] },
  { provider: 'Codex', account: null, metrics: [
    { label: '5h', remaining_percent: null, resets_at: null },
    { label: 'Weekly', remaining_percent: 50, resets_at: '2026-12-02T00:00:00Z' },
  ] },
  { provider: 'OpenCode Go', plan: 'private Go plan', metrics: [
    { label: 'Rolling', remaining_percent: 75, resets_at: null },
    { label: 'Weekly', remaining_percent: 25, resets_at: null },
    { label: 'Monthly', remaining_percent: 0, resets_at: null },
  ] },
  { provider: 'Antigravity', account: { id: 'gemini-models', label: 'Gemini Models', is_active: true }, metrics: [
    { label: '5h', remaining_percent: 40, resets_at: null }, { label: 'weekly', remaining_percent: 60, resets_at: null },
  ] },
  { provider: 'Antigravity', account: { id: 'claude-and-gpt-models', label: 'Claude and GPT Models', is_active: true }, metrics: [
    { label: '5h', remaining_percent: 20, resets_at: null }, { label: 'weekly', remaining_percent: 80, resets_at: null },
  ] },
  { provider: 'Grok', account: { id: 'grok-account-secret', label: 'private Grok account', is_active: true }, metrics: [
    { label: 'Weekly', remaining_percent: 90, resets_at: '2026-12-03T00:00:00Z' },
    { label: 'Monthly', remaining_percent: 70, resets_at: null },
    { label: 'Grok Bot', remaining_percent: 100, resets_at: null, remaining_label: 'grok free text secret' },
  ] },
  { provider: 'Copilot', account: { id: 'copilot-account-secret', label: 'private Copilot account', is_active: true }, metrics: [
    { label: 'Premium', remaining_percent: null, resets_at: null, remaining_label: 'unlimited' },
    { label: 'Chat', remaining_percent: null, resets_at: null },
    { label: 'Completions', remaining_percent: 0, resets_at: null },
  ] },
]);

let h: Harness;
beforeAll(async () => { h = await createHarness(); }, 60_000);
afterAll(async () => { await h.dispose(); });
beforeEach(async () => { await resetData(h.db); });

describe('real Collector quota-preview wire reaches Usage API', () => {
  it('persists the real stdout and reads seven isolated quota groups without private input fields', async () => {
    const run = spawnSync(collectorBinary(), ['--quota-preview', '--generation', '1', '--grok-build-first-party'], {
      input: syntheticUsage, encoding: 'utf8', timeout: 60_000, maxBuffer: 1024 * 1024,
    });
    if (run.error !== undefined) throw run.error;
    expect(run.status, run.stderr).toBe(0);
    expect(run.stderr).toBe('');
    expect(run.stdout.length).toBeGreaterThan(0);

    const put = await h.request(`/v1/collectors/${FIXTURE.collectorA1}/quota`, {
      method: 'PUT', headers: { authorization: 'Bearer write-a1', 'content-type': 'application/json' }, body: run.stdout,
    });
    expect(put.status, await put.clone().text()).toBe(200);
    const read = await h.getQuota();
    expect(read.status).toBe(200);
    const response = await json<{ snapshots: Array<{ collectedAt: string; providers: Array<Record<string, unknown>> }> }>(read);
    expect(response.snapshots).toHaveLength(1);
    expect(response.snapshots[0]!.providers.map(provider => provider['provider']), run.stdout).toEqual([
      'claude', 'codex', 'opencode-go', 'antigravity-gemini', 'antigravity-claude-gpt', 'grok-build', 'copilot',
    ]);
    const groups = response.snapshots[0]!.providers;
    const find = (provider: string) => groups.find(group => group['provider'] === provider)!;
    const windows = (provider: string) => find(provider)['windows'] as Array<Record<string, unknown>>;
    expect(windows('claude')[0]).toMatchObject({ window: 'session', remainingBasisPoints: 0, resetsAt: '2026-12-01T00:00:00Z' });
    expect(windows('codex')[0]).toMatchObject({ window: 'five-hour', remainingBasisPoints: null });
    expect(windows('copilot')[0]).toMatchObject({ window: 'premium', remainingBasisPoints: null, unlimited: true });
    expect(windows('copilot')[1]).not.toHaveProperty('unlimited');
    expect(windows('antigravity-gemini').map(window => window['remainingBasisPoints'])).toEqual([4000, 6000]);
    expect(windows('antigravity-claude-gpt').map(window => window['remainingBasisPoints'])).toEqual([2000, 8000]);
    expect(JSON.stringify(response)).not.toMatch(/private|secret|example\.test|Grok Bot|remaining text|Grok account/i);
  });
});
