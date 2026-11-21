# QuotaSight.Usage.Api (first vertical slice, local only)

A bounded Cloudflare Workers + Hono + D1 service that accepts per-collector
daily token snapshots — daily totals, and optionally a per-client/provider/model
breakdown — and serves owner-scoped daily totals, that breakdown per key, and the
attribution of each day's total. This package is the first vertical slice of the
remote usage design; it is not the full design.

Everything here runs **locally**: Miniflare/`wrangler dev` and a local D1
database. There is no account id, no remote binding and no credential in this
repository. Authentication is Cloudflare Access service-token JWT verification,
configured per deployment and fail-closed when that configuration is absent.

**Deployment plans:** a real deployment is budgeted for the Workers Free, D1 Free
and Zero Trust Free plans; no subscription is required. Workers Free allows 10 ms
of CPU time per request, and only the Worker's own JavaScript is measured — waiting
on a D1 query is not. What is measured is the RS256 verification of the Access
assertion, the `JSON.parse` of a body of up to 32 KiB, the assembly of the write's
`batch()` — up to 63 statements for a v1 body with 62 days, four statements for a
v2 body whatever its size, with the per-row `json_each` decomposition on top of
that — and the `JSON.stringify` of the response, so
Cloudflare's guidance that authentication-handling Workers typically use 10-20 ms
puts this slice at the same order as the limit. The local suite settles correctness
rather than CPU, and the one measurement of a deployed Worker — one account on
2026-11-22, recorded in the runbook — saw no Error 1102 in 24 hours but only 53
invocations including cold starts, so this slice is **conditionally feasible** on
the free plans (unverified). Whether it holds is decided by the **CPU measurement
gate** in the runbook — zero `Exceeded CPU Time Limits` (Error 1102) events over
24 hours and a CPU-time P99 below 8 ms measured on the deployed Worker — and by
nothing else; that gate is still undecided, and you must not upgrade the account
to answer it. The operator runbook — ordering, the gate, the fallback options
that are deliberately not applied, the remote smoke test, the rotation drill and
the list of behaviour that only a real deployment can settle — is
[docs/deployment.md](../../docs/deployment.md).

## Endpoints

```http
PUT  /v1/collectors/{collectorId}/snapshots/{revision}
GET  /v1/usage/daily?from=YYYY-MM-DD&to=YYYY-MM-DD[&collector=collectorId]
GET  /v1/usage/breakdown?from=YYYY-MM-DD&to=YYYY-MM-DD[&collector=collectorId]
```

`PUT` accepts two body shapes, selected by `schemaVersion`:

- `1` — daily totals only. Returns
  `{ status, collectorId, generation, revision, receivedAt, changedDays, ignoredReductions }`;
- `2` — daily totals plus the per-client/provider/model breakdown. Returns the
  same seven fields, in the same order, followed by `changedParts` and
  `ignoredPartReductions`.

`status` is `applied` or `replayed`. `GET /v1/usage/daily` returns
`{ from, to, days: [{ date, collectorId, totalTokens }], totalTokens, collectors }`
and `GET /v1/usage/breakdown` returns
`{ from, to, parts, coverage, collectors }` (see "Breakdown read").

### Read freshness (`collectors`)

```json
{
  "from": "2026-11-01",
  "to": "2026-11-30",
  "days": [{ "date": "2026-11-18", "collectorId": "collector-a", "totalTokens": 100 }],
  "totalTokens": 100,
  "collectors": [
    { "collectorId": "collector-a", "generation": 1, "lastReceivedAt": "2026-11-20T03:04:05.006Z" },
    { "collectorId": "collector-b", "generation": 2, "lastReceivedAt": null }
  ]
}
```

A daily total says nothing about its own age, so every read also reports, per
collector it covers:

