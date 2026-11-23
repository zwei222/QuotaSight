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
import type { DailyQuery, Snapshot, SnapshotV2 } from './schema';
import {
  MAX_BREAKDOWN_COVERAGE,
  MAX_BREAKDOWN_PARTS,
  MAX_COLLECTORS_PER_READ,
  MAX_DAILY_ROWS,
} from './schema';
import { QUOTA_PROVIDERS, isEffectiveQuotaProvider, shouldRetainQuotaValue } from './quota';
import type { QuotaPayload, QuotaProviderEntry, QuotaWindow } from './quota';

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

/**
 * Receipt lookup for a v1 write.
 *
 * It reads only the columns `0003` added, because a v1 collector may talk to a
 * Worker that is newer than the schema: `0004` is applied to D1 after the code
 * that can use it is live, so between the two the table has no
 * `ignored_part_reductions` column and selecting it would fail every v1 write.
 */
const SELECT_RECEIPT =
  'SELECT body_digest, received_at, ignored_reductions FROM snapshot_receipts WHERE collector_id = ? AND generation = ? AND revision = ?';

/** Receipt lookup for a v2 write, which needs the breakdown columns of 0004. */
const SELECT_RECEIPT_V2 =
  'SELECT body_digest, received_at, ignored_reductions, ignored_part_reductions FROM snapshot_receipts WHERE collector_id = ? AND generation = ? AND revision = ?';

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

