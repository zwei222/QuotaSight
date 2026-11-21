import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { createHarness, json, resetData, scalar, snapshotBody, type Harness } from './harness';

let h: Harness;
let prod: Harness;

beforeAll(async () => {
  h = await createHarness();
  prod = await createHarness('src/index.ts');
}, 90_000);

afterAll(async () => {
  await h.dispose();
  await prod.dispose();
});

beforeEach(async () => {
  await resetData(h.db);
  await resetData(prod.db);
});

describe('unconfigured runtime', () => {
  it('denies every request when no authenticator is configured', async () => {
    for (const token of ['write-a1', 'read-a1', 'unconfigured-probe']) {
      const write = await prod.putSnapshot({ revision: 1, body: snapshotBody(), token });
      expect(write.status).toBe(401);
      expect((await json(write)).error.code).toBe('unauthorized');

      const read = await prod.getDaily('?from=2026-11-01&to=2026-11-30', token);
      expect(read.status).toBe(401);
      expect((await json(read)).error.code).toBe('unauthorized');
    }

    expect(await scalar(prod.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(0);
    expect(await scalar(prod.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(0);
  });
});

describe('authentication', () => {
  it('rejects requests without credentials', async () => {
    const write = await h.putSnapshot({ revision: 1, body: snapshotBody(), token: null });
    expect(write.status).toBe(401);

    const read = await h.getDaily('?from=2026-11-01&to=2026-11-30', null);
    expect(read.status).toBe(401);
  });

  it('rejects unknown credentials', async () => {
    const write = await h.putSnapshot({ revision: 1, body: snapshotBody(), token: 'nope' });
    expect(write.status).toBe(401);
    expect((await json(write)).error.code).toBe('unauthorized');
  });
});

describe('scope enforcement', () => {
  it('denies a read-only credential on the write route', async () => {
    const response = await h.putSnapshot({ revision: 1, body: snapshotBody(), token: 'read-a1' });
    expect(response.status).toBe(403);
    expect((await json(response)).error.code).toBe('forbidden_scope');
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(0);
  });

  it('denies a write credential on the read route', async () => {
    const response = await h.getDaily('?from=2026-11-01&to=2026-11-30', 'write-a1');
    expect(response.status).toBe(403);
    expect((await json(response)).error.code).toBe('forbidden_scope');
  });

  it('denies writing to a collector the credential is not scoped to', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody(),
      collectorId: 'col-a2',
      token: 'write-a1',
    });
    expect(response.status).toBe(403);
    expect((await json(response)).error.code).toBe('forbidden_collector');
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(0);
  });

  it('denies writing to another owner’s collector', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody(),
      collectorId: 'col-a1',
      token: 'write-b1',
    });
    expect(response.status).toBe(403);
    expect((await json(response)).error.code).toBe('forbidden_collector');
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(0);
  });

  it('rejects a collector id outside the credential binding without consulting the database', async () => {
    // The binding is checked before the lookup, so an id that does not exist is
    // answered exactly like one that exists under another credential.
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody(),
      collectorId: 'col-missing',
      token: 'write-a1',
    });
    expect(response.status).toBe(403);
    expect((await json(response)).error.code).toBe('forbidden_collector');
  });
});

describe('read ownership', () => {
  it('never returns another owner’s days', async () => {
    expect(
      (
        await h.putSnapshot({
          revision: 1,
          body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
        })
      ).status,
    ).toBe(200);
    expect(
      (
        await h.putSnapshot({
          revision: 1,
          body: snapshotBody({ generation: 1, days: [{ date: '2026-11-18', totalTokens: 7 }] }),
          collectorId: 'col-b1',
          token: 'write-b1',
        })
      ).status,
    ).toBe(200);

    const ownerA = await h.getDaily('?from=2026-11-01&to=2026-11-30', 'read-a1');
    expect((await json(ownerA)).days).toEqual([
      { date: '2026-11-18', collectorId: 'col-a1', totalTokens: 100 },
    ]);

    const ownerB = await h.getDaily('?from=2026-11-01&to=2026-11-30', 'read-b1');
    expect((await json(ownerB)).days).toEqual([
      { date: '2026-11-18', collectorId: 'col-b1', totalTokens: 7 },
    ]);
  });

  it('answers another owner’s collector exactly like an unknown collector', async () => {
    const response = await h.getDaily('?from=2026-11-01&to=2026-11-30&collector=col-a1', 'read-b1');
    // Owner-filtered lookup: probing another owner's id is indistinguishable
    // from probing an id that does not exist.
    expect(response.status).toBe(404);
    expect((await json(response)).error.code).toBe('collector_not_found');
  });

  it('filters by a collector the caller owns', async () => {
    expect(
      (
        await h.putSnapshot({
          revision: 1,
          body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
        })
      ).status,
    ).toBe(200);
    expect(
      (
        await h.putSnapshot({
          revision: 1,
          body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 5 }] }),
          collectorId: 'col-a2',
          token: 'write-a2',
        })
      ).status,
    ).toBe(200);

    const all = await h.getDaily('?from=2026-11-01&to=2026-11-30');
    expect((await json(all)).totalTokens).toBe(105);

    const onlyA2 = await h.getDaily('?from=2026-11-01&to=2026-11-30&collector=col-a2');
    expect((await json(onlyA2)).days).toEqual([
      { date: '2026-11-18', collectorId: 'col-a2', totalTokens: 5 },
    ]);
  });
});