- one entry per collector of the owner, sorted by `collectorId` ascending;
- `generation` is the collector's **active** generation at read time;
- `lastReceivedAt` is the reception time of the newest accepted receipt of that
  active generation, as an ISO-8601 UTC instant with milliseconds (`null` when
  that generation has no receipt yet). Receipts of a superseded generation are
  never reported; a collector re-provisioned to a new generation therefore reads
  as `null` until it reports again.
- A collector that has never reported is **included** with `lastReceivedAt:
  null` rather than omitted, so "configured but not yet reporting" stays
  distinguishable from "no such collector". An omission would make the two look
  identical to a client that only sees the list.
- The list is bounded (`collectors` ≤ 64 per read). A longer list is refused with
  `413 result_too_large` instead of being truncated.
- `collector` filtering narrows both `days` and `collectors`; another owner's
  collectors never appear, exactly like the day rows.

### Only the active generation is readable

Every read joins `usage_day` on the collector's **active** generation:

```sql
JOIN collectors c ON c.id = u.collector_id
WHERE u.generation = c.active_generation
```

So after an operator raises a collector's `active_generation`, the read returns
**no** day rows for that collector until it reports again in the new
generation, and its freshness entry reads `lastReceivedAt: null` (verified by
`test/freshness.test.ts`, which also asserts the old rows still exist).

This is a deliberate limitation of this slice, not a rotation feature:

- the rows of a superseded generation are **retained** in `usage_day` (the
  table is keyed by `(collector_id, generation, bucket_date)` on purpose), but
  no read path returns them;
- there is no endpoint, query parameter or filter that reads a past generation,
  so those retained rows are currently unreadable through the API;
- generation rotation itself is still an operator database update (see
  "Deliberately pending" below), so no client can produce this state on its own.

The comment in `migrations/0001_initial.sql` that a superseded generation
"keeps its history" describes storage only. It does not promise that history is
readable: keeping the rows is what makes a future read-a-past-generation
feature possible without a data loss step, and nothing here exposes it today.


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
  days, at most 5000 rows and 64 collectors per read (a larger result is refused,
  never truncated).

## Snapshot contract (schema v2: the breakdown, opt-in)

```json
{
  "schemaVersion": 2,
  "adapterVersion": "tokscale-4.23.0",
  "generation": 1,
  "collectedAt": "2026-11-20T03:00:00Z",
  "bucketTimeZone": "Asia/Tokyo",
  "coveredRange": { "from": "2026-11-18", "to": "2026-11-19" },
  "keys": [
    ["codex", "", "gpt-5.1"],
    ["codex", "openai", "gpt-5.1"]
  ],
  "days": [
    { "date": "2026-11-18", "totalTokens": 100, "parts": [[0, 70, 10, 5, 5, 10]] },
    { "date": "2026-11-19", "totalTokens": 0 }
  ]
}
```

v2 keeps every v1 key with its v1 meaning and in the v1 order, and adds two:

- `keys` — the distinct `(client, provider, model)` triples the parts index into,
  ascending, unique, at most 128 per snapshot. Each entry is
  `[client, provider, model]` in normalized form.
- `days[].parts` — that day's breakdown rows, ascending by key index, each
  `[keyIndex, input, output, cacheRead, cacheWrite, reasoning]`, at most 32 rows
  per day.

Enforced strictly (unknown keys are rejected, never ignored):

- **A day either omits `parts` or carries its whole breakdown.** `parts` is never
  empty (that would claim "the breakdown exists and is empty", a different
  statement from "no breakdown"), and the five components of its rows must add up
  to exactly that day's `totalTokens`. This is the contract's one arithmetic
  invariant: a day is explained completely or not at all, so a reader can never
  see a breakdown that silently covers part of a day.
- **Key indices are sorted and unique** within a day, and every entry of `keys` is
  referenced by at least one row — an unreferenced key would be dead weight the
  API has to store and the reader has to skip.
