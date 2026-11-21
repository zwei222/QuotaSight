/**
 * GET /v1/usage/breakdown.
 *
 * The breakdown is read-only and owner-scoped: it aggregates every active
 * generation row of the caller's collectors inside the range, and it reports
 * coverage next to the parts precisely because the two are kept under separate
 * monotonic maxima and can therefore disagree.
 */
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import {
  createHarness,
  FIXTURE,
  json,
  resetData,
  seedCollectors,
  seedParts,
  seedUsage,
  snapshotBodyV2,
  type Harness,
} from './harness';

let h: Harness;

beforeAll(async () => {
  h = await createHarness();
}, 60_000);

afterAll(async () => {
  await h.dispose();
});

beforeEach(async () => {
  await resetData(h.db);
});

describe('GET /v1/usage/breakdown authorization', () => {
  it('requires a credential', async () => {
    const response = await h.getBreakdown('?from=2026-11-01&to=2026-11-30', null);
    expect(response.status).toBe(401);
    expect((await json(response)).error.code).toBe('unauthorized');
  });

  it('requires the read scope', async () => {
    const response = await h.getBreakdown('?from=2026-11-01&to=2026-11-30', 'write-a1');
    expect(response.status).toBe(403);
    expect((await json(response)).error.code).toBe('forbidden_scope');
  });

  it('refuses a collector-bound credential even when it carries usage:read', async () => {
    const response = await h.getBreakdown('?from=2026-11-01&to=2026-11-30', 'read-bound-a1');
    expect(response.status).toBe(403);
    expect((await json(response)).error.code).toBe('forbidden_scope');
  });
});

describe('GET /v1/usage/breakdown query', () => {
  it('rejects a missing or malformed range and unknown parameters', async () => {
    expect((await h.getBreakdown('?to=2026-11-30')).status).toBe(400);
    expect((await h.getBreakdown('?from=2026-11-30&to=2026-11-01')).status).toBe(400);
    // 517 days: over the 366-day span the read accepts.
    expect((await h.getBreakdown('?from=2026-01-01&to=2027-06-01')).status).toBe(400);
    expect((await h.getBreakdown('?from=2026-11-01&to=2026-11-30&extra=1')).status).toBe(400);
    expect((await h.getBreakdown('?from=2026-11-01&to=2026-11-30&collector=BAD')).status).toBe(400);
    expect((await h.getBreakdown('?from=2026-11-01&to=2026-11-30&collector=zzz')).status).toBe(404);
  });
});

