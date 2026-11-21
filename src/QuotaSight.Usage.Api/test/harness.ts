/**
 * Test harness: real `workerd` via Miniflare with a real local D1 database
 * (SQLite inside workerd). Nothing about the database or the HTTP layer is
 * mocked: requests are dispatched into the Worker and statements are executed
 * by D1 after the repository migration file has been applied.
 */
import { build } from 'esbuild';
import { Miniflare } from 'miniflare';
import { readdir, readFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const migrationsDir = join(packageRoot, 'migrations');

export const FIXTURE = {
  ownerA: 'owner-a',
  ownerB: 'owner-b',
  collectorA1: 'col-a1',
  collectorA2: 'col-a2',
  collectorB1: 'col-b1',
} as const;

const bundleCache = new Map<string, Promise<string>>();

function bundleWorker(entry: string): Promise<string> {
  const cached = bundleCache.get(entry);
  if (cached !== undefined) return cached;
  const pending = build({
    entryPoints: [join(packageRoot, entry)],
    bundle: true,
    write: false,
    format: 'esm',
    target: 'es2022',
    platform: 'neutral',
    mainFields: ['module', 'main'],
    conditions: ['workerd', 'worker', 'browser', 'import', 'default'],
    logLevel: 'silent',
  }).then((result) => {
    const file = result.outputFiles?.[0];
    if (file === undefined) throw new Error(`esbuild produced no output for ${entry}`);
    return file.text;
  });
  bundleCache.set(entry, pending);
  return pending;
}

async function migrationFiles(): Promise<string[]> {
  const names = (await readdir(migrationsDir)).filter((name) => name.endsWith('.sql')).sort();
  if (names.length === 0) throw new Error('no migration files found');
  return names.map((name) => join(migrationsDir, name));
}

/**
 * D1's `exec()` is line oriented, so the harness splits the migration itself
 * while keeping trigger bodies (`BEGIN ... END;`) together.
 */
function splitStatements(sql: string): string[] {
  const statements: string[] = [];
  let current = '';
  let depth = 0;
  let inString = false;

  for (let index = 0; index < sql.length; index += 1) {
    const char = sql[index] as string;
    if (inString) {
      current += char;
      if (char === "'") inString = false;
      continue;
    }
    if (char === "'") {
      inString = true;
      current += char;
      continue;
    }
    if (/[A-Za-z_]/.test(char)) {
      let end = index;
      while (end < sql.length && /[A-Za-z_]/.test(sql[end] as string)) end += 1;
      const word = sql.slice(index, end).toUpperCase();
      current += sql.slice(index, end);
      if (word === 'BEGIN') depth += 1;
      else if (word === 'END' && depth > 0) depth -= 1;
      index = end - 1;
      continue;
    }
    if (char === ';' && depth === 0) {
      statements.push(current.trim());
      current = '';
      continue;
    }
    current += char;
  }
  if (current.trim() !== '') statements.push(current.trim());
  return statements.filter((statement) => statement.length > 0);
}

export interface PutSnapshotInput {
  revision: string | number;
  body?: unknown;
  collectorId?: string;
  token?: string | null;
  contentType?: string | null;
  /** Raw text body (overrides `body`). */
  raw?: string;
  /** Raw byte body (overrides `body` and `raw`). */
  rawBytes?: Uint8Array;
}

export interface PutStreamInput {
  chunks: readonly Uint8Array[];
  collectorId?: string;
  revision?: string | number;
  token?: string | null;
  contentType?: string | null;
}

export interface Harness {
  readonly mf: Miniflare;
  readonly db: D1Database;
  request(path: string, init?: RequestInit): Promise<Response>;
  putSnapshot(input: PutSnapshotInput): Promise<Response>;
  /**
   * Same route as `putSnapshot`, but the body is a stream of unknown length, so
   * no `content-length` header is available to the Worker. Used to exercise the
   * byte-counting read itself rather than the header pre-check.
   */
  putStream(input: PutStreamInput): Promise<Response>;
  getDaily(search: string, token?: string | null): Promise<Response>;
  dispose(): Promise<void>;
}

export async function createHarness(entry = 'test/worker.ts'): Promise<Harness> {
  const script = await bundleWorker(entry);
  const mf = new Miniflare({
    modules: true,
    script,
    compatibilityDate: '2026-07-30',
    d1Databases: { DB: 'quotasight-usage-test' },
    cache: false,
  });
  const db = (await mf.getD1Database('DB')) as unknown as D1Database;

  for (const file of await migrationFiles()) {
    for (const statement of splitStatements(await readFile(file, 'utf8'))) {
      await db.prepare(statement).run();
    }
  }

  // Miniflare's own types carry Node's `URL`/`Response`; the Worker sees the
  // standard `workerd` globals, so the boundary is cast once, here.
  const request = (path: string, init?: RequestInit): Promise<Response> =>
    mf.dispatchFetch(`https://usage.test${path}`, init as never) as unknown as Promise<Response>;

  return {
    mf,
    db,
    request,
    async putSnapshot(input) {
      const headers: Record<string, string> = {};
      if (input.token !== null) headers.authorization = `Bearer ${input.token ?? 'write-a1'}`;
      const contentType = input.contentType === undefined ? 'application/json' : input.contentType;
      if (contentType !== null) headers['content-type'] = contentType;
      const body = input.rawBytes ?? input.raw ?? JSON.stringify(input.body);
      return request(
        `/v1/collectors/${encodeURIComponent(input.collectorId ?? FIXTURE.collectorA1)}/snapshots/${input.revision}`,
        { method: 'PUT', headers, body: body as never },
      );
    },
    async putStream(input) {
      const headers: Record<string, string> = {};
      if (input.token !== null) headers.authorization = `Bearer ${input.token ?? 'write-a1'}`;
      const contentType = input.contentType === undefined ? 'application/json' : input.contentType;
      if (contentType !== null) headers['content-type'] = contentType;
      const chunks = input.chunks;
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          for (const chunk of chunks) controller.enqueue(chunk);
          controller.close();
        },
      });
      return request(
        `/v1/collectors/${encodeURIComponent(input.collectorId ?? FIXTURE.collectorA1)}/snapshots/${input.revision ?? 1}`,
        { method: 'PUT', headers, body: stream, duplex: 'half' } as never,
      );
    },
    async getDaily(search, token = 'read-a1') {
      const headers: Record<string, string> = {};
      if (token !== null) headers.authorization = `Bearer ${token}`;
      return request(`/v1/usage/daily${search}`, { headers });
    },
    async dispose() {
      await mf.dispose();
    },
  };
}

