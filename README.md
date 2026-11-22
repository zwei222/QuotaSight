# QuotaSight

[English](README.md) | [日本語](README.ja.md)

QuotaSight is a Windows/Linux desktop dashboard for **observed AI token usage**. It does not fetch subscription quotas, does not call provider billing APIs, and is not an API-cost or token-billing tracker. The numbers it shows are observations: a machine's local CLI logs, aggregated by [TokScale](https://github.com/junhoyeo/tokscale) on that machine, sent to a Usage API the operator controls, and read back by the app. Every surface therefore states where a value came from, which period and collectors it covers, and how old it is.

## Current capabilities

- Avalonia dashboard, usage page, settings, runtime English/Japanese switching, and a narrow-width layout.
- Dashboard: observed-usage cards for the providers you add, with today's and the last seven days' token counts.
- Usage page: daily token totals for a 30-day window (days bucketed in `Asia/Tokyo`) with per-collector rows and a freshness line, plus a separate usage breakdown grouped by client, provider, or model.
- Missing, stale, delayed, unsupported, and failed reads are distinct, fixed states; a failed refresh keeps the previous values on screen and marks them stale instead of showing zero.
- Theme, refresh interval, resident mode, and the Usage API connection (endpoint plus Access Client ID and Client Secret) settings.
- Windows Credential Manager and Linux Secret Service/`secret-tool` for the Usage API credential, with session-only storage when a secure store is unavailable.
- No telemetry.
- Optional resident mode with a compact observed-usage window and an explicit exit action.

There are no quotas, rings, gauges, or thresholds anywhere in the current UI, and no local quota history or export. The app also does not scan a machine's logs itself: usage of a machine appears only when a Collector on that machine sends it (see [Collector setup on Linux](docs/collector-linux-setup.md)).

## Data path: sending and reading are separate

| Stage | Where it runs | What it does | What it does not do |
|---|---|---|---|
| TokScale (upstream CLI) | The machine whose usage is counted | Aggregates that machine's local session logs into a `tokscale graph` document. | Does not know about quotas and does not talk to QuotaSight. |
| QuotaSight Collector (Linux x64, optional) | The same machine | Reads **one** `tokscale graph` document from stdin, normalizes it, and **sends** (`PUT`) a snapshot to the Usage API. | Does not scan logs itself, does not read credentials from files other than the systemd credential directory, and does not read the app's settings. |
| Usage API (your deployment) | Cloudflare Workers + D1, set up by the operator | Stores each collector's snapshots, enforces generation/revision monotonicity, and serves daily totals plus the breakdown. | Is not run by the app, and holds no prompt, response body, path, or account name. |
| QuotaSight desktop app | Windows or Linux x64 | **Reads** (`GET`) daily totals and the breakdown from the Usage API origin you configure. | Never sends usage anywhere, never reads TokScale or local logs, and contacts no other host. |

Sending is opt-in twice: a Collector has to be installed and scheduled, and `--send` is explicit (`--preview` is the default). The wire contract carries daily total tokens, and — only with `--wire-schema 2` — the normalized client/provider/model identifiers and five token components. Details are in [security](docs/security.md) and the [deployment runbook](docs/deployment.md).

## Dashboard: observed usage cards

- A card exists only for a provider you added from the catalog (pinned to TokScale 4.23.0). A provider the breakdown has not attributed anything to yet shows **"Not observed yet"**, never a zero reading.
- A row that several providers served together arrives as one comma-joined provider value, and it feeds **every** card it names. Card totals therefore can overlap, and such a card says "Includes tokens shared with other providers". The section badge states the same limit: observed, no limit, no estimates, other devices excluded, cards may share amounts.
- **Do not add card totals together.** The number on a card is "usage attributed through that provider", not a disjoint partition of your tokens.
- Tokens are a count, not a share of any allowance: no card carries a percentage, a ring, or a threshold.

## Usage page: daily totals and breakdown

- **Daily totals.** The page reads a 30-day window ending today. It shows the range, the total token count, one row per collector/day, and a freshness line taken from the **oldest** last-received time across the collectors the read covered — the total is only as fresh as the collector that reported least recently. Data older than 12 hours is labeled as delayed instead of current.
- **Breakdown.** A second, independent read over the same connection, grouped by client, provider, or model. It shows the attributed totals, the share of the range's attributed total, the per-day coverage, an exact unclassified remainder (day total minus attributed), a "only some days have a breakdown" state, and — uncorrected — any day whose breakdown exceeds its day total. The card carries a fixed note: these are values TokScale aggregated from local logs, so log deletion (Claude Code keeps 30 days by default) and API or cache-type clients can leave gaps or delays.
- The two reads keep separate states on purpose: the daily totals can work while the breakdown is unsupported or failing, and neither read stales the other. Switching the grouping key does not trigger a new read.

## Usage API connection, credentials, and failure states

