# QuotaSight.Usage.Api (first vertical slice, local only)

A bounded Cloudflare Workers + Hono + D1 service that accepts per-collector
daily token snapshots and serves owner-scoped daily totals. This package is the
first vertical slice of the remote usage design; it is not the full design.

Everything here runs **locally**: Miniflare/`wrangler dev` and a local D1
database. There is no deployment configuration, no account id, no remote
binding, no real credential and no login flow in this package.

## Endpoints

```http
PUT /v1/collectors/{collectorId}/snapshots/{revision}
GET /v1/usage/daily?from=YYYY-MM-DD&to=YYYY-MM-DD[&collector=collectorId]
```

`PUT` returns `{ status, collectorId, generation, revision, receivedAt, changedDays }`
where `status` is `applied` or `replayed`. `GET` returns
`{ from, to, days: [{ date, collectorId, totalTokens }], totalTokens }`.

## Snapshot contract (schema v1, narrow)

```json
{
  "schemaVersion": 1,
  "adapterVersion": "tokscale-4.23.0",
  "generation": 1,
  "collectedAt": "2026-11-20T03:00:00Z",
  "bucketTimeZone": "Asia/Tokyo",
  "coveredRange": { "from": "2026-11-18", "to": "2026-11-19" },
  "days": [
    { "date": "2026-11-18", "totalTokens": 100 },
    { "date": "2026-11-19", "totalTokens": 0 }
  ]
}
```

Enforced strictly (unknown keys are rejected, never ignored):

- `schemaVersion` must be `1`; `bucketTimeZone` must be `Asia/Tokyo`.
- `generation`, `revision` and `totalTokens` must be non-negative/positive JS
  safe integers; `totalTokens` is per-day and never floating point.
- Dates are strict `YYYY-MM-DD` calendar dates; `days` is ascending, unique, and
  contained in `coveredRange`.
- Bounds: body ≤ 32 KiB, `days` ≤ 62, covered range ≤ 62 days, query range ≤ 366
  days, at most 5000 rows per read (a larger result is refused, never truncated).

## Write semantics

- **Delta of positive authoritative snapshots.** A write replaces only the day
  partitions it carries, with the snapshot's authoritative total. Only strictly
  higher totals (or a first observation) change a row, and only changed days are
  reported in `changedDays`.
- **Never delete on absence.** A day missing from a later snapshot keeps its last
  accepted value.
- **Reductions are rejected** (`409 reduction_unverified`) for this milestone:
  there is no way to verify a correction yet, so the previous value is kept.
- **Measured zero is data.** `totalTokens: 0` for a previously absent day is
  accepted and stored; it is distinct from "not observed" (no row).
- **Idempotent and ordered.** Replaying the same `revision` with the identical
  payload is a no-op (`replayed`); the same revision with a different payload is
  `409 revision_conflict`; a revision older than the accepted maximum is
  `409 stale_revision` and never overwrites. Revisions are scoped to
  (collector, generation).
- **Active generation only.** A snapshot for a superseded generation is
  `409 generation_inactive`.
- **Atomic.** Days and the receipt are written in one D1 `batch()` transaction;
  the guards that abort a write are schema triggers, so a rejected write cannot
  leave a receipt without its days (or vice versa).

## Transport bounds and error hygiene

- **The byte bound applies to the bytes that arrive.** `content-length` is
  caller-supplied, so it is used only as an early rejection; the real ceiling is
  enforced while the request body is streamed in, and decoding/`JSON.parse` only
  happens afterwards. A chunked body with no `content-length` is counted chunk by
  chunk and aborted at 32 KiB, and an oversized body with invalid UTF-8 is
  reported as `413 payload_too_large`, not as a decode or JSON error. Invalid
  UTF-8 inside the bound is `400 invalid_json` (reason `invalid_encoding`), and a
  malformed `content-length` is `400 invalid_request` rather than trusted.
- **Exact media type.** The `Content-Type` type token must be exactly
  `application/json` (parameters such as `charset=utf-8` and case differences are
  accepted). `application/jsonx`, `application/json-patch+json`, `text/json` and
  friends are `415 unsupported_media_type`.
- **No caller input is echoed.** An unknown snapshot field reports the
  server-derived path of its containing object (`$`, `coveredRange`, `days[0]`)
  and a generic message; an unknown query parameter reports no name at all.
  Contract names (for example `from` or `days[0].date`) are still reported, so a
  client can fix a legitimate request.
- **Duplicate query parameters are rejected** (`400 invalid_query`, reason
  `duplicate_parameter`) instead of silently using the first value.
- **Reads are bounded end to end.** The list query asks for one row more than the
  documented maximum: `5000` rows are served, `5001` are `413 result_too_large`
  (reason `too_many_rows`). The aggregate is summed with a safe-integer check, so
  a total that would leave `Number.MAX_SAFE_INTEGER` is `422 result_too_large`
  (reason `sum_out_of_range`) instead of a rounded number.

