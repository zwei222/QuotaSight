import { ApiError } from './errors';

/**
 * One window of the contract quota, exactly as the collector sends it.
 *
 * `remainingBasisPoints` is `null` when the window carries no observable number
 * (an unlimited window, or an unknown one). `unlimited` is only ever present and
 * `true` for a window the collector proved unlimited.
 */
export interface QuotaWindow {
  readonly window: string;
  readonly remainingBasisPoints: number | null;
  readonly resetsAt: string | null;
  readonly unlimited?: true;
}

export type QuotaProviderStatus = 'available' | 'unavailable';
export type QuotaFreshness = 'fresh' | 'cached' | 'unknown';

/** One provider group of the contract quota, exactly as the collector sends it. */
export interface QuotaProviderEntry {
  readonly provider: string;
  readonly status: QuotaProviderStatus;
  readonly freshness: QuotaFreshness;
  readonly observedAt: string | null;
  readonly windows: readonly QuotaWindow[];
}

export interface QuotaPayload {
  readonly schemaVersion: 1;
  readonly generation: number;
  readonly collectedAt: string;
  readonly providers: readonly QuotaProviderEntry[];
}

/**
 * The seven fixed provider groups, in the order every read reports them.
 *
 * Fixed rather than derived from the payload: the retention read has to answer
 * for a group the collector did not report at all (`missing`), so "which groups
 * exist" cannot depend on what one collection happened to contain.
 */
export const QUOTA_PROVIDERS = [
  'claude',
  'codex',
  'opencode-go',
  'antigravity-gemini',
  'antigravity-claude-gpt',
  'grok-build',
  'copilot',
] as const;

const windows: Record<string, readonly string[]> = {
  claude: ['session', 'weekly'], codex: ['five-hour', 'weekly'], 'opencode-go': ['rolling', 'weekly', 'monthly'],
  'antigravity-gemini': ['five-hour', 'weekly'], 'antigravity-claude-gpt': ['five-hour', 'weekly'],
  'grok-build': ['weekly', 'monthly'], copilot: ['premium', 'chat', 'completions'],
};
const iso = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{3})?Z$/;
const keys = (v: Record<string, unknown>, wanted: readonly string[]): boolean =>
  Object.keys(v).length === wanted.length && wanted.every(k => Object.hasOwn(v, k));
const instant = (v: unknown, millis?: boolean): v is string =>
  typeof v === 'string' && iso.test(v) && (millis !== false || !v.includes('.')) &&
  !Number.isNaN(Date.parse(v)) && new Date(Date.parse(v)).toISOString().replace('.000Z','Z') === v.replace('.000Z','Z');

export function parseQuota(raw: unknown, now: Date): QuotaPayload {
  const fail = (): never => { throw new ApiError(400, 'invalid_request', 'The quota snapshot does not match the contract.'); };
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return fail();
  const root = raw as Record<string, unknown>;
  if (!keys(root, ['schemaVersion','generation','collectedAt','providers']) || root.schemaVersion !== 1 ||
      !Number.isSafeInteger(root.generation) || (root.generation as number) <= 0 || !instant(root.collectedAt, true) ||
      Date.parse(root.collectedAt) > now.getTime() + 5 * 60_000 || !Array.isArray(root.providers) || root.providers.length > 7) return fail();
  const seen = new Set<string>();
  for (const item of root.providers) {
    if (!item || typeof item !== 'object' || Array.isArray(item)) return fail();
    const p = item as Record<string, unknown>;
    if (!keys(p, ['provider','status','freshness','observedAt','windows']) || !(QUOTA_PROVIDERS as readonly string[]).includes(p.provider as string) ||
      seen.has(p.provider as string) || !['available','unavailable'].includes(p.status as string) ||
      !['fresh','cached','unknown'].includes(p.freshness as string) ||
      !(p.observedAt === null || instant(p.observedAt, false)) || !Array.isArray(p.windows)) return fail();
    seen.add(p.provider as string);
    const windowSeen = new Set<string>();
    for (const itemWindow of p.windows) {
      if (!itemWindow || typeof itemWindow !== 'object' || Array.isArray(itemWindow)) return fail();
      const win = itemWindow as Record<string, unknown>;
      const hasUnlimited = Object.hasOwn(win, 'unlimited');
      const allowedWindowKeys = p.provider === 'copilot' && hasUnlimited
        ? ['window', 'remainingBasisPoints', 'resetsAt', 'unlimited']
        : ['window', 'remainingBasisPoints', 'resetsAt'];
      if (!keys(win, allowedWindowKeys) || (hasUnlimited && (p.provider !== 'copilot' || win.unlimited !== true || win.remainingBasisPoints !== null)) ||
        !windows[p.provider as string]!.includes(win.window as string) ||
        windowSeen.has(win.window as string) || !(win.remainingBasisPoints === null || (Number.isInteger(win.remainingBasisPoints) && (win.remainingBasisPoints as number) >= 0 && (win.remainingBasisPoints as number) <= 10000)) ||
        !(win.resetsAt === null || instant(win.resetsAt, false))) return fail();
      windowSeen.add(win.window as string);
    }
  }
  return root as unknown as QuotaPayload;
}

