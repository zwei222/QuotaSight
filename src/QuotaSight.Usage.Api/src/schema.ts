/**
 * Strict v1 contract parsing for the narrow first slice.
 *
 * Everything is rejected that is not explicitly part of the contract: unknown
 * keys, wrong scalar types, unsafe integers, non-canonical dates, unsorted or
 * duplicated days, days outside the covered range and out-of-bound payloads.
 * Nothing here guesses or repairs a value.
 */
import { ApiError } from './errors';

export const SNAPSHOT_SCHEMA_VERSION = 1;
export const BUCKET_TIME_ZONE = 'Asia/Tokyo';
export const MAX_BODY_BYTES = 32 * 1024;
export const MAX_DAYS_PER_SNAPSHOT = 62;
export const MAX_DAILY_QUERY_SPAN_DAYS = 366;
export const MAX_DAILY_ROWS = 5_000;

const MAX_SAFE_INTEGER = Number.MAX_SAFE_INTEGER;
const MIN_YEAR = 2000;
const MAX_YEAR = 2100;

const DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;
const INSTANT_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{3})?Z$/;
const ADAPTER_VERSION_PATTERN = /^[a-z0-9][a-z0-9._-]{0,63}$/;
const COLLECTOR_ID_PATTERN = /^[a-z0-9][a-z0-9_-]{2,63}$/;
const REVISION_PATTERN = /^(?:0|[1-9]\d*)$/;

export interface SnapshotDay {
  readonly date: string;
  readonly totalTokens: number;
}

export interface CoveredRange {
  readonly from: string;
  readonly to: string;
}

export interface Snapshot {
  readonly schemaVersion: typeof SNAPSHOT_SCHEMA_VERSION;
  readonly adapterVersion: string;
  readonly generation: number;
  readonly collectedAt: string;
  readonly bucketTimeZone: typeof BUCKET_TIME_ZONE;
  readonly coveredRange: CoveredRange;
  readonly days: readonly SnapshotDay[];
}

export interface DailyQuery {
  readonly from: string;
  readonly to: string;
  readonly collectorId: string | null;
}

const SNAPSHOT_KEYS = [
  'schemaVersion',
  'adapterVersion',
  'generation',
  'collectedAt',
  'bucketTimeZone',
  'coveredRange',
  'days',
] as const;
const RANGE_KEYS = ['from', 'to'] as const;
const DAY_KEYS = ['date', 'totalTokens'] as const;

function snapshotError(field: string, reason: string, message: string): ApiError {
  return new ApiError(400, 'invalid_snapshot', message, { field, reason });
}

function queryError(field: string, reason: string, message: string): ApiError {
  return new ApiError(400, 'invalid_query', message, { field, reason });
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function assertKeys(
  value: Record<string, unknown>,
  allowed: readonly string[],
  field: string,
): void {
  const prefix = field === '' ? '' : `${field}.`;
  for (const key of Object.keys(value)) {
    if (!allowed.includes(key)) {
      // The offending key is caller input and is deliberately not echoed; only
      // the server-derived path of the containing object is reported.
      throw snapshotError(
        field === '' ? '$' : field,
        'unexpected_field',
        'The snapshot contains a field that is not part of the v1 contract.',
      );
    }
  }
  for (const key of allowed) {
    if (!(key in value)) {
      const path = `${prefix}${key}`;
      throw snapshotError(path, 'missing_field', `Field ${path} is required.`);
    }
  }
}

/** Strict `YYYY-MM-DD` calendar date, or `null`. */
export function strictDateOrNull(raw: unknown): string | null {
  if (typeof raw !== 'string' || !DATE_PATTERN.test(raw)) return null;
  const [year, month, day] = raw.split('-').map(Number) as [number, number, number];
  if (year < MIN_YEAR || year > MAX_YEAR) return null;
  const time = Date.UTC(year, month - 1, day);
  const check = new Date(time);
  if (
    check.getUTCFullYear() !== year ||
    check.getUTCMonth() !== month - 1 ||
    check.getUTCDate() !== day
  ) {
    return null;
  }
  return raw;
}

/** Strict UTC instant (`2026-11-20T03:00:00Z` or with milliseconds), or `null`. */
function instantOrNull(raw: unknown): string | null {
  if (typeof raw !== 'string' || !INSTANT_PATTERN.test(raw)) return null;
  const year = Number(raw.slice(0, 4));
  if (year < MIN_YEAR || year > MAX_YEAR) return null;
  const time = Date.parse(raw);
  if (Number.isNaN(time)) return null;
  const normalized = new Date(time).toISOString();
  if (normalized.slice(0, 19) !== raw.slice(0, 19)) return null;
  return raw;
}

function safeIntOrNull(raw: unknown, minimum: number): number | null {
  if (typeof raw !== 'number' || !Number.isSafeInteger(raw)) return null;
  if (raw < minimum || raw > MAX_SAFE_INTEGER) return null;
  return raw;
}

export function dayNumber(date: string): number {
  const [year, month, day] = date.split('-').map(Number) as [number, number, number];
  return Math.floor(Date.UTC(year, month - 1, day) / 86_400_000);
}

export function parseCollectorId(raw: string): string {
  if (!COLLECTOR_ID_PATTERN.test(raw)) {
    throw queryError(
      'collectorId',
      'invalid_format',
      'collectorId must be a lowercase slug of 3-64 characters ([a-z0-9_-]).',
    );
  }
  return raw;
}

export function parseRevision(raw: string): number {
  if (!REVISION_PATTERN.test(raw)) {
    throw new ApiError(400, 'invalid_revision', 'revision must be a decimal integer without leading zeros.', {
      field: 'revision',
      reason: 'invalid_format',
    });
  }
  const value = Number(raw);
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new ApiError(400, 'invalid_revision', 'revision must be a positive safe integer.', {
      field: 'revision',
      reason: 'out_of_range',
    });
  }
  return value;
}