interface ReceiptFieldsV2 extends ReceiptFields {
  ignored_part_reductions: number;
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

async function findReceiptV2(
  db: D1Database,
  collectorId: string,
  generation: number,
  revision: number,
): Promise<ReceiptFieldsV2 | null> {
  return db.prepare(SELECT_RECEIPT_V2).bind(collectorId, generation, revision).first<ReceiptFieldsV2>();
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

/**
 * v2 write path.
 *
 * A v2 snapshot is four statements, always, whatever its size: the day totals,
 * the removal of the days whose breakdown this snapshot replaces, the breakdown
 * itself and the receipt. The alternative — one statement per row — would put
 * the statement count (and therefore the Worker CPU) at the mercy of the
 * payload, which is exactly what the free-plan CPU budget cannot absorb.
 *
 * The breakdown is stored as one *set of rows per day*, not as one maximum per
 * row. The incoming snapshot explains a day by exactly one set of rows (the API
 * requires the five components of every part to add up to that day's total), so
 * the stored set for a day is replaced as a whole — deleted, then inserted —
 * when the incoming snapshot attributes *more* tokens to that day than are
 * stored. A day whose set adds up to the same or less changes nothing at all and
 * is not merged row by row: keeping per-row maxima would let two snapshots add up
 * their explanations (100 stored for one model, 40 + 60 for two, `attributed`
 * 160 against a day total of 100), which is data the collector never observed.
 *
 * The consequence worth stating: an equal total is not a new explanation. A
 * re-scan that moves tokens from one model to another while the day total stays
 * the same is ignored, and the day keeps the set that first reached that total.
 * That is why `ignored_part_reductions` counts *days*.
 *
 * Because the day total only ever moves up and a day's set is only accepted up
 * to that total, `SUM(total_tokens)` of a day's rows can never exceed the stored
 * day total.
 */
export interface SnapshotReceiptV2 {
  readonly status: 'applied' | 'replayed';
  readonly collectorId: string;
  readonly generation: number;
  readonly revision: number;
  readonly receivedAt: string;
  readonly changedDays: readonly string[];
  readonly ignoredReductions: number;
  /** Breakdown rows this write inserted (a replaced day's whole set). */
  readonly changedParts: number;
  /** Days whose stored breakdown adds up to more than this snapshot sent. */
  readonly ignoredPartReductions: number;
}

/**
 * Day-total upsert for a whole v2 snapshot.
 *
 * Same semantics as {@link UPSERT_DAY_SQL} per day, in one statement:
 * `RETURNING` reports every row the statement touched, and the caller keeps the
 * day as "changed" only when the returned total equals the incoming one, because
 * an untouched-but-lower day is also returned (it keeps its larger total).
 */
export const UPSERT_DAYS_SQL = `
  INSERT INTO usage_day (collector_id, generation, bucket_date, total_tokens, updated_at)
  SELECT ?, ?, json_extract(d.value, '$.d'), json_extract(d.value, '$.t'), ?
  FROM json_each(?) AS d
  WHERE true
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
 * Removes the stored breakdown of every day this snapshot replaces.
 *
 * A day is replaced when the incoming total for it is greater than the sum its
 * stored rows add up to; a day with no rows at all counts as a sum of zero, which
 * is why the first v2 write of a day inserts rather than deletes. The incoming
 * totals are passed as `{d, t}` JSON so the decision is one statement, inside the
 * same transaction as the deletes it causes.
 *
 * `MATERIALIZED` matters: SQLite may otherwise re-evaluate the CTE per deleted
 * row, and the subquery reads the very table being deleted from.
 *
 * Bind order: partDays JSON, collectorId, generation, collectorId, generation.
 */
export const DELETE_REPLACED_PARTS_SQL = `
  WITH replaced (bucket_date) AS MATERIALIZED (
    SELECT json_extract(incoming.value, '$.d')
    FROM json_each(?) AS incoming
    WHERE json_extract(incoming.value, '$.t') > COALESCE(
      (
        SELECT SUM(stored.total_tokens)
        FROM usage_part AS stored
        WHERE stored.collector_id = ?
          AND stored.generation = ?
          AND stored.bucket_date = json_extract(incoming.value, '$.d')
      ), 0)
  )
  DELETE FROM usage_part
  WHERE collector_id = ?
    AND generation = ?
    AND bucket_date IN (SELECT bucket_date FROM replaced)
`;

/**
 * Inserts the breakdown of the days that were just emptied.
 *
 * The `WHERE` clause keeps an ignored day untouched, and it is written in the
 * same shape as the delete so the two read as one rule: the day's incoming total
 * (`dt`) must be greater than what is stored for that day, and a row that carries
 * no tokens is not stored at all. Both sides of that comparison are evaluated
 * against the state at the *start of this statement*, which is what makes the
 * two shapes interchangeable: for a day the batch's delete emptied, the stored
 * sum is zero here whatever the statement then inserts, so every row of a
 * replaced day is inserted (order does not matter), and for a day that was not
 * deleted the stored sum is at least the incoming total, so no row of it is
 * inserted. Relying on a running sum instead would make the result depend on the
 * order of the rows within one statement.
 *
 * The zero-token condition is what keeps a key with no usage out of storage: it
 * contributes nothing to any sum and would only widen the day's key set. Nothing
 * is lost by dropping it — the day's parts always add up to exactly the day total
 * the contract carries, so a zero row is not part of any explanation.
 *
 * There is no `ON CONFLICT` clause on purpose. It could only fire for a day whose
 * rows still exist (a day this write did not delete) or for a key repeated inside
 * one statement, and neither is a state the write path produces: a duplicate key
 * is refused by the validation contract before this point, and a day is either
 * deleted and re-inserted or left alone. Keeping the maximum of two rows would
 * turn such a bug into a quietly larger day; without the clause the primary key
 * refuses it and the whole batch rolls back, so the inconsistency cannot be
 * stored.
 *
 * Bind order: collectorId, generation, updatedAt, parts JSON, collectorId,
 * generation.
 */
export const INSERT_PARTS_SQL = `
  INSERT INTO usage_part (
    collector_id, generation, bucket_date, client, provider, model,
    input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
    reasoning_tokens, total_tokens, updated_at)
  SELECT ?, ?, json_extract(p.value, '$.d'), json_extract(p.value, '$.c'),
         json_extract(p.value, '$.p'), json_extract(p.value, '$.m'),
         json_extract(p.value, '$.i'), json_extract(p.value, '$.o'),
         json_extract(p.value, '$.cr'), json_extract(p.value, '$.cw'),
         json_extract(p.value, '$.r'), json_extract(p.value, '$.t'), ?
  FROM json_each(?) AS p
  WHERE json_extract(p.value, '$.dt') > COALESCE(
      (
        SELECT SUM(stored.total_tokens)
        FROM usage_part AS stored
        WHERE stored.collector_id = ?
          AND stored.generation = ?
          AND stored.bucket_date = json_extract(p.value, '$.d')
      ), 0)
    AND json_extract(p.value, '$.t') > 0
  RETURNING bucket_date
`;

/**
 * Receipt insert, with both reduction counts derived inside the same transaction.
 *
 * `ignored_reductions` counts days whose stored total is higher than the incoming
 * one; `ignored_part_reductions` counts days whose stored breakdown adds up to
 * more than the incoming total — a day count, not a row count, because a day's
 * breakdown is accepted or replaced as a whole. An equal total is neither, since
 * nothing was left behind. Because the receipt runs after the deletes and
 * inserts, it reads the state this transaction just produced: a replaced day now
 * adds up to exactly the incoming total and is not counted.
 *
 * The counts are written as `EXISTS`/scalar subqueries rather than as joins: with
 * the table on the inside of a join, SQLite can only use the two leading
 * primary-key columns as constraints (the remaining key columns come from
 * `json_extract` expressions), so it degenerates into scanning the whole table
 * once per incoming row. With a keyed subquery the planner uses the primary key
 * as a seek per incoming day.
 */
const INSERT_RECEIPT_V2_SQL = `
  INSERT INTO snapshot_receipts
    (collector_id, generation, revision, body_digest, received_at,
     ignored_reductions, ignored_part_reductions)
  SELECT ?, ?, ?, ?, ?,
    (
      SELECT COUNT(*)
      FROM json_each(?) AS incoming
      WHERE EXISTS (
        SELECT 1
        FROM usage_day AS u
        WHERE u.collector_id = ?
          AND u.generation = ?
          AND u.bucket_date = json_extract(incoming.value, '$.d')
          AND u.total_tokens > json_extract(incoming.value, '$.t')
      )
    ),
    (
      SELECT COUNT(*)
      FROM json_each(?) AS incoming
      WHERE COALESCE(
        (
          SELECT SUM(p.total_tokens)
          FROM usage_part AS p
          WHERE p.collector_id = ?
            AND p.generation = ?
            AND p.bucket_date = json_extract(incoming.value, '$.d')
        ), 0) > json_extract(incoming.value, '$.t')
    )
  RETURNING ignored_reductions, ignored_part_reductions
`;

/**
 * Applies one v2 snapshot.
 *
 * `changedDays` and `changedParts` are what this write actually moved;
 * `ignoredReductions` and `ignoredPartReductions` are what it left at a higher
 * accepted value. Both counts are derived inside the same transaction as the
 * writes (the receipt statement reads the state the other three statements just
 * left), so a replay answers with the numbers of the first write and nothing is
 * recomputed.
 */
export async function applySnapshotV2(
  db: D1Database,
  collector: CollectorRecord,
  snapshot: SnapshotV2,
  revision: number,
  digest: string,
  receivedAt: string,
): Promise<SnapshotReceiptV2> {
  const generation = snapshot.generation;

  const existing = await findReceiptV2(db, collector.id, generation, revision);
  if (existing !== null) {
    if (existing.body_digest !== digest) {
      throw new ApiError(
        409,
        'revision_conflict',
        'This revision was already accepted with a different payload.',
        { revision, reason: 'revision_conflict' },
      );
    }
    return replayOf(existing, collector.id, generation, revision);
  }

  const days = snapshot.days.map((day) => ({ d: day.date, t: day.totalTokens }));
  // Only a day that carries parts says anything about a breakdown; a day without
  // them must not make its stored set look like a reduction.
  const partDays = snapshot.days
    .filter((day) => day.parts !== undefined)
    .map((day) => ({ d: day.date, t: day.totalTokens }));
  const parts = snapshot.days.flatMap((day) =>
    (day.parts ?? []).map((part) => {
      const key = snapshot.keys[part.keyIndex] as SnapshotV2['keys'][number];
      return {
        d: day.date,
        // The day's own total travels with each row because the insert compares
        // the day against what is stored, not the row against it (see
        // INSERT_PARTS_SQL): the same comparison the delete makes, on the same
        // value, so a day is inserted exactly when it was emptied.
        dt: day.totalTokens,
        c: key.client,
        p: key.provider,
        m: key.model,
        i: part.input,
        o: part.output,
        cr: part.cacheRead,
        cw: part.cacheWrite,
        r: part.reasoning,
        t: part.input + part.output + part.cacheRead + part.cacheWrite + part.reasoning,
      };
    }),
  );

  // Serialized once and reused: the receipt compares against exactly the rows
  // the other statements wrote, not against a second serialization that could
  // describe them differently.
  const daysJson = JSON.stringify(days);
  const partDaysJson = JSON.stringify(partDays);
  const partsJson = JSON.stringify(parts);

  // Four statements, always, whatever the payload size: the day totals, the
  // removal of the days whose breakdown is replaced, the breakdown itself and the
  // receipt. The delete and the insert are always present — an empty day list
  // makes them no-ops — so the count is bound by the shape of the body, not by
  // how many rows it holds.
  const statements: D1PreparedStatement[] = [
    db.prepare(UPSERT_DAYS_SQL).bind(collector.id, generation, receivedAt, daysJson),
    db
      .prepare(DELETE_REPLACED_PARTS_SQL)
      .bind(partDaysJson, collector.id, generation, collector.id, generation),
    db
      .prepare(INSERT_PARTS_SQL)
      .bind(collector.id, generation, receivedAt, partsJson, collector.id, generation),
    db
      .prepare(INSERT_RECEIPT_V2_SQL)
      .bind(
        collector.id,
        generation,
        revision,
        digest,
        receivedAt,
        daysJson,
        collector.id,
        generation,
        partDaysJson,
        collector.id,
        generation,
      ),
  ];

  let results: D1Result<Record<string, unknown>>[];
  try {
    results = await db.batch<Record<string, unknown>>(statements);
  } catch (error) {
    if (DUPLICATE_RECEIPT.test(messageOf(error))) {
      const winner = await findReceiptV2(db, collector.id, generation, revision);
      if (winner !== null) {
        if (winner.body_digest !== digest) {
          throw new ApiError(
            409,
            'revision_conflict',
            'This revision was already accepted with a different payload.',
            { revision, reason: 'revision_conflict' },
          );
        }
        return replayOf(winner, collector.id, generation, revision);
      }
    }
    throw translateWriteAbort(error, revision, generation);
  }

  const incoming = new Map(days.map((day) => [day.d, day.t]));
  const changedDays = (results[0]?.results ?? [])
    .filter((row) => incoming.get(String(row['bucket_date'])) === Number(row['total_tokens']))
    .map((row) => String(row['bucket_date']))
    .sort();

  // The insert statement returns one row per written breakdown row, so its row
  // count is the number of rows this write created.
  const changedParts = (results[2]?.results ?? []).length;
  const receiptRow = (results[3]?.results ?? [])[0] ?? {};

  return {
    status: 'applied',
    collectorId: collector.id,
    generation,
    revision,
    receivedAt,
    changedDays,
    ignoredReductions: Number(receiptRow['ignored_reductions'] ?? 0),
    changedParts,
    ignoredPartReductions: Number(receiptRow['ignored_part_reductions'] ?? 0),
  };
}

function replayOf(
  receipt: ReceiptFieldsV2,
  collectorId: string,
  generation: number,
  revision: number,
): SnapshotReceiptV2 {
  return {
    status: 'replayed',
    collectorId,
    generation,
    revision,
    receivedAt: receipt.received_at,
    changedDays: [],
    // The stored counts belong to the first write; a replay reports them again
    // instead of recomputing anything against a table that has moved on since.
    ignoredReductions: receipt.ignored_reductions,
    changedParts: 0,
    ignoredPartReductions: receipt.ignored_part_reductions,
  };
}

export interface BreakdownPartRow {
  readonly client: string;
  readonly provider: string;
  readonly model: string;
  readonly input: number;
  readonly output: number;
  readonly cacheRead: number;
  readonly cacheWrite: number;
  readonly reasoning: number;
  readonly total: number;
}

export interface BreakdownCoverageRow {
  readonly date: string;
  readonly collectorId: string;
  readonly totalTokens: number;
  /** Sum of the breakdown rows stored for this day, or 0 when none was stored. */
  readonly attributedTokens: number;
}

/**
 * The exact breakdown aggregation, shared with the query-plan test so the plan
 * that is asserted is the plan production runs. Bind order: ownerId, from, to,
 * [collectorId], limit.
 *
 * The aggregation is over the whole range, not per day: the reader wants one
 * row per key for the requested window, ordered by size. It is owner-driven
 * (`collectors_by_owner`) and then a primary-key range per collector, so one
 * owner's request is bounded by that owner's own rows.
 */
export function breakdownPartsSql(query: DailyQuery): string {
  const conditions = [
    'c.owner_id = ?',
    'u.generation = c.active_generation',
    'u.bucket_date >= ?',
    'u.bucket_date <= ?',
  ];
  if (query.collectorId !== null) conditions.push('u.collector_id = ?');

  return `
    SELECT u.client AS client,
           u.provider AS provider,
           u.model AS model,
           SUM(u.input_tokens) AS input_tokens,
           SUM(u.output_tokens) AS output_tokens,
           SUM(u.cache_read_tokens) AS cache_read_tokens,
           SUM(u.cache_write_tokens) AS cache_write_tokens,
           SUM(u.reasoning_tokens) AS reasoning_tokens,
           SUM(u.total_tokens) AS total_tokens
    FROM usage_part u
    JOIN collectors c ON c.id = u.collector_id
    WHERE ${conditions.join(' AND ')}
    GROUP BY u.client, u.provider, u.model
    ORDER BY total_tokens DESC, u.client ASC, u.provider ASC, u.model ASC
    LIMIT ?
  `;
}

export function breakdownPartsParams(ownerId: string, query: DailyQuery): unknown[] {
  const values: unknown[] = [ownerId, query.from, query.to];
  if (query.collectorId !== null) values.push(query.collectorId);
  values.push(MAX_BREAKDOWN_PARTS + 1);
  return values;
}

/**
 * Coverage statement: the accepted day total next to the breakdown total that
 * was actually attributed to it. Bind order: ownerId, from, to, [collectorId],
 * limit.
 *
 * The attribution is a correlated subquery over the same day's breakdown rows,
 * so it is one primary-key seek per coverage row instead of a scan of every
 * owner's parts.
 */
export function breakdownCoverageSql(query: DailyQuery): string {
  const conditions = [
    'c.owner_id = ?',
    'u.generation = c.active_generation',
    'u.bucket_date >= ?',
    'u.bucket_date <= ?',
  ];
  if (query.collectorId !== null) conditions.push('u.collector_id = ?');

  return `
    SELECT u.bucket_date AS bucket_date,
           u.collector_id AS collector_id,
           u.total_tokens AS total_tokens,
           (
             SELECT SUM(p.total_tokens)
             FROM usage_part p
             WHERE p.collector_id = u.collector_id
               AND p.generation = u.generation
               AND p.bucket_date = u.bucket_date
           ) AS attributed_tokens
    FROM usage_day u
    JOIN collectors c ON c.id = u.collector_id
    WHERE ${conditions.join(' AND ')}
    ORDER BY u.bucket_date ASC, u.collector_id ASC
    LIMIT ?
  `;
}

export function breakdownCoverageParams(ownerId: string, query: DailyQuery): unknown[] {
  const values: unknown[] = [ownerId, query.from, query.to];
  if (query.collectorId !== null) values.push(query.collectorId);
  values.push(MAX_BREAKDOWN_COVERAGE + 1);
  return values;
}

function safeAggregate(value: unknown): number {
  const total = Number(value ?? 0);
  if (!Number.isSafeInteger(total) || total < 0) {
    // A stored sum outside the safe integer range cannot be represented in JSON
    // without rounding, and a rounded total would be fabricated data.
    throw new ApiError(422, 'result_too_large', 'The aggregated total is outside the safe integer range.', {
      reason: 'sum_out_of_range',
    });
  }
  return total;
}

/**
 * Reads the breakdown for the range.
 *
 * Neither list is truncated: going over the bound is an error, because a
 * shortened list would look like a complete answer that happens to be small.
 */
export async function readBreakdown(
  db: D1Database,
  ownerId: string,
  query: DailyQuery,
): Promise<{ parts: BreakdownPartRow[]; coverage: BreakdownCoverageRow[] }> {
  const partResult = await db
    .prepare(breakdownPartsSql(query))
    .bind(...breakdownPartsParams(ownerId, query))
    .all<Record<string, unknown>>();
  const partRows = partResult.results ?? [];
  if (partRows.length > MAX_BREAKDOWN_PARTS) {
    throw new ApiError(
      413,
      'result_too_large',
      `A single response may contain at most ${MAX_BREAKDOWN_PARTS} breakdown rows.`,
      { reason: 'too_many_parts', limitParts: MAX_BREAKDOWN_PARTS },
    );
  }

  const coverageResult = await db
    .prepare(breakdownCoverageSql(query))
    .bind(...breakdownCoverageParams(ownerId, query))
    .all<Record<string, unknown>>();
  const coverageRows = coverageResult.results ?? [];
  if (coverageRows.length > MAX_BREAKDOWN_COVERAGE) {
    throw new ApiError(
      413,
      'result_too_large',
      `A single response may contain at most ${MAX_BREAKDOWN_COVERAGE} coverage rows.`,
      { reason: 'too_many_coverage_rows', limitCoverage: MAX_BREAKDOWN_COVERAGE },
    );
  }

  return {
    parts: partRows.map((row) => ({
      client: String(row['client']),
      provider: String(row['provider']),
      model: String(row['model']),
      input: safeAggregate(row['input_tokens']),
      output: safeAggregate(row['output_tokens']),
      cacheRead: safeAggregate(row['cache_read_tokens']),
      cacheWrite: safeAggregate(row['cache_write_tokens']),
      reasoning: safeAggregate(row['reasoning_tokens']),
      total: safeAggregate(row['total_tokens']),
    })),
    coverage: coverageRows.map((row) => ({
      date: String(row['bucket_date']),
      collectorId: String(row['collector_id']),
      totalTokens: safeAggregate(row['total_tokens']),
      attributedTokens: safeAggregate(row['attributed_tokens']),
    })),
  };
}

/*
 * Subscription-quota retention.
 *
 * Two tables, one write. `quota_snapshot` keeps the newest collection exactly as
 * before; `quota_effective` (0006) keeps, per provider, the newest value that was
 * a successful observation, so a later partial or empty collection cannot erase
 * what was known. Both statements run in one `batch()` — one transaction — and
 * the retention statement is ordered *first* on purpose: its guard reads the
 * snapshot row this batch has not replaced yet, which is the same "strictly
 * newer" comparison the snapshot upsert applies. An equal or older collection
 * therefore changes neither table, instead of updating the side table for a
 * snapshot write that was refused.
 *
 * The per-provider recency rules are inside the statement, against the row that
 * is already stored, so two overlapping writes cannot both decide from the same
 * stale read. The instant a value is compared by is `observedAt ?? collectedAt`:
 *   * an explicitly older effective time never rewinds a stored value, whether
 *     the stored value carries an observation time or only the collection time
 *     that adopted it;
 *   * a value whose effective time equals the stored one is not made newer
 *     unless it is `fresh` (the observation did not change, the collection did),
 *     so a re-collected `cached` observation keeps the collection time it was
 *     first adopted with;
 *   * an undated `unknown` value — Codex, OpenCode Go, Grok and Copilot on the
 *     real wire — therefore moves forward by its collection time and keeps
 *     `observed_at = NULL`. No observation time is invented for it.
 * `observedAt` is the wire's second-precision instant (`YYYY-MM-DDTHH:MM:SSZ`)
 * and `collected_at` is the millisecond snapshot instant, so the two are
 * compared by `julianday()` — the parsed instant, not its text. As text,
 * `...:00Z` sorts *after* `...:00.500Z` while describing half a second earlier,
 * and `...:00.000Z` and `...:00Z` are the same instant with different text.
 * Malformed input cannot reach this statement because `parseQuota` refuses it
 * first.
 */

/** Snapshots one v2 quota read may return, matching the v1 read. */
export const MAX_QUOTA_SNAPSHOTS = 64;

/** Byte bound on one v2 quota response, matching the v1 read. */
export const MAX_QUOTA_RESPONSE_BYTES = 256 * 1024;

/** Provider groups per snapshot: the seven fixed wire providers. */
export const MAX_QUOTA_PROVIDERS = QUOTA_PROVIDERS.length;

/**
 * Snapshot upsert, unchanged from the route that shipped it: `newer-only`, an
 * equal collection time keeps the first payload.
 */
export const UPSERT_QUOTA_SNAPSHOT_SQL = `
  INSERT INTO quota_snapshot (collector_id, owner_id, generation, collected_at, payload_json)
  VALUES (?, ?, ?, ?, ?)
  ON CONFLICT(collector_id, generation) DO UPDATE SET
    owner_id = excluded.owner_id,
    collected_at = excluded.collected_at,
    payload_json = excluded.payload_json
  WHERE excluded.collected_at > quota_snapshot.collected_at
`;

/**
 * Upserts the retention rows of one collection.
 *
 * Bind order: collectorId, ownerId, generation, collectedAt, providers JSON,
 * collectorId, generation, collectedAt (snapshot-newer guard),
 * collectedAt (same guard on the conflict path).
 *
 * `providers JSON` is the already-filtered list of `{provider, freshness,
 * observedAt, windows}` entries; the payload-only decisions (effective value,
 * refusal of an undated `cached` value, refusal of an observation dated after
 * its own collection) are made by {@link shouldRetainQuotaValue} before the
 * statement, and everything that depends on stored state is decided here.
 * Because the entry filter runs first, the recency comparison below never sees
 * a claim that would outrank every later observation.
 */
export const UPSERT_QUOTA_EFFECTIVE_SQL = `
  INSERT INTO quota_effective
    (collector_id, owner_id, generation, provider, collected_at, observed_at,
     freshness, payload_json)
  SELECT ?, ?, ?, json_extract(p.value, '$.provider'), ?,
         json_extract(p.value, '$.observedAt'),
         json_extract(p.value, '$.freshness'),
         json_extract(p.value, '$.windows')
  FROM json_each(?) AS p
  WHERE NOT EXISTS (
    SELECT 1 FROM quota_snapshot s
    WHERE s.collector_id = ?
      AND s.generation = ?
      AND s.collected_at >= ?
  )
  ON CONFLICT(collector_id, generation, provider) DO UPDATE SET
    collected_at = excluded.collected_at,
    observed_at = excluded.observed_at,
    freshness = excluded.freshness,
    payload_json = excluded.payload_json
  WHERE NOT EXISTS (
      SELECT 1 FROM quota_snapshot s
      WHERE s.collector_id = quota_effective.collector_id
        AND s.generation = quota_effective.generation
        AND s.collected_at >= ?
    )
    AND NOT (
      julianday(COALESCE(excluded.observed_at, excluded.collected_at))
        < julianday(COALESCE(quota_effective.observed_at, quota_effective.collected_at))
    )
    AND NOT (
      julianday(COALESCE(excluded.observed_at, excluded.collected_at))
        = julianday(COALESCE(quota_effective.observed_at, quota_effective.collected_at))
      AND excluded.freshness <> 'fresh'
    )
`;

/**
 * Stores one accepted collection: the retention rows and the snapshot row in a
 * single transaction. Nothing is returned; a failed statement rolls both back.
 *
 * An entry that is not a success (an `unavailable` or value-less group, an
 * undated `cached` value, an observation dated after its own collection) is left
 * out of the retention rows but still stored in the snapshot exactly as the
 * collector sent it — the write contract, the v1 read and its HTTP response are
 * unchanged.
 */
export async function applyQuota(
  db: D1Database,
  collector: CollectorRecord,
  snapshot: QuotaPayload,
): Promise<void> {
  const retainable = snapshot.providers
    .filter((entry) => shouldRetainQuotaValue(entry, snapshot.collectedAt))
    .map((entry) => ({
      provider: entry.provider,
      freshness: entry.freshness,
      observedAt: entry.observedAt,
      windows: entry.windows,
    }));
  const snapshotJson = JSON.stringify({
    collectedAt: snapshot.collectedAt,
    providers: snapshot.providers,
  });

  await db.batch([
    db
      .prepare(UPSERT_QUOTA_EFFECTIVE_SQL)
      .bind(
        collector.id,
        collector.ownerId,
        snapshot.generation,
        snapshot.collectedAt,
        JSON.stringify(retainable),
        collector.id,
        snapshot.generation,
        snapshot.collectedAt,
        snapshot.collectedAt,
      ),
    db
      .prepare(UPSERT_QUOTA_SNAPSHOT_SQL)
      .bind(
        collector.id,
        collector.ownerId,
        snapshot.generation,
        snapshot.collectedAt,
        snapshotJson,
      ),
  ]);
}

interface QuotaV2Row {
  readonly collector_id: string;
  readonly generation: number;
  readonly snapshot_collected_at: string;
  readonly snapshot_payload_json: string;
  readonly provider: string | null;
  readonly effective_collected_at: string | null;
  readonly effective_observed_at: string | null;
  readonly effective_freshness: QuotaProviderEntry['freshness'] | null;
  readonly effective_payload_json: string | null;
}

/**
 * One read for a whole v2 response.
 *
 * The snapshot set is the same owner-scoped, active-generation set the v1 read
 * returns (one row per collector, newest first, one row beyond the bound so a
 * truncated result is detected). Each snapshot is left-joined to its retention
 * rows in the *same statement*, so a value can never come from a different
 * point in time than the snapshot it is attached to. The join is bounded by the
 * seven-provider primary key, so the statement reads at most 7 rows per
 * snapshot; the collector id is only a grouping key and never reaches the wire.
 *
 * Bind order: ownerId. Both LIMITs are derived from the documented bounds.
 */
export const QUOTA_V2_READ_SQL = `
  SELECT s.collector_id AS collector_id,
         s.generation AS generation,
         s.collected_at AS snapshot_collected_at,
         s.payload_json AS snapshot_payload_json,
         e.provider AS provider,
         e.collected_at AS effective_collected_at,
         e.observed_at AS effective_observed_at,
         e.freshness AS effective_freshness,
         e.payload_json AS effective_payload_json
  FROM (
    SELECT q.collector_id, q.owner_id, q.generation, q.collected_at, q.payload_json
    FROM quota_snapshot q
    JOIN collectors c
      ON c.id = q.collector_id
     AND c.owner_id = q.owner_id
     AND c.active_generation = q.generation
    WHERE q.owner_id = ?
    ORDER BY q.collected_at DESC
    LIMIT ${MAX_QUOTA_SNAPSHOTS + 1}
  ) AS s
  LEFT JOIN quota_effective e
    ON e.collector_id = s.collector_id
   AND e.generation = s.generation
   AND e.owner_id = s.owner_id
  ORDER BY s.collected_at DESC, s.collector_id ASC, e.provider ASC
  LIMIT ${(MAX_QUOTA_SNAPSHOTS + 1) * MAX_QUOTA_PROVIDERS + 1}
`;

export type QuotaLatestStatus = 'available' | 'unavailable' | 'missing';

export interface QuotaRetainedValue {
  /** `latest` only when this snapshot's own entry was the value that was kept. */
  readonly source: 'latest' | 'previous';
  /** Collection time of the snapshot that adopted this value. */
  readonly collectedAt: string;
  /**
   * The collector's own observation time, or `null` when the group reports none
   * (Codex, OpenCode Go, Grok and Copilot on the real wire) — then
   * `collectedAt` is the only time the value has.
   */
  readonly observedAt: string | null;
  readonly freshness: QuotaProviderEntry['freshness'];
  /** The stored wire windows, including values whose reset time has passed. */
  readonly windows: readonly QuotaWindow[];
}

export interface QuotaV2Provider {
  readonly provider: string;
  readonly latestStatus: QuotaLatestStatus;
  readonly value: QuotaRetainedValue | null;
}

export interface QuotaV2Snapshot {
  readonly collectedAt: string;
  readonly providers: readonly QuotaV2Provider[];
}

export interface QuotaV2Body {
  readonly schemaVersion: 2;
  readonly snapshots: readonly QuotaV2Snapshot[];
}

/**
 * Reads the v2 quota body: each snapshot's own provider status next to the last
 * successful value known for that collector and provider.
 *
 * `latestStatus` describes what this collection reported — an effective
 * `available` entry, an availability with no usable window (reported as
 * `unavailable`), an explicit `unavailable`, or no entry at all (`missing`).
 * `value` is the retained value and is independent of that status: it survives a
 * partial or empty collection, and its `source` says whether it was adopted by
 * this collection or comes from an earlier one. Nothing is inferred: a group
 * with no retained value is `null`, not zero and not the snapshot's own payload.
 */
export async function readQuotaV2(db: D1Database, ownerId: string): Promise<QuotaV2Body> {
  const result = await db.prepare(QUOTA_V2_READ_SQL).bind(ownerId).all<QuotaV2Row>();
  const rows = result.results ?? [];

  interface Group {
    readonly collectedAt: string;
    readonly payload: { readonly providers?: readonly QuotaProviderEntry[] };
    readonly effective: Map<string, QuotaV2Row>;
  }

  const groups = new Map<string, Group>();
  for (const row of rows) {
    const key = `${row.collector_id}\u0000${row.generation}`;
    let group = groups.get(key);
    if (group === undefined) {
      group = {
        collectedAt: row.snapshot_collected_at,
        payload: JSON.parse(row.snapshot_payload_json) as { providers?: readonly QuotaProviderEntry[] },
        effective: new Map(),
      };
      groups.set(key, group);
    }
    if (row.provider !== null) group.effective.set(row.provider, row);
  }

  if (groups.size > MAX_QUOTA_SNAPSHOTS || rows.length > MAX_QUOTA_SNAPSHOTS * MAX_QUOTA_PROVIDERS) {
    throw new ApiError(
      422,
      'result_too_large',
      'The quota response exceeds its row limit.',
      { reason: 'too_many_rows', limitRows: MAX_QUOTA_SNAPSHOTS },
    );
  }

  const snapshots: QuotaV2Snapshot[] = [];
  for (const group of groups.values()) {
    const entries = new Map<string, QuotaProviderEntry>();
    for (const entry of group.payload.providers ?? []) entries.set(entry.provider, entry);

    const providers = QUOTA_PROVIDERS.map((provider): QuotaV2Provider => {
      const entry = entries.get(provider);
      const retained = group.effective.get(provider);
      const latestStatus: QuotaLatestStatus =
        entry === undefined ? 'missing' : isEffectiveQuotaProvider(entry) ? 'available' : 'unavailable';
      // A stored row is adopted by *this* collection only when the collection it
      // was written with is this snapshot's own: the write stamps the row with
      // the snapshot's `collected_at`, and a refused or older write leaves the
      // earlier stamp in place.
      const adopted = entry !== undefined && isEffectiveQuotaProvider(entry) &&
        retained !== undefined && retained.effective_collected_at === group.collectedAt;
      const value: QuotaRetainedValue | null = retained === undefined
        ? null
        : {
            source: adopted ? 'latest' : 'previous',
            collectedAt: retained.effective_collected_at as string,
            observedAt: retained.effective_observed_at,
            freshness: retained.effective_freshness as QuotaProviderEntry['freshness'],
            windows: JSON.parse(retained.effective_payload_json as string) as readonly QuotaWindow[],
          };
      return { provider, latestStatus, value };
    });

    snapshots.push({ collectedAt: group.collectedAt, providers });
  }

  const body: QuotaV2Body = { schemaVersion: 2, snapshots };
  if (new TextEncoder().encode(JSON.stringify(body)).byteLength > MAX_QUOTA_RESPONSE_BYTES) {
    throw new ApiError(422, 'result_too_large', 'The quota response exceeds its byte limit.', {
      reason: 'response_too_large',
      limitBytes: MAX_QUOTA_RESPONSE_BYTES,
    });
  }
  return body;
}
