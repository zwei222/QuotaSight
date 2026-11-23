/**
 * Strict, bounded parsing of the API wire.
 *
 * v1 (`schemaVersion: 1`) is the daily-total shape that shipped first. v2
 * (`schemaVersion: 2`) adds a per-(client, provider, model) breakdown to it and
 * keeps every v1 rule for the fields the two versions share.
 *
 * Everything is rejected that is not explicitly part of the contract: unknown
 * keys, wrong scalar types, unsafe integers, non-canonical dates, unsorted or
 * duplicated days, days outside the covered range and out-of-bound payloads.
 * Nothing here guesses or repairs a value. Identifier normalization is done by
 * the collector (see `TokscaleGraphNormalizer` on that side); this module only
 * checks that a value is already in the normalized form, and it does so against
 * the very same rules, so the two sides cannot disagree about what "normalized"
 * means without a test failing.
 */
import { ApiError } from './errors';

export const SNAPSHOT_SCHEMA_VERSION = 1;
export const SNAPSHOT_V2_SCHEMA_VERSION = 2;
export const BUCKET_TIME_ZONE = 'Asia/Tokyo';
export const MAX_BODY_BYTES = 32 * 1024;
export const MAX_DAYS_PER_SNAPSHOT = 62;
export const MAX_DAILY_QUERY_SPAN_DAYS = 366;
export const MAX_DAILY_ROWS = 5_000;

/** Upper bound on the normalized identifier keys of one v2 snapshot. */
export const MAX_KEYS_PER_SNAPSHOT = 128;

/** Upper bound on the breakdown rows of one day. Days above it carry no parts. */
export const MAX_PARTS_PER_DAY = 32;

/** Upper bound on the breakdown rows of one v2 snapshot as a whole. */
export const MAX_PARTS_PER_SNAPSHOT = 1_024;

/** Upper bound on the breakdown rows of one GET /v1/usage/breakdown response. */
export const MAX_BREAKDOWN_PARTS = 2_000;

/** Upper bound on the coverage rows of one GET /v1/usage/breakdown response. */
export const MAX_BREAKDOWN_COVERAGE = 5_000;

/**
 * Widest window `GET /v1/usage/breakdown` answers.
 *
 * The breakdown aggregates every key of every day in the range, so a wide range
 * multiplies the rows the database has to read. The reader asks for 7 or 30
 * days; 92 (three months) leaves room for a month-boundary window and no more.
 */
export const MAX_BREAKDOWN_QUERY_SPAN_DAYS = 92;

/** Longest accepted client identifier (without the terminator). */
export const MAX_CLIENT_LENGTH = 32;

/** Longest accepted provider identifier, including its separating commas. */
export const MAX_PROVIDER_LENGTH = 64;

/** Longest accepted model identifier (without the terminator). */
export const MAX_MODEL_LENGTH = 96;

/** Greatest number of providers that may be joined into one provider identifier. */
export const MAX_PROVIDER_ELEMENTS = 4;


/**
 * Upper bound on the collector freshness entries of one read response.
 *
 * Collectors are provisioned server-side (this slice has no creation route), so
 * an owner normally has a handful. The bound exists so a response stays finite:
 * it is checked one row beyond the limit and an over-bound read is an error, not
 * a silently truncated freshness list.
 */
export const MAX_COLLECTORS_PER_READ = 64;

const MAX_SAFE_INTEGER = Number.MAX_SAFE_INTEGER;
const MIN_YEAR = 2000;
const MAX_YEAR = 2100;

const DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;
const INSTANT_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{3})?Z$/;
const ADAPTER_VERSION_PATTERN = /^[a-z0-9][a-z0-9._-]{0,63}$/;
const COLLECTOR_ID_PATTERN = /^[a-z0-9][a-z0-9_-]{2,63}$/;
const REVISION_PATTERN = /^(?:0|[1-9]\d*)$/;

/**
 * Normalized identifier rules for wiring v2, mirrored by the collector.
 *
 * * client: lowercase ASCII slug, or `''` when upstream's value did not match.
 * * provider: one to four lowercase elements joined by `,` in ascending order
 *   (the collector splits, trims, lowercases, de-duplicates and sorts), or `''`.
 * * model: lowercase identifier that may contain brackets; a path-shaped value
 *   is never a model id, so it is `''` instead.
 *
 * Lengths are inclusive of the terminator: a client is at most 32 characters, a
 * provider element at most 64, a model at most 96. The collector enforces the
 * same numbers, and both sides have boundary tests at the exact edge.
 */