export function parseSnapshot(raw: unknown): Snapshot {
  if (!isRecord(raw)) {
    throw snapshotError('$', 'invalid_body', 'The snapshot body must be a JSON object.');
  }
  assertKeys(raw, SNAPSHOT_KEYS, '');

  if (raw['schemaVersion'] !== SNAPSHOT_SCHEMA_VERSION) {
    throw snapshotError(
      'schemaVersion',
      'unsupported_version',
      `schemaVersion must be ${SNAPSHOT_SCHEMA_VERSION}.`,
    );
  }

  const adapterVersion = raw['adapterVersion'];
  if (typeof adapterVersion !== 'string' || !ADAPTER_VERSION_PATTERN.test(adapterVersion)) {
    throw snapshotError(
      'adapterVersion',
      'invalid_format',
      'adapterVersion must be a normalized lowercase identifier (max 64 characters).',
    );
  }

  const generation = safeIntOrNull(raw['generation'], 1);
  if (generation === null) {
    throw snapshotError('generation', 'invalid_integer', 'generation must be a positive safe integer.');
  }

  const collectedAt = instantOrNull(raw['collectedAt']);
  if (collectedAt === null) {
    throw snapshotError(
      'collectedAt',
      'invalid_instant',
      'collectedAt must be a UTC instant such as 2026-11-20T03:00:00Z.',
    );
  }

  if (raw['bucketTimeZone'] !== BUCKET_TIME_ZONE) {
    throw snapshotError(
      'bucketTimeZone',
      'unsupported_time_zone',
      `bucketTimeZone must be ${BUCKET_TIME_ZONE}.`,
    );
  }

  const range = raw['coveredRange'];
  if (!isRecord(range)) {
    throw snapshotError('coveredRange', 'invalid_type', 'coveredRange must be an object.');
  }
  assertKeys(range, RANGE_KEYS, 'coveredRange');
  const from = strictDateOrNull(range['from']);
  if (from === null) {
    throw snapshotError('coveredRange.from', 'invalid_date', 'coveredRange.from must be a YYYY-MM-DD date.');
  }
  const to = strictDateOrNull(range['to']);
  if (to === null) {
    throw snapshotError('coveredRange.to', 'invalid_date', 'coveredRange.to must be a YYYY-MM-DD date.');
  }
  const span = dayNumber(to) - dayNumber(from);
  if (span < 0) {
    throw snapshotError('coveredRange', 'inverted_range', 'coveredRange.from must not be after to.');
  }
  if (span > MAX_DAYS_PER_SNAPSHOT - 1) {
    throw snapshotError(
      'coveredRange',
      'range_too_long',
      `coveredRange must not exceed ${MAX_DAYS_PER_SNAPSHOT} days.`,
    );
  }

  const rawDays = raw['days'];
  if (!Array.isArray(rawDays)) {
    throw snapshotError('days', 'invalid_type', 'days must be an array.');
  }
  if (rawDays.length === 0) {
    throw snapshotError('days', 'empty', 'A snapshot must contain at least one day.');
  }
  if (rawDays.length > MAX_DAYS_PER_SNAPSHOT) {
    throw snapshotError(
      'days',
      'too_many_days',
      `A snapshot may contain at most ${MAX_DAYS_PER_SNAPSHOT} days.`,
    );
  }

  const days: SnapshotDay[] = [];
  let previous = '';
  for (let index = 0; index < rawDays.length; index += 1) {
    const field = `days[${index}]`;
    const entry = rawDays[index];
    if (!isRecord(entry)) {
      throw snapshotError(field, 'invalid_type', `${field} must be an object.`);
    }
    assertKeys(entry, DAY_KEYS, field);

    const date = strictDateOrNull(entry['date']);
    if (date === null) {
      throw snapshotError(`${field}.date`, 'invalid_date', `${field}.date must be a YYYY-MM-DD date.`);
    }
    const totalTokens = safeIntOrNull(entry['totalTokens'], 0);
    if (totalTokens === null) {
      throw snapshotError(
        `${field}.totalTokens`,
        'invalid_integer',
        `${field}.totalTokens must be a non-negative safe integer.`,
      );
    }
    if (index > 0) {
      if (date === previous) {
        throw snapshotError('days', 'duplicate_date', `Day ${date} is listed more than once.`);
      }
      if (date < previous) {
        throw snapshotError('days', 'unsorted_days', 'Days must be listed in ascending date order.');
      }
    }
    if (dayNumber(date) < dayNumber(from) || dayNumber(date) > dayNumber(to)) {
      throw snapshotError(
        `${field}.date`,
        'day_outside_covered_range',
        `${field}.date is outside coveredRange.`,
      );
    }
    previous = date;
    days.push({ date, totalTokens });
  }

  return {
    schemaVersion: SNAPSHOT_SCHEMA_VERSION,
    adapterVersion,
    generation,
    collectedAt,
    bucketTimeZone: BUCKET_TIME_ZONE,
    coveredRange: { from, to },
    days,
  };
}

