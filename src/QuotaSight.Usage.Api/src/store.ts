/**
 * D1 persistence for the first slice.
 *
 * A write is a single `batch()` (one D1 transaction) containing one prepared
 * upsert per day plus the receipt insert. If any statement aborts, D1 rolls the
 * whole batch back, so "receipt recorded" and "days written" can never disagree.
 * Ordering, generation and monotonic-total rules are enforced by the statements
 * and the schema triggers, i.e. inside the write, not only by a preceding read.
 */
import { ApiError } from './errors';
import type { DailyQuery, Snapshot } from './schema';
import { MAX_COLLECTORS_PER_READ, MAX_DAILY_ROWS } from './schema';

export interface CollectorRecord {
  readonly id: string;
  readonly ownerId: string;
  readonly activeGeneration: number;
}

export interface SnapshotReceipt {
  readonly status: 'applied' | 'replayed';
  readonly collectorId: string;
  readonly generation: number;
  readonly revision: number;
  readonly receivedAt: string;
  readonly changedDays: readonly string[];
  readonly ignoredReductions: number;
}

export interface DailyRow {
  readonly date: string;
  readonly collectorId: string;
  readonly totalTokens: number;
}

const SELECT_OWNED_COLLECTOR =
  'SELECT id, owner_id, active_generation FROM collectors WHERE id = ? AND owner_id = ?';

const SELECT_RECEIPT =
  'SELECT body_digest, received_at, ignored_reductions FROM snapshot_receipts WHERE collector_id = ? AND generation = ? AND revision = ?';

/**
 * Upsert one day partition.
 *
 * The stored total is `MAX(accepted, incoming)`:
 *   * higher than the accepted total (or a new day) — written, and `RETURNING`
 *     reports the incoming total;
 *   * equal — the `WHERE` clause makes it a genuine no-op, so no row comes back
 *     and the day is neither changed nor ignored;
 *   * lower — the accepted total is kept (`updated_at` is kept with it) and
 *     `RETURNING` reports that larger stored total, which is how the caller
 *     counts an ignored reduction.
 *
 * Because the value only ever moves up, `usage_day_rejects_unverified_reduction`
 * from 0001 can never abort this statement; it stays as the guard for anything
 * that writes the table directly.
 *
 * Exported so the atomicity test can build the same statement it asserts on.
 */
export const UPSERT_DAY_SQL = `
  INSERT INTO usage_day (collector_id, generation, bucket_date, total_tokens, updated_at)
  VALUES (?, ?, ?, ?, ?)
  ON CONFLICT (collector_id, generation, bucket_date) DO UPDATE
    SET total_tokens = MAX(usage_day.total_tokens, excluded.total_tokens),
        updated_at = CASE
          WHEN excluded.total_tokens > usage_day.total_tokens THEN excluded.updated_at
          ELSE usage_day.updated_at
        END
    WHERE usage_day.total_tokens <> excluded.total_tokens
  RETURNING bucket_date, total_tokens
`;

/**
 * Insert the receipt. A duplicate revision aborts the transaction instead of
 * being ignored, and the ignored-reduction count is computed by the statement
 * itself from the days the batch just left in `usage_day`, so the count always
 * belongs to the same transaction as the writes.
 */
const INSERT_RECEIPT_SQL = `
  INSERT INTO snapshot_receipts
    (collector_id, generation, revision, body_digest, received_at, ignored_reductions)
  SELECT ?, ?, ?, ?, ?,
    (
      SELECT COUNT(*)
      FROM json_each(?) AS incoming
      JOIN usage_day AS u
        ON u.collector_id = ?
       AND u.generation = ?
       AND u.bucket_date = json_extract(incoming.value, '$.d')
      WHERE u.total_tokens > json_extract(incoming.value, '$.t')
    )
  RETURNING ignored_reductions
`;

const DUPLICATE_RECEIPT = /UNIQUE constraint failed: snapshot_receipts/i;

interface ReceiptFields {
  body_digest: string;
  received_at: string;
  ignored_reductions: number;
}

interface CollectorFields {
  id: string;
  owner_id: string;
  active_generation: number;
}

/**
 * Owner-filtered collector lookup.
 *
 * Callers never look a collector up by id alone: the id is only a lookup key,
 * so the owner of the credential is part of the predicate. A row owned by
 * somebody else and a row that does not exist are therefore the same result,
 * and the caller cannot tell them apart in a response.
 */
export async function loadOwnedCollector(
  db: D1Database,
  collectorId: string,
  ownerId: string,
): Promise<CollectorRecord | null> {
  const row = await db
    .prepare(SELECT_OWNED_COLLECTOR)
    .bind(collectorId, ownerId)
    .first<CollectorFields>();
  if (row === null) return null;
  return { id: row.id, ownerId: row.owner_id, activeGeneration: row.active_generation };
}

