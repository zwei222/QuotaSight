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
 * The `WHERE` clause is what keeps an ignored day untouched: for a day that was
 * not deleted, its stored rows still add up to at least the incoming total, so no
 * row of it is inserted. For a replaced day the rows are inserted one by one and
 * the running sum stays below the incoming total, because the parts of a day add
 * up to exactly that total and no component is negative. The one case this skips
 * is a zero-token row that comes after the day is already fully accounted — a row
 * that contributes nothing to any sum and would only be stored as a key with no
 * usage.
 *
 * `ON CONFLICT` cannot fire for a replaced day (its rows are gone and a day
 * carries each key at most once), so it is only a guard: a same-key write during
 * the same statement would otherwise abort the whole transaction.
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
  WHERE json_extract(p.value, '$.t') > COALESCE(
    (
      SELECT SUM(stored.total_tokens)
      FROM usage_part AS stored
      WHERE stored.collector_id = ?
        AND stored.generation = ?
        AND stored.bucket_date = json_extract(p.value, '$.d')
    ), 0)
  ON CONFLICT (collector_id, generation, bucket_date, client, provider, model) DO UPDATE
    SET input_tokens = excluded.input_tokens,
        output_tokens = excluded.output_tokens,
        cache_read_tokens = excluded.cache_read_tokens,
        cache_write_tokens = excluded.cache_write_tokens,
        reasoning_tokens = excluded.reasoning_tokens,
        total_tokens = excluded.total_tokens,
        updated_at = excluded.updated_at
    WHERE excluded.total_tokens > usage_part.total_tokens
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