- **Identifiers are the normalized forms the API stores.** `client` matches
  `[a-z0-9][a-z0-9._-]{0,31}`; `provider` is an ascending comma-joined list of at
  most 4 lowercase elements (max 64 characters); `model` is lowercase, at most 96
  characters and never a path (no leading separator, no backslash, no drive
  letter, no local weight-file suffix, at most one `/` so `org/model` survives).
  An identifier the collector cannot read is the empty string — an unknown key,
  never a repaired guess.
- **Bounds:** body ≤ 32 KiB, `days` ≤ 62 (ascending, unique, inside
  `coveredRange`), `keys` ≤ 128, `parts` ≤ 32 per day and ≤ 1024 per snapshot.
- **The collector keeps itself below this ceiling.** Its default
  `--parts-budget-bytes` is `16384` (16 KiB), down from 30 KiB after the
  2026-11-22 measurement in the deployment runbook, so a default v2 send arrives
  with headroom under the 32 KiB body bound. To stay inside it the collector
  drops the oldest day's breakdown first, and it never drops a day total.

## Write semantics

- **Delta of positive authoritative snapshots.** A write replaces only the day
  partitions it carries, with the snapshot's authoritative total. A day is
  changed only when the incoming total is higher than the accepted one (or the
  day is new), and only changed days are listed in `changedDays`. `changedDays`
  therefore always describes what was actually written, never what was merely
  submitted.
- **A decreased day keeps the accepted total.** Daily totals are cumulative, and
  a re-scan can carry a smaller number for the same day (a shorter log window,
  a rotated file). Such a day is not written: the accepted total stays, the
  receipt counts it in `ignoredReductions` (an integer, `0` or more), and the
  rest of the snapshot is accepted normally. The API does **not** record that
  the upstream total went back — there is no correction, only "the previous
  value is still the higher one".
- **An equal total is neither a change nor an ignored reduction.** It is a
  no-op and is reported in neither field.
- **All-decreased snapshots are accepted.** A snapshot whose every day is lower
  is `200 applied` with `changedDays: []` and `ignoredReductions` equal to the
  number of days it carried. The revision is consumed either way.
- **Never delete on absence.** A day missing from a later snapshot keeps its last
  accepted value.
- **Measured zero is data.** `totalTokens: 0` for a previously absent day is
  accepted and stored; it is distinct from "not observed" (no row).
- **Idempotent and ordered.** Replaying the same `revision` with the identical
  payload is a no-op (`replayed`) that returns the first write's `receivedAt`,
  `changedDays: []` and the same `ignoredReductions` (the count is stored in the
  receipt, not recomputed); the same revision with a different payload is
  `409 revision_conflict`; a revision older than the accepted maximum is
  `409 stale_revision` and never overwrites. Revisions are scoped to
  (collector, generation).
- **Active generation only.** A snapshot for a superseded generation is
  `409 generation_inactive`.
- **Atomic.** Days and the receipt are written in one D1 `batch()` transaction;
  the guards that abort a write are statements and schema triggers, so a
  rejected write cannot leave a receipt without its days (or vice versa), and an
  ignored reduction can never be half-applied. The D1 documentation describes the
  rollback scope of a partly failing `batch()` inconsistently; the runbook's
  check 4 observed the whole batch rolling back on remote D1 on 2026-11-22, in one
  account, so this is measured rather than assumed — for that deployment.

### How the breakdown is written (v2)

A day is the unit, exactly as it is for the totals — but for the breakdown the
unit is the day's **whole set of rows**, not one row per key:

- the write path deletes a day's stored rows and inserts the incoming set in the
  same transaction as the day total and the receipt, and only for a day whose
  incoming total is greater than the sum its stored rows already add up to. The
  first v2 write of a day has no stored rows, so it inserts;
- a day whose incoming breakdown adds up to the same or less than the stored set
  is left alone. The receipt counts those days in `ignoredPartReductions` (a
  **day** count, not a row count) and reports the rows this write inserted in
  `changedParts`;
- a day that arrives without `parts` says nothing about a breakdown: its total
  may move, and it neither replaces a stored set nor counts as a reduction.

