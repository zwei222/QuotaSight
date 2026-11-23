import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import {
  applyMigrationFile,
  createHarness,
  json,
  resetData,
  scalar,
  seedCollectors,
  FIXTURE,
  type Harness,
} from './harness';

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

/*
 * Last-successful-value retention (read schema 2).
 *
 * The snapshot row keeps only the newest collection, so a partial or empty
 * collection would otherwise erase what was last known. These tests describe
 * the second table's contract through the HTTP surface: what the write keeps,
 * what it refuses to make newer, and the exact shape `?schema=2` answers.
 */

interface WindowValue {
  window: string;
  remainingBasisPoints: number | null;
  resetsAt: string | null;
  unlimited?: true;
}

type ProviderStatus = 'available' | 'unavailable';
type Freshness = 'fresh' | 'cached' | 'unknown';

interface ProviderValue {
  provider: string;
  status: ProviderStatus;
  freshness: Freshness;
  observedAt: string | null;
  windows: WindowValue[];
}

const windowValue = (
  window: string,
  remainingBasisPoints: number | null,
  resetsAt: string | null = null,
  unlimited?: true,
): WindowValue => ({
  window,
  remainingBasisPoints,
  resetsAt,
  ...(unlimited === true ? { unlimited: true as const } : {}),
});

const providerValue = (
  provider: string,
  windows: WindowValue[],
  overrides: Partial<Omit<ProviderValue, 'provider' | 'windows'>> = {},
): ProviderValue => ({
  provider,
  status: 'available',
  freshness: 'fresh',
  observedAt: '2026-11-20T02:59:00Z',
  windows,
  ...overrides,
});

const quotaBody = (collectedAt: string, providers: ProviderValue[], generation = 1) => ({
  schemaVersion: 1,
  generation,
  collectedAt,
  providers,
});

type LatestStatus = 'available' | 'unavailable' | 'missing';

const v2Provider = (provider: string, latestStatus: LatestStatus, value: unknown) => ({
  provider,
  latestStatus,
  value,
});

const retained = (
  source: 'latest' | 'previous',
  collectedAt: string,
  observedAt: string | null,
  freshness: Freshness,
  windows: WindowValue[],
) => ({ source, collectedAt, observedAt, freshness, windows });

const claude1 = [windowValue('session', 7000), windowValue('weekly', 5000, '2026-11-25T00:00:00Z')];
const claude2 = [windowValue('session', 6000), windowValue('weekly', 4000, '2026-11-25T00:00:00Z')];
const copilot1 = [windowValue('premium', null, null, true), windowValue('chat', 3000)];
const OBSERVED_T1 = '2026-11-20T02:59:00Z';
const OBSERVED_T2 = '2026-11-20T03:58:00Z';

const providerOf = (body: unknown, provider: string): Record<string, unknown> => {
  const snapshots = (body as { snapshots: Array<{ providers: Array<Record<string, unknown>> }> }).snapshots;
  const found = snapshots[0]?.providers.find((entry) => entry['provider'] === provider);
  if (found === undefined) throw new Error(`no such provider ${provider}`);
  return found;
};

