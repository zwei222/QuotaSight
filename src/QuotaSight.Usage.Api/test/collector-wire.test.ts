/**
 * Local wire integration: the real collector preview output is PUT into the
 * real Worker (Hono + Miniflare/workerd + local D1) and then read back through
 * `GET /v1/usage/daily`.
 *
 * Nothing is mocked and nothing leaves the machine: the fixture is the
 * permanent synthetic input under `tests/QuotaSight.Collector.Tests/Fixtures`,
 * the collector runs in its existing `--preview` mode (stdin in, wire body on
 * stdout), and the database is a local D1 instance created by `harness.ts`.
 */
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { createHarness, FIXTURE, json, resetData, type Harness } from './harness';

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = resolve(packageRoot, '../..');
const fixturePath = join(
  repositoryRoot,
  'tests/QuotaSight.Collector.Tests/Fixtures/graph-4.23.0-positive.json',
);

const BUILD_HINT =
  'dotnet build src/QuotaSight.Collector/QuotaSight.Collector.csproj -c Release ' +
  '(or set QUOTASIGHT_COLLECTOR_BIN)';

interface SnapshotDay {
  readonly date: string;
  readonly totalTokens: number;
}

interface Snapshot {
  readonly schemaVersion: number;
  readonly adapterVersion: string;
  readonly generation: number;
  readonly collectedAt: string;
  readonly bucketTimeZone: string;
  readonly coveredRange: { readonly from: string; readonly to: string };
  readonly days: readonly SnapshotDay[];
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
}

interface ReceiptBody {
  readonly status: string;
  readonly collectorId: string;
  readonly generation: number;
  readonly revision: number;
  readonly changedDays: readonly string[];
  readonly ignoredReductions: number;
}

/** Locates the already-built collector preview binary without building anything. */
function collectorBinary(): string {
  const candidates = [
    process.env['QUOTASIGHT_COLLECTOR_BIN'],
    join(repositoryRoot, 'src/QuotaSight.Collector/bin/Release/net10.0/QuotaSight.Collector'),
    join(repositoryRoot, 'src/QuotaSight.Collector/bin/Debug/net10.0/QuotaSight.Collector'),
  ];
  for (const candidate of candidates) {
    if (typeof candidate === 'string' && candidate.length > 0 && existsSync(candidate)) {
      return candidate;
    }
  }
  throw new Error(`The QuotaSight.Collector preview binary was not found. Build it with: ${BUILD_HINT}`);
}

/**
 * Runs the C# collector over the permanent synthetic fixture. The wire body is
 * whatever the collector actually writes; this test never re-serializes it.
 */
function previewSnapshot(): { wire: string; snapshot: Snapshot } {
  const result = spawnSync(
    collectorBinary(),
    ['--preview', '--generation', '1', '--bucket-time-zone', 'Asia/Tokyo'],
    {
      input: readFileSync(fixturePath),
      encoding: 'utf8',
      timeout: 60_000,
      maxBuffer: 1024 * 1024,
    },
  );

  if (result.error !== undefined) {
    throw result.error;
  }
  expect(result.status).toBe(0);
  expect(result.stderr).toBe('');
  expect(result.stdout.length).toBeGreaterThan(0);

  return { wire: result.stdout, snapshot: JSON.parse(result.stdout) as Snapshot };
}

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