The reasoning is the one the total already follows, applied to a group: per-row
maxima would let two snapshots add their explanations together (100 stored for
one model, 40 + 60 for two, i.e. 160 attributed against a day total of 100),
which is data the collector never observed.

Three consequences are deliberate and worth knowing before you rely on the
breakdown:

- **An equal total is not a new explanation.** A re-scan that moves tokens from
  one model to another while the day total stays the same is ignored, and the day
  keeps the set that first reached that total.
- **A changed normalization rule does not repair past days.** The identifiers of
  an accepted day are stored as they were sent. If a later collector release
  normalizes a value differently, earlier days keep the old spelling until they
  are written again — which only happens when their day total grows. Starting a
  fresh breakdown means raising the collector's `active_generation`, which is an
  operator database update (see "Deliberately pending").
- **Attribution never exceeds the day.** `usage_part` rows are only accepted up
  to `usage_day.total_tokens` for the same collector, generation and day. A table
  CHECK keeps `total_tokens` equal to the sum of the five components, and a
  trigger refuses any insert that would push the day's rows past the day as a
  whole, so a breakdown read can always state `attributed ≤ total`. Reaching that
  guard is a defect, not a client error: it answers `500 internal_error`
  (`reason: write_failed`) and the whole batch rolls back, leaving no day, no row
  and no receipt behind.

v1 and v2 writes can be mixed against the same collector: a v1 write never touches
`usage_part`, so a collector that is later switched to v2 starts from an empty
breakdown for every day it reports again — and those days' sets are accepted even
though their totals were already accepted by v1 writes, because a day's set is
compared with the sum its stored rows add up to (zero), not with the day total.

### Why a decrease is ignored rather than refused

A refusal would let one short upstream scan stop every later snapshot for that
collector: the collector blocks on a `409` until an operator intervenes, and the
upstream number is not something it can repair. Keeping the accepted total is
the conservative reading of a cumulative total — the stored value is the maximum
ever accepted for that day, so a day can be over-counted but never
under-counted. A client that needs to know this happened reads
`ignoredReductions` on the receipt.

## Breakdown read

```http
GET /v1/usage/breakdown?from=YYYY-MM-DD&to=YYYY-MM-DD[&collector=collectorId]
```

```json
{
  "from": "2026-11-01",
  "to": "2026-11-30",
  "parts": [
    {
      "client": "codex",
      "provider": "openai",
      "model": "gpt-5.1",
      "input": 150,
      "output": 30,
      "cacheRead": 0,
      "cacheWrite": 0,
      "reasoning": 0,
      "total": 180
    }
  ],
  "coverage": [
    { "date": "2026-11-17", "collectorId": "collector-a", "totalTokens": 400, "attributedTokens": 380 },
    { "date": "2026-11-18", "collectorId": "collector-a", "totalTokens": 250, "attributedTokens": 0 }
  ],
  "collectors": [
    { "collectorId": "collector-a", "generation": 1, "lastReceivedAt": "2026-11-20T03:04:05.006Z" }
  ]
}
```

- **`parts`** — one row per `(client, provider, model)` over the whole range, its
  five components summed, largest `total` first; ties break on client, then
  provider, then model.
- **`coverage`** — one row per collector-day in the range: the accepted day total
  next to the part of it the breakdown explains (`attributedTokens`, `0` when no
  breakdown is stored). This is what makes a partial breakdown visible instead of
  implied — `attributedTokens < totalTokens` means that day's key-level split is
  unknown, withheld or was never sent, and the API does not spread the difference
  over the keys it does know.
- **`collectors`** — the same freshness list `GET /v1/usage/daily` returns.
- **Scope** — `usage:read`, owner-wide. A collector-bound credential is `403
  forbidden_scope`, another owner's rows never appear, only the collector's active
  generation is read, and `collector` narrows both lists.