Settings stores the endpoint as an ordinary, non-secret setting and the Access Client ID/Client Secret in the OS credential store only. The endpoint must be an `https` origin: no path, query, user information, or fragment, at most 256 characters. After saving, the credential cannot be viewed or copied; only its presence is shown. If the credential store is unavailable or locked, the pair is kept for the current session only and must be entered again after a restart. Removing the connection deletes both stored values.

Read failures are classified and shown as fixed sentences: credential rejected (401/403), endpoint or response problem (404 and contract violations), a redirect (not followed), a missing credential, and transient failures (timeout, connection failure, 429, 5xx). The breakdown adds its own `not_found` (the server does not serve the breakdown yet) and `collector_not_found` states. Nothing in a failure message echoes a status code, URI, response body, or identifier.

## Notifications and status

All alerts in the current UI are an **in-app banner**: connection state, read failures, stale values, tray-unavailable notices, and resident-mode save failures. There is no threshold notification, and no notification or threshold setting in the UI.

The desktop notification backends are still in the build (Windows App SDK 2.7.0 `AppNotifications`; Linux `org.freedesktop.Notifications` over the session D-Bus), but **no current application flow calls them**, so this build does not send an OS notification. What remains executable is the Windows diagnostic `--notification-probe`, which checks the registration lifecycle and exits `0` on success; accepting a registration is not the same as displaying a popup, and visible delivery has never been confirmed on physical Windows hardware. The procedure and its record template are in the [Windows notification acceptance procedure](docs/windows-notification-acceptance.md).

## Requirements

- .NET SDK 10.0.x to build from source; the release ZIP is self-contained.
- Windows x64 or Linux x64.
- On Linux, the GUI requires GTK. Tray support needs a StatusNotifier host; Secret Service is optional (session-only credentials without it).
- Network access to the Usage API origin you configure — the only host the app contacts.
- To collect usage: Linux x86-64 with systemd 250+, Node.js (TokScale is distributed on npm), and a write-scoped Cloudflare Access service token. See [Collector setup on Linux](docs/collector-linux-setup.md).

## Distribution

The supported release artifact is the self-contained .NET 10 Native AOT ZIP for Windows x64 or Linux x64. MSIX, deb, and AppImage packages are not currently supported. Releases for Linux x64 also attach `QuotaSight-Collector-<version>-linux-x64.zip`, the sender that normalizes a local `tokscale graph` document and sends it to a Usage API; the host setup (binary, TokScale, systemd credentials, timer) is in [Collector setup on Linux](docs/collector-linux-setup.md).

## Build and run

```bash
dotnet restore QuotaSight.slnx
dotnet build QuotaSight.slnx -c Release --no-restore
dotnet test QuotaSight.slnx -c Release --no-build --no-restore
dotnet format QuotaSight.slnx --verify-no-changes --no-restore
dotnet run --project src/QuotaSight.UI/QuotaSight.UI.csproj
# headless startup contract
dotnet run --project src/QuotaSight.UI/QuotaSight.UI.csproj -- --smoke-test
```

## Security and privacy

QuotaSight is local-first and sends no telemetry. Provider CLI credential files are not read or parsed, and the app keeps no usage data on disk: only settings are written, and they contain no secret. Tokens, API keys, cookies, passwords, raw provider responses, and complete CLI output never reach settings, logs, crash reports, or UI text. The single outbound destination is the Usage API origin you configure, authenticated with credentials held in Windows Credential Manager or Linux Secret Service; if that store is unavailable or locked, the credential stays in memory for the current session only. See [security details](docs/security.md).

## Upgrading from versions with quota views

The settings file is read name by name: a file written by an earlier version still loads, keeps every setting the current build uses, and drops the quota-era fields (thresholds, provider integration values, reference credits) on the next save. Earlier versions' provider credentials in the OS credential store and their 30-day quota-history files are **not read** by the current build and are **not deleted automatically** by the upgrade; they simply stop being used.

## Roadmap

- Return a visible OS notification path, or remove the unused notification backends, and then validate display on physical Windows hardware.
- Update the provider catalog deliberately when a TokScale release changes attribution ids.
- Broader collector coverage only when the upstream tool documents a stable contract.

## Historical documents

The GitHub App guides are kept as a record of an earlier design: they describe Device Flow authentication and Copilot Billing Usage retrieval that current versions do not perform.

- [GitHub App setup for QuotaSight](docs/github-app-setup.md) (historical)
- [QuotaSightのGitHub Appセットアップ](docs/github-app-setup.ja.md) (historical)

## Official references

- [TokScale (upstream usage aggregator)](https://github.com/junhoyeo/tokscale)
- [Cloudflare Access service tokens](https://developers.cloudflare.com/cloudflare-one/access-controls/service-credentials/service-tokens/)
- [Cloudflare Workers limits](https://developers.cloudflare.com/workers/platform/limits/)
- [.NET 10 downloads](https://dotnet.microsoft.com/download/dotnet/10.0)

[日本語版](README.ja.md) · [Collector setup on Linux](docs/collector-linux-setup.md) · [Deployment runbook](docs/deployment.md)