describe('GET /v1/usage/breakdown rows', () => {
  it('returns empty lists for a range with no data', async () => {
    const response = await h.getBreakdown('?from=2026-11-01&to=2026-11-30');
    expect(response.status).toBe(200);
    expect(await json(response)).toEqual({
      from: '2026-11-01',
      to: '2026-11-30',
      parts: [],
      coverage: [],
      // Configured collectors that never reported are listed with no reception
      // time: "never reported" has to stay distinguishable from "no collector".
      collectors: [
        { collectorId: FIXTURE.collectorA1, generation: 1, lastReceivedAt: null },
        { collectorId: FIXTURE.collectorA2, generation: 1, lastReceivedAt: null },
      ],
    });
  });

  it('aggregates the breakdown across days and collectors, largest total first', async () => {
    await seedParts(h.db, [
      // Same key on two days and two collectors of one owner: one row, summed.
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', client: 'codex', provider: 'openai', model: 'gpt-5.1', input: 100, output: 20 },
      { collectorId: FIXTURE.collectorA2, date: '2026-11-18', client: 'codex', provider: 'openai', model: 'gpt-5.1', input: 50, output: 10 },
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', client: 'gemini', provider: 'anthropic', model: 'claude-sonnet-4-5', input: 300, output: 0 },
      // Another owner's row must never be counted.
      { collectorId: FIXTURE.collectorB1, date: '2026-11-17', client: 'codex', provider: 'openai', model: 'gpt-5.1', input: 999, output: 0 },
      // Outside the requested window.
      { collectorId: FIXTURE.collectorA1, date: '2026-10-31', client: 'gemini', provider: 'anthropic', model: 'claude-sonnet-4-5', input: 777, output: 0 },
    ]);

    const response = await h.getBreakdown('?from=2026-11-01&to=2026-11-30');
    expect(response.status).toBe(200);
    const body = await json(response);

    expect(body.from).toBe('2026-11-01');
    expect(body.to).toBe('2026-11-30');
    expect(body.parts).toEqual([
      {
        client: 'gemini',
        provider: 'anthropic',
        model: 'claude-sonnet-4-5',
        input: 300,
        output: 0,
        cacheRead: 0,
        cacheWrite: 0,
        reasoning: 0,
        total: 300,
      },
      {
        client: 'codex',
        provider: 'openai',
        model: 'gpt-5.1',
        input: 150,
        output: 30,
        cacheRead: 0,
        cacheWrite: 0,
        reasoning: 0,
        total: 180,
      },
    ]);
  });

  it('breaks ties by client, provider and model, in that order', async () => {
    await seedParts(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', client: 'b', provider: '', model: 'm', input: 10, output: 0 },
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', client: 'a', provider: 'z', model: 'm', input: 10, output: 0 },
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', client: 'a', provider: 'a', model: 'm', input: 10, output: 0 },
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', client: 'b', provider: '', model: 'a', input: 10, output: 0 },
    ]);

    const body = await json(await h.getBreakdown('?from=2026-11-01&to=2026-11-30'));
    expect(body.parts.map((part: { client: string; provider: string; model: string }) => [
      part.client,
      part.provider,
      part.model,
    ])).toEqual([
      ['a', 'a', 'm'],
      ['a', 'z', 'm'],
      ['b', '', 'a'],
      ['b', '', 'm'],
    ]);
  });

  it('reports the day total next to the attributed breakdown total', async () => {
    await seedUsage(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', totalTokens: 400 },
      // A day whose breakdown was refused: attributed stays 0, the total does not.
      { collectorId: FIXTURE.collectorA1, date: '2026-11-18', totalTokens: 250 },
      { collectorId: FIXTURE.collectorA2, date: '2026-11-17', totalTokens: 90 },
    ]);
    await seedParts(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', client: 'codex', provider: 'openai', model: 'gpt-5.1', input: 350, output: 30 },
      { collectorId: FIXTURE.collectorA2, date: '2026-11-17', client: 'codex', provider: 'openai', model: 'gpt-5.1', input: 90, output: 0 },
    ]);

    const body = await json(await h.getBreakdown('?from=2026-11-01&to=2026-11-30'));
    expect(body.coverage).toEqual([
      { date: '2026-11-17', collectorId: FIXTURE.collectorA1, totalTokens: 400, attributedTokens: 380 },
      { date: '2026-11-17', collectorId: FIXTURE.collectorA2, totalTokens: 90, attributedTokens: 90 },
      { date: '2026-11-18', collectorId: FIXTURE.collectorA1, totalTokens: 250, attributedTokens: 0 },
    ]);
    expect(body.collectors).toEqual([
      { collectorId: FIXTURE.collectorA1, generation: 1, lastReceivedAt: null },
      { collectorId: FIXTURE.collectorA2, generation: 1, lastReceivedAt: null },
    ]);
  });

  it('never reads a superseded generation', async () => {
    // Generation 1 is accepted while it is active, then superseded: its rows stay
    // in storage but must not be read any more.
    await seedUsage(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', totalTokens: 400, generation: 1 },
    ]);
    await seedParts(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', client: 'old', model: 'm', input: 400, output: 0 },
    ]);
    await h.db
      .prepare('UPDATE collectors SET active_generation = 2 WHERE id = ?')
      .bind(FIXTURE.collectorA1)
      .run();
    await seedUsage(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', totalTokens: 120, generation: 2 },
    ]);
    await seedParts(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', client: 'new', model: 'm', input: 120, output: 0, generation: 2 },
    ]);

    const body = await json(await h.getBreakdown('?from=2026-11-01&to=2026-11-30'));
    expect(body.parts.map((part: { client: string }) => part.client)).toEqual(['new']);
    expect(body.coverage.map((row: { totalTokens: number }) => row.totalTokens)).toEqual([120]);
    expect(body.collectors).toEqual([
      { collectorId: FIXTURE.collectorA1, generation: 2, lastReceivedAt: null },
      { collectorId: FIXTURE.collectorA2, generation: 1, lastReceivedAt: null },
    ]);
  });

  it('narrows to one collector of the owner', async () => {
    await seedParts(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', client: 'codex', model: 'm', input: 10, output: 0 },
      { collectorId: FIXTURE.collectorA2, date: '2026-11-17', client: 'gemini', model: 'm', input: 20, output: 0 },
    ]);

    const body = await json(
      await h.getBreakdown(`?from=2026-11-01&to=2026-11-30&collector=${FIXTURE.collectorA1}`),
    );
    expect(body.parts.map((part: { client: string }) => part.client)).toEqual(['codex']);
    expect(body.collectors.map((row: { collectorId: string }) => row.collectorId)).toEqual([
      FIXTURE.collectorA1,
    ]);
  });

  it('accepts a snapshot through the write route and reads it back', async () => {
    const write = await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({
        keys: [
          ['codex', 'openai', 'gpt-5.1'],
          ['gemini', 'anthropic', 'claude-sonnet-4-5'],
        ],
        days: [
          {
            date: '2026-11-18',
            totalTokens: 300,
            parts: [
              [0, 60, 10, 10, 5, 15],
              [1, 170, 20, 10, 0, 0],
            ],
          },
        ],
      }),
    });
    expect(write.status).toBe(200);

    const body = await json(await h.getBreakdown('?from=2026-11-01&to=2026-11-30'));
    expect(body.parts.map((part: { model: string; total: number }) => [part.model, part.total])).toEqual([
      ['claude-sonnet-4-5', 200],
      ['gpt-5.1', 100],
    ]);
    expect(body.coverage).toEqual([
      { date: '2026-11-18', collectorId: FIXTURE.collectorA1, totalTokens: 300, attributedTokens: 300 },
    ]);
  });
});