- **Bounds** — the range is at most **92 days** (three months), narrower than the
  daily read's 366, because this read aggregates every key of every day.
  `parts` is capped at **2000** rows and `coverage` at **5000**; going over is
  `413 result_too_large` (`reason: too_many_parts` / `too_many_coverage_rows`),
  never a truncated list that would look complete. Aggregated values are
  safe-integer checked, so an out-of-range sum is `422 result_too_large`
  (`reason: sum_out_of_range`) rather than a rounded number.
- **Plans** — owner-driven from `collectors_by_owner`, then a primary-key range per
  collector, and the coverage attribution is one primary-key seek per row.
  `test/storage-v2.test.ts` asserts both plans against the production SQL, so a
  change to either is a test failure rather than a silent scan.

## Data model

Two tables hold usage, and both are keyed by `(collector_id, generation, …)` so a
superseded generation keeps its history and can never be written again:

- **`usage_day (collector_id, generation, bucket_date, total_tokens, updated_at)`**
  — the authoritative daily total. It only ever moves up (the upsert stores the
  larger value and the day is listed in `changedDays` only when the incoming total
  won).
- **`usage_part (collector_id, generation, bucket_date, client, provider, model,
  input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
  reasoning_tokens, total_tokens, updated_at)`** — the breakdown, one row per key
  per day per generation. That six-column primary key is the table's only index:
  every read filters on collector, generation and a date range, and every write
  seeks by the same prefix.

