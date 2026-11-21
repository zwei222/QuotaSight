/**
 * Wire v2 write contract.
 *
 * v2 is the same PUT route as v1 with a different body shape (`schemaVersion:
 * 2`), so these tests pin what "same route, second shape" means: the v1 keys
 * keep their names and order, and the v2 keys are added after them.
 *
 * The breakdown is stored as one *set of rows per day*, not as one maximum per
 * row. A day's rows are replaced as a whole — deleted, then re-inserted — and
 * only when the incoming snapshot attributes more tokens to that day than are
 * stored. A day whose total is equal or smaller changes nothing at all, so the
 * keys a day carries can never be mixed between two snapshots.
 */
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { readFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
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

/** The identifier boundaries both sides of the wire test against. */
const identifierRulesPath = join(
  resolve(dirname(fileURLToPath(import.meta.url))),
  'fixtures',
  'identifier-rules.json',
);

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

describe('v2 day totals and day breakdown sets', () => {
  it('ignores a smaller day total and reports it, keeping the accepted day', async () => {
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

  /**
   * The reproduction an independent review measured: a re-scan that keeps the
   * day total but moves tokens from one model to another. Per-row maxima added
   * the two explanations together (100 + 60 = 160 attributed against a day total
   * of 100). A day total that does not grow is not a new explanation, so the
   * accepted set is left exactly as it was.
   */
  it('keeps the accepted set when the day total is unchanged and only its components moved', async () => {
    await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({
        keys: [['codex', 'openai', 'gpt-5.1']],
        days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 100, 0, 0, 0, 0]] }],
      }),
    });

    const response = await h.putSnapshot({
      revision: 2,
      body: snapshotBodyV2({
        keys: [
          ['codex', 'openai', 'gpt-5.1'],
          ['codex', 'openai', 'gpt-5.1-mini'],
        ],
        days: [
          {
            date: '2026-11-18',
            totalTokens: 100,
            parts: [
              [0, 40, 0, 0, 0, 0],
              [1, 60, 0, 0, 0, 0],
            ],
          },
        ],
      }),
    });

    expect(response.status).toBe(200);
    expect(await json(response)).toMatchObject({
      changedDays: [],
      changedParts: 0,
      ignoredReductions: 0,
      // An equal total is not a reduction, so it is not counted either.
      ignoredPartReductions: 0,
    });

    const rows = await partRows(h.db);
    expect(rows.map((row) => [row.model, row.total_tokens])).toEqual([['gpt-5.1', 100]]);
    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([100]);

    const breakdown = await json(await h.getBreakdown('?from=2026-11-01&to=2026-11-30'));
    expect(breakdown.parts.map((part: { model: string; total: number }) => [part.model, part.total])).toEqual([
      ['gpt-5.1', 100],
    ]);
    expect(breakdown.coverage).toEqual([
      { date: '2026-11-18', collectorId: FIXTURE.collectorA1, totalTokens: 100, attributedTokens: 100 },
    ]);
  });

  it('replaces the whole set of a day whose total grew', async () => {
    await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({
        keys: [['codex', 'openai', 'gpt-5.1']],
        days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 90, 10, 0, 0, 0]] }],
      }),
    });

    const response = await h.putSnapshot({
      revision: 2,
      body: snapshotBodyV2({
        keys: [
          ['codex', 'openai', 'gpt-5.1'],
          ['codex', 'openai', 'gpt-5.1-mini'],
        ],
        days: [
          {
            // 60 + 100 = 160 > 100: the day is explained again, by a set that no
            // longer contains what the first snapshot claimed for gpt-5.1 alone.
            date: '2026-11-18',
            totalTokens: 160,
            parts: [
              [0, 50, 10, 0, 0, 0],
              [1, 90, 10, 0, 0, 0],
            ],
          },
        ],
      }),
    });

    expect(response.status).toBe(200);
    expect(await json(response)).toMatchObject({
      changedDays: ['2026-11-18'],
      changedParts: 2,
      ignoredReductions: 0,
      ignoredPartReductions: 0,
    });

    // The old row is gone, not merged: the day carries exactly two rows whose
    // components are the ones this snapshot sent.
    expect(
      (await partRows(h.db)).map((row) => [row.model, row.input_tokens, row.output_tokens, row.total_tokens]),
    ).toEqual([
      ['gpt-5.1', 50, 10, 60],
      ['gpt-5.1-mini', 90, 10, 100],
    ]);
    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([160]);
  });

  it('ignores a smaller day breakdown as a whole, without adding its new keys', async () => {
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

    const response = await h.putSnapshot({
      revision: 2,
      body: snapshotBodyV2({
        keys: [
          ['codex', 'openai', 'gpt-5.1'],
          ['gemini', 'anthropic', 'claude-sonnet-4-5'],
          ['mistral', '', 'mistral-large'],
        ],
        days: [
          {
            date: '2026-11-18',
            totalTokens: 200,
            parts: [
              [0, 10, 0, 0, 0, 0],
              [1, 10, 0, 0, 0, 0],
              [2, 180, 0, 0, 0, 0],
            ],
          },
        ],
      }),
    });

    expect(response.status).toBe(200);
    expect(await json(response)).toMatchObject({
      changedDays: [],
      changedParts: 0,
      // The day total of 300 is kept, so the day is a reduction for both counts.
      ignoredReductions: 1,
      ignoredPartReductions: 1,
    });
    // Not even the key the day did not carry before is added: a day's set is
    // accepted or ignored as a whole.
    expect((await partRows(h.db)).map((row) => [row.model, row.total_tokens])).toEqual([
      ['gpt-5.1', 200],
      ['claude-sonnet-4-5', 100],
    ]);
    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([300]);
  });

  it('does nothing at all for an identical day', async () => {
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

  it('decides each day of a snapshot on its own', async () => {
    await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({
        days: [
          { date: '2026-11-17', totalTokens: 100, parts: [[0, 100, 0, 0, 0, 0]] },
          { date: '2026-11-18', totalTokens: 400, parts: [[0, 400, 0, 0, 0, 0]] },
        ],
      }),
    });

    const response = await h.putSnapshot({
      revision: 2,
      body: snapshotBodyV2({
        days: [
          // Grew: replaced.
          { date: '2026-11-17', totalTokens: 120, parts: [[0, 120, 0, 0, 0, 0]] },
          // Shrank: ignored, and counted once for this day.
          { date: '2026-11-18', totalTokens: 300, parts: [[0, 300, 0, 0, 0, 0]] },
        ],
      }),
    });

    expect(response.status).toBe(200);
    expect(await json(response)).toMatchObject({
      changedDays: ['2026-11-17'],
      changedParts: 1,
      ignoredReductions: 1,
      ignoredPartReductions: 1,
    });
    expect((await partRows(h.db)).map((row) => [row.bucket_date, row.total_tokens])).toEqual([
      ['2026-11-17', 120],
      ['2026-11-18', 400],
    ]);
  });

  it('keeps a day total that grew while the day carried no breakdown', async () => {
    await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({
        days: [{ date: '2026-11-18', totalTokens: 100, parts: [[0, 100, 0, 0, 0, 0]] }],
      }),
    });

    // No parts array: this says nothing about the breakdown, so the stored set
    // stays and is not counted as a reduction either.
    const response = await h.putSnapshot({
      revision: 2,
      body: snapshotBodyV2({
        keys: [],
        days: [{ date: '2026-11-18', totalTokens: 250 }],
      }),
    });

    expect(response.status).toBe(200);
    expect(await json(response)).toMatchObject({
      changedDays: ['2026-11-18'],
      changedParts: 0,
      ignoredReductions: 0,
      ignoredPartReductions: 0,
    });
    expect((await partRows(h.db)).map((row) => row.total_tokens)).toEqual([100]);
    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([250]);
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
    // Neither the day rows nor the breakdown rows of the rejected snapshot exist,
    // and the replacement the rejected snapshot asked for did not happen: the
    // delete and the insert are part of the same transaction as the receipt.
    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([100]);
    expect((await partRows(h.db)).map((row) => [row.bucket_date, row.total_tokens])).toEqual([
      ['2026-11-18', 100],
    ]);
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(1);
  });

  it('stores no row for a day whose parts are all zero', async () => {
    // A zero-token breakdown adds up to a zero day total, so there is nothing to
    // attribute: no row is stored and nothing is counted as a reduction.
    const body = snapshotBodyV2({
      keys: [['codex', 'openai', 'gpt-5.1']],
      days: [{ date: '2026-11-18', totalTokens: 0, parts: [[0, 0, 0, 0, 0, 0]] }],
    });

    const first = await h.putSnapshot({ revision: 1, body });
    expect(first.status).toBe(200);
    expect(await json(first)).toMatchObject({ changedParts: 0, ignoredPartReductions: 0 });

    const second = await h.putSnapshot({ revision: 2, body });
    expect(second.status).toBe(200);
    expect(await json(second)).toMatchObject({ changedParts: 0, ignoredPartReductions: 0 });
    expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_part')).toBe(0);
    expect((await usageRows(h.db)).map((row) => row.total_tokens)).toEqual([0]);
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

  /**
   * The provider element bound is shared with the collector, which accepts an
   * element of up to 64 characters. The boundary cases come from
   * `fixtures/identifier-rules.json`, which the collector's own tests read as
   * well, so the two sides cannot drift to different lengths.
   */
  it('accepts the provider element lengths the shared rule accepts and rejects the rest', async () => {
    const rules = JSON.parse(await readFile(identifierRulesPath, 'utf8'));
    const accepted: number[] = rules.providerElementLengths.accepted;
    const rejected: number[] = rules.providerElementLengths.rejected;
    let revision = 0;

    for (const length of accepted) {
      revision += 1;
      const response = await h.putSnapshot({
        revision,
        body: snapshotBodyV2({ keys: [['codex', 'p'.repeat(length), 'gpt-5.1']] }),
      });
      expect(response.status, `provider element of ${length} characters`).toBe(200);
    }

    for (const length of rejected) {
      revision += 1;
      const response = await h.putSnapshot({
        revision,
        body: snapshotBodyV2({ keys: [['codex', 'p'.repeat(length), 'gpt-5.1']] }),
      });
      expect(response.status, `provider element of ${length} characters`).toBe(400);
      expect((await json(response)).error.details?.field).toBe('keys[0][1]');
    }
  });

  it('rejects a local weight file as a model but keeps the org/model form', async () => {
    const rules = JSON.parse(await readFile(identifierRulesPath, 'utf8'));

    for (const suffix of rules.modelFileSuffixes as string[]) {
      const response = await h.putSnapshot({
        revision: 1,
        body: snapshotBodyV2({ keys: [['codex', 'openai', `model${suffix}`]] }),
      });
      expect(response.status, `model${suffix}`).toBe(400);
      expect((await json(response)).error.details?.field).toBe('keys[0][2]');
    }

    const scoped = await h.putSnapshot({
      revision: 1,
      body: snapshotBodyV2({ keys: [['codex', 'openai', rules.acceptedModelForm as string]] }),
    });
    expect(scoped.status).toBe(200);
  });
});
