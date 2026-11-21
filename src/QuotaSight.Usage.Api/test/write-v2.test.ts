/**
 * Wire v2 write contract.
 *
 * v2 is the same PUT route as v1 with a different body shape (`schemaVersion:
 * 2`), so these tests pin what "same route, second shape" means: the v1 keys
 * keep their names and order, the v2 keys are added after them, and the daily
 * total and the per-key breakdown are kept under their own monotonic rules.
 */
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import {
  createHarness,
  FIXTURE,
  json,
  partRows,
  resetData,
  scalar,
  snapshotBody,
  snapshotBodyV2,
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

describe('PUT with schemaVersion 2', () => {
  it('writes the day total and its breakdown and answers with the v2 receipt', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({
        keys: [
          ['codex', 'openai', 'gpt-5.1'],
          ['gemini', 'anthropic', 'claude-sonnet-4-5'],
        ],
        days: [
          {
            date: '2026-11-17',
            totalTokens: 340,
            parts: [
              [0, 60, 10, 10, 5, 15],
              [1, 200, 20, 10, 0, 10],
            ],
          },
        ],
      }),
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
      'changedParts',
      'ignoredPartReductions',
    ]);
    expect(receipt).toMatchObject({
      status: 'applied',
      collectorId: FIXTURE.collectorA1,
      generation: 1,
      revision: 1,
      changedDays: ['2026-11-17'],
      ignoredReductions: 0,
      changedParts: 2,
      ignoredPartReductions: 0,
    });

    expect((await usageRows(h.db)).map((row) => [row.bucket_date, row.total_tokens])).toEqual([
      ['2026-11-17', 340],
    ]);
    expect(
      (await partRows(h.db)).map((row) => [
        row.client,
        row.provider,
        row.model,
        row.input_tokens,
        row.output_tokens,
        row.cache_read_tokens,
        row.cache_write_tokens,
        row.reasoning_tokens,
        row.total_tokens,
      ]),
    ).toEqual([
      ['codex', 'openai', 'gpt-5.1', 60, 10, 10, 5, 15, 100],
      ['gemini', 'anthropic', 'claude-sonnet-4-5', 200, 20, 10, 0, 10, 240],
    ]);

    // The receipt is written in the same transaction with both reduction counts.
    expect(
      await scalar(
        h.db,
        'SELECT ignored_part_reductions AS value FROM snapshot_receipts WHERE collector_id = ? AND generation = ? AND revision = ?',
        FIXTURE.collectorA1,
        1,
        1,
      ),
    ).toBe(0);
  });

  it('accepts a day without parts and stores no breakdown row for it', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({
        keys: [['codex', 'openai', 'gpt-5.1']],
        days: [
          { date: '2026-11-17', totalTokens: 100, parts: [[0, 60, 10, 10, 5, 15]] },
          { date: '2026-11-18', totalTokens: 250 },
        ],
      }),
    });

    expect(response.status).toBe(200);
    expect((await json(response)).changedParts).toBe(1);
    expect((await usageRows(h.db)).map((row) => [row.bucket_date, row.total_tokens])).toEqual([
      ['2026-11-17', 100],
      ['2026-11-18', 250],
    ]);
    expect((await partRows(h.db)).map((row) => row.bucket_date)).toEqual(['2026-11-17']);
  });

  it('counts a replay as a replay and reuses the stored reduction counts', async () => {
    const body = snapshotBodyV2({
      days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 60, 10, 10, 5, 15]] }],
    });
    const first = await h.putSnapshot({ revision: 4, body });
    expect((await json(first)).status).toBe('applied');

    const second = await h.putSnapshot({ revision: 4, body });
    expect(second.status).toBe(200);
    expect(await json(second)).toMatchObject({
      status: 'replayed',
      revision: 4,
      changedDays: [],
      changedParts: 0,
      ignoredPartReductions: 0,
    });
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_part')).toBe(1);
  });

  it('is not the same revision as a v1 body with the same days', async () => {
    const v1 = await h.putSnapshot({
      revision: 7,
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
    });
    expect(v1.status).toBe(200);

    const v2 = await h.putSnapshot({
      revision: 7,
      body: snapshotBodyV2({
        days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 60, 10, 10, 5, 15]] }],
      }),
    });
    expect(v2.status).toBe(409);
    expect((await json(v2)).error.code).toBe('revision_conflict');
  });
});

