/**
 * Freshness contract for `GET /v1/usage/daily`.
 *
 * A daily total without a reception time cannot be told apart from a fresh one,
 * so the read response also answers "when did each collector of mine last land
 * an accepted snapshot?". The rules under test:
 *
 *   * one entry per collector of the owner that the read covers, sorted by
 *     collector id, carrying the collector's active generation;
 *   * `lastReceivedAt` is the reception time of the newest accepted receipt of
 *     that active generation, as an ISO-8601 UTC instant with milliseconds;
 *   * a collector that has never had a receipt in its active generation is still
 *     listed, with `lastReceivedAt: null`, so "never reported" is not silently
 *     indistinguishable from "not configured";
 *   * receipts of a superseded generation never leak into the reported time;
 *   * the list is bounded, and an over-bound answer is an error rather than a
 *     truncated list.
 *
 * Everything runs against the real Worker and a real local D1 database.
 */
import { beforeEach, afterAll, beforeAll, describe, expect, it } from 'vitest';
import {
  createHarness,
  FIXTURE,
  json,
  resetData,
  scalar,
  seedCollectors,
  snapshotBody,
  type Harness,
} from './harness';
import { collectorFreshnessParams, collectorFreshnessSql } from '../src/store';

interface CollectorFreshness {
  readonly collectorId: string;
  readonly generation: number;
  readonly lastReceivedAt: string | null;
}

interface DailyBody {
  readonly from: string;
  readonly to: string;
  readonly days: ReadonlyArray<{
    readonly date: string;
    readonly collectorId: string;
    readonly totalTokens: number;
  }>;
  readonly totalTokens: number;
  readonly collectors: readonly CollectorFreshness[];
}

interface ReceiptBody {
  readonly status: string;
  readonly receivedAt: string;
}

const MILLISECOND_UTC = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/;

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

async function readDaily(search = '?from=2026-11-17&to=2026-11-19', token = 'read-a1') {
  const response = await h.getDaily(search, token);
  expect(response.status).toBe(200);
  return json<DailyBody>(response);
}

async function put(revision: number, collectorId: string = FIXTURE.collectorA1, generation = 1) {
  const response = await h.putSnapshot({
    revision,
    collectorId,
    body: snapshotBody({ generation }),
  });
  expect(response.status).toBe(200);
  return json<ReceiptBody>(response);
}

