/**
 * Storage-level contract for the v2 breakdown table.
 *
 * The breakdown is kept as one set of rows per day (see `store.ts`): the write
 * path deletes a day's rows and re-inserts them when that day's total grows, so
 * a row is never edited downwards in place. The schema is what makes that safe
 * rather than merely likely: the sum rule, the identifier bounds and the
 * active-generation guard are enforced by the table and its triggers, so a
 * direct statement that bypasses `store.ts` still cannot store an inconsistent
 * breakdown. The query plans are asserted against the exact statements
 * production runs, because the read cost has to stay bounded by the caller's own
 * collectors.
 */
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { ApiError } from '../src/errors';
import { canonicalSnapshotJson, parseSnapshotBody, sha256Hex } from '../src/schema';
import {
  applySnapshot,
  applySnapshotV2,
  breakdownCoverageParams,
  breakdownCoverageSql,
  breakdownPartsParams,
  breakdownPartsSql,
  INSERT_PARTS_SQL,
  loadOwnedCollector,
} from '../src/store';
import {
  createHarness,
  FIXTURE,
  json,
  partRows,
  resetData,
  scalar,
  seedParts,
  seedUsage,
  snapshotBody,
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

describe('usage_part schema guards', () => {
  it('refuses a total that is not the sum of the five components', async () => {
    await expect(
      h.db
        .prepare(
          `INSERT INTO usage_part (collector_id, generation, bucket_date, client, provider, model,
             input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens,
             total_tokens, updated_at)
           VALUES (?, 1, '2026-11-18', 'codex', '', 'm', 10, 0, 0, 0, 0, 11, '2026-11-20T00:00:00Z')`,
        )
        .bind(FIXTURE.collectorA1)
        .run(),
    ).rejects.toThrow(/CHECK constraint failed/);
  });

  it('refuses a negative component and an out-of-bound component', async () => {
    await expect(
      h.db
        .prepare(
          `INSERT INTO usage_part (collector_id, generation, bucket_date, client, provider, model,
             input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens,
             total_tokens, updated_at)
           VALUES (?, 1, '2026-11-18', 'codex', '', 'm', -1, 0, 0, 0, 0, -1, '2026-11-20T00:00:00Z')`,
        )
        .bind(FIXTURE.collectorA1)
        .run(),
    ).rejects.toThrow(/CHECK constraint failed/);

    await expect(
      h.db
        .prepare(
          `INSERT INTO usage_part (collector_id, generation, bucket_date, client, provider, model,
             input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens,
             total_tokens, updated_at)
           VALUES (?, 1, '2026-11-18', 'codex', '', 'm', 9007199254740992, 0, 0, 0, 0,
                   9007199254740992, '2026-11-20T00:00:00Z')`,
        )
        .bind(FIXTURE.collectorA1)
        .run(),
    ).rejects.toThrow(/CHECK constraint failed/);
  });

  it('refuses identifiers longer than the wire contract allows', async () => {
    const insert = h.db.prepare(
      `INSERT INTO usage_part (collector_id, generation, bucket_date, client, provider, model,
         input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens,
         total_tokens, updated_at)
       VALUES (?, 1, '2026-11-18', ?, '', ?, 1, 0, 0, 0, 0, 1, '2026-11-20T00:00:00Z')`,
    );

    await expect(insert.bind(FIXTURE.collectorA1, 'c'.repeat(33), 'm').run()).rejects.toThrow(
      /CHECK constraint failed/,
    );
    await expect(insert.bind(FIXTURE.collectorA1, 'c', 'm'.repeat(97)).run()).rejects.toThrow(
      /CHECK constraint failed/,
    );
  });

  it('refuses a direct in-place decrease of an accepted breakdown row', async () => {
    await seedParts(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-18', client: 'codex', model: 'm', input: 100, output: 0 },
    ]);

    // The write path replaces a day's rows instead of editing one, so an UPDATE
    // that lowers a stored total is never part of a write and is refused.
    await expect(
      h.db
        .prepare(
          `UPDATE usage_part SET input_tokens = 50, total_tokens = 50
           WHERE collector_id = ? AND generation = 1 AND bucket_date = '2026-11-18'`,
        )
        .bind(FIXTURE.collectorA1)
        .run(),
    ).rejects.toThrow(/usage_part_reduction_unverified/);

    expect((await partRows(h.db)).map((row) => row.total_tokens)).toEqual([100]);
  });

  it('refuses a breakdown row for a generation that is not active', async () => {
    await expect(
      h.db
        .prepare(
          `INSERT INTO usage_part (collector_id, generation, bucket_date, client, provider, model,
             input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens,
             total_tokens, updated_at)
           VALUES (?, 2, '2026-11-18', 'codex', '', 'm', 1, 0, 0, 0, 0, 1, '2026-11-20T00:00:00Z')`,
        )
        .bind(FIXTURE.collectorA1)
        .run(),
    ).rejects.toThrow(/collector_generation_inactive/);
  });

  it('bounds the ignored breakdown reduction count of a receipt', async () => {
    // The count is a number of days now, so its bound is the snapshot day bound.
    await h.db
      .prepare(
        'INSERT INTO snapshot_receipts (collector_id, generation, revision, body_digest, received_at, ignored_part_reductions) VALUES (?, 1, 1, ?, ?, 62)',
      )
      .bind(FIXTURE.collectorA1, 'a'.repeat(64), '2026-11-20T00:00:00Z')
      .run();

    await expect(
      h.db
        .prepare(
          'INSERT INTO snapshot_receipts (collector_id, generation, revision, body_digest, received_at, ignored_part_reductions) VALUES (?, 1, 2, ?, ?, 63)',
        )
        .bind(FIXTURE.collectorA1, 'b'.repeat(64), '2026-11-20T00:00:00Z')
        .run(),
    ).rejects.toThrow(/CHECK constraint failed/);
  });

  it('cascades breakdown rows when the collector is deleted', async () => {
    await seedParts(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-18', client: 'codex', model: 'm', input: 1, output: 0 },
    ]);
    await h.db.prepare('DELETE FROM collectors WHERE id = ?').bind(FIXTURE.collectorA1).run();
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_part')).toBe(0);
  });
});