describe('quota read schema 2 (last successful value)', () => {
  it('retains the last effective value across partial and empty collections', async () => {
    const t1 = '2026-11-20T03:00:00Z';
    const t2 = '2026-11-20T04:00:00Z';
    const t3 = '2026-11-20T05:00:00Z';

    expect((await h.putQuota(quotaBody(t1, [
      providerValue('claude', claude1),
      providerValue('codex', [windowValue('five-hour', null), windowValue('weekly', null)], {
        status: 'unavailable',
        observedAt: '2026-11-20T02:58:00Z',
      }),
      providerValue('copilot', copilot1),
    ]))).status).toBe(200);
    // The write contract itself is unchanged: the same accepted body, no new field.
    const accepted = await h.putQuota(quotaBody(t1, [providerValue('claude', claude1)]));
    expect(await json(accepted)).toEqual({ status: 'accepted' });

    // Partial: only one provider reported, at a newer time and with a newer value.
    expect((await h.putQuota(quotaBody(t2, [
      providerValue('claude', claude2, { observedAt: OBSERVED_T2 }),
    ]))).status).toBe(200);

    const partial = await h.getQuotaV2().then(json);
    expect(partial).toEqual({
      schemaVersion: 2,
      snapshots: [{
        collectedAt: t2,
        providers: [
          v2Provider('claude', 'available', retained('latest', t2, OBSERVED_T2, 'fresh', claude2)),
          v2Provider('codex', 'missing', null),
          v2Provider('opencode-go', 'missing', null),
          v2Provider('antigravity-gemini', 'missing', null),
          v2Provider('antigravity-claude-gpt', 'missing', null),
          v2Provider('grok-build', 'missing', null),
          v2Provider('copilot', 'missing', retained('previous', t1, OBSERVED_T1, 'fresh', copilot1)),
        ],
      }],
    });

    // Empty: the collector reported nothing at all.
    expect((await h.putQuota(quotaBody(t3, []))).status).toBe(200);

    const empty = await h.getQuotaV2().then(json);
    expect(empty).toEqual({
      schemaVersion: 2,
      snapshots: [{
        collectedAt: t3,
        providers: [
          v2Provider('claude', 'missing', retained('previous', t2, OBSERVED_T2, 'fresh', claude2)),
          v2Provider('codex', 'missing', null),
          v2Provider('opencode-go', 'missing', null),
          v2Provider('antigravity-gemini', 'missing', null),
          v2Provider('antigravity-claude-gpt', 'missing', null),
          v2Provider('grok-build', 'missing', null),
          v2Provider('copilot', 'missing', retained('previous', t1, OBSERVED_T1, 'fresh', copilot1)),
        ],
      }],
    });

    // The v1 read is untouched: it is still the newest collection's own payload.
    expect(await h.getQuota().then(json)).toEqual({ snapshots: [{ collectedAt: t3, providers: [] }] });
  });

  it('keeps zero, unlimited and explicit unavailable distinct, and never calls a value-less availability available', async () => {
    const t = '2026-11-20T03:00:00Z';
    expect((await h.putQuota(quotaBody(t, [
      providerValue('claude', [windowValue('session', 0), windowValue('weekly', null)]),
      // `available` without a single usable window is not a value.
      providerValue('codex', [windowValue('five-hour', null), windowValue('weekly', null)]),
      providerValue('copilot', [windowValue('premium', null, null, true), windowValue('chat', null)]),
      // An explicit `unavailable` wins even when a number is present.
      providerValue('grok-build', [windowValue('weekly', 1200)], { status: 'unavailable' }),
    ]))).status).toBe(200);

    expect(await h.getQuotaV2().then(json)).toEqual({
      schemaVersion: 2,
      snapshots: [{
        collectedAt: t,
        providers: [
          v2Provider('claude', 'available', retained('latest', t, OBSERVED_T1, 'fresh', [windowValue('session', 0), windowValue('weekly', null)])),
          v2Provider('codex', 'unavailable', null),
          v2Provider('opencode-go', 'missing', null),
          v2Provider('antigravity-gemini', 'missing', null),
          v2Provider('antigravity-claude-gpt', 'missing', null),
          v2Provider('grok-build', 'unavailable', null),
          v2Provider('copilot', 'available', retained('latest', t, OBSERVED_T1, 'fresh', [windowValue('premium', null, null, true), windowValue('chat', null)])),
        ],
      }],
    });
  });

  it('never moves the retained time backwards and refuses older or same-time cached values', async () => {
    const t1 = '2026-11-20T03:00:00Z';
    expect((await h.putQuota(quotaBody(t1, [
      providerValue('claude', [windowValue('session', 5000)], { observedAt: OBSERVED_T1 }),
    ]))).status).toBe(200);

    // Cached, older observation, newer collection: the value is not adopted.
    expect((await h.putQuota(quotaBody('2026-11-20T04:00:00Z', [
      providerValue('claude', [windowValue('session', 1000)], { freshness: 'cached', observedAt: '2026-11-20T02:00:00Z' }),
    ]))).status).toBe(200);
    let body = await h.getQuotaV2().then(json);
    expect(body.snapshots[0].collectedAt).toBe('2026-11-20T04:00:00Z');
    expect(providerOf(body, 'claude')).toEqual(
      v2Provider('claude', 'available', retained('previous', t1, OBSERVED_T1, 'fresh', [windowValue('session', 5000)])),
    );

    // Cached with the same observation time: no newer stored time either.
    expect((await h.putQuota(quotaBody('2026-11-20T05:00:00Z', [
      providerValue('claude', [windowValue('session', 2000)], { freshness: 'cached', observedAt: OBSERVED_T1 }),
    ]))).status).toBe(200);
    // An explicitly older observation time is not a rewind, even when `fresh`.
    expect((await h.putQuota(quotaBody('2026-11-20T06:00:00Z', [
      providerValue('claude', [windowValue('session', 3000)], { observedAt: '2026-11-20T01:00:00Z' }),
    ]))).status).toBe(200);

    body = await h.getQuotaV2().then(json);
    expect(body.snapshots[0].collectedAt).toBe('2026-11-20T06:00:00Z');
    expect(providerOf(body, 'claude')).toEqual(
      v2Provider('claude', 'available', retained('previous', t1, OBSERVED_T1, 'fresh', [windowValue('session', 5000)])),
    );

    // Cached with a demonstrably newer observation is a newer known value.
    expect((await h.putQuota(quotaBody('2026-11-20T07:00:00Z', [
      providerValue('claude', [windowValue('session', 4000)], { freshness: 'cached', observedAt: '2026-11-20T06:30:00Z' }),
    ]))).status).toBe(200);

    body = await h.getQuotaV2().then(json);
    expect(providerOf(body, 'claude')).toEqual(
      v2Provider('claude', 'available', retained('latest', '2026-11-20T07:00:00Z', '2026-11-20T06:30:00Z', 'cached', [windowValue('session', 4000)])),
    );
  });

  it('never retains an observation dated more than five minutes after its collection', async () => {
    const t1 = '2026-11-20T03:00:00.000Z';
    const t2 = '2026-11-20T04:00:00.000Z';
    const t3 = '2026-11-20T05:00:00.000Z';
    expect((await h.putQuota(quotaBody(t1, [
      providerValue('claude', [windowValue('session', 5000)], { observedAt: OBSERVED_T1 }),
    ]))).status).toBe(200);

    // A group cannot have observed its quota after the collection that carried
    // it. The write contract is untouched (the body is accepted, the v1 read
    // returns it as sent), but the claim cannot become the last known value.
    const future = quotaBody(t2, [
      providerValue('claude', [windowValue('session', 100)], { observedAt: '2099-01-01T00:00:00Z' }),
    ]);
    expect((await h.putQuota(future)).status).toBe(200);
    expect(await h.getQuota().then(json)).toEqual({
      snapshots: [{ collectedAt: t2, providers: future.providers }],
    });

    let body = await h.getQuotaV2().then(json);
    expect(body.snapshots[0].collectedAt).toBe(t2);
    expect(providerOf(body, 'claude')).toEqual(
      v2Provider('claude', 'available', retained('previous', t1, OBSERVED_T1, 'fresh', [windowValue('session', 5000)])),
    );

    // It must not poison later comparisons either: the next sane observation is
    // a newer value even though it is far older than the refused claim.
    expect((await h.putQuota(quotaBody(t3, [
      providerValue('claude', [windowValue('session', 4000)], { observedAt: '2026-11-20T04:59:00Z' }),
    ]))).status).toBe(200);
    body = await h.getQuotaV2().then(json);
    expect(providerOf(body, 'claude')).toEqual(
      v2Provider('claude', 'available', retained('latest', t3, '2026-11-20T04:59:00Z', 'fresh', [windowValue('session', 4000)])),
    );
  });

  it('allows an observation exactly five minutes ahead and refuses the next second', async () => {
    const t = '2026-11-20T03:00:00.000Z';
    const sent = quotaBody(t, [
      providerValue('claude', [windowValue('session', 5000)], { observedAt: '2026-11-20T03:05:00Z' }),
      providerValue('codex', [windowValue('weekly', 6000)], { observedAt: '2026-11-20T03:05:01Z' }),
    ]);
    expect((await h.putQuota(sent)).status).toBe(200);

    const body = await h.getQuotaV2().then(json);
    expect(providerOf(body, 'claude')).toEqual(
      v2Provider('claude', 'available', retained('latest', t, '2026-11-20T03:05:00Z', 'fresh', [windowValue('session', 5000)])),
    );
    // Beyond the tolerance the observation is no value at all — the same shape
    // as an undated cached value: the status is what the collection said, the
    // retained value is null.
    expect(providerOf(body, 'codex')).toEqual(v2Provider('codex', 'available', null));
    // Nothing was refused on the wire.
    expect(await h.getQuota().then(json)).toEqual({ snapshots: [{ collectedAt: t, providers: sent.providers }] });
  });

  it('compares observation instants by time, not by their text', async () => {
    const t0 = '2026-11-20T02:00:00.500Z';
    const t1 = '2026-11-20T03:00:00.000Z';
    const t2 = '2026-11-20T04:00:00.000Z';
    expect((await h.putQuota(quotaBody(t0, [
      providerValue('grok-build', [windowValue('weekly', 9000)], { freshness: 'unknown', observedAt: null }),
    ]))).status).toBe(200);
    expect((await h.putQuota(quotaBody(t1, [
      providerValue('codex', [windowValue('five-hour', 10), windowValue('weekly', 20)], { freshness: 'unknown', observedAt: null }),
      providerValue('copilot', [windowValue('premium', null, null, true)], { freshness: 'unknown', observedAt: null }),
    ]))).status).toBe(200);

    const sent = quotaBody(t2, [
      // Half a second *before* the collection that adopted the undated value:
      // older, even though `...:00Z` sorts after `...:00.500Z`.
      providerValue('grok-build', [windowValue('weekly', 1000)], { observedAt: '2026-11-20T02:00:00Z' }),
      // The very instant of the adopting collection (`.000` and no fraction are
      // the same instant), `fresh`: the observation is newer than nothing, so it
      // is adopted at this collection.
      providerValue('codex', [windowValue('five-hour', 30), windowValue('weekly', 40)], { observedAt: '2026-11-20T03:00:00Z' }),
      // The same instant, `cached`: not adopted, and the stored collection time
      // is kept instead of being rewritten with this snapshot's.
      providerValue('copilot', [windowValue('premium', null, null, true), windowValue('chat', 1)], { freshness: 'cached', observedAt: '2026-11-20T03:00:00Z' }),
    ]);
    expect((await h.putQuota(sent)).status).toBe(200);

    const body = await h.getQuotaV2().then(json);
    expect(body.snapshots[0].collectedAt).toBe(t2);
    expect(providerOf(body, 'grok-build')).toEqual(
      v2Provider('grok-build', 'available', retained('previous', t0, null, 'unknown', [windowValue('weekly', 9000)])),
    );
    expect(providerOf(body, 'codex')).toEqual(
      v2Provider('codex', 'available', retained('latest', t2, '2026-11-20T03:00:00Z', 'fresh', [windowValue('five-hour', 30), windowValue('weekly', 40)])),
    );
    expect(providerOf(body, 'copilot')).toEqual(
      v2Provider('copilot', 'available', retained('previous', t1, null, 'unknown', [windowValue('premium', null, null, true)])),
    );
  });

  it('does not assert an undated cached value as the last success', async () => {
    const t1 = '2026-11-20T03:00:00Z';
    expect((await h.putQuota(quotaBody(t1, [
      providerValue('claude', [windowValue('session', 5000)], { observedAt: OBSERVED_T1 }),
    ]))).status).toBe(200);

    // Cached without an observation time: it may not overwrite the known success.
    expect((await h.putQuota(quotaBody('2026-11-20T04:00:00Z', [
      providerValue('claude', [windowValue('session', 900)], { freshness: 'cached', observedAt: null }),
      providerValue('codex', [windowValue('weekly', 8000)], { freshness: 'cached', observedAt: null }),
    ]))).status).toBe(200);

    const body = await h.getQuotaV2().then(json);
    expect(providerOf(body, 'claude')).toEqual(
      v2Provider('claude', 'available', retained('previous', t1, OBSERVED_T1, 'fresh', [windowValue('session', 5000)])),
    );
    // Nothing is known about codex, so nothing is claimed as a successful value.
    expect(providerOf(body, 'codex')).toEqual(v2Provider('codex', 'available', null));
  });

  it('retains an undated unknown value and dates it by the collection, not by an older observation', async () => {
    const t1 = '2026-11-20T03:00:00Z';
    const t2 = '2026-11-20T04:00:00Z';
    const t3 = '2026-11-20T05:00:00Z';
    // Codex, OpenCode Go, Grok and Copilot are the real wire's undated groups:
    // `freshness: "unknown"` with `observedAt: null`, because TokScale reports no
    // fetch time for them. Their collection time is the only time they carry.
    const codexWire = [windowValue('five-hour', null), windowValue('weekly', 5000, '2026-11-25T00:00:00Z')];
    const grokWire = [windowValue('weekly', 9000), windowValue('monthly', 7000)];
    const copilotWire = [
      windowValue('premium', null, null, true),
      windowValue('chat', null),
      windowValue('completions', 0),
    ];
    const undated = { freshness: 'unknown' as const, observedAt: null };

    expect((await h.putQuota(quotaBody(t1, [
      providerValue('codex', codexWire, undated),
      providerValue('grok-build', grokWire, undated),
      providerValue('copilot', copilotWire, undated),
    ]))).status).toBe(200);

    // A usable number (or a proven unlimited window) is a value even with no
    // observation time, and no observation time is invented for it.
    let body = await h.getQuotaV2().then(json);
    expect(providerOf(body, 'codex')).toEqual(
      v2Provider('codex', 'available', retained('latest', t1, null, 'unknown', codexWire)),
    );
    expect(providerOf(body, 'grok-build')).toEqual(
      v2Provider('grok-build', 'available', retained('latest', t1, null, 'unknown', grokWire)),
    );
    expect(providerOf(body, 'copilot')).toEqual(
      v2Provider('copilot', 'available', retained('latest', t1, null, 'unknown', copilotWire)),
    );

    // An explicitly older observation cannot rewind an undated value, and it
    // does not replace the collection time the value was adopted with.
    expect((await h.putQuota(quotaBody(t2, [
      providerValue('codex', [windowValue('five-hour', 10), windowValue('weekly', 20)], {
        freshness: 'cached',
        observedAt: '2026-11-20T02:00:00Z',
      }),
    ]))).status).toBe(200);

    body = await h.getQuotaV2().then(json);
    expect(providerOf(body, 'codex')).toEqual(
      v2Provider('codex', 'available', retained('previous', t1, null, 'unknown', codexWire)),
    );

    // A newer undated value is newer by its collection time — still with a null
    // observation time — and the groups this collection did not report survive
    // with the collection time they were adopted with.
    const codexAgain = [windowValue('five-hour', 100), windowValue('weekly', 200)];
    expect((await h.putQuota(quotaBody(t3, [
      providerValue('codex', codexAgain, undated),
    ]))).status).toBe(200);

    body = await h.getQuotaV2().then(json);
    expect(body.snapshots[0].collectedAt).toBe(t3);
    expect(providerOf(body, 'codex')).toEqual(
      v2Provider('codex', 'available', retained('latest', t3, null, 'unknown', codexAgain)),
    );
    expect(providerOf(body, 'grok-build')).toEqual(
      v2Provider('grok-build', 'missing', retained('previous', t1, null, 'unknown', grokWire)),
    );
  });

  it('keeps the first payload at an equal collection time and never adopts a rejected write', async () => {
    const t = '2026-11-20T03:00:00Z';
    expect((await h.putQuota(quotaBody(t, [
      providerValue('claude', [windowValue('session', 100)]),
    ]))).status).toBe(200);

    // Same time, different payload: the snapshot is first-wins, so the retention
    // table must not move either — not even for a provider the first one lacked.
    expect((await h.putQuota(quotaBody(t, [
      providerValue('claude', [windowValue('session', 200)]),
      providerValue('codex', [windowValue('weekly', 300)]),
    ]))).status).toBe(200);

    let body = await h.getQuotaV2().then(json);
    expect(providerOf(body, 'claude')).toEqual(
      v2Provider('claude', 'available', retained('latest', t, OBSERVED_T1, 'fresh', [windowValue('session', 100)])),
    );
    expect(providerOf(body, 'codex')).toEqual(v2Provider('codex', 'missing', null));
    expect(await h.getQuota().then(json)).toEqual({
      snapshots: [{ collectedAt: t, providers: [providerValue('claude', [windowValue('session', 100)])] }],
    });

    const t2 = '2026-11-20T04:00:00Z';
    expect((await h.putQuota(quotaBody(t2, [
      providerValue('claude', [windowValue('session', 400)]),
    ]))).status).toBe(200);
    // Older collection arriving late: ignored by both tables.
    expect((await h.putQuota(quotaBody('2026-11-20T03:30:00Z', [
      providerValue('claude', [windowValue('session', 50)]),
      providerValue('codex', [windowValue('weekly', 60)]),
    ]))).status).toBe(200);

    body = await h.getQuotaV2().then(json);
    expect(body.snapshots[0].collectedAt).toBe(t2);
    expect(providerOf(body, 'claude')).toEqual(
      v2Provider('claude', 'available', retained('latest', t2, OBSERVED_T1, 'fresh', [windowValue('session', 400)])),
    );
    expect(providerOf(body, 'codex')).toEqual(v2Provider('codex', 'missing', null));
  });

  it('leaves the newer of two overlapping collections in both tables', async () => {
    const t1 = '2026-11-20T03:00:00Z';
    const t2 = '2026-11-20T04:00:00Z';
    const [first, second] = await Promise.all([
      h.putQuota(quotaBody(t1, [providerValue('claude', [windowValue('session', 100)])])),
      h.putQuota(quotaBody(t2, [providerValue('claude', [windowValue('session', 200)])])),
    ]);
    expect(first.status).toBe(200);
    expect(second.status).toBe(200);

    // Whichever order the two writes commit in, the guard makes the result the
    // newer collection in both tables: a late older write cannot pair its
    // retention value with the newer snapshot.
    const body = await h.getQuotaV2().then(json);
    expect(body.snapshots).toHaveLength(1);
    expect(providerOf(body, 'claude')).toEqual(
      v2Provider('claude', 'available', retained('latest', t2, OBSERVED_T1, 'fresh', [windowValue('session', 200)])),
    );
    expect(await h.getQuota().then(json)).toEqual({
      snapshots: [{ collectedAt: t2, providers: [providerValue('claude', [windowValue('session', 200)])] }],
    });
  });

  it('rolls the retention row back with the snapshot row when the write aborts', async () => {
    const t = '2026-11-20T03:00:00Z';
    // A schema-level abort on the snapshot statement is the same class of failure
    // as an inactive generation: one statement of the batch cannot commit, so the
    // retention row written before it must not survive.
    await h.db
      .prepare("CREATE TRIGGER quota_test_abort_snapshot BEFORE INSERT ON quota_snapshot BEGIN SELECT RAISE(ABORT, 'test_abort'); END")
      .run();
    try {
      const response = await h.putQuota(quotaBody(t, [providerValue('claude', claude1)]));
      expect(response.status).toBe(500);
      expect((await json(response)).error.code).toBe('internal_error');
      expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM quota_effective')).toBe(0);
      expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM quota_snapshot')).toBe(0);
    } finally {
      await h.db.prepare('DROP TRIGGER quota_test_abort_snapshot').run();
    }
  });

  it('never reads the quota of a superseded generation', async () => {
    const t1 = '2026-11-20T03:00:00Z';
    const t2 = '2026-11-20T04:00:00Z';
    expect((await h.putQuota(quotaBody(t1, [
      providerValue('claude', [windowValue('session', 5000)]),
    ]))).status).toBe(200);

    await h.db.prepare('UPDATE collectors SET active_generation = 2 WHERE id = ?').bind(FIXTURE.collectorA1).run();

    // The old generation is no longer writable, and its retention row is unreadable.
    expect((await h.putQuota(quotaBody(t2, [
      providerValue('claude', [windowValue('session', 100)]),
    ]))).status).toBe(409);

    expect((await h.putQuota(quotaBody(t2, [
      providerValue('codex', [windowValue('weekly', 7000)]),
    ], 2))).status).toBe(200);

    const body = await h.getQuotaV2().then(json);
    expect(body.snapshots).toHaveLength(1);
    expect(body.snapshots[0].collectedAt).toBe(t2);
    expect(providerOf(body, 'claude')).toEqual(v2Provider('claude', 'missing', null));
    expect(providerOf(body, 'codex')).toEqual(
      v2Provider('codex', 'available', retained('latest', t2, OBSERVED_T1, 'fresh', [windowValue('weekly', 7000)])),
    );
  });

  it('isolates the retained value by owner and by collector', async () => {
    const t1 = '2026-11-20T03:00:00Z';
    const t2 = '2026-11-20T04:00:00Z';
    expect((await h.putQuota(quotaBody(t1, [
      providerValue('claude', [windowValue('session', 5000)]),
    ]))).status).toBe(200);

    const second = await h.request(`/v1/collectors/${FIXTURE.collectorA2}/quota`, {
      method: 'PUT',
      headers: { authorization: 'Bearer write-a2', 'content-type': 'application/json' },
      body: JSON.stringify(quotaBody(t2, [providerValue('codex', [windowValue('weekly', 7000)])])),
    });
    expect(second.status).toBe(200);

    const body = await h.getQuotaV2().then(json);
    expect(body.snapshots.map((snapshot: { collectedAt: string }) => snapshot.collectedAt)).toEqual([t2, t1]);
    expect(providerOf(body, 'claude')).toEqual(v2Provider('claude', 'missing', null));
    const own = (body.snapshots[0] as { providers: Array<Record<string, unknown>> }).providers;
    expect(own[0]).toEqual(v2Provider('claude', 'missing', null));
    expect(own[1]).toEqual(
      v2Provider('codex', 'available', retained('latest', t2, OBSERVED_T1, 'fresh', [windowValue('weekly', 7000)])),
    );
    expect(await h.getQuotaV2('read-b1').then(json)).toEqual({ schemaVersion: 2, snapshots: [] });
  });

  it('accepts only schema=2 on the shared route and keeps the other query strings as v1', async () => {
    expect((await h.putQuota(payload())).status).toBe(200);
    expect(await h.getQuota().then(json)).toEqual({
      snapshots: [{ collectedAt: payload().collectedAt, providers: payload().providers }],
    });

    for (const search of ['?schema=1', '?schema=3', '?schema=', '?schema=2&schema=2']) {
      const response = await h.request(`/v1/quota/latest${search}`, { headers: { authorization: 'Bearer read-a1' } });
      expect(response.status, search).toBe(400);
      expect((await json(response)).error.code, search).toBe('invalid_query');
    }

    expect((await h.getQuotaV2('write-a1')).status).toBe(403);
    expect((await h.getQuotaV2('read-bound-a1')).status).toBe(403);
    expect((await h.getQuotaV2('read-b1')).status).toBe(200);
  });

  it('refuses to serve more than 64 snapshots and keeps every group at seven providers', async () => {
    const t = '2026-11-20T03:00:00Z';
    const ids = Array.from({ length: 65 }, (_, index) => `col-p${String(index).padStart(2, '0')}`);
    await seedCollectors(h.db, ids.map((id) => ({ id, ownerId: FIXTURE.ownerA })));
    const insert = h.db.prepare(
      'INSERT INTO quota_snapshot (collector_id, owner_id, generation, collected_at, payload_json) VALUES (?, ?, ?, ?, ?)',
    );
    await h.db.batch(ids.map((id) => insert.bind(id, FIXTURE.ownerA, 1, t, JSON.stringify({ collectedAt: t, providers: [] }))));

    const response = await h.getQuotaV2();
    expect(response.status).toBe(422);
    expect(await json(response)).toMatchObject({
      error: { code: 'result_too_large', details: { reason: 'too_many_rows', limitRows: 64 } },
    });
  });

  it('backfills only the active generation snapshot when migration 0006 is applied', async () => {
    const older = await createHarness('test/worker.ts', {
      migrations: [
        '0001_initial.sql',
        '0002_read_indexes.sql',
        '0003_receipt_ignored_reductions.sql',
        '0004_usage_parts.sql',
        '0005_quota_snapshots.sql',
      ],
    });
    const t = '2026-11-20T03:00:00Z';
    try {
      await resetData(older.db);
      const insert = older.db.prepare(
        'INSERT INTO quota_snapshot (collector_id, owner_id, generation, collected_at, payload_json) VALUES (?, ?, ?, ?, ?)',
      );
      await insert.bind(
        FIXTURE.collectorA1,
        FIXTURE.ownerA,
        1,
        t,
        JSON.stringify({
          collectedAt: t,
          providers: [
            providerValue('claude', [windowValue('session', 5000)]),
            providerValue('codex', [windowValue('weekly', null), windowValue('five-hour', null)]),
            // A dated cached value is restorable; an undated one is not.
            providerValue('copilot', [windowValue('premium', null, null, true)], {
              freshness: 'cached',
              observedAt: '2026-11-20T02:30:00Z',
            }),
            providerValue('grok-build', [windowValue('weekly', 8000)], { freshness: 'cached', observedAt: null }),
            // An undated unknown value is restorable too: the collection time is its time.
            providerValue('opencode-go', [windowValue('rolling', 7500), windowValue('weekly', 2500), windowValue('monthly', 0)], {
              freshness: 'unknown',
              observedAt: null,
            }),
          ],
        }),
      ).run();
      await insert.bind(
        FIXTURE.collectorA2,
        FIXTURE.ownerA,
        1,
        t,
        JSON.stringify({ collectedAt: t, providers: [providerValue('codex', [windowValue('weekly', 9000)])] }),
      ).run();
      // col-a2 was re-provisioned: its gen-1 snapshot must not be restored.
      await older.db.prepare('UPDATE collectors SET active_generation = 2 WHERE id = ?').bind(FIXTURE.collectorA2).run();

      await applyMigrationFile(older.db, '0006_quota_effective.sql');

      expect(await scalar(older.db, 'SELECT COUNT(*) AS value FROM quota_effective')).toBe(3);

      const body = await older.getQuotaV2().then(json);
      expect(body.snapshots).toHaveLength(1);
      expect(body.snapshots[0].collectedAt).toBe(t);
      expect(providerOf(body, 'claude')).toEqual(
        v2Provider('claude', 'available', retained('latest', t, OBSERVED_T1, 'fresh', [windowValue('session', 5000)])),
      );
      expect(providerOf(body, 'codex')).toEqual(v2Provider('codex', 'unavailable', null));
      // The dated cached value was restored with its own observation time.
      expect(providerOf(body, 'copilot')).toEqual(
        v2Provider('copilot', 'available', retained('latest', t, '2026-11-20T02:30:00Z', 'cached', [windowValue('premium', null, null, true)])),
      );
      // The undated cached value was not.
      expect(providerOf(body, 'grok-build')).toEqual(v2Provider('grok-build', 'available', null));
      // The undated unknown value was, with its collection time and no observation time.
      expect(providerOf(body, 'opencode-go')).toEqual(
        v2Provider('opencode-go', 'available', retained('latest', t, null, 'unknown', [
          windowValue('rolling', 7500),
          windowValue('weekly', 2500),
          windowValue('monthly', 0),
        ])),
      );
    } finally {
      await older.dispose();
    }
  });

  it('does not backfill an observation dated after the collection that carried it', async () => {
    const older = await createHarness('test/worker.ts', {
      migrations: [
        '0001_initial.sql',
        '0002_read_indexes.sql',
        '0003_receipt_ignored_reductions.sql',
        '0004_usage_parts.sql',
        '0005_quota_snapshots.sql',
      ],
    });
    const t = '2026-11-20T03:00:00.500Z';
    try {
      await resetData(older.db);
      await older.db.prepare(
        'INSERT INTO quota_snapshot (collector_id, owner_id, generation, collected_at, payload_json) VALUES (?, ?, ?, ?, ?)',
      ).bind(
        FIXTURE.collectorA1,
        FIXTURE.ownerA,
        1,
        t,
        JSON.stringify({
          collectedAt: t,
          providers: [
            // Exactly five minutes ahead is still the same collection's value.
            providerValue('claude', [windowValue('session', 5000)], { observedAt: '2026-11-20T03:05:00Z' }),
            // One second further is not: the same rule the write path applies.
            providerValue('codex', [windowValue('weekly', 6000)], { observedAt: '2026-11-20T03:05:01Z' }),
            providerValue('antigravity-gemini', [windowValue('five-hour', 100)], { observedAt: '2099-01-01T00:00:00Z' }),
          ],
        }),
      ).run();

      await applyMigrationFile(older.db, '0006_quota_effective.sql');

      expect(await scalar(older.db, 'SELECT COUNT(*) AS value FROM quota_effective')).toBe(1);
      const body = await older.getQuotaV2().then(json);
      expect(providerOf(body, 'claude')).toEqual(
        v2Provider('claude', 'available', retained('latest', t, '2026-11-20T03:05:00Z', 'fresh', [windowValue('session', 5000)])),
      );
      expect(providerOf(body, 'codex')).toEqual(v2Provider('codex', 'available', null));
      expect(providerOf(body, 'antigravity-gemini')).toEqual(v2Provider('antigravity-gemini', 'available', null));
    } finally {
      await older.dispose();
    }
  });
});
