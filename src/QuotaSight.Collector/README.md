# QuotaSight.Collector (local candidate normalizer)

A small, dependency-free .NET 10 CLI that reads **one** `tokscale graph` JSON
document from an **explicit stdin** and normalizes it into a **candidate**
`PUT /v1/collectors/{collectorId}/snapshots/{revision}` body for
`src/QuotaSight.Usage.Api` (wire v1 by default, wire v2 opt-in — see
`src/QuotaSight.Usage.Api/src/schema.ts`).

It is a normalizer, not a collector of record:

- Preview is the default. Nothing is sent unless `--send` is explicit.
- `--preview` alone never opens a socket and never touches the send state.
- The snapshot revision is **allocated locally**, never passed in by the caller.
- No logging framework and no payload echo on stderr.
- Never claims the upstream scan was complete.
- Does not launch another process.

## Installing the released binary

Releases attach `QuotaSight-Collector-<version>-linux-x64.zip`: one self-contained
Native AOT executable named `QuotaSight.Collector`, with no runtime to install and
no debug symbols. Unzip it and place the executable on `PATH`:

```bash
unzip QuotaSight-Collector-<version>-linux-x64.zip
install -m 0755 QuotaSight-Collector-<version>-linux-x64/QuotaSight.Collector /usr/local/bin/QuotaSight.Collector
```

The upstream scan it consumes is `tokscale` 4.23.0 — the collector rejects any
other `meta.version`. The full Linux host setup (TokScale, systemd credentials,
wrapper, timer, preview before the first send) is in
[docs/collector-linux-setup.md](../../docs/collector-linux-setup.md).

## Usage

```bash
dotnet run --project src/QuotaSight.Collector -- --preview --generation 1 --bucket-time-zone Asia/Tokyo [--upstream-partial] < graph.json
```