const CLIENT_PATTERN = /^[a-z0-9][a-z0-9._-]{0,31}$/;
const PROVIDER_ELEMENT_PATTERN = /^[a-z0-9][a-z0-9._:/@+-]{0,63}$/;
const MODEL_PATTERN = /^[a-z0-9][a-z0-9._:/@+[\]-]{0,95}$/;
const DRIVE_LETTER_PATTERN = /^[a-z]:/;
/** Suffixes of local weight files: a value ending in one describes a file. */
const MODEL_FILE_SUFFIXES = ['.gguf', '.safetensors', '.bin', '.onnx'] as const;

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

/** One normalized (client, provider, model) triple, in wire order. */
export interface BreakdownKey {
  readonly client: string;
  readonly provider: string;
  readonly model: string;
}

/**
 * One breakdown row of one day: five token components attributed to one key.
 * The tuple form on the wire is `[keyIndex, input, output, cacheRead, cacheWrite, reasoning]`.
 */
export interface BreakdownPart {
  readonly keyIndex: number;
  readonly input: number;
  readonly output: number;
  readonly cacheRead: number;
  readonly cacheWrite: number;
  readonly reasoning: number;
}

export interface SnapshotDayV2 {
  readonly date: string;
  readonly totalTokens: number;
  /** Absent when the collector withheld this day's breakdown; never empty when present. */
  readonly parts?: readonly BreakdownPart[];
}

export interface SnapshotV2 {
  readonly schemaVersion: typeof SNAPSHOT_V2_SCHEMA_VERSION;
  readonly adapterVersion: string;
  readonly generation: number;
  readonly collectedAt: string;
  readonly bucketTimeZone: typeof BUCKET_TIME_ZONE;
  readonly coveredRange: CoveredRange;
  readonly keys: readonly BreakdownKey[];
  readonly days: readonly SnapshotDayV2[];
}

/** Either wire version, as the PUT route accepts them. */
export type SnapshotBody = Snapshot | SnapshotV2;

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

/**
 * True when `value` is a path, which is never a model identifier.
 *
 * The rules are deliberately narrow so that a real identifier survives:
 * a leading separator, any backslash, a drive letter, a local weight-file suffix
 * (`.gguf`, `.safetensors`, `.bin`, `.onnx`), or two or more slashes. A single
 * slash is the legitimate `org/model` form and stays accepted; the collector
 * applies exactly the same rules.
 */
function isPathShaped(value: string): boolean {
  if (value.startsWith('/') || value.includes('\\')) return true;
  if (DRIVE_LETTER_PATTERN.test(value)) return true;
  for (const suffix of MODEL_FILE_SUFFIXES) {
    if (value.endsWith(suffix)) return true;
  }
  // Two separators describe a directory (`org/sub/model`), while a single one is
  // the legitimate `org/model` form and stays accepted.
  return value.split('/').length - 1 >= 2;
}

/** True when `value` is a client identifier in the normalized form (or empty). */
export function isNormalizedClient(value: string): boolean {
  return value === '' || CLIENT_PATTERN.test(value);
}

/** True when `value` is a provider identifier in the normalized form (or empty). */
export function isNormalizedProvider(value: string): boolean {
  if (value === '') return true;
  if (value.length > MAX_PROVIDER_LENGTH) return false;
  const elements = value.split(',');
  if (elements.length > MAX_PROVIDER_ELEMENTS) return false;
  let previous = '';
  for (const element of elements) {
    if (!PROVIDER_ELEMENT_PATTERN.test(element)) return false;
    // The collector sorts and de-duplicates, so the normalized form is exactly
    // the strictly ascending one.
    if (element <= previous) return false;
    previous = element;
  }
  return true;
}

/** True when `value` is a model identifier in the normalized form (or empty). */
export function isNormalizedModel(value: string): boolean {
  if (value === '') return true;
  return MODEL_PATTERN.test(value) && !isPathShaped(value);
}