describe('v2 monotonic rules', () => {
  it('ignores a per-key reduction and reports it, keeping the accepted components', async () => {
    await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({
        days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 60, 10, 10, 5, 15]] }],
      }),
    });

    const response = await h.putSnapshot({
      revision: 2,
      body: snapshotBodyV2({
        days: [{ date: '2026-11-18', totalTokens: 50, parts: [[0, 30, 10, 5, 0, 5]] }],
      }),
    });

    expect(response.status).toBe(200);
    const receipt = await json(response);
    expect(receipt).toMatchObject({
      changedDays: [],
      changedParts: 0,
      ignoredReductions: 1,
      ignoredPartReductions: 1,
    });
    expect((await partRows(h.db)).map((row) => row.total_tokens)).toEqual([100]);
    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([100]);
  });

  it('replaces all five components when the key total grows', async () => {
    await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({
        days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 60, 10, 10, 5, 15]] }],
      }),
    });
    const before = (await partRows(h.db))[0]!;

    const response = await h.putSnapshot({
      revision: 2,
      body: snapshotBodyV2({
        days: [
          {
            date: '2026-11-18',
            // 90 + 20 + 0 + 0 + 10 = 120 > 100, and every component is different:
            // the whole row is replaced, not merged per column.
            totalTokens: 120,
            parts: [[0, 90, 20, 0, 0, 10]],
          },
        ],
      }),
    });

    expect(response.status).toBe(200);
    expect(await json(response)).toMatchObject({ changedParts: 1, ignoredPartReductions: 0 });
    const after = (await partRows(h.db))[0]!;
    expect([after.input_tokens, after.output_tokens, after.cache_read_tokens, after.cache_write_tokens, after.reasoning_tokens])
      .toEqual([90, 20, 0, 0, 10]);
    expect(after.total_tokens).toBe(120);
    expect(after.updated_at >= before.updated_at).toBe(true);
  });

  it('does nothing at all for an unchanged key', async () => {
    const body = snapshotBodyV2({
      days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 60, 10, 10, 5, 15]] }],
    });
    await h.putSnapshot({ revision: 1, body });
    const before = (await partRows(h.db))[0]!;

    const response = await h.putSnapshot({ revision: 2, body });

    expect(response.status).toBe(200);
    expect(await json(response)).toMatchObject({
      changedDays: [],
      changedParts: 0,
      ignoredReductions: 0,
      ignoredPartReductions: 0,
    });
    const after = (await partRows(h.db))[0]!;
    expect(after).toEqual(before);
  });

  it('keeps the day total and the breakdown sum under separate maxima', async () => {
    // Day total already accepted at 300 with one key at 200.
    await h.putSnapshot({
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
              [0, 150, 30, 10, 5, 5],
              [1, 80, 10, 5, 0, 5],
            ],
          },
        ],
      }),
    });

    // The second key grows while the first shrinks, so the incoming day total is
    // still 300 and the day itself does not move. The stored rows do not add up
    // to 300 afterwards, because each row is kept at its own accepted maximum:
    // that divergence is what the read API reports as coverage.
    const response = await h.putSnapshot({
      revision: 2,
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
              [0, 100, 10, 5, 0, 5],
              [1, 120, 40, 10, 5, 5],
            ],
          },
        ],
      }),
    });

    expect(response.status).toBe(200);
    expect(await json(response)).toMatchObject({
      changedDays: [],
      changedParts: 1,
      ignoredPartReductions: 1,
    });
    expect((await partRows(h.db)).map((row) => [row.model, row.total_tokens])).toEqual([
      ['gpt-5.1', 200],
      ['claude-sonnet-4-5', 180],
    ]);
    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([300]);
  });

  it('rolls back the breakdown rows of a stale revision', async () => {
    await h.putSnapshot({
      revision: 9,
      body: snapshotBodyV2({
        days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 60, 10, 10, 5, 15]] }],
      }),
    });

    const stale = await h.putSnapshot({
      revision: 8,
      body: snapshotBodyV2({
        days: [
          { date: '2026-11-18', totalTokens: 500, parts: [[0, 500, 0, 0, 0, 0]] },
          { date: '2026-11-19', totalTokens: 700, parts: [[0, 700, 0, 0, 0, 0]] },
        ],
      }),
    });

    expect(stale.status).toBe(409);
    expect((await json(stale)).error.code).toBe('stale_revision');
    // Neither the day rows nor the breakdown rows of the rejected snapshot exist.
    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([100]);
    expect((await partRows(h.db)).map((row) => [row.bucket_date, row.total_tokens])).toEqual([
      ['2026-11-18', 100],
    ]);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
  });

  it('refuses a generation that is not active', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({
        generation: 2,
        days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 60, 10, 10, 5, 15]] }],
      }),
    });

    expect(response.status).toBe(409);
    expect((await json(response)).error.code).toBe('generation_inactive');
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_part')).toBe(0);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(0);
  });
});