## Access model

- **Order of checks** for a write: authenticate → `usage:write` → collector
  binding → media type → bounded body → contract validation → owner-filtered
  lookup → active generation → atomic write.
- **The binding is checked before any lookup.** A write credential names exactly
  one collector, so any other id is `403 forbidden_collector` — including ids
  that do not exist. A credential therefore cannot probe for existence.
- **Every lookup is owner-filtered** (`WHERE id = ? AND owner_id = ?`). If the
  credential's own collector row is missing, the write is `404
  collector_not_found`; for reads, another owner's collector id and an id that
  does not exist produce the same `404 collector_not_found` body.
- **A collector-bound identity cannot read owner-wide usage.** `usage:read`
  covers every collector of the owner, so a credential with a collector binding
  is `403 forbidden_scope` on `GET /v1/usage/daily`, even if it also holds
  `usage:read`.

## Authentication

`createApp({ authenticator })` takes an injected `Authenticator`. The runtime
entry point (`src/index.ts`) wires `UnconfiguredAuthenticator`, which denies
every request: there is no environment-variable token, hardcoded secret or
fallback identity. Tests inject fixture identities. The collector id in the path
or query is only a lookup key: the credential's own binding is checked before the
lookup, and ownership plus the active generation are checked in the database
through owner-filtered statements.

Errors are JSON: `{ "error": { "code": "...", "message": "...", "details"?: {...} } }`
with codes `unauthorized`, `forbidden_scope`, `forbidden_collector`,
`collector_not_found`, `invalid_snapshot`, `invalid_revision`, `invalid_query`,
`invalid_request`, `invalid_json`, `unsupported_media_type`, `payload_too_large`,
`result_too_large`, `revision_conflict`, `stale_revision`,
`reduction_unverified`, `generation_inactive`.

## Local development and tests

```bash
npm install
npm run typecheck
npm test                                     # vitest -> bundled Worker in workerd + local D1
npx wrangler d1 migrations apply DB --local  # optional: local .wrangler state
npx wrangler dev --local                     # local Worker on http://localhost:8787
```

Tests do not mock the database: `test/harness.ts` bundles the Worker with
esbuild, starts Miniflare (`workerd`), applies every `migrations/*.sql` file to a
real local D1 database and dispatches real `fetch` requests into it.

### Read index and query plan

`migrations/0002_read_indexes.sql` adds `collectors_by_owner (owner_id, id)` so
the owner filter drives the read instead of the date range. The plan and the
latency were measured on the local D1 before adding it (5000 rows for the owner,
5000 for a second owner, 366-day window, 20 iterations of the exact statement in
`src/store.ts`):

| | plan | time |
|---|---|---|
| before | `SEARCH u USING INDEX usage_day_by_bucket_date` → `SEARCH c USING INDEX sqlite_autoindex_collectors_1 (id=?)` | 725 ms |
| after | `SEARCH c USING INDEX collectors_by_owner (owner_id=?)` → `SEARCH u USING PRIMARY KEY` → temp b-tree for order | 630 ms |

So the read is now owner-driven, without a measured latency regression.
`usage_day_by_bucket_date` is kept: nothing measured argues for removing it, and
both indexes are asserted in `test/access.test.ts`, which also asserts that the
plan for the production SQL does not scan `usage_day`.

## No exposure

`wrangler.toml` sets `workers_dev = false` and `preview_urls = false`, has no
`account_id`, no `route`/`routes`, no `zone_id` and no remote binding.
`test/config.test.ts` asserts this against the real configuration file, so a
later edit cannot quietly publish the Worker.

## Deliberately pending (not part of this slice)

These are deferred as one coherent later package rather than being half-built
here; nothing in this slice pretends to cover them:

- **Server clock authority and retention/pruning.** `receivedAt` is the Worker's
  `now()` and `collectedAt` is only shape-validated. Deciding who owns time
  (clock skew budgets, recorded-vs-received time) and how long rows live are the
  same decision, so both are deferred; no retention job exists.
- **Generation rotation.** `generation` is written and enforced against
  `collectors.active_generation`, but there is no operation that rotates it, so
  the only way to change it is a direct database update. Rotation needs its own
  migration story and is deferred.
- **Receipt high-water migration.** Revision ordering is currently enforced by a
  trigger that compares against the accepted receipts of
  (collector, generation). Replacing that scan with a stored high-water revision
  changes the schema and needs a backfill, so it is deferred as part of the same
  later package.

Also out of scope for this slice:

- Source / model / provider breakdown and `client` filtering.
- Real authentication and credential issuance/rotation/revocation, management
  and delete operations, `GET /v1/collectors`.
- Source coverage and per-source last-observation status, `sourceStates`,
  `schemaVersion` migration.
- Verified reductions (explicit re-collection or reset), multi-owner accounts,
  deployment configuration and any remote D1 access.
- No production hardcoded secret, no fallback auth: wiring real authentication is
  a prerequisite for any deployment.
