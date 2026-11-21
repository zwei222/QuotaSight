/**
 * A Worker that is newer than its schema.
 *
 * Deployments move the Worker and the migrations separately, and the Worker goes
 * first: `0004` is applied to D1 after the code that can use it is live. Until
 * then the database has only `0001`-`0003`, and the v1 routes have to keep
 * working there — a v1 PUT that reads a column 0004 adds would answer 500 for
 * every deployed collector, which is a worse failure than not having the
 * breakdown yet.
 *
 * These tests run the real Worker against a real local D1 that stopped at 0003.
 */
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import {
  createHarness,
  FIXTURE,
  json,
  resetData,
  scalar,
  snapshotBody,
  snapshotBodyV2,
  usageRows,
  type Harness,
} from './harness';

const MIGRATIONS_BEFORE_THE_BREAKDOWN = ['0001_initial.sql', '0002_read_indexes.sql', '0003_receipt_ignored_reductions.sql'];

let h: Harness;

beforeAll(async () => {
  h = await createHarness('test/worker.ts', { migrations: MIGRATIONS_BEFORE_THE_BREAKDOWN });
}, 60_000);

afterAll(async () => {
  await h.dispose();
});

beforeEach(async () => {
  await resetData(h.db, { withParts: false });
});

describe('v1 routes on a database without migration 0004', () => {
  it('has no breakdown table at all', async () => {
    expect(
      await scalar(h.db, "SELECT COUNT(*) AS value FROM sqlite_master WHERE type = 'table' AND name = 'usage_part'"),
    ).toBe(0);
  });

  it('accepts a v1 snapshot and answers with the seven-field receipt', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
    });

    expect(response.status).toBe(200);
    const receipt = await json(response);
    expect(Object.keys(receipt)).toEqual([
      'status',
      'collectorId',
      'generation',
      'revision',
      'receivedAt',
      'changedDays',
      'ignoredReductions',
    ]);
    expect(receipt).toMatchObject({
      status: 'applied',
      collectorId: FIXTURE.collectorA1,
      changedDays: ['2026-11-18'],
    });
    expect((await usageRows(h.db)).map((row) => [row.bucket_date, row.total_tokens])).toEqual([
      ['2026-11-18', 100],
    ]);
  });

  it('keeps the v1 replay and the v1 reduction count working', async () => {
    const body = snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] });
    expect((await h.putSnapshot({ revision: 5, body })).status).toBe(200);

    const replay = await h.putSnapshot({ revision: 5, body });
    expect(replay.status).toBe(200);
    expect(await json(replay)).toMatchObject({ status: 'replayed', revision: 5, changedDays: [] });

    const lowered = await h.putSnapshot({
      revision: 6,
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 90 }] }),
    });
    expect(lowered.status).toBe(200);
    expect(await json(lowered)).toMatchObject({ changedDays: [], ignoredReductions: 1 });
    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([100]);
  });

  it('serves the v1 daily read', async () => {
    await h.putSnapshot({
      revision: 1,
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
    });

    const response = await h.getDaily('?from=2026-11-01&to=2026-11-30');
    expect(response.status).toBe(200);
    expect(await json(response)).toEqual({
      from: '2026-11-01',
      to: '2026-11-30',
      days: [{ date: '2026-11-18', collectorId: FIXTURE.collectorA1, totalTokens: 100 }],
      totalTokens: 100,
      collectors: [
        { collectorId: FIXTURE.collectorA1, generation: 1, lastReceivedAt: expect.any(String) },
        { collectorId: FIXTURE.collectorA2, generation: 1, lastReceivedAt: null },
      ],
    });
  });

  it('fails a v2 write as a write failure instead of pretending it stored a breakdown', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({
        days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 60, 10, 10, 5, 15]] }],
      }),
    });

    // There is no table to write, so nothing is stored and nothing is claimed:
    // the day row and the receipt are rolled back with the breakdown statement.
    expect(response.status).toBe(500);
    expect((await json(response)).error.code).toBe('internal_error');
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(0);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(0);
  });
});