| Argument | Meaning |
| --- | --- |
| `--preview` | Local preview. Without `--send`, this is required. Nothing is sent. |
| `--generation <n>` | Required. Collector generation, a positive integer ≤ 2^53-1. Not derivable from upstream data. |
| `--bucket-time-zone <zone>` | Required. The zone the operator *verified* that the upstream scan bucketed days in. The `graph` JSON does **not** report it, so it is never inferred from the dates; API v1 only accepts `Asia/Tokyo` today, so anything else is rejected. |
| `--upstream-partial` | Declares that upstream reported partial, unpriced or stale-priced usage (its stderr warning). The candidate is still emitted in preview, but the exit code becomes `3`. Cannot be combined with `--send`. |
| `--wire-schema <1\|2>` | Default `1`. `1` sends daily totals only. `2` also sends the per-client/provider/model breakdown (see "Wire v2: the breakdown"), which the API stores and serves from `GET /v1/usage/breakdown`. Opt-in on purpose: the API has to understand v2 before a collector starts sending it. |
| `--parts-budget-bytes <bytes>` | Wire v2 only; with `--wire-schema 1` it is a usage error, because v1 carries no breakdown. Body budget for the breakdown, `4096` to `32768` bytes, default `16384` (16 KiB — headroom below the API's 32 KiB body ceiling; lowered from 30 KiB by the 2026-11-22 measurement in the deployment runbook, §6.0.1). The day totals are always sent; this only decides how much breakdown fits beside them. |

A v2 preview, byte-identical to what a v2 send would transmit:

```bash
dotnet run --project src/QuotaSight.Collector -- --preview --wire-schema 2 \
  --generation 1 --bucket-time-zone Asia/Tokyo < graph.json
```

## Sending (explicit only)

Nothing is transmitted unless `--send`, `--endpoint`, `--collector-id`,
`--generation` and `--bucket-time-zone` are all present. `--preview` together
with `--send` is a usage error. `--endpoint` and `--collector-id` are rejected
unless `--send` is also set.

```bash
dotnet run --project src/QuotaSight.Collector -- \
  --send \
  --endpoint https://example.invalid \
  --collector-id collector-id \
  --generation 1 \
  --bucket-time-zone Asia/Tokyo \
  < graph.json
```

| Argument | Meaning |
| --- | --- |
| `--send` | Transmit the candidate. Off by default. |
| `--endpoint <https-base-url>` | HTTPS origin only. Empty path or `/`. No user info, query or fragment. `http` is rejected, including loopback. Redirects are not followed. |
| `--collector-id <id>` | Collector id, a lowercase slug of 3-64 characters (`[a-z0-9_-]`). |
| `--generation <n>` | Collector generation, a positive integer ≤ 2^53-1. |
| `--bucket-time-zone <zone>` | As in preview. |

`--revision` no longer exists. The revision is a path parameter, it is not part
of the snapshot body, and it is allocated from the state directory described
below. Passing `--revision` is a usage error.

The request is `PUT {endpoint}/v1/collectors/{collectorId}/snapshots/{revision}`
with `Content-Type: application/json` and the exact bytes `SnapshotWire`
would write in preview. Access service-token headers are attached per request.
`Authorization` and `Cookie` are not sent.

stderr reports one reason class per run; response bodies, headers, the full URL,
identifiers and paths are never printed.

## Wire v2: the breakdown (opt-in)

`--wire-schema 2` adds the per-client/provider/model breakdown to the same
snapshot. The day totals are unchanged: every day still carries its own
`totalTokens`, and a day whose breakdown is not sent is still a complete day —
what is missing is the explanation, not the usage.

**What a row is.** One row is the usage of one `(client, provider, model)` key on
one day, split into the five components the API stores: input, output, cache
read, cache write and reasoning. A day's rows must add up to exactly the day's
`totalTokens`; when they do not, the day is sent with its total only. Tokens an
upstream row cannot attribute stay unattributed: the collector never invents a row
to close the gap.

**Identifier normalization.** Upstream values are folded and validated, never
repaired:

- **client**: ASCII lowercase, then `[a-z0-9][a-z0-9._-]{0,31}`;
- **provider**: upstream may report a comma-separated list of every provider that
  served a request, so it is split on `,`, trimmed, lowercased, de-duplicated and
  sorted, then re-joined with `,` — at most 4 elements and 64 characters;
- **model**: lowercased, at most 96 characters, and kept only if it names an
  identifier rather than a location. A path-shaped value becomes `""`: a leading
  `/` or `\`, any `\`, a drive letter (`c:…`), a local weight-file suffix
  (`.gguf`, `.safetensors`, `.bin`, `.onnx`), or two or more `/`. A single `/`
  stays, because `org/model` is a real model-id form.

A value that fails any of these rules becomes the empty string, which means
"observed usage whose key is unknown". Rows that normalize to the same key are
summed component by component — the only addition the document itself supports.
The same rules live in the API (`src/QuotaSight.Usage.Api/src/schema.ts`), and a
shared fixture keeps the two from drifting.

**Limits, and what happens at them.** At most 32 rows per day, 1024 rows per
snapshot and 128 distinct keys; a day above its row bound loses its breakdown
rather than being truncated. The serialized body is then reduced until it fits
`--parts-budget-bytes`: the **oldest** day's breakdown is dropped first, because a
reader looks at recent days first, and a day total is never dropped. If even the
day totals alone do not fit the budget, the run exits `4` and sends nothing.

The default budget is 16 KiB (`16384` bytes, down from 30 KiB). The change comes
from the 2026-11-22 measurement of a deployed Worker: a v2 send of about 31.9 KiB
measured a 7.5 ms median with three of eight runs above 10 ms, while the same
replacement pattern at 16 KiB measured 3.5 ms at the median and 7 ms at the
maximum. The accepted range (`4096` to `32768`) is unchanged, so a larger body
can still be requested explicitly.

**Withheld days.** Every day whose breakdown is withheld — no client rows
upstream, a sum that does not match the day total, an unreadable row, more rows or
keys than the bound, or the byte budget — is counted, and the run prints one
fixed stderr line:

```text
parts_omitted: <n> days were sent without a breakdown
```

The exit code stays `0`: the snapshot is complete at the day level, and the line
says how many days lost their explanation. Which days lost it is deliberately not
printed.

**Input size.** The upstream document is bounded at 512 KiB locally
(`MaxInputBytes`), not at 64 KiB. Reading `contributions[].clients` as well as the
daily totals made the document much larger: a real 17-day document is already
about 34 KiB, and a 30-day window (the `--since` filter 30 days back) is larger
than the old 64 KiB ceiling, so that ceiling would have turned a normal month
into a rejection. A
document above 512 KiB exits `4`. The emitted body stays bounded at 32 KiB
whatever the input size.

## Revision allocation and the send state

Every send allocates the next revision for its `(collector, generation)` pair
from a state file in `STATE_DIRECTORY`, and writes that number **before** the
request leaves the process. A revision is never reused: if the outcome of a
send is unknown, the number is dropped and the next run uses the next one.

**The order of a send is fixed:**

1. take the inter-process lock (`send.lock`),
2. validate the state directory and read `send-state.json`,
3. read the upstream document from stdin and normalize it,
4. read the Access credential,
5. allocate the next revision and fsync it (state file, then the directory),
6. transmit the same bytes, up to three attempts,
7. record the outcome.

A failure before step 5 consumes no revision. A failure after it does: the
number is gone, and the next run sends the next one.

### `STATE_DIRECTORY` (required, Linux only)

The collector reads **only** the `STATE_DIRECTORY` environment variable. There
is no command line option for the path and no fallback. The directory must:

- exist and be a directory, not a symbolic link,
- be an absolute path in its own normalized form (no `..`, `//` or `/.`),
- contain a single path (not a systemd colon-separated list),
- be owned by the effective user,
- have mode `0700` — nothing for group or other.

Inside it the collector uses exactly three files, all opened with `O_NOFOLLOW`
and re-checked with `fstat` (regular file, owner, `0600`):

| File | Purpose |
| --- | --- |
| `send-state.json` | The state document. At most 4 KiB. |
| `send-state.json.tmp` | Temporary sibling of the atomic replace. |
| `send.lock` | Serialises concurrent collector processes. |

A write is atomic and durable: a stale temporary file of ours is removed, the
temporary file is created exclusively (`O_CREAT|O_EXCL|O_NOFOLLOW`), forced to
`0600` with `fchmod` (the umask cannot widen it), re-checked with `fstat`
(regular file, one link, our owner), written, `fsync`ed, renamed over the state
document, and the directory is `fsync`ed. The state document is machine-written;
a hand edit is detected (the bytes must be exactly what the collector writes)
and refused.

A crash between creating the temporary file and renaming it leaves
`send-state.json.tmp` behind. The next write removes it if it is a regular file
this user owns, and refuses anything else at that path (a FIFO, a directory,
another owner, a symbolic link) with `23` (`state_unavailable:
StateWriteFailed`). To recover by hand: stop the unit, verify that
`send-state.json` is intact (`--state-show`), remove `send-state.json.tmp` as the
unit's user, and start the unit again. Never copy another collector's state
document into this directory.

Two collector processes cannot send at the same time: the second waits up to
5 seconds and then exits with `23` (`state_unavailable: StateLocked`).

**Platform support:** the state directory, its lock and its mode/owner checks
are implemented for Linux x86-64 only. On any other host, preview works but
send and the state commands refuse with `23`
(`state_unavailable: UnsupportedPlatform`). `DynamicUser=` is not supported:
the state directory must be owned by the unit's fixed `User=`.

The native calls need a C library that exports `lstat` and `fstat`: glibc 2.33
or newer, or musl. A host whose library or symbol is missing is refused with the
same `23` (`state_unavailable: UnsupportedPlatform`) instead of failing with an
unhandled exception. The musl path is **unverified**: this build has only been
exercised against glibc 2.33 and newer.

```ini
# placeholders only — replace the ids, paths and user with the operator's own
[Unit]
Description=QuotaSight collector run (example)
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
User=example-user
StateDirectory=example-collector
StateDirectoryMode=0700
# The wrapper below runs the upstream scan and pipes it into the collector.
# systemd has no shell, so the pipe cannot live in ExecStart itself.
ExecStart=/usr/local/bin/example-collector-run
LoadCredential=access-client-id:/etc/example/access-client-id
LoadCredential=access-client-secret:/etc/example/access-client-secret

[Install]
WantedBy=multi-user.target
```

The wrapper named by `ExecStart=` supplies stdin. Its path, the upstream command
and every path in it are placeholders:

```sh
#!/bin/sh
# placeholders only — replace the endpoint, the ids and the paths
# tokscale 4.23.0 writes JSON unconditionally: `graph` has neither --json nor --days, so
# the window is a date filter and --no-spinner keeps the report on stdout clean.
set -eu
npx --yes tokscale@4.23.0 graph \
    --since "$(date -d '-30 days' +%F)" \
    --no-spinner \
  | /usr/local/bin/QuotaSight.Collector --send \
      --endpoint https://example.invalid \
      --collector-id collector-id \
      --generation 1 \
      --bucket-time-zone Asia/Tokyo
```

The pipeline's exit status is the collector's, so a refused input fails the unit.
`systemd` sets `CREDENTIALS_DIRECTORY` for the `LoadCredential=` files; the
collector reads the credentials from there and nowhere else. This example is a
single `oneshot` run; a matching timer runs it on the interval described below:

```ini
# placeholders only — example-collector.timer
[Unit]
Description=Run the QuotaSight collector every three hours

[Timer]
OnBootSec=15min
OnUnitActiveSec=3h

[Install]
WantedBy=timers.target
```

`StateDirectory=example-collector` becomes `STATE_DIRECTORY=/var/lib/example-collector`
(owned by `User=` and mode `0700` because of `StateDirectoryMode=0700`).

### Run interval

Aim for one run every 1-6 hours. The upstream report window must stay well
longer than the interval, and inside the API's 62-day snapshot bound: keep it
at 7 days or more and 62 days or less. A window longer than 62 days is not
trimmed: the whole document is rejected and the run exits `4`, sending nothing.

`--bucket-time-zone` must be the zone the operator verified that the upstream
scan bucketed its dates in; the collector never infers it from the dates. The
upstream dates are calendar days in that zone, so the upstream tool's own day
boundary (not the host's) decides which day a use belongs to, and API v1 only
accepts `Asia/Tokyo` today.

A scheduled run cannot detect upstream partiality. The partial signal is the
upstream tool's own stderr, and `--upstream-partial` cannot be combined with
`--send`, so a timer-driven send is never marked partial. Treat a scheduled
snapshot as complete only when the operator has separately confirmed that the
upstream scan ran clean; inspect the upstream stderr or run the preview first
when that cannot be assumed.

### State commands

| Command | Effect |
| --- | --- |
| `--state-show` | Prints the canonical state document to stdout and changes nothing. |
| `--state-init --first-revision <n>` | Writes a new state whose next allocated revision is `n`. Requires `--collector-id` and `--generation`. |
| `--state-advance` | Clears a block (a `409`, or a `2xx` whose receipt could not be verified) after the operator has resolved the cause. Changes nothing else. |

`--state-init` is how a lost or damaged state is recovered, and it may only move
forward. When a usable state document exists, it is refused with `25`
(`state_mismatch`) unless all of this holds:

- `--collector-id` is the same collector — another collector's state is never overwritten;
- `--generation` is the same or higher, never lower;
- for the same generation, `--first-revision` is greater than the last allocated revision.

So the only two accepted moves are "raise the generation" or "raise the floor of
the same generation". Revisions are never reused: choose a floor above every
revision this collector has sent for that `(collector, generation)` pair, or a
`409` will follow. A state document that exists but is unsafe (wrong owner or
mode, a symbolic link) is never overwritten either.

A generation is raised by the operator only. After a reset of the collector's
active generation, run `--state-init` with the new `--generation` and a floor of
`1` for it.

### 409 reasons and what to do

| Reason | Meaning | Action |
| --- | --- | --- |
| `revision_conflict` | That revision was already accepted with a different payload. | Confirm the state belongs to this collector generation, then `--state-init --first-revision <above it>`. |
| `stale_revision` | A newer revision was already accepted for this generation. | `--state-init --first-revision <above the accepted revision>`. |
| `generation_inactive` | The snapshot generation is no longer the collector's active generation. | Set `--generation` to the active one, then `--state-init --first-revision 1` for that generation. |
| `conflict_unknown` | The 409 body was missing, unreadable or named a reason this build does not know. | Inspect the API and the operator records, then decide; `--state-advance` unblocks. |
| `response_unverified` | A `2xx` arrived, but its body was not the receipt this collector, generation and revision expect. The write may or may not have been applied. | Check the API for that revision and the collector's records; the number is never reused, so `--state-advance` and the next run send the next revision. |

A decreased day is **not** a 409 any more: the API keeps the accepted total for
that day, accepts the rest of the snapshot and reports the count in
`ignoredReductions`. This build neither blocks nor fails for it. A state file
written by a build that still had the previous contract may carry
`reduction_unverified` as its blocked reason; it is still read (so
`--state-advance` can clear it) but nothing produces that reason any more.

A `409`, and a `2xx` whose receipt could not be verified, block the next send
until `--state-advance`. Nothing recovers automatically: the collector never
raises `generation` by itself and never guesses a revision above its own record.

Other outcomes do not block: `400`/`404`/`413`/`415`/`422` and the other client
errors drop the allocated revision and the next run sends the next one; `5xx`,
timeouts and transport failures leave the outcome unknown and also move on.

A `2xx` is a success only when its body is the receipt the API documents
(`status`, `collectorId`, `generation`, `revision`, `receivedAt`,
`changedDays`, `ignoredReductions`) for exactly this collector, generation and
revision. `ignoredReductions` is required and must be an integer between `0` and
`62` (the API's per-snapshot day bound); a receipt without it, with a negative
or fractional value, or with a value above that bound is not verified. A **v2**
send expects two more fields after those seven — `changedParts` (`0`…`1024`) and
`ignoredPartReductions` (`0`…`62`, a day count like `ignoredReductions`) — and a
**v1** receipt must carry exactly the seven, so a v2 receipt on a v1 send (or the
reverse) is not verified either. Anything else — including a `204`, an
unexpected field, or a body over 8 KiB — is
`send_receipt_unverified` (`27`), or `send_response_too_large` (`22`) for the
size case. Both record the revision as unverified and set the
`response_unverified` block, because the local record and the server may now
disagree. No body is ever printed.

When the verified receipt reports `ignoredReductions` above `0`, the run prints
one extra fixed line to stderr: `reductions_ignored: <n>`. A v2 send whose receipt
reports `ignoredPartReductions` above `0` prints `part_reductions_ignored: <n>` in
the same way. The exit code stays `0` (`sent: accepted`): the API accepted the
snapshot and consumed the revision, and the counts only say how many days kept an
already accepted, higher total — for the day total, and for the day's breakdown.
Each is a fixed token and an integer, never a response excerpt.

When the recorded outcome of the previous revision is `none` or `unknown`, every
send first reports `previous_send_unresolved` and then continues with the next
revision: the number is never retried, so the warning only says that no result
was confirmed for it.

If the send itself succeeds but its result cannot be written to the state
document (for example a foreign object at the temporary path), the run reports
`state_unrecorded` and exits `28`. The revision is already allocated and the
request has already been sent, so the next run treats it as unresolved and moves
on.

### Credentials

The client id and client secret are read from systemd `LoadCredential` files
named `access-client-id` and `access-client-secret` in the directory named by
`CREDENTIALS_DIRECTORY`. They are not accepted as arguments or as environment
variables. There is no plaintext-file fallback. On a non-Linux host this source
is unsupported and send is refused.

```ini
# placeholder paths only — replace with the operator's credential files
LoadCredential=access-client-id:/etc/example/access-client-id
LoadCredential=access-client-secret:/etc/example/access-client-secret
```

`LoadCredential=` copies a plaintext file, so that file still sits in cleartext on
disk. Registering the values with `systemd-creds encrypt` and using
`LoadCredentialEncrypted=` instead keeps an encrypted copy that systemd decrypts
into `CREDENTIALS_DIRECTORY` for the unit only; the collector reads the same two
file names either way. [docs/collector-linux-setup.md](../../docs/collector-linux-setup.md)
has the `systemd-creds` commands, the user-unit variant and the timer.

A user unit resolves `StateDirectory=` under `~/.local/state/` instead of
`/var/lib/`. The name still becomes `STATE_DIRECTORY` with mode `0700`, and the
collector's ownership and mode checks are the same either way.

### Before sending real data

The operator must confirm all of the following before the first send of real
usage data:

- The candidate was previewed with `--preview` and the wire is the intended snapshot.
- Upstream did not report partial, unpriced or stale-priced usage. `--upstream-partial` cannot be combined with `--send`.
- `--endpoint` is the intended Access-protected HTTPS origin, not a preview or unauthenticated host.
- `--generation` and `--bucket-time-zone` are the intended values.
- If `--wire-schema 2` is used: the API has migration `0004` applied (a v2 body sent to a database without it fails with a server error and the revision is consumed), and the preview's `keys` / `parts` are the breakdown you intend to send — normalized identifiers can differ from the upstream spelling, and a path-shaped model id arrives as `""`.
- No other collector reads the same logs: two collector ids counting the same usage cannot be told apart by the API.
- `STATE_DIRECTORY` is set by `StateDirectory=` with `StateDirectoryMode=0700` and a fixed `User=`, and `--state-init --first-revision` has been run once with a floor above every revision this collector has sent for that `(collector, generation)` pair.
- Credentials are installed only through systemd `LoadCredential`, not in the unit environment and not on the command line.
- Sending is intentional. Preview remains the default; omitting `--send` sends nothing.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Preview candidate emitted, send accepted, or a state command completed. |
| `2` | No dated rows upstream. No snapshot, empty stdout, nothing sent. |
| `3` | Preview candidate emitted **but** upstream declared partial/unpriced evidence. Not sent. |
| `4` | Input violates the pinned contract, the API bounds, or the bounded stdin limit. Empty stdout, nothing sent. |
| `5` | Unusable invocation (missing mode or required arguments, `--send` with `--preview`, `--revision`, bad endpoint, unknown argument). |
| `11` | Send refused: authentication failed (401). Not retried. |
| `12` | Send refused: authorization failed (403). Not retried. |
| `13` | Send refused: redirect, not followed. |
| `14` | Send refused: conflict (409). The state is now blocked until `--state-advance`. |
| `15` | Send refused: precondition failed (412). Not retried. |
| `16` | Send refused: unprocessable (422). Not retried. |
| `17` | Send refused: other client error. Not retried. |
| `18` | Send failed: server unavailable after retries (5xx, including 503). Outcome unknown, revision consumed. |
| `19` | Send failed: timed out after retries. Outcome unknown, revision consumed. |
| `20` | Send failed: transport failed after retries. Outcome unknown, revision consumed. |
| `21` | Send refused: credentials unavailable or unsupported. The revision is not consumed. |
| `22` | Send refused: response body exceeded the read bound. The revision is recorded as unverified and blocks until `--state-advance`. |
| `23` | The state directory or state document is unusable, or another process holds the lock. Nothing was sent. |
| `24` | An unresolved 409 is waiting for `--state-advance`. Nothing was sent. |
| `25` | The state belongs to another collector or generation, or a floor would move backwards. Nothing was sent or changed. |
| `26` | No revision is left to allocate for this (collector, generation). Nothing was sent. |
| `27` | 2xx, but the body was not the expected receipt. The revision is consumed, recorded as unverified, and blocks until `--state-advance`. |
| `28` | The request was sent, but its result could not be recorded in the state document. The revision is consumed; the next run reports it as unresolved. |

The collector cannot detect partiality by itself: `summary.costProvenance.kind`
is `unknown` for complete and partial documents alike. The partial signal lives
in the upstream tool's stderr, so it must be passed in explicitly by the caller.
A caller that wants completeness must treat `0` as the only clean signal and
must still never treat the emitted snapshot as authoritative.

## Pinned upstream contract

`meta.version` must be exactly `4.23.0` (adapter version `tokscale-4.23.0`,
plain ASCII). Any other version is rejected rather than guessed.

Consumed fields, and nothing else:

- `meta.version` — pinned.
- `meta.generatedAt` — RFC 3339 instant with an explicit offset, canonicalized to
  `YYYY-MM-DDTHH:MM:SS.mmmZ` (sub-millisecond digits truncated, never rounded).
- `contributions[].date` — strict `YYYY-MM-DD` calendar date, 2000-2100.
- `contributions[].totals.tokens` — non-negative integer ≤ 2^53-1, never a
  fraction, an exponent, a string or a boolean.
- `contributions[].activeTimeMs` — optional, and only on the days that had a
  measured session. Non-negative integer ≤ 2^53-1 under the same rules as
  `totals.tokens`; a fraction, a negative, a string, a boolean, `null`, an
  object or a value above the bound is a rejection, in both wire versions. The
  value is never mapped, so a day's presence or absence of it cannot change the
  emitted bytes.
- **Only with `--wire-schema 2`**, `contributions[].clients[]` —
  `client`, `modelId`, `providerId` and
  `tokens.{input,output,cacheRead,cacheWrite,reasoning}`, at most 512 rows read
  per day. `cost`, `messages` and `costProvenance` are allowed at that level and
  never mapped; an unknown key there, a repeated key and a missing `tokens` are
  rejections.

Everything else (`summary`, `years`, `timeMetrics`, `intensity`,
`tokenBreakdown`, the day-level `clients` in wire v1, `cost`, `messages`,
`costProvenance`, `meta.dateRange`) is skipped and never echoed. Unknown keys at
the consumed levels, repeated keys, duplicate dates, unsorted-but-unique dates
(canonicalized into ascending order), out-of-order or out-of-bound values, and
trailing content after the single JSON value are handled explicitly — see
`tests/QuotaSight.Collector.Tests`.

## Wire shape

Wire v1 (the default):

```json
{"schemaVersion":1,"adapterVersion":"tokscale-4.23.0","generation":1,"collectedAt":"2026-11-19T18:49:08.875Z","bucketTimeZone":"Asia/Tokyo","coveredRange":{"from":"2026-11-17","to":"2026-11-19"},"days":[{"date":"2026-11-17","totalTokens":400},{"date":"2026-11-18","totalTokens":1200},{"date":"2026-11-19","totalTokens":1440}]}
```

Wire v2 (`--wire-schema 2`), which keeps the v1 keys in the v1 order and adds
`keys` and `days[].parts` after them:

```json
{"schemaVersion":2,"adapterVersion":"tokscale-4.23.0","generation":1,"collectedAt":"2026-11-19T18:49:08.875Z","bucketTimeZone":"Asia/Tokyo","coveredRange":{"from":"2026-11-17","to":"2026-11-18"},"keys":[["codex","","gpt-5.1"],["gemini","anthropic","claude-sonnet-4-5"]],"days":[{"date":"2026-11-17","totalTokens":400,"parts":[[0,300,40,20,20,20],[1,20,0,0,0,0]]},{"date":"2026-11-18","totalTokens":1200}]}
```

- Only allowlisted API fields, in the API's own key order, compact, one trailing newline.
- The bytes sent are these bytes; dropping the trailing newline gives exactly the
  string the API hashes for `body_digest`.
- `coveredRange` is the tight min/max of the emitted dated rows, never upstream `dateRange`.
- An **absent** day produces no row; an explicitly observed `0` on a dated row is preserved.
- In v2, `keys` is ascending by `(client, provider, model)`, `parts` is ascending
  by key index, each part is `[keyIndex, input, output, cacheRead, cacheWrite, reasoning]`,
  and a day either carries its whole breakdown (adding up to its `totalTokens`) or
  omits `parts` entirely — never an empty array.
- Bounds enforced before writing: body ≤ 32 KiB, ≤ 62 days, covered span ≤ 62 days,
  and for v2 ≤ 128 keys, ≤ 32 rows per day, ≤ 1024 rows per snapshot.
- `revision` is not part of this body. It is allocated from the state directory.

## Tests

```bash
dotnet test tests/QuotaSight.Collector.Tests/QuotaSight.Collector.Tests.csproj -c Release
```

The fixtures under `tests/QuotaSight.Collector.Tests/Fixtures` are minimized
synthetic shapes derived from real public `tokscale` 4.23.0 output; they contain
no private paths, accounts or credentials. Tests drive the CLI in-process with
injected streams, a fake HTTP handler and a private temporary state directory.
No socket is opened and no real state directory is used. The state, send and
lock tests run on Linux only and are reported as skipped elsewhere.