What the schema enforces (so a statement that bypasses `store.ts` still cannot
store an inconsistent breakdown): only the collector's active generation may be
written; `total_tokens` equals the sum of the five components and every value is a
non-negative safe integer; identifiers are bounded to the contract's own limits
(client ≤ 32, provider ≤ 64, model ≤ 96 characters); a stored row is never edited
to a lower total in place; and a day's rows never add up to more than that day's
`usage_day.total_tokens` (a trigger comparing against the day's own primary key).

What lives in `store.ts` instead, because a table cannot express it: **when** a
day's set is replaced. A day's rows are deleted and re-inserted as a whole only
when the incoming breakdown adds up to more than the stored one, and the whole
write — day totals, the deletes, the inserts, the receipt — is one `batch()`, one
transaction.

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
- **A malformed path segment reuses the query error code.** A `collectorId`
  that fails its slug rule is `400 invalid_query` with
  `details.field: "collectorId"` (it is validated by the same helper as the
  query filter), while a malformed `revision` is `400 invalid_revision`. Both
  are current behaviour: the transport is not re-shaped in this slice, and no
  client branches on the difference — each is a `4xx` the Collector treats as a
  rejected request.
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

The Worker verifies Cloudflare Access **service token** JWTs. It does not issue,
rotate or revoke credentials; those stay in Access. There is no bearer token in
an environment variable, no hardcoded secret and no fallback identity.

`src/index.ts` reads configuration from the Worker environment once per isolate
and keeps the JWKS cache for that isolate. If any required value is missing or
invalid, it installs `UnconfiguredAuthenticator`, emits one fixed
`access_config_invalid` log line and answers every request with `503
auth_unconfigured` (never `401`, never a 500). It does not fetch a key set in
that case. A valid configuration installs `AccessJwtAuthenticator`, which:

1. Reads the JWT only from the `Cf-Access-Jwt-Assertion` header. `Authorization`,
   cookies (including `CF_Authorization`) and `Cf-Access-Authenticated-User-Email`
   are ignored. A missing, empty or oversized (over 8 KiB) header is denied.
2. Verifies the token as RS256 against the team's Access JWKS
   (`https://<name>.cloudflareaccess.com/cdn-cgi/access/certs`). The URL is built
   from the configured team domain, never from a token `iss` or `jku`. Issuer and
   audience must match exactly (a string or an array; a prefix is not a match).
   `exp` and `iat` are required. Clock tolerance is 60 seconds. Maximum token age
   defaults to 24 hours.
3. Takes the client id from the `common_name` claim and looks it up in the
   server-side principal map. Claims inside the JWT, including any scope claim,
   never grant permissions.

### Configuration

Set these as Worker secrets or a local `.dev.vars` (see `.dev.vars.example`).
Do not put them in `wrangler.toml` `[vars]`.

| Key | Meaning |
| --- | --- |
| `ACCESS_TEAM_DOMAIN` | Team domain origin, `https://<name>.cloudflareaccess.com` |
| `ACCESS_AUD` | Access application AUD tag, or several separated by commas |
| `ACCESS_PRINCIPALS` | JSON array of principals (see below) |
| `ACCESS_MAX_TOKEN_AGE_SECONDS` | Optional. Integer seconds from 1 to 604800. Default 86400 |

Placeholder shape (not a real deployment):

```text
ACCESS_TEAM_DOMAIN=https://example.cloudflareaccess.com
ACCESS_AUD=<aud-tag>
ACCESS_PRINCIPALS=[{"id":"<client-id>.access","ownerId":"owner-a","collectorId":"col-a1","scopes":["usage:write"]}]
```

Each principal is exactly `{ id, ownerId, collectorId, scopes }`:

- A writer is bound to one collector: `collectorId` is that collector's id and
  `scopes` is `["usage:write"]`.
- A reader is not collector-bound: `collectorId` is `null` and `scopes` is
  `["usage:read"]`.
- A principal must not hold both scopes. Duplicate ids, unknown keys, unknown
  scopes and an empty list are rejected. `ownerId` and `collectorId` use the same
  slug rules as the rest of this API. `id` is the Access service token client id
  (the JWT `common_name`).

If any value is invalid, the whole configuration is discarded and every request
is denied. That is fail-closed: a partial or mistyped map never becomes a
broader permission.

### 401 and 503

- **401 `unauthorized`** — the request is not authenticated: no usable assertion,
  bad signature, expired or wrong claims, or a client id that is not in the map.
  The body is always `{ "error": { "code": "unauthorized", "message": "Credentials are required." } }`.
  It does not say which check failed. This answer is defense-in-depth: with an
  Access application in front of the hostname, Access evaluates the service token
  first and answers a bad or missing one itself, so a `401` from this Worker means
  the request reached it while bypassing Access — another route, a Service
  Binding, or a hostname outside the application.
- **503 `auth_unavailable`** — the Access JWKS could not be used (fetch failure,
  timeout, malformed set, no matching key). Nothing is written. The body has no
  reason, token or client id.
- **503 `auth_unconfigured`** — the deployment has no valid Access
  configuration, so the API cannot authenticate anyone. This is not a rejected
  credential and no client can fix it by changing its token: it is answered in
  the retryable `5xx` class so a client keeps its state and retries later
  instead of discarding a valid credential and its send state. The body is
  always `{ "error": { "code": "auth_unconfigured", "message": "Authentication is not configured for this deployment." } }`,
  with no `details`, no domain, no audience and no principal. Every path under
  `/v1/*` answers this, including a request that carries a valid-looking token.

```json
{ "error": { "code": "auth_unconfigured", "message": "Authentication is not configured for this deployment." } }
```

Because the status is `503`, a client that classifies by status alone cannot
tell `auth_unconfigured` from `auth_unavailable`; both mean "retry later", and
neither means "your credential is wrong". Clients of this slice (the Collector
and the desktop GUI) classify every `5xx` as temporary and retryable, and none
of them reads the body to decide.

Unexpected verifier failures are returned as 401 or 503, never as a 500 that
might echo internal details. Responses and logs do not include the JWT, header
values, client ids or key material.

### Unverified, and what only a real deployment can settle

The list below is the behaviour this package depends on that the official
documentation does not fix, or that only a real deployment can settle; what has
been observed is marked as such. The same list appears as a checklist in
[docs/deployment.md](../../docs/deployment.md).

**Observed in one account on 2026-11-22**, on the free plans, with strict service
token authentication on, a `Service Auth`-only policy and Managed OAuth off:

- no credential at all → **HTTP 403**, Access's HTML error page (`text/html`), and
  the request never reached this Worker;
- a correct Client ID with a wrong secret → **HTTP 401**;
- a revoked secret (the previous one after a rotation) → **HTTP 401**;
- no `302` for any of the three;
- a valid write token on `GET /v1/usage/daily` passed Access and this Worker
  answered `403 forbidden_scope`, while the read token answered `200` with the
  daily totals and `collectors[].lastReceivedAt`.

So the classification this package and its clients assume — `403` is a
credential/scope problem, `401` a rejected credential, `3xx` a redirect that is
never followed — is the one that deployment produced. It is one account, and the
rest of this section is what it did not settle.

- **The `302` shape and the remaining rejections.** An organization with strict
  service token authentication off, an expired token, a token minted for another
  application and an application with Managed OAuth on have not been tested. With
  the setting off, a non-browser client is redirected (`302`) to a login it cannot
  complete, but that is documentation rather than an observation here: do not
  assume the `403`/`401` split above for such an organization. Organizations
  created on or after 2026-10-05 have the setting on by default and cannot turn it
  off.
- **Whether the deployment fits in 10 ms of CPU time on the free plans** is not
  settled here: the suite proves behaviour, not CPU. One measurement of a deployed
  Worker exists — the runbook's §6.0: 53 invocations in 24 hours, no Error 1102,
  P99 14.93 ms with cold starts included — and it is not the 24-hour gate reading,
  so the runbook's CPU measurement gate is still undecided. Its fallback options
  are documented as not applied.
- **Whether remote D1 supports `RETURNING`** is not described in the D1
  documentation. `changedDays` and `ignoredReductions` are read from the rows that
  `RETURNING` hands back inside the write's `batch()`, so a database that did not
  support it would still write the days and report `changedDays: []` instead of
  failing loudly. Local tests run against a real local D1 and cannot settle this;
  the runbook's checks 1 to 4 did, on 2026-11-22, in one account and against one
  D1 release. That is a proof to reproduce per deployment, not a property this
  repository can assert.
- **The rollback scope of `batch()`** is described inconsistently: the official
  page says batched statements are SQL transactions whose failing statement
  aborts or rolls back the sequence, and in the same section says D1 operates in
  auto-commit with each statement committing in turn. This package assumes the
  whole batch rolls back, and the runbook's check 4 confirmed it once, remotely,
  by making the last statement fail and observing that the earlier day write was
  absent. A different D1 release is still an assumption.

### Operator signal for a missing configuration

An isolate that starts without a usable Access configuration emits exactly one
fixed line to the Worker log:

```text
access_config_invalid
```

It is written once per isolate (the app is built once per isolate), contains no
secret, team domain, audience, principal or other configuration value, and is
what an operator sees in `wrangler dev` / `wrangler tail`. `wrangler.toml`
keeps `[observability] enabled = false`, so this line is **not** shipped to a
retained log product; there is no log-based alerting for it in this slice, and
`test/unconfigured.test.ts` pins that the token is emitted once and that no
configuration value accompanies it.

### Access must front every path

JWT verification is not a substitute for putting Access in front of every path
that can reach the Worker:

- `workers_dev = false` and `preview_urls = false` stay set, so deploy does not
  publish a hostname outside the Access application.
- The Access application must cover every public route to this Worker.
- Do not expose the Worker through a Service Binding. A binding invokes the
  Worker directly, so Access never authenticates the caller.

Tests inject fixture identities or synthetic keys. They do not call Cloudflare.

The code list includes `auth_unconfigured`; `method_not_allowed` is declared in
`src/errors.ts` but no route emits it — a method that a known path does not
serve is answered by the Hono not-found handler as `404 not_found` (verified
behaviour, not a planned `405`).

Errors are JSON: `{ "error": { "code": "...", "message": "...", "details"?: {...} } }`
with codes `unauthorized`, `auth_unconfigured`, `auth_unavailable`, `forbidden_scope`, `forbidden_collector`,
`collector_not_found`, `invalid_snapshot`, `invalid_revision`, `invalid_query`,
`invalid_request`, `invalid_json`, `unsupported_media_type`, `payload_too_large`,
`result_too_large`, `revision_conflict`, `stale_revision`, `generation_inactive`,
`internal_error`. `internal_error` is the answer when a write aborts for a reason
the contract cannot explain — a schema guard such as the day-ceiling trigger —
and it carries `details.reason: "write_failed"` with no table, row or value
named.

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

The freshness statement uses the same index from the other direction:
`SEARCH c USING INDEX collectors_by_owner (owner_id=?)` for the collector list,
and a correlated `SEARCH r USING PRIMARY KEY (collector_id=? AND generation=?)`
on `snapshot_receipts` for the newest receipt of the active generation. Both are
asserted in `test/freshness.test.ts` (no ledger scan, no extra sort).

### Migrations

`migrations/0001_initial.sql` and `0002_read_indexes.sql` are unchanged.
`migrations/0003_receipt_ignored_reductions.sql` adds
`snapshot_receipts.ignored_reductions` (a bounded, non-negative integer,
`DEFAULT 0`) because a replay must answer with the count of the first write; the
alternative — recomputing it from `usage_day` on replay — is wrong, since other
days may have moved on in between. `migrations/0004_usage_parts.sql` adds the
breakdown: the `usage_part` table, its two triggers (active generation, no
in-place reduction), a third trigger that keeps a day's rows at or below that
day's total, and `snapshot_receipts.ignored_part_reductions`. The migration
history is therefore append-only rather than rewriting `0001` for an unpublished
local database.

`0004` is additive: `usage_day` stays the authority, a v1 write path never touches
the new table, and a v1 worker runs unchanged on a database that has `0004`
applied. That is what lets the Worker and the migration be deployed in either
order for v1 traffic — but a v2 write needs both (the Worker to accept
`schemaVersion: 2` and `0004` for the table), so the recommended order is
migration first, Worker second.

The `usage_day_rejects_unverified_reduction` trigger from `0001` stays. The
upsert now stores `MAX(accepted, incoming)`, so the API path can never lower a
stored total and the trigger never fires there; it remains the schema-level
guard that a direct `UPDATE` cannot move a stored total backwards either.

`usage_part_rejects_in_place_reduction` plays the same role for a breakdown row,
and `usage_part_exceeds_day_total` closes the one hole the write path cannot
otherwise guarantee to a reader: a direct insert cannot attribute more to a day
than the day itself reports. Neither trigger fires for a well-formed write: the
day's rows are deleted before they are re-inserted, so the sum a replaced day
starts from is zero.

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
  migration story and is deferred. There is also no read of a superseded
  generation: raising `active_generation` makes the collector's earlier day
  rows unreadable through this API even though they stay stored.
- **Receipt high-water migration.** Revision ordering is currently enforced by a
  trigger that compares against the accepted receipts of
  (collector, generation). Replacing that scan with a stored high-water revision
  changes the schema and needs a backfill, so it is deferred as part of the same
  later package.

Also out of scope for this slice:

- **Client-level filtering of the breakdown** (`?client=`), cost and message
  counts, and any per-key retention. The breakdown is stored and served; it is
  not queried by client and carries no money.
- Credential issuance, rotation and revocation, management and delete operations,
  `GET /v1/collectors`. Access service-token verification is implemented; Access
  remains the authority that issues those tokens.
- Source coverage and per-source last-observation status, `sourceStates`,
  `schemaVersion` migration.
- Verified reductions (explicit re-collection or reset), multi-owner accounts
  and any remote D1 access.
- No production hardcoded secret and no fallback identity. A deployment without
  a valid Access configuration denies every request.