describe('GET /v1/usage/breakdown bounds', () => {
  it('refuses to truncate more than 2000 breakdown rows', async () => {
    const entries = Array.from({ length: 2001 }, (_, index) => ({
      collectorId: FIXTURE.collectorA1,
      date: '2026-11-17',
      client: 'codex',
      model: `model-${index}`,
      input: 1,
      output: 0,
    }));
    await seedParts(h.db, entries);

    const response = await h.getBreakdown('?from=2026-11-01&to=2026-11-30');
    expect(response.status).toBe(413);
    expect((await json(response)).error.code).toBe('result_too_large');
  }, 120_000);

  it('refuses to truncate more than 5000 coverage rows', async () => {
    // 14 collectors x 366 days is 5124 rows: over the bound, and still a range
    // the API accepts, so the bound (not the range limit) is what rejects it.
    const collectors = Array.from({ length: 14 }, (_, index) => ({
      id: `col-${1000 + index}`,
      ownerId: FIXTURE.ownerA,
    }));
    await seedCollectors(h.db, collectors);
    const start = Date.UTC(2026, 0, 1);
    const seed = [];
    for (const collector of collectors) {
      for (let index = 0; index < 366; index += 1) {
        seed.push({
          collectorId: collector.id,
          date: new Date(start + index * 86_400_000).toISOString().slice(0, 10),
          totalTokens: 1,
        });
      }
    }
    await seedUsage(h.db, seed);

    const response = await h.getBreakdown('?from=2026-01-01&to=2026-12-31');
    expect(response.status).toBe(413);
    expect((await json(response)).error.code).toBe('result_too_large');
  }, 120_000);
});