async function findReceipt(
  db: D1Database,
  collectorId: string,
  generation: number,
  revision: number,
): Promise<ReceiptFields | null> {
  return db.prepare(SELECT_RECEIPT).bind(collectorId, generation, revision).first<ReceiptFields>();
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function translateWriteAbort(error: unknown, revision: number, generation: number): ApiError {
  const message = messageOf(error);
  // `usage_reduction_unverified` is deliberately not translated: the API path
  // can no longer lower a stored total (the upsert keeps the larger value), so
  // that abort is only reachable by a write to `usage_day` outside this module.
  if (message.includes('collector_revision_stale')) {
    return new ApiError(
      409,
      'stale_revision',
      'A newer revision was already accepted for this collector generation.',
      { revision, reason: 'stale_revision' },
    );
  }
  if (message.includes('collector_generation_inactive')) {
    return new ApiError(
      409,
      'generation_inactive',
      'The snapshot generation is not the active generation of this collector.',
      { generation, reason: 'generation_inactive' },
    );
  }
  return new ApiError(500, 'internal_error', 'The snapshot could not be stored.', {
    reason: 'write_failed',
  });
}

export async function applySnapshot(
  db: D1Database,
  collector: CollectorRecord,
  snapshot: Snapshot,
  revision: number,
  digest: string,
  receivedAt: string,
): Promise<SnapshotReceipt> {
  const generation = snapshot.generation;

  // Fast path for an exact replay; the batch below still protects the race.
  const existing = await findReceipt(db, collector.id, generation, revision);
  if (existing !== null) {
    if (existing.body_digest !== digest) {
      throw new ApiError(
        409,
        'revision_conflict',
        'This revision was already accepted with a different payload.',
        { revision, reason: 'revision_conflict' },
      );
    }
    return {
      status: 'replayed',
      collectorId: collector.id,
      generation,
      revision,
      receivedAt: existing.received_at,
      changedDays: [],
      // Same value the first write returned; nothing is recomputed on a replay.
      ignoredReductions: existing.ignored_reductions,
    };
  }

  const statements: D1PreparedStatement[] = snapshot.days.map((day) =>
    db.prepare(UPSERT_DAY_SQL).bind(collector.id, generation, day.date, day.totalTokens, receivedAt),
  );
  // The ignored count is derived inside the same transaction, from the state the
  // upserts of this batch left behind: a day that kept a larger accepted total is
  // a reduction that was not written. `NULL` for an empty day list is not needed
  // because the schema requires at least one day.
  statements.push(
    db
      .prepare(INSERT_RECEIPT_SQL)
      .bind(
        collector.id,
        generation,
        revision,
        digest,
        receivedAt,
        JSON.stringify(snapshot.days.map((day) => ({ d: day.date, t: day.totalTokens }))),
        collector.id,
        generation,
      ),
  );

  let results: D1Result<Record<string, unknown>>[];
  try {
    results = await db.batch<Record<string, unknown>>(statements);
  } catch (error) {
    if (DUPLICATE_RECEIPT.test(messageOf(error))) {
      // Another writer committed this revision while we were preparing.
      const winner = await findReceipt(db, collector.id, generation, revision);
      if (winner !== null) {
        if (winner.body_digest !== digest) {
          throw new ApiError(
            409,
            'revision_conflict',
            'This revision was already accepted with a different payload.',
            { revision, reason: 'revision_conflict' },
          );
        }
        return {
          status: 'replayed',
          collectorId: collector.id,
          generation,
          revision,
          receivedAt: winner.received_at,
          changedDays: [],
          ignoredReductions: winner.ignored_reductions,
        };
      }
    }
    throw translateWriteAbort(error, revision, generation);
  }

  // A day is "changed" only when the stored total is the incoming one. The
  // upsert returns a row for a reduction too (it keeps the larger accepted
  // total), so the returned value, not the presence of a row, is what counts.
  const changedDays: string[] = [];
  snapshot.days.forEach((day, index) => {
    const row = (results[index]?.results ?? [])[0];
    if (row !== undefined && Number(row['total_tokens']) === day.totalTokens) {
      changedDays.push(day.date);
    }
  });
  changedDays.sort();

  const ignoredReductions = Number(
    (results[snapshot.days.length]?.results ?? [{ ignored_reductions: 0 }])[0]?.[
      'ignored_reductions'
    ] ?? 0,
  );

  return {
    status: 'applied',
    collectorId: collector.id,
    generation,
    revision,
    receivedAt,
    changedDays,
    ignoredReductions,
  };
}

/**
 * The exact read statement, shared with the query-plan test so the plan that is
 * asserted is the plan that production runs.
 *
 * Bind order: ownerId, from, to, [collectorId], limit.
 */
export function dailyReadSql(query: DailyQuery): string {
  const conditions = [
    'c.owner_id = ?',
    'u.generation = c.active_generation',
    'u.bucket_date >= ?',
    'u.bucket_date <= ?',
  ];
  if (query.collectorId !== null) conditions.push('u.collector_id = ?');

  return `
    SELECT u.bucket_date AS bucket_date, u.collector_id AS collector_id, u.total_tokens AS total_tokens
    FROM usage_day u
    JOIN collectors c ON c.id = u.collector_id
    WHERE ${conditions.join(' AND ')}
    ORDER BY u.bucket_date ASC, u.collector_id ASC
    LIMIT ?
  `;
}

export function dailyReadParams(ownerId: string, query: DailyQuery): unknown[] {
  const values: unknown[] = [ownerId, query.from, query.to];
  if (query.collectorId !== null) values.push(query.collectorId);
  values.push(MAX_DAILY_ROWS + 1);
  return values;
}

export async function readDaily(
  db: D1Database,
  ownerId: string,
  query: DailyQuery,
): Promise<DailyRow[]> {
  // One row beyond the documented maximum is requested so a truncated result is
  // detected instead of being returned as if it were complete.
  const result = await db
    .prepare(dailyReadSql(query))
    .bind(...dailyReadParams(ownerId, query))
    .all<{ bucket_date: string; collector_id: string; total_tokens: number }>();

  const rows = result.results ?? [];
  if (rows.length > MAX_DAILY_ROWS) {
    throw new ApiError(
      413,
      'result_too_large',
      `A single response may contain at most ${MAX_DAILY_ROWS} day rows.`,
      { reason: 'too_many_rows', limitRows: MAX_DAILY_ROWS },
    );
  }

  return rows.map((row) => ({
    date: row.bucket_date,
    collectorId: row.collector_id,
    totalTokens: row.total_tokens,
  }));
}

export interface CollectorFreshnessRow {
  readonly collectorId: string;
  readonly generation: number;
  /** Reception time of the newest accepted receipt of the active generation, or `null` when none. */
  readonly lastReceivedAt: string | null;
}

/**
 * Freshness statement, shared with the query-plan test so the plan that is
 * asserted is the plan production runs.
 *
 * Bind order: ownerId, [collectorId], limit.
 *
 * The outer scan is `collectors`, so `collectors_by_owner` (0002) drives it and
 * the list is already ordered by id. The correlated subquery walks the
 * `snapshot_receipts` primary key backwards, so "newest receipt of the active
 * generation" is one index seek per collector rather than a scan of the ledger.
 */
export function collectorFreshnessSql(query: DailyQuery): string {
  const conditions = ['c.owner_id = ?'];
  if (query.collectorId !== null) conditions.push('c.id = ?');

  return `
    SELECT c.id AS collector_id,
           c.active_generation AS generation,
           (
             SELECT r.received_at
             FROM snapshot_receipts r
             WHERE r.collector_id = c.id
               AND r.generation = c.active_generation
             ORDER BY r.revision DESC
             LIMIT 1
           ) AS last_received_at
    FROM collectors c
    WHERE ${conditions.join(' AND ')}
    ORDER BY c.id ASC
    LIMIT ?
  `;
}

export function collectorFreshnessParams(ownerId: string, query: DailyQuery): unknown[] {
  const values: unknown[] = [ownerId];
  if (query.collectorId !== null) values.push(query.collectorId);
  values.push(MAX_COLLECTORS_PER_READ + 1);
  return values;
}

/**
 * Reports, per covered collector, the reception time of its newest accepted
 * receipt in the active generation.
 *
 * A collector with no receipt in its active generation is included with
 * `lastReceivedAt: null`: "configured but never reported" is a state the caller
 * has to be able to show, and dropping the entry would make it look exactly
 * like a collector that does not exist. Receipts of a superseded generation are
 * never read, because the join pins the active generation.
 */
export async function readCollectorFreshness(
  db: D1Database,
  ownerId: string,
  query: DailyQuery,
): Promise<CollectorFreshnessRow[]> {
  const result = await db
    .prepare(collectorFreshnessSql(query))
    .bind(...collectorFreshnessParams(ownerId, query))
    .all<{ collector_id: string; generation: number; last_received_at: string | null }>();

  const rows = result.results ?? [];
  if (rows.length > MAX_COLLECTORS_PER_READ) {
    throw new ApiError(
      413,
      'result_too_large',
      `A single response may describe at most ${MAX_COLLECTORS_PER_READ} collectors.`,
      { reason: 'too_many_collectors', limitCollectors: MAX_COLLECTORS_PER_READ },
    );
  }

  return rows.map((row) => ({
    collectorId: row.collector_id,
    generation: row.generation,
    lastReceivedAt: row.last_received_at,
  }));
}