const V2_SNAPSHOT_KEYS = [
  'schemaVersion',
  'adapterVersion',
  'generation',
  'collectedAt',
  'bucketTimeZone',
  'coveredRange',
  'keys',
  'days',
] as const;
const V2_DAY_KEYS = ['date', 'totalTokens', 'parts'] as const;
const V2_DAY_REQUIRED_KEYS = ['date', 'totalTokens'] as const;
const PART_ARITY = 6;

/** True when `value` is a `[client, provider, model]` array of normalized strings. */
function isKeyTriple(value: unknown): value is [string, string, string] {
  return (
    Array.isArray(value) &&
    value.length === 3 &&
    value.every((element) => typeof element === 'string')
  );
}

function keyTuple(key: BreakdownKey): string[] {
  return [key.client, key.provider, key.model];
}

function compareKeyTuples(left: readonly string[], right: readonly string[]): number {
  for (let index = 0; index < 3; index += 1) {
    const a = left[index] as string;
    const b = right[index] as string;
    if (a < b) return -1;
    if (a > b) return 1;
  }
  return 0;
}

/**
 * Dispatches on `schemaVersion` so the PUT route can accept both wire versions.
 *
 * v1 keeps its own parser unchanged, so a v1 body is validated (and its errors
 * worded) exactly as before; only `schemaVersion: 2` reaches the v2 parser.
 */
export function parseSnapshotBody(raw: unknown): SnapshotBody {
  if (isRecord(raw) && raw['schemaVersion'] === SNAPSHOT_V2_SCHEMA_VERSION) {
    return parseSnapshotV2(raw);
  }
  return parseSnapshot(raw);
}