/**
 * The day ceiling is a schema guard, not a write-path convention: a direct
 * statement that bypasses `store.ts` cannot make a day's breakdown add up to
 * more than the day total it is attributed against.
 */
describe('usage_part day ceiling', () => {
  async function plan(sql: string, params: unknown[]): Promise<string> {
    const result = await h.db
      .prepare(`EXPLAIN QUERY PLAN ${sql}`)
      .bind(...params)
      .all<{ detail: string }>();
    return (result.results ?? []).map((row) => row.detail).join(' | ');
  }

  it('refuses a row that would push the day above its accepted total', async () => {
    await seedUsage(h.db, [{ collectorId: FIXTURE.collectorA1, date: '2026-11-18', totalTokens: 100 }]);
    await seedParts(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-18', client: 'codex', model: 'm', input: 60, output: 0 },
    ]);

    // 60 are already attributed, so 41 would make the day explain 101 of 100.
    await expect(
      seedParts(h.db, [
        { collectorId: FIXTURE.collectorA1, date: '2026-11-18', client: 'gemini', model: 'm', input: 41, output: 0 },
      ]),
    ).rejects.toThrow(/usage_part_exceeds_day_total/);
    expect(await scalar(h.db, 'SELECT SUM(total_tokens) AS value FROM usage_part')).toBe(60);

    // Equality is the boundary the rule allows: attributed <= total.
    await seedParts(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-18', client: 'gemini', model: 'm', input: 40, output: 0 },
    ]);
    expect(await scalar(h.db, 'SELECT SUM(total_tokens) AS value FROM usage_part')).toBe(100);
  });

  it('compares against the total of its own collector, generation and day', async () => {
    await seedUsage(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-18', totalTokens: 10 },
      { collectorId: FIXTURE.collectorA2, date: '2026-11-18', totalTokens: 1000 },
      { collectorId: FIXTURE.collectorA1, date: '2026-11-19', totalTokens: 1000 },
    ]);

    // A2's larger total on the same date and the same collector's larger total
    // on another date are both irrelevant to A1's 2026-11-18.
    await expect(
      seedParts(h.db, [
        { collectorId: FIXTURE.collectorA1, date: '2026-11-18', client: 'codex', model: 'm', input: 11, output: 0 },
      ]),
    ).rejects.toThrow(/usage_part_exceeds_day_total/);
    await seedParts(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-18', client: 'codex', model: 'm', input: 10, output: 0 },
    ]);
    expect(await scalar(h.db, 'SELECT SUM(total_tokens) AS value FROM usage_part')).toBe(10);
  });

  it('checks only a day that has an accepted total', async () => {
    // The guard compares against `usage_day`, and a day nothing has been written
    // to has no total to compare with — the table cannot invent one. The write
    // path always writes the day first (the day upsert leads the batch), so this
    // only states what the schema can know on its own.
    await seedParts(h.db, [
      { collectorId: FIXTURE.collectorA1, date: '2026-11-18', client: 'codex', model: 'm', input: 500, output: 0 },
    ]);
    expect(await scalar(h.db, 'SELECT SUM(total_tokens) AS value FROM usage_part')).toBe(500);
  });

  it('resolves the day and the stored sum by primary key, not by a scan', async () => {
    const details = await plan(
      `SELECT SUM(p.total_tokens) FROM usage_part AS p
        WHERE p.collector_id = ? AND p.generation = ? AND p.bucket_date = ?`,
      [FIXTURE.collectorA1, 1, '2026-11-18'],
    );
    expect(details).toContain('SEARCH p USING PRIMARY KEY');
    expect(details).not.toContain('SCAN p');
    expect(details).not.toContain('SCAN usage_part');

    const dayDetails = await plan(
      'SELECT d.total_tokens FROM usage_day AS d WHERE d.collector_id = ? AND d.generation = ? AND d.bucket_date = ?',
      [FIXTURE.collectorA1, 1, '2026-11-18'],
    );
    expect(dayDetails).toContain('SEARCH d USING PRIMARY KEY');
    expect(dayDetails).not.toContain('SCAN d');
  });
});

