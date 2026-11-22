import { ApiError } from './errors';

export interface QuotaPayload {
  readonly schemaVersion: 1;
  readonly generation: number;
  readonly collectedAt: string;
  readonly providers: readonly unknown[];
}

const providers = ['claude', 'codex', 'opencode-go', 'antigravity-gemini', 'antigravity-claude-gpt', 'grok-build', 'copilot'] as const;
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
    if (!keys(p, ['provider','status','freshness','observedAt','windows']) || !providers.includes(p.provider as typeof providers[number]) ||
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