/**
 * True when a provider group carries a value a reader may use.
 *
 * `available` alone is not enough: an entry whose windows are all `null` (and
 * none of them proven unlimited) is an availability with no observable number,
 * which the retention contract reports as `unavailable`, never as a value. An
 * explicit `unavailable` never carries one either, whatever its windows say.
 */
export function isEffectiveQuotaProvider(entry: QuotaProviderEntry): boolean {
  if (entry.status !== 'available') return false;
  return entry.windows.some(
    (window) => window.unlimited === true || typeof window.remainingBasisPoints === 'number',
  );
}

/**
 * How far a provider's observation time may lead the collection that carried it.
 *
 * The same five minutes `parseQuota` allows a collection to lead the Worker's
 * clock: a group cannot have observed its quota after the collection that
 * carried the observation, so anything beyond this tolerance is clock skew or a
 * bogus claim, not a value.
 */
export const MAX_OBSERVATION_LEAD_MS = 5 * 60_000;

/**
 * True when the entry's observation time is later than its own collection time
 * by more than {@link MAX_OBSERVATION_LEAD_MS}.
 *
 * `observedAt` is the wire's second-precision instant and `collectedAt` the
 * millisecond snapshot instant, so both are compared as parsed instants rather
 * than as text (`...:00Z` sorts after `...:00.500Z` as text but is half a second
 * earlier as a time).
 */
export function isFutureQuotaObservation(entry: QuotaProviderEntry, collectedAt: string): boolean {
  return (
    entry.observedAt !== null &&
    Date.parse(entry.observedAt) > Date.parse(collectedAt) + MAX_OBSERVATION_LEAD_MS
  );
}

/**
 * True when a value may be stored as the last successful one.
 *
 * On top of {@link isEffectiveQuotaProvider}:
 *   * a `cached` entry with no observation time cannot be shown to be newer than
 *     what is stored, so it is never stored. The collector reports Codex,
 *     OpenCode Go, Grok and Copilot as `unknown` with no `observedAt` at all, and
 *     those entries are real observations — the value is kept, and the
 *     collection time is the only time it has. A `fresh` entry without an
 *     observation time is kept for the same reason it always was: the collector
 *     just dated the whole collection rather than the provider group;
 *   * an observation dated after its own collection (beyond
 *     {@link MAX_OBSERVATION_LEAD_MS}) is not a success at all. The write
 *     contract still accepts the body — the v1 snapshot and its HTTP result are
 *     unchanged — but such a claim must not become the last known value, and must
 *     not win the recency comparison against every later, sane observation.
 */
export function shouldRetainQuotaValue(entry: QuotaProviderEntry, collectedAt: string): boolean {
  return (
    isEffectiveQuotaProvider(entry) &&
    !(entry.freshness === 'cached' && entry.observedAt === null) &&
    !isFutureQuotaObservation(entry, collectedAt)
  );
}