describe('GET /v1/usage/daily collector freshness', () => {
  it('reports each covered collector sorted by id with the active generation and receipt time', async () => {
    const receipt = await put(1);

    const body = await readDaily();

    expect(body.collectors.map((entry) => entry.collectorId)).toEqual([
      FIXTURE.collectorA1,
      FIXTURE.collectorA2,
    ]);
    expect(body.collectors).toEqual([
      {
        collectorId: FIXTURE.collectorA1,
        generation: 1,
        lastReceivedAt: receipt.receivedAt,
      },
      // Never reported in this generation, but still configured: it is listed
      // with a null time instead of being dropped.
      { collectorId: FIXTURE.collectorA2, generation: 1, lastReceivedAt: null },
    ]);
    expect(receipt.receivedAt).toMatch(MILLISECOND_UTC);
  });

  it('moves the reported time forward with each accepted revision', async () => {
    const first = await put(1);
    const second = await put(2);

    const body = await readDaily();

    const entry = body.collectors.find((item) => item.collectorId === FIXTURE.collectorA1);
    expect(entry?.lastReceivedAt).toBe(second.receivedAt);
    expect(Date.parse(second.receivedAt)).toBeGreaterThanOrEqual(Date.parse(first.receivedAt));
  });

  it('never reports a superseded generation and follows the active one', async () => {
    await put(1);

    // The collector is re-provisioned: its active generation moves on, so the
    // receipts of generation 1 are history and must not be read as freshness.
    await h.db
      .prepare('UPDATE collectors SET active_generation = 2 WHERE id = ?')
      .bind(FIXTURE.collectorA1)
      .run();

    const withoutReceipt = await readDaily();
    expect(withoutReceipt.collectors.find((item) => item.collectorId === FIXTURE.collectorA1)).toEqual({
      collectorId: FIXTURE.collectorA1,
      generation: 2,
      lastReceivedAt: null,
    });

    const receipt = await put(1, FIXTURE.collectorA1, 2);
    const withReceipt = await readDaily();
    expect(withReceipt.collectors.find((item) => item.collectorId === FIXTURE.collectorA1)).toEqual({
      collectorId: FIXTURE.collectorA1,
      generation: 2,
      lastReceivedAt: receipt.receivedAt,
    });
  });

  it('reads only the active generation: a raised generation empties the days but keeps the rows stored', async () => {
    // The daily rows of a superseded generation are retained in `usage_day` (the
    // table is keyed by generation on purpose), but the read API never returns
    // them: every read joins on the collector's active generation. There is no
    // endpoint that reads a past generation, so this is a documented limitation
    // rather than a rotation feature.
    const receipt = await put(1);
    expect(receipt.status).toBe('applied');

    await h.db
      .prepare('UPDATE collectors SET active_generation = 2 WHERE id = ?')
      .bind(FIXTURE.collectorA1)
      .run();

    const body = await readDaily();
    expect(body.days).toEqual([]);
    expect(body.totalTokens).toBe(0);
    expect(body.collectors.find((item) => item.collectorId === FIXTURE.collectorA1)).toEqual({
      collectorId: FIXTURE.collectorA1,
      generation: 2,
      lastReceivedAt: null,
    });

    // The old rows are still there; nothing was deleted, they are simply not read.
    const retained = await scalar(
      h.db,
      'SELECT COUNT(*) AS value FROM usage_day WHERE collector_id = ? AND generation = 1',
      FIXTURE.collectorA1,
    );
    expect(retained).toBe(1);
  });

  it('stays owner-scoped and honours the collector filter', async () => {
    await put(1);

    const otherOwner = await readDaily('?from=2026-11-17&to=2026-11-19', 'read-b1');
    expect(otherOwner.collectors).toEqual([
      { collectorId: FIXTURE.collectorB1, generation: 1, lastReceivedAt: null },
    ]);

    const filtered = await readDaily(
      `?from=2026-11-17&to=2026-11-19&collector=${FIXTURE.collectorA2}`,
    );
    expect(filtered.collectors).toEqual([
      { collectorId: FIXTURE.collectorA2, generation: 1, lastReceivedAt: null },
    ]);
    expect(filtered.days).toEqual([]);
  });

  it('keeps the freshness list bounded instead of truncating it', async () => {
    const extra = Array.from({ length: 63 }, (_, index) => ({
      id: `col-x${String(index).padStart(2, '0')}`,
      ownerId: FIXTURE.ownerA,
    }));
    await seedCollectors(h.db, extra);

    // owner-a now owns 65 collectors, one past the documented bound.
    const response = await h.getDaily('?from=2026-11-17&to=2026-11-19', 'read-a1');

    expect(response.status).toBe(413);
    const error = await json<{ error: { code: string } }>(response);
    expect(error.error.code).toBe('result_too_large');
  });

  it('keeps the rest of the read contract unchanged', async () => {
    await put(1);

    const body = await readDaily();

    expect(Object.keys(body).sort()).toEqual(
      ['collectors', 'days', 'from', 'to', 'totalTokens'].sort(),
    );
    expect(body.from).toBe('2026-11-17');
    expect(body.to).toBe('2026-11-19');
    expect(body.days.map((day) => [day.date, day.totalTokens])).toEqual([
      ['2026-11-18', 100],
    ]);
    expect(body.totalTokens).toBe(100);
  });
});

describe('collector freshness query plan (real local D1)', () => {
  it('drives the owner filter through the owner index and seeks the receipt ledger', async () => {
    const query = { from: '2026-11-17', to: '2026-11-19', collectorId: null };
    const plan = await h.db
      .prepare(`EXPLAIN QUERY PLAN ${collectorFreshnessSql(query)}`)
      .bind(...collectorFreshnessParams(FIXTURE.ownerA, query))
      .all<{ detail: string }>();

    const details = (plan.results ?? []).map((row) => row.detail).join(' | ');
    expect(details).toContain('collectors_by_owner');
    // The newest receipt is an index seek on the ledger key, never a scan.
    expect(details).toContain('SEARCH r USING PRIMARY KEY');
    expect(/SCAN (r|snapshot_receipts)\b/.test(details)).toBe(false);
    // The list is already ordered by the driving index, so no extra sort is added.
    expect(details).not.toContain('TEMP B-TREE');
  });
});
