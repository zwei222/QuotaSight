/**
 * Storage-level contract tests against the real local D1.
 *
 * The HTTP layer can only be observed as a serialized sequence in the local
 * runtime (workerd runs one isolate), so the write-ordering and atomicity
 * guarantees are exercised here directly: through `applySnapshot()` for the
 * application logic and through raw prepared statements for the schema guards
 * that make those guarantees hold inside the transaction.
 */
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { ApiError } from '../src/errors';
import { canonicalSnapshotJson, parseSnapshot, sha256Hex } from '../src/schema';
import { applySnapshot, loadOwnedCollector } from '../src/store';
import { createHarness, FIXTURE, resetData, scalar, snapshotBody, usageRows, type Harness } from './harness';

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

async function prepare(body: unknown) {
  const snapshot = parseSnapshot(body);
  const digest = await sha256Hex(canonicalSnapshotJson(snapshot));
  const collector = await loadOwnedCollector(h.db, FIXTURE.collectorA1, FIXTURE.ownerA);
  if (collector === null) throw new Error('fixture collector missing');
  return { snapshot, digest, collector };
}

describe('applySnapshot write ordering', () => {
  it('lets only one of two overlapping writes for a revision commit', async () => {
    const first = await prepare(snapshotBody({ days: [{ date: '2026-11-01', totalTokens: 1 }] }));
    const second = await prepare(snapshotBody({ days: [{ date: '2026-11-02', totalTokens: 2 }] }));

    const results = await Promise.allSettled([
      applySnapshot(h.db, first.collector, first.snapshot, 40, first.digest, '2026-11-20T03:00:00Z'),
      applySnapshot(h.db, second.collector, second.snapshot, 40, second.digest, '2026-11-20T03:00:00Z'),
    ]);

    const fulfilled = results.filter((result) => result.status === 'fulfilled');
    const rejected = results.filter((result) => result.status === 'rejected');
    expect(fulfilled).toHaveLength(1);
    expect(rejected).toHaveLength(1);

    const error = (rejected[0] as PromiseRejectedResult).reason;
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).code).toBe('revision_conflict');

    // The rejected transaction must leave neither a receipt nor a day behind.
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(1);
  });

  it('treats an overlapping identical write as applied plus replay', async () => {
    const prepared = await prepare(snapshotBody({ days: [{ date: '2026-11-03', totalTokens: 3 }] }));

    const results = await Promise.all([
      applySnapshot(h.db, prepared.collector, prepared.snapshot, 41, prepared.digest, '2026-11-20T03:00:00Z'),
      applySnapshot(h.db, prepared.collector, prepared.snapshot, 41, prepared.digest, '2026-11-20T03:00:00Z'),
    ]);

    expect(results.map((result) => result.status).sort()).toEqual(['applied', 'replayed']);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(1);
  });
});

describe('schema guards inside the transaction', () => {
  it('rolls back the day write when the receipt insert aborts', async () => {
    await h.db
      .prepare(
        'INSERT INTO snapshot_receipts (collector_id, generation, revision, body_digest, received_at) VALUES (?, ?, ?, ?, ?)',
      )
      .bind(FIXTURE.collectorA1, 1, 1, 'a'.repeat(64), '2026-11-20T00:00:00Z')
      .run();

    const upsert = h.db.prepare(
      `INSERT INTO usage_day (collector_id, generation, bucket_date, total_tokens, updated_at)
       VALUES (?, ?, ?, ?, ?)
       ON CONFLICT (collector_id, generation, bucket_date) DO UPDATE
         SET total_tokens = excluded.total_tokens, updated_at = excluded.updated_at`,
    );

    await expect(
      h.db.batch([
        upsert.bind(FIXTURE.collectorA1, 1, '2026-11-05', 5, '2026-11-20T03:00:00Z'),
        h.db
          .prepare(
            'INSERT INTO snapshot_receipts (collector_id, generation, revision, body_digest, received_at) VALUES (?, ?, ?, ?, ?)',
          )
          .bind(FIXTURE.collectorA1, 1, 1, 'b'.repeat(64), '2026-11-20T03:00:00Z'),
      ]),
    ).rejects.toThrow(/UNIQUE constraint failed: snapshot_receipts/);

    expect(await usageRows(h.db)).toEqual([]);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
  });

  it('refuses a decrease of an accepted day', async () => {
    await h.db
      .prepare(
        'INSERT INTO usage_day (collector_id, generation, bucket_date, total_tokens, updated_at) VALUES (?, ?, ?, ?, ?)',
      )
      .bind(FIXTURE.collectorA1, 1, '2026-11-06', 100, '2026-11-20T00:00:00Z')
      .run();

    await expect(
      h.db
        .prepare(
          'UPDATE usage_day SET total_tokens = ? WHERE collector_id = ? AND generation = ? AND bucket_date = ?',
        )
        .bind(50, FIXTURE.collectorA1, 1, '2026-11-06')
        .run(),
    ).rejects.toThrow(/usage_reduction_unverified/);

    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([100]);
  });

  it('refuses a write for a generation that is not active', async () => {
    await expect(
      h.db
        .prepare(
          'INSERT INTO usage_day (collector_id, generation, bucket_date, total_tokens, updated_at) VALUES (?, ?, ?, ?, ?)',
        )
        .bind(FIXTURE.collectorA1, 2, '2026-11-07', 1, '2026-11-20T00:00:00Z')
        .run(),
    ).rejects.toThrow(/collector_generation_inactive/);

    await expect(
      h.db
        .prepare(
          'INSERT INTO snapshot_receipts (collector_id, generation, revision, body_digest, received_at) VALUES (?, ?, ?, ?, ?)',
        )
        .bind(FIXTURE.collectorA1, 2, 1, 'c'.repeat(64), '2026-11-20T00:00:00Z')
        .run(),
    ).rejects.toThrow(/collector_generation_inactive/);

    expect(await usageRows(h.db)).toEqual([]);
  });

  it('refuses an older revision after a newer one is recorded', async () => {
    const insert = h.db.prepare(
      'INSERT INTO snapshot_receipts (collector_id, generation, revision, body_digest, received_at) VALUES (?, ?, ?, ?, ?)',
    );
    await insert.bind(FIXTURE.collectorA1, 1, 9, 'd'.repeat(64), '2026-11-20T00:00:00Z').run();

    await expect(
      insert.bind(FIXTURE.collectorA1, 1, 8, 'e'.repeat(64), '2026-11-20T00:00:00Z').run(),
    ).rejects.toThrow(/collector_revision_stale/);

    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
  });

  it('scopes revision ordering to the collector generation', async () => {
    const insert = h.db.prepare(
      'INSERT INTO snapshot_receipts (collector_id, generation, revision, body_digest, received_at) VALUES (?, ?, ?, ?, ?)',
    );
    await insert.bind(FIXTURE.collectorA1, 1, 9, 'f'.repeat(64), '2026-11-20T00:00:00Z').run();

    await h.db
      .prepare('UPDATE collectors SET active_generation = 2 WHERE id = ?')
      .bind(FIXTURE.collectorA1)
      .run();

    // Revision 1 restarts legitimately for the new generation.
    await insert.bind(FIXTURE.collectorA1, 2, 1, 'a'.repeat(64), '2026-11-20T00:00:00Z').run();
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(2);
  });
});
