# QuotaSight.Collector (local candidate normalizer)

A small, dependency-free .NET 10 CLI that reads **one** `tokscale graph --json`
document from an **explicit stdin** and normalizes it into a **candidate**
`PUT /v1/collectors/{collectorId}/snapshots/{revision}` body for
`src/QuotaSight.Usage.Api` (wire v1, see `src/QuotaSight.Usage.Api/src/schema.ts`).

It is a normalizer, not a collector of record:

- Preview is the default. Nothing is sent unless `--send` is explicit.
- `--preview` alone never opens a socket.
- No logging framework and no payload echo on stderr.
- Never claims the upstream scan was complete.
- Does not launch another process.

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

## Sending (explicit only)

Nothing is transmitted unless `--send`, `--endpoint`, `--collector-id` and
`--revision` are all present. `--preview` together with `--send` is a usage
error. `--endpoint`, `--collector-id` and `--revision` are rejected unless
`--send` is also set.

```bash
dotnet run --project src/QuotaSight.Collector -- \
  --send \
  --endpoint https://example.invalid \
  --collector-id collector-id \
  --revision 1 \
  --generation 1 \
  --bucket-time-zone Asia/Tokyo \
  < graph.json
```

| Argument | Meaning |
| --- | --- |
| `--send` | Transmit the candidate. Off by default. |
| `--endpoint <https-base-url>` | HTTPS origin only. Empty path or `/`. No user info, query or fragment. `http` is rejected, including loopback. Redirects are not followed. |
| `--collector-id <id>` | Collector id, a lowercase slug of 3-64 characters (`[a-z0-9_-]`). |
| `--revision <n>` | Client-chosen snapshot revision for that collector generation. A positive integer ≤ 2^53-1, no leading zeros. It is a path parameter, not a field of the snapshot body. |

The request is `PUT {endpoint}/v1/collectors/{collectorId}/snapshots/{revision}`
with `Content-Type: application/json` and the exact bytes `SnapshotWire`
would write in preview. Access service-token headers are attached per request.
`Authorization` and `Cookie` are not sent.

The API treats an identical revision and payload as success (replay). A
different payload for the same revision, a stale revision, an inactive
generation, or an unverified reduction is `409` and is not retried. `401` and
`403` stop immediately. `5xx` (including `503`), timeouts and transport
failures are retried at most 3 times with exponential backoff and jitter.
The revision and body do not change between attempts. stderr reports a reason
class only; response bodies, headers and the full URL are not printed.

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

### Before sending real data

The operator must confirm all of the following before the first send of real
usage data:

- The candidate was previewed with `--preview` and the wire is the intended snapshot.
- Upstream did not report partial, unpriced or stale-priced usage. `--upstream-partial` cannot be combined with `--send`.
- `--endpoint` is the intended Access-protected HTTPS origin, not a preview or unauthenticated host.
- `--collector-id`, `--generation`, `--revision` and `--bucket-time-zone` are the intended values.
- Credentials are installed only through systemd `LoadCredential`, not in the unit environment and not on the command line.
- Sending is intentional. Preview remains the default; omitting `--send` sends nothing.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Preview candidate emitted, or send accepted (`sent: accepted` on stderr). Still not a claim that the upstream scan was complete. |
| `2` | No dated rows upstream. No snapshot, empty stdout, nothing sent. |
| `3` | Preview candidate emitted **but** upstream declared partial/unpriced evidence. Not sent. |
| `4` | Input violates the pinned contract, the API bounds, or the bounded stdin limit. Empty stdout, nothing sent. |
| `5` | Unusable invocation (missing mode or required arguments, `--send` with `--preview`, bad endpoint, unknown argument). |
| `11` | Send refused: authentication failed (401). Not retried. |
| `12` | Send refused: authorization failed (403). Not retried. |
| `13` | Send refused: redirect, not followed. |
| `14` | Send refused: conflict (409). Not retried. |
| `15` | Send refused: precondition failed (412). Not retried. |
| `16` | Send refused: unprocessable (422). Not retried. |
| `17` | Send refused: other client error. Not retried. |
| `18` | Send failed: server unavailable after retries (5xx, including 503). |
| `19` | Send failed: timed out after retries. |
| `20` | Send failed: transport failed after retries. |
| `21` | Send refused: credentials unavailable or unsupported. |
| `22` | Send refused: response body exceeded the read bound. |

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

Everything else (`summary`, `years`, `timeMetrics`, `intensity`,
`tokenBreakdown`, `clients`, `cost`, `messages`, `costProvenance`,
`meta.dateRange`) is skipped and never echoed. Unknown keys at the consumed
levels, repeated keys, duplicate dates, unsorted-but-unique dates (canonicalized
into ascending order), out-of-order or out-of-bound values, and trailing content
after the single JSON value are handled explicitly — see
`tests/QuotaSight.Collector.Tests`.

## Wire shape

```json
{"schemaVersion":1,"adapterVersion":"tokscale-4.23.0","generation":1,"collectedAt":"2026-11-19T18:49:08.875Z","bucketTimeZone":"Asia/Tokyo","coveredRange":{"from":"2026-11-17","to":"2026-11-19"},"days":[{"date":"2026-11-17","totalTokens":400},{"date":"2026-11-18","totalTokens":1200},{"date":"2026-11-19","totalTokens":1440}]}
```

- Only allowlisted API fields, in the API's own key order, compact, one trailing newline.
- `coveredRange` is the tight min/max of the emitted dated rows, never upstream `dateRange`.
- An **absent** day produces no row; an explicitly observed `0` on a dated row is preserved.
- Bounds enforced before writing: body ≤ 32 KiB, ≤ 62 days, covered span ≤ 62 days.
- `revision` is not part of this body. The operator supplies it on `--send`.

## Tests

```bash
dotnet test tests/QuotaSight.Collector.Tests/QuotaSight.Collector.Tests.csproj -c Release
```

The fixtures under `tests/QuotaSight.Collector.Tests/Fixtures` are minimized
synthetic shapes derived from real public `tokscale` 4.23.0 output; they contain
no private paths, accounts or credentials. Tests drive the CLI in-process with
injected streams and, for send, a fake HTTP handler. No socket is opened.
