/**
 * D1 persistence for the first slice.
 *
 * A write is a single `batch()` (one D1 transaction) containing one prepared
 * upsert per changed day plus the receipt insert. If any statement aborts, D1
 * rolls the whole batch back, so "receipt recorded" and "days replaced" can
 * never disagree. Ordering, generation and non-reduction rules are enforced by
 * schema triggers, i.e. inside the write, not only by a preceding read.
 */
import { ApiError } from './errors';
import type { DailyQuery, Snapshot } from './schema';
import { MAX_DAILY_ROWS } from './schema';

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
}

export interface DailyRow {
  readonly date: string;
  readonly collectorId: string;
  readonly totalTokens: number;
}

const SELECT_OWNED_COLLECTOR =
  'SELECT id, owner_id, active_generation FROM collectors WHERE id = ? AND owner_id = ?';

const SELECT_RECEIPT =
  'SELECT body_digest, received_at FROM snapshot_receipts WHERE collector_id = ? AND generation = ? AND revision = ?';

/**
 * Replace only the affected day partitions. The `WHERE` clause makes an
 * unchanged total a genuine no-op (no row returned, so it is not reported as a
 * changed day), and the schema trigger rejects any decrease.
 */
const UPSERT_DAY = `
  INSERT INTO usage_day (collector_id, generation, bucket_date, total_tokens, updated_at)
  VALUES (?, ?, ?, ?, ?)
  ON CONFLICT (collector_id, generation, bucket_date) DO UPDATE
    SET total_tokens = excluded.total_tokens,
        updated_at = excluded.updated_at
    WHERE usage_day.total_tokens <> excluded.total_tokens
  RETURNING bucket_date
`;

/** Plain insert: a duplicate revision aborts the transaction instead of being ignored. */
const INSERT_RECEIPT = `
  INSERT INTO snapshot_receipts (collector_id, generation, revision, body_digest, received_at)
  VALUES (?, ?, ?, ?, ?)
`;

const DUPLICATE_RECEIPT = /UNIQUE constraint failed: snapshot_receipts/i;

interface ReceiptFields {
  body_digest: string;
  received_at: string;
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
  if (message.includes('usage_reduction_unverified')) {
    return new ApiError(
      409,
      'reduction_unverified',
      'A previously accepted day total would decrease. Unverified reductions are not accepted in this milestone.',
      { revision, reason: 'reduction_unverified' },
    );
  }
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
    };
  }

  const statements: D1PreparedStatement[] = snapshot.days.map((day) =>
    db.prepare(UPSERT_DAY).bind(collector.id, generation, day.date, day.totalTokens, receivedAt),
  );
  statements.push(db.prepare(INSERT_RECEIPT).bind(collector.id, generation, revision, digest, receivedAt));

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
        };
      }
    }
    throw translateWriteAbort(error, revision, generation);
  }

  const changedDays = results
    .slice(0, snapshot.days.length)
    .flatMap((result) => (result.results ?? []).map((row) => String(row['bucket_date'])))
    .sort();

  return {
    status: 'applied',
    collectorId: collector.id,
    generation,
    revision,
    receivedAt,
    changedDays,
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
