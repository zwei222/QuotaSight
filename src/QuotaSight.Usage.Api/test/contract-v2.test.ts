/**
 * Cross-lane contract check for wire v2.
 *
 * `fixtures/wire-v2-golden.json` is the one byte sequence that both sides of the
 * wire agree on: the API test below stores it as if a collector had sent it, and
 * the collector's own test asserts that its serializer produces exactly these
 * bytes. Either side drifting fails here or there, so the two lanes cannot
 * diverge silently.
 */
import { readFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import {
  createHarness,
  FIXTURE,
  json,
  partRows,
  resetData,
  snapshotBody,
  usageRows,
  type Harness,
} from './harness';

const fixturePath = join(
  resolve(dirname(fileURLToPath(import.meta.url))),
  'fixtures',
  'wire-v2-golden.json',
);

let h: Harness;
let golden: string;

beforeAll(async () => {
  h = await createHarness();
  golden = await readFile(fixturePath, 'utf8');
}, 60_000);

afterAll(async () => {
  await h.dispose();
});

beforeEach(async () => {
  await resetData(h.db);
});

describe('wire v2 golden fixture', () => {
  it('is one compact JSON object with a single trailing newline', () => {
    expect(golden.endsWith('}\n')).toBe(true);
    expect(golden.trimEnd().includes('\n')).toBe(false);
    expect(golden).toBe(JSON.stringify(JSON.parse(golden)) + '\n');
  });

  it('is accepted unchanged and stored exactly as it describes', async () => {
    const body = JSON.parse(golden) as {
      generation: number;
      keys: string[][];
      days: Array<{ date: string; totalTokens: number; parts?: number[][] }>;
    };

    const response = await h.putSnapshot({ revision: 3, raw: golden });
    expect(response.status).toBe(200);
    const receipt = await json(response);
    expect(receipt).toMatchObject({
      status: 'applied',
      collectorId: FIXTURE.collectorA1,
      generation: body.generation,
      revision: 3,
      changedDays: body.days.map((day) => day.date),
      ignoredReductions: 0,
      changedParts: body.days.reduce((total, day) => total + (day.parts?.length ?? 0), 0),
      ignoredPartReductions: 0,
    });

    expect((await usageRows(h.db)).map((row) => [row.bucket_date, row.total_tokens])).toEqual(
      body.days.map((day) => [day.date, day.totalTokens]),
    );

    const stored = await partRows(h.db);
    const expected: Array<Array<string | number>> = [];
    for (const day of body.days) {
      for (const part of day.parts ?? []) {
        const [index, input, output, cacheRead, cacheWrite, reasoning] = part as number[];
        const key = body.keys[index as number] as string[];
        expected.push([
          day.date,
          key[0] as string,
          key[1] as string,
          key[2] as string,
          input as number,
          output as number,
          cacheRead as number,
          cacheWrite as number,
          reasoning as number,
          (input as number) + (output as number) + (cacheRead as number) + (cacheWrite as number) + (reasoning as number),
        ]);
      }
    }
    expected.sort((left, right) => (left.join('\u0000') < right.join('\u0000') ? -1 : 1));

    expect(
      stored.map((row) => [
        row.bucket_date,
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
    ).toEqual(expected);
  });

  it('covers the normalization decisions the collector makes', () => {
    const body = JSON.parse(golden) as { keys: string[][] };

    // A mixed-case client is folded to lower case, exactly like provider and model.
    expect(body.keys).toContainEqual(['conduit', 'anthropic,openai', 'claude-sonnet-5-5[1m]']);
    // A path-shaped model is withheld, and its provider was not supplied.
    expect(body.keys).toContainEqual(['codex', '', '']);
    expect(body.keys).toContainEqual(['codex', 'openai', 'gpt-5.1']);
  });
});

describe('wire v1 response bytes', () => {
  it('keeps the v1 receipt byte for byte', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 100 }] }),
    });
    const text = await response.text();
    const receivedAt = (JSON.parse(text) as { receivedAt: string }).receivedAt;

    expect(text).toBe(
      `{"status":"applied","collectorId":"${FIXTURE.collectorA1}","generation":1,"revision":1,` +
        `"receivedAt":"${receivedAt}","changedDays":["2026-11-18"],"ignoredReductions":0}`,
    );
  });

  it('leaves the v1 daily read untouched', async () => {
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
});