/**
 * The insert of a replaced day no longer carries an `ON CONFLICT ... DO UPDATE`
 * clause: that branch could not be reached by the write path, and reaching it via
 * a duplicate key would have silently kept the larger row. A duplicate key now
 * aborts the statement, so the whole batch (and its receipt) rolls back.
 */
describe('usage_part duplicate key', () => {
  it('aborts instead of keeping the larger row', async () => {
    await seedUsage(h.db, [{ collectorId: FIXTURE.collectorA1, date: '2026-11-18', totalTokens: 30 }]);
    const parts = JSON.stringify([
      { d: '2026-11-18', c: 'codex', p: '', m: 'm', i: 10, o: 0, cr: 0, cw: 0, r: 0, t: 10, dt: 30 },
      { d: '2026-11-18', c: 'codex', p: '', m: 'm', i: 20, o: 0, cr: 0, cw: 0, r: 0, t: 20, dt: 30 },
    ]);

    await expect(
      h.db
        .prepare(INSERT_PARTS_SQL)
        .bind(FIXTURE.collectorA1, 1, '2026-11-20T00:00:00Z', parts, FIXTURE.collectorA1, 1)
        .run(),
    ).rejects.toThrow(/constraint failed/);

    // Nothing of the aborted statement survives, not even its first row.
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_part')).toBe(0);
  });
});

describe('applySnapshotV2 write ordering', () => {
  async function prepare(body: unknown) {
    const snapshot = parseSnapshotBody(body);
    const digest = await sha256Hex(canonicalSnapshotJson(snapshot));
    const collector = await loadOwnedCollector(h.db, FIXTURE.collectorA1, FIXTURE.ownerA);
    if (collector === null) throw new Error('fixture collector missing');
    return { snapshot, digest, collector };
  }

  it('records one receipt and one breakdown row per key for a single write', async () => {
    const prepared = await prepare(
      snapshotBodyV2({
        keys: [
          ['a', '', 'm'],
          ['b', '', 'm'],
        ],
        days: [
          {
            date: '2026-11-18',
            totalTokens: 30,
            parts: [
              [0, 10, 5, 0, 0, 0],
              [1, 10, 5, 0, 0, 0],
            ],
          },
        ],
      }),
    );

    const receipt = await applySnapshotV2(
      h.db,
      prepared.collector,
      prepared.snapshot as never,
      11,
      prepared.digest,
      '2026-11-20T03:00:00Z',
    );

    expect(receipt.status).toBe('applied');
    expect(receipt.changedDays).toEqual(['2026-11-18']);
    expect(receipt.changedParts).toBe(2);
    expect(receipt.ignoredReductions).toBe(0);
    expect(receipt.ignoredPartReductions).toBe(0);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_part')).toBe(2);
  });

  it('lets only one of two overlapping writes for a revision commit', async () => {
    const first = await prepare(
      snapshotBodyV2({ days: [{ date: '2026-11-18', totalTokens: 10, parts: [[0, 10, 0, 0, 0, 0]] }] }),
    );
    const second = await prepare(
      snapshotBodyV2({
        keys: [['gemini', '', 'm']],
        days: [{ date: '2026-11-19', totalTokens: 20, parts: [[0, 20, 0, 0, 0, 0]] }],
      }),
    );

    const results = await Promise.allSettled([
      applySnapshotV2(h.db, first.collector, first.snapshot as never, 12, first.digest, '2026-11-20T03:00:00Z'),
      applySnapshotV2(h.db, second.collector, second.snapshot as never, 12, second.digest, '2026-11-20T03:00:00Z'),
    ]);

    const rejected = results.filter((result) => result.status === 'rejected');
    expect(rejected).toHaveLength(1);
    const error = (rejected[0] as PromiseRejectedResult).reason;
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).code).toBe('revision_conflict');

    // The losing transaction leaves neither a receipt, a day nor a part behind.
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(1);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_part')).toBe(1);
  });

  it('keeps a v1 write path untouched by the v2 table', async () => {
    const v1 = parseSnapshotBody(snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 5 }] }));
    const digest = await sha256Hex(canonicalSnapshotJson(v1));
    const collector = await loadOwnedCollector(h.db, FIXTURE.collectorA1, FIXTURE.ownerA);

    const receipt = await applySnapshot(
      h.db,
      collector!,
      v1 as never,
      13,
      digest,
      '2026-11-20T03:00:00Z',
    );

    // The v1 receipt keeps exactly its seven fields: adding a v2 field to it
    // would change the response bytes every deployed collector already parses.
    expect(Object.keys(receipt).sort()).toEqual([
      'changedDays',
      'collectorId',
      'generation',
      'ignoredReductions',
      'receivedAt',
      'revision',
      'status',
    ]);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_part')).toBe(0);
  });

  it('answers a guard abort as a server error and commits nothing', async () => {
    // A body the contract rejects (`sum(parts) != totalTokens`) is handed to the
    // storage layer directly, so the schema trigger is what refuses it. The point
    // of the test is the classification: an abort the write path cannot explain
    // is a server error, not a 409 and not a silent partial write.
    const snapshot = {
      schemaVersion: 2,
      adapterVersion: 'smoke-1',
      generation: 1,
      collectedAt: '2026-11-20T03:00:00Z',
      bucketTimeZone: 'Asia/Tokyo',
      coveredRange: { from: '2026-11-18', to: '2026-11-18' },
      keys: [{ client: 'codex', provider: '', model: 'm' }],
      days: [
        {
          date: '2026-11-18',
          totalTokens: 50,
          parts: [{ keyIndex: 0, input: 60, output: 0, cacheRead: 0, cacheWrite: 0, reasoning: 0 }],
        },
      ],
    };
    const digest = 'a'.repeat(64);
    const collector = await loadOwnedCollector(h.db, FIXTURE.collectorA1, FIXTURE.ownerA);
    if (collector === null) throw new Error('fixture collector missing');

    const error = await applySnapshotV2(
      h.db,
      collector,
      snapshot as never,
      14,
      digest,
      '2026-11-20T03:00:00Z',
    ).catch((thrown: unknown) => thrown);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({
      status: 500,
      code: 'internal_error',
      details: { reason: 'write_failed' },
    });

    // The whole batch rolled back: no day, no part, no receipt.
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(0);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_part')).toBe(0);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(0);
  });
});

