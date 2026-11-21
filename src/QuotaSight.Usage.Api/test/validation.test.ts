import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { createHarness, json, resetData, scalar, snapshotBody, type Harness } from './harness';

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

async function expectRejected(
  response: Response,
  status: number,
  code: string,
  field?: string,
): Promise<void> {
  expect(response.status).toBe(status);
  const body = await json(response);
  expect(body.error.code).toBe(code);
  if (field !== undefined) {
    expect(body.error.details?.field).toBe(field);
  }
  expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM snapshot_receipts')).toBe(0);
  expect(await scalar(h.db, 'SELECT COUNT(*) AS value FROM usage_day')).toBe(0);
}

/** A day list spanning `count` consecutive dates from `start`. */
function consecutiveDays(start: string, count: number) {
  const base = Date.parse(`${start}T00:00:00Z`);
  return Array.from({ length: count }, (_, index) => ({
    date: new Date(base + index * 86_400_000).toISOString().slice(0, 10),
    totalTokens: index + 1,
  }));
}

describe('snapshot body validation', () => {
  const cases: Array<{ name: string; body: unknown; field?: string }> = [
    {
      name: 'rejects an unsupported schemaVersion',
      body: snapshotBody({ schemaVersion: 3 }),
      field: 'schemaVersion',
    },
    {
      name: 'rejects a different bucket time zone',
      body: snapshotBody({ bucketTimeZone: 'UTC' }),
      field: 'bucketTimeZone',
    },
    {
      name: 'rejects fields outside the v1 contract',
      body: snapshotBody({ sourceStates: [] }),
      field: '$',
    },
    {
      name: 'rejects a missing days array',
      body: (() => {
        const body = snapshotBody();
        delete body['days'];
        return body;
      })(),
      field: 'days',
    },
    { name: 'rejects an empty days array', body: snapshotBody({ days: [] }), field: 'days' },
    { name: 'rejects days that are not an array', body: snapshotBody({ days: 'x' }), field: 'days' },
    {
      name: 'rejects an impossible calendar date',
      body: snapshotBody({
        days: [{ date: '2026-02-30', totalTokens: 1 }],
        coveredRange: { from: '2026-02-01', to: '2026-02-28' },
      }),
      field: 'days[0].date',
    },
    {
      name: 'rejects a non-padded date',
      body: snapshotBody({
        days: [{ date: '2026-1-5', totalTokens: 1 }],
        coveredRange: { from: '2026-01-01', to: '2026-01-31' },
      }),
      field: 'days[0].date',
    },
    {
      name: 'rejects negative tokens',
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: -1 }] }),
      field: 'days[0].totalTokens',
    },
    {
      name: 'rejects fractional tokens',
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 1.5 }] }),
      field: 'days[0].totalTokens',
    },
    {
      name: 'rejects unsafe integer tokens',
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 2 ** 53 }] }),
      field: 'days[0].totalTokens',
    },
    {
      name: 'rejects tokens sent as a string',
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: '100' }] }),
      field: 'days[0].totalTokens',
    },
    {
      name: 'rejects boolean tokens',
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: true }] }),
      field: 'days[0].totalTokens',
    },
    {
      name: 'rejects duplicate dates',
      body: snapshotBody({
        days: [
          { date: '2026-11-18', totalTokens: 1 },
          { date: '2026-11-18', totalTokens: 2 },
        ],
      }),
      field: 'days',
    },
    {
      name: 'rejects unsorted dates',
      body: snapshotBody({
        days: [
          { date: '2026-11-19', totalTokens: 1 },
          { date: '2026-11-18', totalTokens: 2 },
        ],
        coveredRange: { from: '2026-11-17', to: '2026-11-20' },
      }),
      field: 'days',
    },
    {
      name: 'rejects a day outside the covered range',
      body: snapshotBody({
        days: [{ date: '2026-11-18', totalTokens: 1 }],
        coveredRange: { from: '2026-11-01', to: '2026-11-02' },
      }),
      field: 'days[0].date',
    },
    {
      name: 'rejects extra keys on a day',
      body: snapshotBody({ days: [{ date: '2026-11-18', totalTokens: 1, client: 'codex' }] }),
      field: 'days[0]',
    },
    { name: 'rejects a non-numeric generation', body: snapshotBody({ generation: '1' }), field: 'generation' },
    { name: 'rejects a zero generation', body: snapshotBody({ generation: 0 }), field: 'generation' },
    {
      name: 'rejects an unsafe generation',
      body: snapshotBody({ generation: 2 ** 53 }),
      field: 'generation',
    },
    {
      name: 'rejects collectedAt without a time',
      body: snapshotBody({ collectedAt: '2026-11-20' }),
      field: 'collectedAt',
    },
    {
      name: 'rejects collectedAt with an offset instead of UTC',
      body: snapshotBody({ collectedAt: '2026-11-20T03:00:00+09:00' }),
      field: 'collectedAt',
    },
    {
      name: 'rejects an incomplete covered range',
      body: snapshotBody({ coveredRange: { from: '2026-11-18' } }),
      field: 'coveredRange.to',
    },
    {
      name: 'rejects an inverted covered range',
      body: snapshotBody({ coveredRange: { from: '2026-11-19', to: '2026-11-18' } }),
      field: 'coveredRange',
    },
    {
      name: 'rejects a non-normalized adapter version',
      body: snapshotBody({ adapterVersion: 'Tokscale/4.23.0' }),
      field: 'adapterVersion',
    },
    {
      name: 'rejects a null body',
      body: null,
    },
    {
      name: 'rejects an array body',
      body: [],
    },
  ];

  for (const testCase of cases) {
    it(testCase.name, async () => {
      const response = await h.putSnapshot({ revision: 1, body: testCase.body });
      await expectRejected(response, 400, 'invalid_snapshot', testCase.field);
    });
  }

  it('rejects a covered range beyond the snapshot bound', async () => {
    const days = consecutiveDays('2026-01-01', 63);
    const response = await h.putSnapshot({ revision: 1, body: snapshotBody({ days }) });
    await expectRejected(response, 400, 'invalid_snapshot', 'coveredRange');
  });

  it('rejects a day outside a bounded covered range', async () => {
    const days = consecutiveDays('2026-01-01', 62);
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody({ days, coveredRange: { from: '2026-01-01', to: '2026-03-02' } }),
    });
    await expectRejected(response, 400, 'invalid_snapshot', 'days[61].date');
  });

  it('accepts exactly the maximum number of days', async () => {
    const days = consecutiveDays('2026-01-01', 62);
    const response = await h.putSnapshot({ revision: 1, body: snapshotBody({ days }) });
    expect(response.status).toBe(200);
    expect(await json(response)).toMatchObject({ status: 'applied' });
  });
});

