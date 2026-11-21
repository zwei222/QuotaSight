import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import {
  createHarness,
  FIXTURE,
  json,
  resetData,
  scalar,
  snapshotBody,
  usageRows,
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

describe('PUT /v1/collectors/:collectorId/snapshots/:revision', () => {
  it('accepts an authenticated snapshot and reads the days back', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody({
        days: [
          { date: '2026-11-17', totalTokens: 40 },
          { date: '2026-11-18', totalTokens: 100 },
        ],
      }),
    });

    expect(response.status).toBe(200);
    const receipt = await json(response);
    expect(receipt).toMatchObject({
      status: 'applied',
      collectorId: FIXTURE.collectorA1,
      generation: 1,
      revision: 1,
      changedDays: ['2026-11-17', '2026-11-18'],
    });
    expect(typeof receipt.receivedAt).toBe('string');
    expect(Number.isNaN(Date.parse(receipt.receivedAt))).toBe(false);

    const rows = await usageRows(h.db);
    expect(rows.map((row) => [row.bucket_date, row.total_tokens])).toEqual([
      ['2026-11-17', 40],
      ['2026-11-18', 100],
    ]);

    const receipts = await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts');
    expect(receipts).toBe(1);
    const digestLength = await scalar(
      h.db,
      'SELECT length(body_digest) AS value FROM snapshot_receipts WHERE revision = 1',
    );
    expect(digestLength).toBe(64);

    const read = await h.getDaily('?from=2026-11-01&to=2026-11-30');
    expect(read.status).toBe(200);
    const body = await json(read);
    expect(body).toMatchObject({ from: '2026-11-01', to: '2026-11-30', totalTokens: 140 });
    expect(body.days).toEqual([
      { date: '2026-11-17', collectorId: FIXTURE.collectorA1, totalTokens: 40 },
      { date: '2026-11-18', collectorId: FIXTURE.collectorA1, totalTokens: 100 },
    ]);
  });

  it('treats the same revision with the identical payload as a no-op replay', async () => {
    const body = snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] });
    expect((await h.putSnapshot({ revision: 3, body })).status).toBe(200);
    const before = await usageRows(h.db);

    const replay = await h.putSnapshot({ revision: 3, body });
    expect(replay.status).toBe(200);
    expect(await json(replay)).toMatchObject({ status: 'replayed', revision: 3, changedDays: [] });

    expect(await usageRows(h.db)).toEqual(before);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
  });

  it('rejects the same revision with a different payload without overwriting', async () => {
    expect(
      (
        await h.putSnapshot({
          revision: 4,
          body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
        })
      ).status,
    ).toBe(200);
    const digestBefore = await h.db
      .prepare('SELECT body_digest FROM snapshot_receipts WHERE revision = 4')
      .first<{ body_digest: string }>();

    const conflict = await h.putSnapshot({
      revision: 4,
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 999 }] }),
    });
    expect(conflict.status).toBe(409);
    expect((await json(conflict)).error.code).toBe('revision_conflict');

    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([100]);
    const digestAfter = await h.db
      .prepare('SELECT body_digest FROM snapshot_receipts WHERE revision = 4')
      .first<{ body_digest: string }>();
    expect(digestAfter?.body_digest).toBe(digestBefore?.body_digest);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
  });

  it('rejects an out-of-order revision and keeps the newer value', async () => {
    expect(
      (
        await h.putSnapshot({
          revision: 10,
          body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
        })
      ).status,
    ).toBe(200);

    const stale = await h.putSnapshot({
      revision: 9,
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 500 }] }),
    });
    expect(stale.status).toBe(409);
    expect((await json(stale)).error.code).toBe('stale_revision');

    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([100]);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
  });

  it('keeps a decreased day at its accepted total and applies the other days', async () => {
    expect(
      (
        await h.putSnapshot({
          revision: 1,
          body: snapshotBody({
            days: [
              { date: '2026-11-17', totalTokens: 120 },
              { date: '2026-11-18', totalTokens: 10 },
            ],
          }),
        })
      ).status,
    ).toBe(200);

    // 2026-11-17 is lower than the accepted 120, so it is ignored rather than
    // written; the new day 2026-11-19 is still accepted in the same batch.
    const accepted = await h.putSnapshot({
      revision: 2,
      body: snapshotBody({
        days: [
          { date: '2026-11-17', totalTokens: 100 },
          { date: '2026-11-19', totalTokens: 99 },
        ],
      }),
    });
    expect(accepted.status).toBe(200);
    expect(await json(accepted)).toMatchObject({
      status: 'applied',
      revision: 2,
      changedDays: ['2026-11-19'],
      ignoredReductions: 1,
    });

    const rows = await usageRows(h.db);
    expect(rows.map((row) => [row.bucket_date, row.total_tokens])).toEqual([
      ['2026-11-17', 120],
      ['2026-11-18', 10],
      ['2026-11-19', 99],
    ]);
    expect(
      await scalar(h.db, 'SELECT ignored_reductions AS value FROM snapshot_receipts WHERE revision = 2'),
    ).toBe(1);
    // The revision was consumed even though one day was not written.
    const next = await h.putSnapshot({
      revision: 3,
      body: snapshotBody({ days: [{ date: '2026-11-17', totalTokens: 130 }] }),
    });
    expect(next.status).toBe(200);
    expect(await json(next)).toMatchObject({ status: 'applied', changedDays: ['2026-11-17'] });
  });

  it('accepts a snapshot in which every day decreased and writes no day', async () => {
    expect(
      (
        await h.putSnapshot({
          revision: 1,
          body: snapshotBody({
            days: [
              { date: '2026-11-17', totalTokens: 120 },
              { date: '2026-11-18', totalTokens: 10 },
            ],
          }),
        })
      ).status,
    ).toBe(200);

    const allLower = await h.putSnapshot({
      revision: 2,
      body: snapshotBody({
        days: [
          { date: '2026-11-17', totalTokens: 100 },
          { date: '2026-11-18', totalTokens: 5 },
        ],
      }),
    });
    expect(allLower.status).toBe(200);
    expect(await json(allLower)).toMatchObject({
      status: 'applied',
      revision: 2,
      changedDays: [],
      ignoredReductions: 2,
    });

    const rows = await usageRows(h.db);
    expect(rows.map((row) => [row.bucket_date, row.total_tokens])).toEqual([
      ['2026-11-17', 120],
      ['2026-11-18', 10],
    ]);
    // The revision is still consumed: the API recorded it even though no day
    // was written, so the next allowed revision is 3.
    expect(
      await scalar(h.db, 'SELECT MAX(revision) AS value FROM snapshot_receipts'),
    ).toBe(2);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(2);
  });

  it('leaves the observation time of a decreased day untouched', async () => {
    const first = await h.putSnapshot({
      revision: 1,
      body: snapshotBody({
        days: [
          { date: '2026-11-17', totalTokens: 120 },
          { date: '2026-11-18', totalTokens: 10 },
        ],
      }),
    });
    const firstReceipt = await json(first);

    const second = await h.putSnapshot({
      revision: 2,
      body: snapshotBody({
        days: [
          { date: '2026-11-17', totalTokens: 100 },
          { date: '2026-11-18', totalTokens: 20 },
        ],
      }),
    });
    const secondReceipt = await json(second);

    // The ignored day keeps both its total and the time it was observed; the
    // written day takes the time of the write that changed it.
    const rows = await h.db
      .prepare('SELECT bucket_date, total_tokens, updated_at FROM usage_day ORDER BY bucket_date')
      .all<{ bucket_date: string; total_tokens: number; updated_at: string }>();
    expect(rows.results).toEqual([
      { bucket_date: '2026-11-17', total_tokens: 120, updated_at: firstReceipt.receivedAt },
      { bucket_date: '2026-11-18', total_tokens: 20, updated_at: secondReceipt.receivedAt },
    ]);
  });

  it('treats an equal total as neither a change nor an ignored reduction', async () => {
    expect(
      (
        await h.putSnapshot({
          revision: 1,
          body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
        })
      ).status,
    ).toBe(200);

    const same = await h.putSnapshot({
      revision: 2,
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
    });
    expect(same.status).toBe(200);
    expect(await json(same)).toMatchObject({ changedDays: [], ignoredReductions: 0 });
  });

  it('replays an ignored reduction with the same ignoredReductions count', async () => {
    expect(
      (
        await h.putSnapshot({
          revision: 1,
          body: snapshotBody({
            days: [
              { date: '2026-11-17', totalTokens: 120 },
              { date: '2026-11-18', totalTokens: 10 },
            ],
          }),
        })
      ).status,
    ).toBe(200);

    const body = snapshotBody({
      days: [
        { date: '2026-11-17', totalTokens: 100 },
        { date: '2026-11-18', totalTokens: 5 },
      ],
    });
    const first = await h.putSnapshot({ revision: 2, body });
    expect(first.status).toBe(200);
    expect(await json(first)).toMatchObject({ status: 'applied', ignoredReductions: 2 });

    const replay = await h.putSnapshot({ revision: 2, body });
    expect(replay.status).toBe(200);
    expect(await json(replay)).toMatchObject({
      status: 'replayed',
      revision: 2,
      changedDays: [],
      ignoredReductions: 2,
    });
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(2);
  });

  it('accepts a measured zero for a previously absent day', async () => {
    const zero = await h.putSnapshot({
      revision: 1,
      body: snapshotBody({ days: [{ date: '2026-11-16', totalTokens: 0 }] }),
    });
    expect(zero.status).toBe(200);
    expect(await json(zero)).toMatchObject({ status: 'applied', changedDays: ['2026-11-16'] });

    const read = await h.getDaily('?from=2026-11-01&to=2026-11-30');
    expect((await json(read)).days).toEqual([
      { date: '2026-11-16', collectorId: FIXTURE.collectorA1, totalTokens: 0 },
    ]);
  });

  it('never deletes a day that is absent from a later snapshot', async () => {
    expect(
      (
        await h.putSnapshot({
          revision: 1,
          body: snapshotBody({
            days: [
              { date: '2026-11-17', totalTokens: 10 },
              { date: '2026-11-18', totalTokens: 20 },
            ],
          }),
        })
      ).status,
    ).toBe(200);

    const later = await h.putSnapshot({
      revision: 2,
      body: snapshotBody({ days: [{ date: '2026-11-19', totalTokens: 5 }] }),
    });
    expect(later.status).toBe(200);
    expect(await json(later)).toMatchObject({ changedDays: ['2026-11-19'] });

    const rows = await usageRows(h.db);
    expect(rows.map((row) => [row.bucket_date, row.total_tokens])).toEqual([
      ['2026-11-17', 10],
      ['2026-11-18', 20],
      ['2026-11-19', 5],
    ]);
  });

  it('does not report an unchanged day as changed', async () => {
    expect(
      (
        await h.putSnapshot({
          revision: 1,
          body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
        })
      ).status,
    ).toBe(200);
    const before = await usageRows(h.db);

    const same = await h.putSnapshot({
      revision: 2,
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
    });
    expect(same.status).toBe(200);
    expect(await json(same)).toMatchObject({ status: 'applied', changedDays: [] });
    expect(await usageRows(h.db)).toEqual(before);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(2);
  });
});