describe('Collector preview output accepted by the local Usage API', () => {
  it('emits the pinned synthetic days and nothing upstream-only', () => {
    const { wire, snapshot } = previewSnapshot();

    expect(snapshot.schemaVersion).toBe(1);
    expect(snapshot.adapterVersion).toBe('tokscale-4.23.0');
    expect(snapshot.generation).toBe(1);
    expect(snapshot.bucketTimeZone).toBe('Asia/Tokyo');
    expect(snapshot.coveredRange).toEqual({ from: '2026-11-17', to: '2026-11-19' });
    expect(snapshot.days).toEqual([
      { date: '2026-11-17', totalTokens: 400 },
      { date: '2026-11-18', totalTokens: 1200 },
      { date: '2026-11-19', totalTokens: 1440 },
    ]);
    // The collector's allowlist keeps upstream cost/client detail off the wire.
    expect(wire).not.toContain('"cost"');
    expect(wire).not.toContain('"clients"');
    expect(wire).not.toContain('summary');
  });

  it('stores the collector snapshot in local D1 and reads the same days back', async () => {
    const { snapshot } = previewSnapshot();

    const put = await h.putSnapshot({
      revision: 1,
      body: snapshot,
      collectorId: FIXTURE.collectorA1,
    });
    expect(put.status).toBe(200);
    const receipt = await json<ReceiptBody>(put);
    expect(receipt).toMatchObject({
      status: 'applied',
      collectorId: FIXTURE.collectorA1,
      generation: 1,
      revision: 1,
      changedDays: ['2026-11-17', '2026-11-18', '2026-11-19'],
      ignoredReductions: 0,
    });

    const read = await h.getDaily('?from=2026-11-17&to=2026-11-19', 'read-a1');
    expect(read.status).toBe(200);
    const body = await json<DailyBody>(read);
    expect(body.from).toBe('2026-11-17');
    expect(body.to).toBe('2026-11-19');
    // The range the caller asked for is echoed exactly, and every row carries
    // the collector the snapshot was written for.
    expect(body.days).toEqual([
      { date: '2026-11-17', collectorId: FIXTURE.collectorA1, totalTokens: 400 },
      { date: '2026-11-18', collectorId: FIXTURE.collectorA1, totalTokens: 1200 },
      { date: '2026-11-19', collectorId: FIXTURE.collectorA1, totalTokens: 1440 },
    ]);
    expect(body.totalTokens).toBe(3040);
  });

  it('accepts a collector wire body whose old day decreased and keeps the previous total', async () => {
    const { snapshot } = previewSnapshot();
    expect((await h.putSnapshot({ revision: 1, body: snapshot })).status).toBe(200);

    // The shape is unchanged; only one day total is lower than what the API
    // already accepted. That day keeps its value, the others are unchanged too.
    const lowered = {
      ...snapshot,
      days: snapshot.days.map((day) =>
        day.date === '2026-11-17' ? { date: day.date, totalTokens: 399 } : day,
      ),
    };
    const second = await h.putSnapshot({ revision: 2, body: lowered });
    expect(second.status).toBe(200);
    const receipt = await json<ReceiptBody>(second);
    expect(receipt.status).toBe('applied');
    expect(receipt.changedDays).toEqual([]);
    expect(receipt.ignoredReductions).toBe(1);

    const read = await json<DailyBody>(await h.getDaily('?from=2026-11-17&to=2026-11-19', 'read-a1'));
    expect(read.days.map((day) => [day.date, day.totalTokens])).toEqual([
      ['2026-11-17', 400],
      ['2026-11-18', 1200],
      ['2026-11-19', 1440],
    ]);
  });

  it('replays the same revision as a no-op and keeps the readback identical', async () => {
    const { snapshot } = previewSnapshot();

    const first = await h.putSnapshot({ revision: 7, body: snapshot });
    expect(first.status).toBe(200);
    expect((await json<ReceiptBody>(first)).status).toBe('applied');

    const before = await json<DailyBody>(await h.getDaily('?from=2026-11-17&to=2026-11-19', 'read-a1'));

    const replay = await h.putSnapshot({ revision: 7, body: snapshot });
    expect(replay.status).toBe(200);
    const receipt = await json<ReceiptBody>(replay);
    expect(receipt.status).toBe('replayed');
    expect(receipt.changedDays).toEqual([]);

    const after = await json<DailyBody>(await h.getDaily('?from=2026-11-17&to=2026-11-19', 'read-a1'));
    expect(after).toEqual(before);
    expect(after.totalTokens).toBe(3040);
  });

  it('only returns the days inside the requested window', async () => {
    const { snapshot } = previewSnapshot();
    expect((await h.putSnapshot({ revision: 1, body: snapshot })).status).toBe(200);

    const read = await h.getDaily('?from=2026-11-18&to=2026-11-19', 'read-a1');
    expect(read.status).toBe(200);
    const body = await json<DailyBody>(read);
    expect(body.from).toBe('2026-11-18');
    expect(body.to).toBe('2026-11-19');
    expect(body.days.map((day) => day.date)).toEqual(['2026-11-18', '2026-11-19']);
    expect(body.totalTokens).toBe(1200 + 1440);
  });

  it('is owner-scoped: another owner reads nothing after the same snapshot is stored', async () => {
    const { snapshot } = previewSnapshot();
    expect((await h.putSnapshot({ revision: 1, body: snapshot })).status).toBe(200);

    const other = await h.getDaily('?from=2026-11-17&to=2026-11-19', 'read-b1');
    expect(other.status).toBe(200);
    const body = await json<DailyBody>(other);
    expect(body.days).toEqual([]);
    expect(body.totalTokens).toBe(0);
  });
});