describe('revision path validation', () => {
  for (const revision of ['0', '-1', '1.0', 'abc', '01', '9007199254740993']) {
    it(`rejects revision ${JSON.stringify(revision)}`, async () => {
      const response = await h.putSnapshot({ revision, body: snapshotBody() });
      await expectRejected(response, 400, 'invalid_revision');
    });
  }
});

describe('collector path validation', () => {
  for (const collectorId of ['COL-A1', 'a', 'col a1', 'col.a1']) {
    it(`rejects collector id ${JSON.stringify(collectorId)}`, async () => {
      const response = await h.putSnapshot({
        revision: 1,
        body: snapshotBody(),
        collectorId,
      });
      await expectRejected(response, 400, 'invalid_query', 'collectorId');
    });
  }
});

describe('transport validation', () => {
  it('rejects malformed JSON', async () => {
    const response = await h.putSnapshot({ revision: 1, body: null, raw: '{"schemaVersion":1,' });
    await expectRejected(response, 400, 'invalid_json');
  });

  it('rejects a non-JSON content type', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody(),
      contentType: 'text/plain',
    });
    await expectRejected(response, 415, 'unsupported_media_type');
  });

  it('rejects a missing content type', async () => {
    const response = await h.putSnapshot({
      revision: 1,
      body: snapshotBody(),
      contentType: null,
    });
    await expectRejected(response, 415, 'unsupported_media_type');
  });

  it('rejects an oversized body before parsing it', async () => {
    const raw = `{"pad":"${'a'.repeat(40_000)}"}`;
    const response = await h.putSnapshot({ revision: 1, body: null, raw });
    await expectRejected(response, 413, 'payload_too_large');
  });
});

describe('daily query validation', () => {
  it('returns an empty, non-fabricated result for a range with no data', async () => {
    const response = await h.getDaily('?from=2026-11-01&to=2026-11-30');
    expect(response.status).toBe(200);
    expect(await json(response)).toMatchObject({ days: [], totalTokens: 0 });
  });

  const invalid = [
    { name: 'missing from', search: '?to=2026-11-30' },
    { name: 'inverted range', search: '?from=2026-11-30&to=2026-11-01' },
    { name: 'impossible date', search: '?from=2026-02-30&to=2026-03-01' },
    { name: 'range longer than bound', search: '?from=2026-01-01&to=2027-01-02' },
    { name: 'unsupported parameter', search: '?from=2026-11-01&to=2026-11-02&client=codex' },
    { name: 'unknown parameter', search: '?from=2026-11-01&to=2026-11-02&foo=1' },
    { name: 'malformed collector filter', search: '?from=2026-11-01&to=2026-11-02&collector=BAD' },
  ];

  for (const testCase of invalid) {
    it(`rejects ${testCase.name}`, async () => {
      const response = await h.getDaily(testCase.search);
      expect(response.status).toBe(400);
      expect((await json(response)).error.code).toBe('invalid_query');
    });
  }
});