describe('generation enforcement', () => {
  it('rejects a snapshot for a generation that is not active', async () => {
    const rejected = await h.putSnapshot({
      revision: 1,
      body: snapshotBody({ generation: 2 }),
    });
    expect(rejected.status).toBe(409);
    expect((await json(rejected)).error.code).toBe('generation_inactive');
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(0);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(0);
  });

  it('invalidates the old generation and restarts revision scoping for the new one', async () => {
    expect(
      (
        await h.putSnapshot({
          revision: 7,
          body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
        })
      ).status,
    ).toBe(200);

    await h.db
      .prepare('UPDATE collectors SET active_generation = 2 WHERE id = ?')
      .bind(FIXTURE.collectorA1)
      .run();

    const superseded = await h.putSnapshot({
      revision: 8,
      body: snapshotBody({ generation: 1, days: [{ date: '2026-11-19', totalTokens: 1 }] }),
    });
    expect(superseded.status).toBe(409);
    expect((await json(superseded)).error.code).toBe('generation_inactive');

    const nextGeneration = await h.putSnapshot({
      revision: 1,
      body: snapshotBody({ generation: 2, days: [{ date: '2026-11-19', totalTokens: 42 }] }),
    });
    expect(nextGeneration.status).toBe(200);
    expect(await json(nextGeneration)).toMatchObject({
      status: 'applied',
      generation: 2,
      revision: 1,
    });

    const read = await h.getDaily('?from=2026-11-01&to=2026-11-30');
    expect((await json(read)).days).toEqual([
      { date: '2026-11-19', collectorId: FIXTURE.collectorA1, totalTokens: 42 },
    ]);
  });
});

