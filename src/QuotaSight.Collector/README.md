# QuotaSight.Collector (local dry-run candidate normalizer)

A small, dependency-free .NET 10 CLI that reads **one** `tokscale graph --json`
document from an **explicit stdin** and normalizes it into a **candidate**
`PUT /v1/collectors/{collectorId}/snapshots/{revision}` body for
`src/QuotaSight.Usage.Api` (wire v1, see `src/QuotaSight.Usage.Api/src/schema.ts`).

It is a normalizer, not a collector of record:

- Never opens a socket, never starts another process, never writes a file.
- No authentication, no credentials, no logging framework, no payload echo.
- Emits nothing but the API wire candidate — and only in explicit `--preview` mode.
- Never claims the upstream scan was complete.

## Usage

```bash
dotnet run --project src/QuotaSight.Collector --   --preview --generation 1 --bucket-time-zone Asia/Tokyo [--upstream-partial] < graph.json
```

| Argument | Meaning |
| --- | --- |
| `--preview` | Required. Without it nothing is written to stdout (exit 5). |
| `--generation <n>` | Required. Collector generation, a positive integer ≤ 2^53-1. Not derivable from upstream data. |
| `--bucket-time-zone <zone>` | Required. The zone the operator *verified* that the upstream scan bucketed days in. The `graph` JSON does **not** report it, so it is never inferred from the dates; API v1 only accepts `Asia/Tokyo` today, so anything else is rejected. |
| `--upstream-partial` | Declares that upstream reported partial, unpriced or stale-priced usage (its stderr warning). The candidate is still emitted, but the exit code becomes `3`. |

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Candidate emitted; upstream reported no partial/pricing warning. Still a candidate — never authoritative. |
| `2` | No dated rows upstream (empty *or* "malformed" empty-range document). No snapshot, empty stdout. |
| `3` | Candidate emitted **but** upstream declared partial/unpriced evidence. Do not treat as complete; do not auto-send. |
| `4` | Input violates the pinned contract, the API bounds, or the bounded stdin limit. Empty stdout. |
| `5` | Unusable invocation (missing `--preview`, `--generation` or `--bucket-time-zone`, unknown argument). |

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

## Tests

```bash
dotnet test tests/QuotaSight.Collector.Tests/QuotaSight.Collector.Tests.csproj -c Release
```

The fixtures under `tests/QuotaSight.Collector.Tests/Fixtures` are minimized
synthetic shapes derived from real public `tokscale` 4.23.0 output; they contain
no private paths, accounts or credentials. Tests drive the CLI in-process with
injected streams — no binary is launched.