/** Stable serialization used for the idempotency digest. */
export function canonicalSnapshotJson(snapshot: Snapshot): string {
  return JSON.stringify({
    schemaVersion: snapshot.schemaVersion,
    adapterVersion: snapshot.adapterVersion,
    generation: snapshot.generation,
    collectedAt: snapshot.collectedAt,
    bucketTimeZone: snapshot.bucketTimeZone,
    coveredRange: { from: snapshot.coveredRange.from, to: snapshot.coveredRange.to },
    days: snapshot.days.map((day) => ({ date: day.date, totalTokens: day.totalTokens })),
  });
}

export async function sha256Hex(input: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(input));
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, '0')).join('');
}

/**
 * Exact media type match: the type must be `application/json`. Parameters such
 * as `charset=utf-8` are allowed, but a longer or decorated type
 * (`application/jsonx`, `application/json-patch+json`, `text/json`) is not.
 */
export function isJsonMediaType(raw: string | undefined | null): boolean {
  if (raw === undefined || raw === null) return false;
  const semi = raw.indexOf(';');
  const type = (semi === -1 ? raw : raw.slice(0, semi)).trim().toLowerCase();
  return type === 'application/json';
}

/**
 * Sums token totals without ever leaving the safe integer range: a wrapped or
 * rounded total would be fabricated data, so an overflow is an error instead.
 */
export function sumTokensSafely(values: readonly number[]): number {
  let total = 0;
  for (const value of values) {
    total += value;
    if (!Number.isSafeInteger(total)) {
      throw new ApiError(422, 'result_too_large', 'The aggregated total is outside the safe integer range.', {
        reason: 'sum_out_of_range',
      });
    }
  }
  return total;
}

export function parseDailyQuery(url: URL): DailyQuery {
  const allowed = new Set(['from', 'to', 'collector']);
  for (const key of url.searchParams.keys()) {
    if (!allowed.has(key)) {
      // Unknown parameter names are caller input and are never echoed back.
      throw queryError(
        '$',
        'unexpected_parameter',
        'The query contains a parameter that is not supported by this slice.',
      );
    }
  }
  for (const key of ['from', 'to', 'collector']) {
    if (url.searchParams.getAll(key).length > 1) {
      throw queryError(key, 'duplicate_parameter', `Query parameter ${key} must be provided at most once.`);
    }
  }

  const fromRaw = url.searchParams.get('from');
  if (fromRaw === null) {
    throw queryError('from', 'missing_parameter', 'Query parameter from is required.');
  }
  const toRaw = url.searchParams.get('to');
  if (toRaw === null) {
    throw queryError('to', 'missing_parameter', 'Query parameter to is required.');
  }

  const from = strictDateOrNull(fromRaw);
  if (from === null) {
    throw queryError('from', 'invalid_date', 'from must be a YYYY-MM-DD date.');
  }
  const to = strictDateOrNull(toRaw);
  if (to === null) {
    throw queryError('to', 'invalid_date', 'to must be a YYYY-MM-DD date.');
  }
  const span = dayNumber(to) - dayNumber(from);
  if (span < 0) {
    throw queryError('from', 'inverted_range', 'from must not be after to.');
  }
  if (span > MAX_DAILY_QUERY_SPAN_DAYS - 1) {
    throw queryError('from', 'range_too_long', `The range must not exceed ${MAX_DAILY_QUERY_SPAN_DAYS} days.`);
  }

  const collectorRaw = url.searchParams.get('collector');
  let collectorId: string | null = null;
  if (collectorRaw !== null) {
    if (!COLLECTOR_ID_PATTERN.test(collectorRaw)) {
      throw queryError(
        'collector',
        'invalid_format',
        'collector must be a lowercase slug of 3-64 characters ([a-z0-9_-]).',
      );
    }
    collectorId = collectorRaw;
  }

  return { from, to, collectorId };
}