describe('breakdown read plans', () => {
  async function plan(sql: string, params: unknown[]): Promise<string> {
    const result = await h.db
      .prepare(`EXPLAIN QUERY PLAN ${sql}`)
      .bind(...params)
      .all<{ detail: string }>();
    return (result.results ?? []).map((row) => row.detail).join(' | ');
  }

  it('drives the parts aggregation from the owner index, not a table scan', async () => {
    const query = { from: '2026-11-01', to: '2026-11-30', collectorId: null };
    const details = await plan(breakdownPartsSql(query), breakdownPartsParams(FIXTURE.ownerA, query));

    expect(details).toContain('collectors_by_owner');
    expect(details).toContain('SEARCH u USING PRIMARY KEY');
    expect(details).not.toContain('SCAN u');
    expect(details).not.toContain('SCAN usage_part');
  });

  it('reads coverage from the day rows and attributes through the part key', async () => {
    const query = { from: '2026-11-01', to: '2026-11-30', collectorId: null };
    const details = await plan(
      breakdownCoverageSql(query),
      breakdownCoverageParams(FIXTURE.ownerA, query),
    );

    expect(details).toContain('collectors_by_owner');
    expect(details).toContain('SEARCH u USING PRIMARY KEY');
    expect(details).not.toContain('SCAN usage_part');
  });
});

describe('v2 response shape', () => {
  it('adds only the two v2 fields after the v1 seven', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({
        days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 60, 10, 10, 5, 15]] }],
      }),
    });
    const text = await response.text();
    const parsed = JSON.parse(text);

    expect(Object.keys(parsed)).toEqual([
      'status',
      'collectorId',
      'generation',
      'revision',
      'receivedAt',
      'changedDays',
      'ignoredReductions',
      'changedParts',
      'ignoredPartReductions',
    ]);
    expect(parsed.changedParts).toBe(1);
    expect(parsed.ignoredPartReductions).toBe(0);
    // Serialized exactly as the ordered object above, with no pretty printing.
    expect(text).toBe(
      `{"status":"applied","collectorId":"${FIXTURE.collectorA1}","generation":1,"revision":1,` +
        `"receivedAt":"${parsed.receivedAt}","changedDays":["2026-11-18"],"ignoredReductions":0,` +
        '"changedParts":1,"ignoredPartReductions":0}',
    );
  });
});
