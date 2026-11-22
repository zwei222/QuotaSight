import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { createHarness, json, resetData, FIXTURE, type Harness } from './harness';

let h: Harness;
beforeAll(async () => { h = await createHarness(); }, 60_000);
afterAll(async () => { await h.dispose(); });
beforeEach(async () => { await resetData(h.db); });

const payload = (collectedAt = '2026-11-20T03:00:00.123Z', remaining = 0) => ({
  schemaVersion: 1, generation: 1, collectedAt,
  providers: [{ provider: 'claude', status: 'available', freshness: 'fresh',
    observedAt: '2026-11-20T02:59:00Z', windows: [
      { window: 'session', remainingBasisPoints: remaining, resetsAt: null },
      { window: 'weekly', remainingBasisPoints: null, resetsAt: '2026-11-25T00:00:00Z' },
    ] }],
});

describe('independent quota snapshot API', () => {
  it('stores and returns exact provider boundaries, including unknown distinct from zero', async () => {
    const written = await h.putQuota(payload());
    expect(written.status).toBe(200);
    expect(await json(await h.getQuota())).toEqual({ snapshots: [{ collectedAt: payload().collectedAt, providers: payload().providers }] });
    expect(await h.getQuota('read-b1').then(r => r.status)).toBe(200);
    expect(await h.getQuota('read-b1').then(json)).toEqual({ snapshots: [] });
  });

  it('atomically retains only the newest collection and rejects malformed combinations', async () => {
    await h.putQuota(payload('2026-11-20T03:00:00.123Z', 5));
    await h.putQuota(payload('2026-11-20T03:01:00.000Z', 9));
    await h.putQuota(payload('2026-11-20T03:00:30.000Z', 7));
    expect(await h.getQuota().then(json)).toEqual({ snapshots: [{ collectedAt: '2026-11-20T03:01:00.000Z', providers: payload('2026-11-20T03:01:00.000Z', 9).providers }] });
    const invalid = payload(); invalid.providers[0]!.windows.push({ window: 'session', remainingBasisPoints: 1, resetsAt: null });
    expect((await h.putQuota(invalid)).status).toBe(400);
  });

  it('rejects oversized bodies and a collector credential cannot read owner-wide values', async () => {
    expect((await h.putQuota({ ...payload(), padding: 'x'.repeat(5000) })).status).toBe(413);
    expect((await h.getQuota('write-a1')).status).toBe(403);
    expect(FIXTURE.ownerA).toBe('owner-a');
  });

  it('rejects a future collection and inactive generations without storing them', async () => {
    const tooFarAhead = new Date(Date.now() + 6 * 60_000).toISOString();
    expect((await h.putQuota(payload(tooFarAhead))).status).toBe(400);
    expect((await h.putQuota({ ...payload(), generation: 2 })).status).toBe(409);
    expect(await h.getQuota().then(json)).toEqual({ snapshots: [] });
  });

  it('rejects unknown fields and preserves equal-time conflicting values as replay-safe no-ops', async () => {
    expect((await h.putQuota({ ...payload(), extra: true })).status).toBe(400);
    const first = payload('2026-11-20T03:00:00.123Z', 10);
    const changed = payload('2026-11-20T03:00:00.123Z', 11);
    await h.putQuota(first);
    await h.putQuota(first);
    await h.putQuota(changed);
    expect(await h.getQuota().then(json)).toEqual({ snapshots: [{ collectedAt: first.collectedAt, providers: first.providers }] });
  });

  it('refuses to use a collector-bound credential to widen the read scope', async () => {
    expect((await h.getQuota('write-a1')).status).toBe(403);
  });

  it('round-trips all seven fixed providers and keeps unlimited distinct from zero and unknown', async () => {
    const base = { status: 'available', freshness: 'fresh', observedAt: '2026-11-20T02:59:00Z' };
    const quotaWindow = (window: string, remainingBasisPoints: number | null, resetsAt: string | null = null, unlimited?: true) => ({
      window, remainingBasisPoints, resetsAt, ...(unlimited === true ? { unlimited: true } : {}),
    });
    const all = { schemaVersion: 1, generation: 1, collectedAt: '2026-11-20T03:00:00.123Z', providers: [
      { ...base, provider: 'claude', windows: [quotaWindow('session', 0), quotaWindow('weekly', null)] },
      { ...base, provider: 'codex', windows: [quotaWindow('five-hour', 10000), quotaWindow('weekly', 1)] },
      { ...base, provider: 'opencode-go', windows: [quotaWindow('rolling', null), quotaWindow('weekly', 5), quotaWindow('monthly', 0)] },
      { ...base, provider: 'antigravity-gemini', windows: [quotaWindow('five-hour', 4), quotaWindow('weekly', 5)] },
      { ...base, provider: 'antigravity-claude-gpt', windows: [quotaWindow('five-hour', 6), quotaWindow('weekly', 7)] },
      { ...base, provider: 'grok-build', windows: [quotaWindow('weekly', 8), quotaWindow('monthly', 9)] },
      { ...base, provider: 'copilot', windows: [quotaWindow('premium', null, null, true), quotaWindow('chat', null), quotaWindow('completions', 21)] },
    ] };
    const response = await h.putQuota(all);
    expect(response.status, await response.clone().text()).toBe(200);
    expect(await h.getQuota().then(json)).toEqual({ snapshots: [{ collectedAt: all.collectedAt, providers: all.providers }] });

    const illegal = (provider: string, windowName: string, remaining: number | null, unlimited: unknown) => {
      const one = { ...base, provider, windows: [{ window: windowName, remainingBasisPoints: remaining, resetsAt: null, unlimited }] };
      return h.putQuota({ schemaVersion: 1, generation: 1, collectedAt: all.collectedAt, providers: [one] });
    };
    expect((await illegal('copilot', 'premium', 0, true)).status).toBe(400);
    expect((await illegal('copilot', 'premium', null, false)).status).toBe(400);
    expect((await illegal('claude', 'session', null, true)).status).toBe(400);
    expect((await illegal('grok-build', 'daily', null, undefined)).status).toBe(400);
  });
});