describe('concurrent writes', () => {
  it('lets exactly one of two different bodies win the same revision', async () => {
    const dayA = snapshotBody({ days: [{ date: '2026-11-17', totalTokens: 100 }] });
    const dayB = snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] });

    const [first, second] = await Promise.all([
      h.putSnapshot({ revision: 5, body: dayA }),
      h.putSnapshot({ revision: 5, body: dayB }),
    ]);

    const statuses = [first.status, second.status].sort((a, b) => a - b);
    expect(statuses).toEqual([200, 409]);

    const winner = [first, second].find((response) => response.status === 200)!;
    const loser = [first, second].find((response) => response.status === 409)!;
    expect((await json(winner)).status).toBe('applied');
    expect((await json(loser)).error.code).toBe('revision_conflict');

    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
    const rows = await usageRows(h.db);
    expect(rows).toHaveLength(1);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(1);
  });

  it('treats two simultaneous identical submissions as applied plus replay', async () => {
    const body = snapshotBody({ days: [{ date: '2026-11-20', totalTokens: 7 }] });
    const [first, second] = await Promise.all([
      h.putSnapshot({ revision: 6, body }),
      h.putSnapshot({ revision: 6, body }),
    ]);

    expect([first.status, second.status]).toEqual([200, 200]);
    const statuses = [(await json(first)).status, (await json(second)).status].sort();
    expect(statuses).toEqual(['applied', 'replayed']);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
    expect(await scalar(h.db, "SELECT COUNT(*) AS value FROM usage_day WHERE total_tokens = 7")).toBe(
      1,
    );
  });

  it('keeps a single receipt under a burst of bodies for one revision', async () => {
    const bodies = Array.from({ length: 8 }, (_, index) => ({
      date: `2026-11-${String(index + 1).padStart(2, '0')}`,
      totalTokens: 10 + index,
    }));

    const responses = await Promise.all(
      bodies.map((day) => h.putSnapshot({ revision: 40, body: snapshotBody({ days: [day] }) })),
    );

    const applied = responses.filter((response) => response.status === 200);
    const rejected = responses.filter((response) => response.status === 409);
    expect(applied).toHaveLength(1);
    expect(rejected).toHaveLength(bodies.length - 1);
    for (const response of rejected) {
      expect((await json(response)).error.code).toBe('revision_conflict');
    }

    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(1);
  });

  it('keeps receipts and rows consistent when different revisions race', async () => {
    const [first, second] = await Promise.all([
      h.putSnapshot({
        revision: 11,
        body: snapshotBody({ days: [{ date: '2026-11-21', totalTokens: 11 }] }),
      }),
      h.putSnapshot({
        revision: 12,
        body: snapshotBody({ days: [{ date: '2026-11-22', totalTokens: 12 }] }),
      }),
    ]);

    // Either ordering is legitimate for independent revisions, but a rejected
    // write must leave no receipt and no partial day behind.
    for (const response of [first, second]) {
      expect([200, 409]).toContain(response.status);
    }
    const applied = [first, second].filter((response) => response.status === 200).length;
    expect(applied).toBeGreaterThanOrEqual(1);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(applied);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(applied);
    for (const response of [first, second]) {
      if (response.status === 409) {
        expect((await json(response)).error.code).toBe('stale_revision');
      }
    }
  });
});