export async function resetData(db: D1Database): Promise<void> {
  await db.batch([
    db.prepare('DELETE FROM usage_day'),
    db.prepare('DELETE FROM snapshot_receipts'),
    db.prepare('DELETE FROM collectors'),
  ]);
  const insert = db.prepare(
    'INSERT INTO collectors (id, owner_id, active_generation, created_at) VALUES (?, ?, ?, ?)',
  );
  await db.batch([
    insert.bind(FIXTURE.collectorA1, FIXTURE.ownerA, 1, '2026-11-01T00:00:00Z'),
    insert.bind(FIXTURE.collectorA2, FIXTURE.ownerA, 1, '2026-11-01T00:00:00Z'),
    insert.bind(FIXTURE.collectorB1, FIXTURE.ownerB, 1, '2026-11-01T00:00:00Z'),
  ]);
}

export interface CollectorSeed {
  readonly id: string;
  readonly ownerId: string;
  readonly activeGeneration?: number;
}

export interface UsageSeed {
  readonly collectorId: string;
  readonly date: string;
  readonly totalTokens: number;
  readonly generation?: number;
}

const INSERT_BATCH = 500;

/** Seeds many collectors directly, in bounded batches, for read-path bounds. */
export async function seedCollectors(db: D1Database, entries: readonly CollectorSeed[]): Promise<void> {
  let pending: D1PreparedStatement[] = [];
  const flush = async (): Promise<void> => {
    if (pending.length === 0) return;
    await db.batch(pending);
    pending = [];
  };
  for (const entry of entries) {
    pending.push(
      db
        .prepare('INSERT INTO collectors (id, owner_id, active_generation, created_at) VALUES (?, ?, ?, ?)')
        .bind(entry.id, entry.ownerId, entry.activeGeneration ?? 1, '2026-11-01T00:00:00Z'),
    );
    if (pending.length === INSERT_BATCH) await flush();
  }
  await flush();
}

/** Seeds many accepted day rows directly, in bounded batches. */
export async function seedUsage(db: D1Database, entries: readonly UsageSeed[]): Promise<void> {
  let pending: D1PreparedStatement[] = [];
  const flush = async (): Promise<void> => {
    if (pending.length === 0) return;
    await db.batch(pending);
    pending = [];
  };
  for (const entry of entries) {
    pending.push(
      db
        .prepare(
          'INSERT INTO usage_day (collector_id, generation, bucket_date, total_tokens, updated_at) VALUES (?, ?, ?, ?, ?)',
        )
        .bind(entry.collectorId, entry.generation ?? 1, entry.date, entry.totalTokens, '2026-11-20T00:00:00Z'),
    );
    if (pending.length === INSERT_BATCH) await flush();
  }
  await flush();
}

/** Builds a valid v1 snapshot body; `days` drive `coveredRange` by default. */
export function snapshotBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  const days = (overrides.days as Array<{ date: string; totalTokens: number }> | undefined) ?? [
    { date: '2026-11-18', totalTokens: 100 },
  ];
  const first = days[0]?.date ?? '2026-11-18';
  const last = days[days.length - 1]?.date ?? first;
  return {
    schemaVersion: 1,
    adapterVersion: 'tokscale-4.23.0',
    generation: 1,
    collectedAt: '2026-11-20T03:00:00Z',
    bucketTimeZone: 'Asia/Tokyo',
    coveredRange: { from: first, to: last },
    days,
    ...overrides,
  };
}

export async function json<T = any>(response: Response): Promise<T> {
  return (await response.json()) as T;
}

export async function scalar(db: D1Database, sql: string, ...values: unknown[]): Promise<number> {
  const row = await db
    .prepare(sql)
    .bind(...values)
    .first<{ value: number }>();
  return row?.value ?? -1;
}

export async function usageRows(
  db: D1Database,
): Promise<Array<{ bucket_date: string; total_tokens: number; updated_at: string }>> {
  const result = await db
    .prepare('SELECT bucket_date, total_tokens, updated_at FROM usage_day ORDER BY bucket_date')
    .all<{ bucket_date: string; total_tokens: number; updated_at: string }>();
  return result.results ?? [];
}
