/**
 * Access-path review fixes.
 *
 * Three properties are pinned here:
 *
 * 1. A write credential is bound to exactly one collector, and that binding is
 *    checked before the database is consulted, so an out-of-scope collector id
 *    is rejected identically whether or not it exists.
 * 2. A read is resolved with an owner-filtered lookup, so an id owned by
 *    somebody else and an id that does not exist produce the same response.
 * 3. A collector-bound identity may never read owner-wide usage, and read
 *    results are bounded in rows and in aggregate size rather than truncated or
 *    silently rounded.
 */
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { dailyReadSql } from '../src/store';
import {
  createHarness,
  FIXTURE,
  json,
  resetData,
  scalar,
  seedCollectors,
  seedUsage,
  snapshotBody,
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

const MAX_SAFE_INTEGER = Number.MAX_SAFE_INTEGER;

function day(offset: number): string {
  return new Date(Date.parse('2026-01-01T00:00:00Z') + offset * 86_400_000).toISOString().slice(0, 10);
}

async function removeCollector(id: string): Promise<void> {
  await h.db.prepare('DELETE FROM collectors WHERE id = ?').bind(id).run();
}

describe('write binding is enforced before the collector lookup', () => {
  it('rejects a collector that is not the credential binding, even when the row is absent', async () => {
    // If the route looked the collector up first it would have to answer 404.
    await removeCollector(FIXTURE.collectorA2);
    const response = await h.putSnapshot({ revision: 1, body: snapshotBody(), collectorId: 'col-a2' });

    expect(response.status).toBe(403);
    expect((await json(response)).error.code).toBe('forbidden_collector');
  });

  it('answers an out-of-scope id exactly like an unknown id', async () => {
    const outOfScope = await h.putSnapshot({ revision: 1, body: snapshotBody(), collectorId: 'col-a2' });
    const unknown = await h.putSnapshot({ revision: 1, body: snapshotBody(), collectorId: 'col-zzzz' });

    expect(outOfScope.status).toBe(403);
    expect(unknown.status).toBe(403);
    expect(await json(unknown)).toEqual(await json(outOfScope));
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(0);
  });

  it('answers an out-of-scope id exactly like another owner’s id', async () => {
    const otherOwner = await h.putSnapshot({
      revision: 1,
      body: snapshotBody(),
      collectorId: FIXTURE.collectorB1,
      token: 'write-a1',
    });
    const outOfScope = await h.putSnapshot({ revision: 1, body: snapshotBody(), collectorId: 'col-a2' });

    expect(otherOwner.status).toBe(403);
    expect(await json(otherOwner)).toEqual(await json(outOfScope));
  });

  it('rejects an out-of-scope collector before reading an oversized body', async () => {
    // The binding check runs before the body is touched, so a credential cannot
    // spend the server's body budget on a collector it may not address.
    const oversized = new TextEncoder().encode(JSON.stringify({ pad: 'a'.repeat(40_000) }));
    const response = await h.putStream({ chunks: [oversized], collectorId: 'col-a2' });

    expect(response.status).toBe(403);
    expect((await json(response)).error.code).toBe('forbidden_collector');
  });

  it('still reports a missing collector for the credential’s own binding', async () => {
    await removeCollector(FIXTURE.collectorA1);
    const response = await h.putSnapshot({ revision: 1, body: snapshotBody() });

    expect(response.status).toBe(404);
    expect((await json(response)).error.code).toBe('collector_not_found');
  });
});

describe('read collector filter is owner-scoped', () => {
  it('answers another owner’s collector exactly like an unknown one', async () => {
    const otherOwner = await h.getDaily(
      `?from=2026-11-01&to=2026-11-30&collector=${FIXTURE.collectorB1}`,
      'read-a1',
    );
    const unknown = await h.getDaily('?from=2026-11-01&to=2026-11-30&collector=col-zzzz', 'read-a1');

    expect(otherOwner.status).toBe(404);
    expect(unknown.status).toBe(404);
    const otherBody = await json(otherOwner);
    const unknownBody = await json(unknown);
    expect(unknownBody).toEqual(otherBody);
    expect(unknownBody.error.code).toBe('collector_not_found');
  });

  it('still filters by a collector the caller owns', async () => {
    expect((await h.putSnapshot({ revision: 1, body: snapshotBody() })).status).toBe(200);
    const response = await h.getDaily(
      `?from=2026-11-01&to=2026-11-30&collector=${FIXTURE.collectorA1}`,
      'read-a1',
    );
    expect(response.status).toBe(200);
    expect((await json(response)).days).toHaveLength(1);
  });
});

describe('collector-bound identities cannot read owner-wide usage', () => {
  it('denies usage:read to a collector-bound credential', async () => {
    const response = await h.getDaily('?from=2026-11-01&to=2026-11-30', 'read-bound-a1');
    expect(response.status).toBe(403);
    expect((await json(response)).error.code).toBe('forbidden_scope');
  });

  it('denies usage:read even when the credential also holds usage:write', async () => {
    const response = await h.getDaily('?from=2026-11-01&to=2026-11-30', 'write-read-a1');
    expect(response.status).toBe(403);
    expect((await json(response)).error.code).toBe('forbidden_scope');
  });

  it('still serves an unbound owner credential', async () => {
    const response = await h.getDaily('?from=2026-11-01&to=2026-11-30', 'read-a1');
    expect(response.status).toBe(200);
  });
});

describe('read result bounds', () => {
  const BULK_COLLECTORS = 14;

  /**
   * Fills the range 2026-01-01.. with `count` distinct (collector, date) rows
   * across `BULK_COLLECTORS` collectors, in one statement: the rows are real
   * rows in the real local D1, but seeding them does not cost one round trip
   * per row.
   */
  async function seedGrid(count: number): Promise<void> {
    await seedCollectors(
      h.db,
      Array.from({ length: BULK_COLLECTORS }, (_, index) => ({
        id: `col-bulk-${String(index).padStart(2, '0')}`,
        ownerId: FIXTURE.ownerA,
      })),
    );
    await h.db
      .prepare(
        `INSERT INTO usage_day (collector_id, generation, bucket_date, total_tokens, updated_at)
         WITH RECURSIVE seq(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM seq WHERE n < ?)
         SELECT 'col-bulk-' || printf('%02d', n % ?),
                1,
                date('2026-01-01', '+' || (n / ?) || ' days'),
                1,
                '2026-11-20T00:00:00Z'
         FROM seq`,
      )
      .bind(count - 1, BULK_COLLECTORS, BULK_COLLECTORS)
      .run();
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(count);
  }

  it('serves exactly the maximum row count and rejects one row more', async () => {
    await seedGrid(5_000);

    const atLimit = await h.getDaily('?from=2026-01-01&to=2026-12-31');
    expect(atLimit.status).toBe(200);
    const body = await json(atLimit);
    expect(body.days).toHaveLength(5_000);
    expect(body.totalTokens).toBe(5_000);

    // One row beyond the documented maximum: the caller gets a bounded error,
    // never a silently truncated list that looks complete.
    await seedUsage(h.db, [{ collectorId: 'col-bulk-00', date: day(358), totalTokens: 1 }]);
    const overLimit = await h.getDaily('?from=2026-01-01&to=2026-12-31');
    expect(overLimit.status).toBe(413);
    const error = await json(overLimit);
    expect(error.error.code).toBe('result_too_large');
    expect(error.error.details?.reason).toBe('too_many_rows');
  }, 60_000);

  it('refuses an aggregate total that would leave the safe integer range', async () => {
    await seedUsage(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', totalTokens: MAX_SAFE_INTEGER },
      { collectorId: FIXTURE.collectorA2, date: '2026-11-18', totalTokens: MAX_SAFE_INTEGER },
    ]);

    const response = await h.getDaily('?from=2026-11-01&to=2026-11-30');
    expect(response.status).toBe(422);
    const body = await json(response);
    expect(body.error.code).toBe('result_too_large');
    expect(body.error.details?.reason).toBe('sum_out_of_range');
  });

  it('returns the exact total just below the safe integer bound', async () => {
    const half = 4_503_599_627_370_495;
    await seedUsage(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-17', totalTokens: half },
      { collectorId: FIXTURE.collectorA2, date: '2026-11-18', totalTokens: half },
    ]);

    const response = await h.getDaily('?from=2026-11-01&to=2026-11-30');
    expect(response.status).toBe(200);
    expect((await json(response)).totalTokens).toBe(9_007_199_254_740_990);
  });
});

describe('read query plan (real local D1)', () => {
  it('drives the owner filter through the owner index without scanning usage_day', async () => {
    const sql = dailyReadSql({ from: '2026-01-01', to: '2026-12-31', collectorId: null });
    const plan = await h.db
      .prepare(`EXPLAIN QUERY PLAN ${sql}`)
      .bind('owner-a', '2026-01-01', '2026-12-31', 5_001)
      .all<{ detail: string }>();

    const details = (plan.results ?? []).map((row) => row.detail);
    expect(details.join(' | ')).toContain('collectors_by_owner');
    expect(details.some((detail) => /^SCAN (u|usage_day)\b/.test(detail))).toBe(false);
  });

  it('keeps both read indexes in the schema', async () => {
    const rows = await h.db
      .prepare("SELECT name FROM sqlite_master WHERE type = 'index' ORDER BY name")
      .all<{ name: string }>();
    const names = (rows.results ?? []).map((row) => row.name);
    expect(names).toContain('collectors_by_owner');
    expect(names).toContain('usage_day_by_bucket_date');
  });
});