/** Zero-padded so the generated key list is also in ascending order. */
function key(index: number): string {
  return String(index).padStart(3, '0');
}

describe('wire v2 body validation', () => {
  async function rejection(body: unknown): Promise<{ status: number; code: string; field: string }> {
    const response = await h.putSnapshot({ revision: 1, body });
    const parsed = await json(response);
    return {
      status: response.status,
      code: parsed.error.code,
      field: parsed.error.details?.field,
    };
  }

  it('rejects a body that keeps the v1 shape but claims version 2', async () => {
    const body = snapshotBody({ schemaVersion: 2 });
    expect(await rejection(body)).toEqual({
      status: 400,
      code: 'invalid_snapshot',
      field: 'keys',
    });
  });

  it('rejects keys that are not ascending', async () => {
    expect(
      await rejection(
        snapshotBodyV2({
          keys: [
            ['zulu', '', 'model'],
            ['alpha', '', 'model'],
          ],
        }),
      ),
    ).toMatchObject({ status: 400, field: 'keys' });
  });

  it('rejects a duplicated key', async () => {
    expect(
      await rejection(
        snapshotBodyV2({
          keys: [
            ['codex', 'openai', 'gpt-5.1'],
            ['codex', 'openai', 'gpt-5.1'],
          ],
        }),
      ),
    ).toMatchObject({ status: 400, field: 'keys' });
  });

  it('rejects a key tuple that is not three strings', async () => {
    expect(await rejection(snapshotBodyV2({ keys: [['codex', 'openai']] }))).toMatchObject({
      status: 400,
      field: 'keys[0]',
    });
  });

  it('rejects a key that no part references', async () => {
    expect(
      await rejection(
        snapshotBodyV2({
          keys: [
            ['codex', 'openai', 'gpt-5.1'],
            ['gemini', 'anthropic', 'claude-sonnet-4-5'],
          ],
        }),
      ),
    ).toMatchObject({ status: 400, field: 'keys[1]' });
  });

  it('rejects a client that was not normalized to the wire pattern', async () => {
    expect(
      await rejection(snapshotBodyV2({ keys: [['Codex', 'openai', 'gpt-5.1']] })),
    ).toMatchObject({ status: 400, field: 'keys[0][0]' });
  });

  it('rejects a provider list that is not the normalized form', async () => {
    expect(
      await rejection(snapshotBodyV2({ keys: [['codex', 'OpenAI', 'gpt-5.1']] })),
    ).toMatchObject({ status: 400, field: 'keys[0][1]' });
  });

  it('accepts a bracketed model identifier and rejects a path-shaped one', async () => {
    const bracketed = await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({ keys: [['codex', 'openai', 'claude-sonnet-5-5[1m]']] }),
    });
    expect(bracketed.status).toBe(200);

    await resetData(h.db);
    const path = await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({ keys: [['codex', 'openai', '/models/llama-3-8b.gguf']] }),
    });
    expect(path.status).toBe(400);
    expect((await json(path)).error.details?.field).toBe('keys[0][2]');
  });

  it('rejects parts that are empty, unsorted or duplicated by key index', async () => {
    expect(
      await rejection(
        snapshotBodyV2({ days: [{ date: '2026-11-18', totalTokens: 0, parts: [] }] }),
      ),
    ).toMatchObject({ status: 400, field: 'days[0].parts' });

    expect(
      await rejection(
        snapshotBodyV2({
          keys: [
            ['a', '', 'm'],
            ['b', '', 'm'],
          ],
          days: [
            {
              date: '2026-11-18',
              totalTokens: 20,
              parts: [
                [1, 10, 0, 0, 0, 0],
                [0, 10, 0, 0, 0, 0],
              ],
            },
          ],
        }),
      ),
    ).toMatchObject({ status: 400, field: 'days[0].parts[1][0]' });
  });

  it('rejects a part tuple that does not have six integers', async () => {
    expect(
      await rejection(
        snapshotBodyV2({ days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 60, 10, 10, 5]] }] }),
      ),
    ).toMatchObject({ status: 400, field: 'days[0].parts[0]' });

    expect(
      await rejection(
        snapshotBodyV2({
          days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 60, 10, 10, 5, -1]] }],
        }),
      ),
    ).toMatchObject({ status: 400, field: 'days[0].parts[0][5]' });

    expect(
      await rejection(
        snapshotBodyV2({
          days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 60, 10, 10, 5, 1.5]] }],
        }),
      ),
    ).toMatchObject({ status: 400, field: 'days[0].parts[0][5]' });
  });

  it('rejects parts whose components do not add up to the day total', async () => {
    expect(
      await rejection(
        snapshotBodyV2({ days: [{ date: '2026-11-18', totalTokens: 99, parts: [[0, 60, 10, 10, 5, 15]] }] }),
      ),
    ).toMatchObject({ status: 400, field: 'days[0].totalTokens' });
  });

  it('rejects a part that indexes a key that does not exist', async () => {
    expect(
      await rejection(
        snapshotBodyV2({ days: [{ date: '2026-11-18', totalTokens: 100, parts: [[4, 60, 10, 10, 5, 15]] }] }),
      ),
    ).toMatchObject({ status: 400, field: 'days[0].parts[0][0]' });
  });

  it('rejects more than 128 keys', async () => {
    const keys = Array.from({ length: 129 }, (_, index) => [`client-${key(index)}`, '', 'model']);
    const parts = keys.map((_, index) => [index, 1, 0, 0, 0, 0]);
    expect(
      await rejection(
        snapshotBodyV2({
          keys,
          days: [{ date: '2026-11-18', totalTokens: 129, parts }],
        }),
      ),
    ).toMatchObject({ status: 400, field: 'keys' });
  });

  it('rejects more than 32 parts in one day', async () => {
    const keys = Array.from({ length: 33 }, (_, index) => [`client-${key(index)}`, '', 'model']);
    const parts = keys.map((_, index) => [index, 1, 0, 0, 0, 0]);
    expect(
      await rejection(
        snapshotBodyV2({ keys, days: [{ date: '2026-11-18', totalTokens: 33, parts }] }),
      ),
    ).toMatchObject({ status: 400, field: 'days[0].parts' });
  });

  it('rejects more than 1024 parts in one snapshot', async () => {
    // 33 days x 32 parts is 1056 rows: each day is within its own bound, the
    // snapshot as a whole is not.
    const keys = Array.from({ length: 32 }, (_, index) => [`client-${key(index)}`, '', 'model']);
    const parts = keys.map((_, index) => [index, 1, 0, 0, 0, 0]);
    const days = Array.from({ length: 33 }, (_, index) => {
      const date = new Date(Date.UTC(2026, 0, 1 + index)).toISOString().slice(0, 10);
      return { date, totalTokens: 32, parts };
    });
    expect(await rejection(snapshotBodyV2({ keys, days }))).toMatchObject({
      status: 400,
      field: 'days',
    });
  });

  it('rejects an unexpected v2 field', async () => {
    expect(await rejection(snapshotBodyV2({ summary: {} }))).toMatchObject({
      status: 400,
      field: '$',
    });
  });
});