/** Strict v2 parsing: the v1 daily shape plus the per-key breakdown. */
export function parseSnapshotV2(raw: unknown): SnapshotV2 {
  if (!isRecord(raw)) {
    throw snapshotError('$', 'invalid_body', 'The snapshot body must be a JSON object.');
  }
  assertKeysV2(raw, V2_SNAPSHOT_KEYS, '');

  if (raw['schemaVersion'] !== SNAPSHOT_V2_SCHEMA_VERSION) {
    throw snapshotError(
      'schemaVersion',
      'unsupported_version',
      `schemaVersion must be ${SNAPSHOT_V2_SCHEMA_VERSION}.`,
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
  assertKeysV2(range, RANGE_KEYS, 'coveredRange');
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

  const rawKeys = raw['keys'];
  if (!Array.isArray(rawKeys)) {
    throw snapshotError('keys', 'invalid_type', 'keys must be an array.');
  }
  if (rawKeys.length > MAX_KEYS_PER_SNAPSHOT) {
    throw snapshotError(
      'keys',
      'too_many_keys',
      `A snapshot may contain at most ${MAX_KEYS_PER_SNAPSHOT} breakdown keys.`,
    );
  }

  const keys: BreakdownKey[] = [];
  let previousKey: BreakdownKey | null = null;
  for (let index = 0; index < rawKeys.length; index += 1) {
    const field = `keys[${index}]`;
    const entry = rawKeys[index];
    if (!isKeyTriple(entry)) {
      throw snapshotError(field, 'invalid_type', `${field} must be an array of three strings.`);
    }
    const [client, provider, model] = entry;
    if (!isNormalizedClient(client)) {
      throw snapshotError(
        `${field}[0]`,
        'invalid_client',
        `${field}[0] must be a normalized lowercase client identifier of at most ${MAX_CLIENT_LENGTH} characters.`,
      );
    }
    if (!isNormalizedProvider(provider)) {
      throw snapshotError(
        `${field}[1]`,
        'invalid_provider',
        `${field}[1] must be an ascending comma-joined list of at most ${MAX_PROVIDER_ELEMENTS} lowercase providers (max ${MAX_PROVIDER_LENGTH} characters).`,
      );
    }
    if (!isNormalizedModel(model)) {
      throw snapshotError(
        `${field}[2]`,
        'invalid_model',
        `${field}[2] must be a normalized lowercase model identifier of at most ${MAX_MODEL_LENGTH} characters, never a path.`,
      );
    }

    const key: BreakdownKey = { client, provider, model };
    if (previousKey !== null) {
      const order = compareKeyTuples(keyTuple(previousKey), keyTuple(key));
      if (order === 0) {
        throw snapshotError('keys', 'duplicate_key', `${field} repeats the preceding key.`);
      }
      if (order > 0) {
        throw snapshotError('keys', 'unsorted_keys', 'keys must be listed in ascending order.');
      }
    }
    previousKey = key;
    keys.push(key);
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

  const days: SnapshotDayV2[] = [];
  const referencedKeys = new Set<number>();
  let totalParts = 0;
  let previousDate = '';
  for (let index = 0; index < rawDays.length; index += 1) {
    const field = `days[${index}]`;
    const entry = rawDays[index];
    if (!isRecord(entry)) {
      throw snapshotError(field, 'invalid_type', `${field} must be an object.`);
    }
    assertKeysV2(entry, V2_DAY_KEYS, field, V2_DAY_REQUIRED_KEYS);

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
      if (date === previousDate) {
        throw snapshotError('days', 'duplicate_date', `Day ${date} is listed more than once.`);
      }
      if (date < previousDate) {
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
    previousDate = date;

    const rawParts = entry['parts'];
    if (rawParts === undefined) {
      days.push({ date, totalTokens });
      continue;
    }
    if (!Array.isArray(rawParts)) {
      throw snapshotError(`${field}.parts`, 'invalid_type', `${field}.parts must be an array.`);
    }
    if (rawParts.length === 0) {
      // An empty array would claim "the breakdown exists and is empty", which is
      // a different statement from "no breakdown for this day". Omit it instead.
      throw snapshotError(
        `${field}.parts`,
        'empty',
        `${field}.parts must be omitted rather than empty when the breakdown is not sent.`,
      );
    }
    if (rawParts.length > MAX_PARTS_PER_DAY) {
      throw snapshotError(
        `${field}.parts`,
        'too_many_parts',
        `${field}.parts may contain at most ${MAX_PARTS_PER_DAY} rows.`,
      );
    }
    totalParts += rawParts.length;
    if (totalParts > MAX_PARTS_PER_SNAPSHOT) {
      throw snapshotError(
        'days',
        'too_many_parts',
        `A snapshot may contain at most ${MAX_PARTS_PER_SNAPSHOT} breakdown rows.`,
      );
    }

    const parts: BreakdownPart[] = [];
    let previousPartKey = -1;
    let sum = 0;
    for (let partIndex = 0; partIndex < rawParts.length; partIndex += 1) {
      const partField = `${field}.parts[${partIndex}]`;
      const tuple = rawParts[partIndex];
      if (!Array.isArray(tuple) || tuple.length !== PART_ARITY) {
        throw snapshotError(
          partField,
          'invalid_type',
          `${partField} must be an array of ${PART_ARITY} integers.`,
        );
      }
      const values: number[] = [];
      for (let slot = 0; slot < PART_ARITY; slot += 1) {
        const value = safeIntOrNull(tuple[slot], 0);
        if (value === null) {
          throw snapshotError(
            `${partField}[${slot}]`,
            'invalid_integer',
            `${partField}[${slot}] must be a non-negative safe integer.`,
          );
        }
        values.push(value);
      }
      const [keyIndex, input, output, cacheRead, cacheWrite, reasoning] = values as [
        number,
        number,
        number,
        number,
        number,
        number,
      ];
      if (keyIndex >= keys.length) {
        throw snapshotError(
          `${partField}[0]`,
          'unknown_key',
          `${partField}[0] does not index an entry of keys.`,
        );
      }
      if (partIndex > 0) {
        if (keyIndex === previousPartKey) {
          throw snapshotError(
            `${partField}[0]`,
            'duplicate_key',
            `${partField}[0] repeats the preceding key index.`,
          );
        }
        if (keyIndex < previousPartKey) {
          throw snapshotError(
            `${partField}[0]`,
            'unsorted_parts',
            `${field}.parts must be listed in ascending key order.`,
          );
        }
      }
      previousPartKey = keyIndex;
      referencedKeys.add(keyIndex);

      sum += input + output + cacheRead + cacheWrite + reasoning;
      if (!Number.isSafeInteger(sum)) {
        throw snapshotError(`${partField}`, 'out_of_range', 'The breakdown sum is outside the safe integer range.');
      }
      parts.push({ keyIndex, input, output, cacheRead, cacheWrite, reasoning });
    }

    if (sum !== totalTokens) {
      // The contract's one arithmetic invariant: the breakdown either explains
      // the whole day total or is not sent for that day.
      throw snapshotError(
        `${field}.totalTokens`,
        'part_sum_mismatch',
        `${field}.totalTokens must equal the sum of the five components of its parts.`,
      );
    }

    days.push({ date, totalTokens, parts });
  }

  // A key that no day references would be dead weight the API has to store and
  // the reader has to skip; the collector builds keys from the rows it keeps.
  for (let index = 0; index < keys.length; index += 1) {
    if (!referencedKeys.has(index)) {
      throw snapshotError(
        `keys[${index}]`,
        'unreferenced_key',
        `keys[${index}] is not referenced by any breakdown row.`,
      );
    }
  }

  return {
    schemaVersion: SNAPSHOT_V2_SCHEMA_VERSION,
    adapterVersion,
    generation,
    collectedAt,
    bucketTimeZone: BUCKET_TIME_ZONE,
    coveredRange: { from, to },
    keys,
    days,
  };
}

/** Like {@link assertKeys}, but the required subset may be smaller than the allowed set. */
function assertKeysV2(
  value: Record<string, unknown>,
  allowed: readonly string[],
  field: string,
  required: readonly string[] = allowed,
): void {
  const prefix = field === '' ? '' : `${field}.`;
  for (const key of Object.keys(value)) {
    if (!allowed.includes(key)) {
      throw snapshotError(
        field === '' ? '$' : field,
        'unexpected_field',
        'The snapshot contains a field that is not part of the v2 contract.',
      );
    }
  }
  for (const key of required) {
    if (!(key in value)) {
      const path = `${prefix}${key}`;
      throw snapshotError(path, 'missing_field', `Field ${path} is required.`);
    }
  }
}

/** Stable serialization used for the idempotency digest. */
export function canonicalSnapshotJson(snapshot: SnapshotBody): string {
  if (snapshot.schemaVersion === SNAPSHOT_V2_SCHEMA_VERSION) {
    return JSON.stringify({
      schemaVersion: SNAPSHOT_V2_SCHEMA_VERSION,
      adapterVersion: snapshot.adapterVersion,
      generation: snapshot.generation,
      collectedAt: snapshot.collectedAt,
      bucketTimeZone: snapshot.bucketTimeZone,
      coveredRange: { from: snapshot.coveredRange.from, to: snapshot.coveredRange.to },
      keys: snapshot.keys.map((key) => [key.client, key.provider, key.model]),
      days: snapshot.days.map((day) =>
        day.parts === undefined
          ? { date: day.date, totalTokens: day.totalTokens }
          : {
              date: day.date,
              totalTokens: day.totalTokens,
              parts: day.parts.map((part) => [
                part.keyIndex,
                part.input,
                part.output,
                part.cacheRead,
                part.cacheWrite,
                part.reasoning,
              ]),
            },
      ),
    });
  }

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

/**
 * Read schema selector for the shared `GET /v1/quota/latest` path.
 *
 * The path is shared on purpose: a client that predates the retention read keeps
 * asking the same URL and receives exactly the v1 body it always received. Only
 * `?schema=2` selects the retention body, and any other value of the selector is
 * refused rather than silently answered with v1 — a caller that asked for a
 * schema must not receive a different one. Other query parameters keep their v1
 * behaviour (ignored), so adding the selector cannot change an existing request.
 */
export function parseQuotaReadQuery(url: URL): 1 | 2 {
  const values = url.searchParams.getAll('schema');
  if (values.length === 0) return 1;
  if (values.length > 1) {
    throw queryError('schema', 'duplicate_parameter', 'Query parameter schema must be provided at most once.');
  }
  if (values[0] !== '2') {
    throw queryError('schema', 'unsupported_schema', 'schema must be 2 when it is provided.');
  }
  return 2;
}

/**
 * Parses the shared `from`/`to`/`collector` query.
 *
 * `maxSpanDays` is the only thing the two read routes decide differently: the
 * daily read accepts a year, the breakdown three months (see
 * {@link MAX_BREAKDOWN_QUERY_SPAN_DAYS}). The error for an over-long range is the
 * same 400 `invalid_query`/`range_too_long` shape on both, so a caller does not
 * have to know which route answered to understand it.
 */
export function parseDailyQuery(url: URL, maxSpanDays: number = MAX_DAILY_QUERY_SPAN_DAYS): DailyQuery {
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
  if (span > maxSpanDays - 1) {
    throw queryError('from', 'range_too_long', `The range must not exceed ${maxSpanDays} days.`);
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
