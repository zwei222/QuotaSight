# QuotaSight

[English](README.md) | [日本語](README.ja.md)

QuotaSight is a Windows/Linux desktop dashboard for **subscription quota remaining**. The dashboard reads the configured read-only quota API and shows only services you explicitly add; it does not call provider billing APIs. A separate Usage page retains **observed AI token usage** from the operator-controlled Usage API, aggregated by TokScale on the sending machine. Quota remaining and observed token counts are distinct data and are never combined.

## Current capabilities

- Avalonia dashboard, usage page, settings, runtime English/Japanese switching, and a narrow-width layout.
- Dashboard: contract quota cards for the services you add, with remaining percentages, reset periods and rings. The default selection is empty.
- Usage page: observed daily token totals for a 30-day window (days bucketed in `Asia/Tokyo`) with per-collector rows and freshness, plus a separate breakdown grouped by client, provider, or model.
- Missing, stale, delayed, unsupported, and failed reads are distinct, fixed states; a failed refresh keeps the previous values on screen and marks them stale instead of showing zero.
- Theme, refresh interval, resident mode, and the Usage API connection (endpoint plus Access Client ID and Client Secret) settings.
- Windows Credential Manager and Linux Secret Service/`secret-tool` for the Usage API credential, with session-only storage when a secure store is unavailable.
- No telemetry.
- Optional resident mode with a compact quota window and an explicit exit action.

Quota cards show only subscription quota data returned by the read-only quota endpoint. The dashboard starts with no selected services; observed usage, local quota history and export are not mixed into this surface. The app does not scan machine logs itself: usage appears on the separate Usage page only when a Collector sends it (see [Collector setup on Linux](docs/collector-linux-setup.md)).

## Data path: sending and reading are separate

| Stage | Where it runs | What it does | What it does not do |
|---|---|---|---|
| TokScale (upstream CLI) | The machine whose usage is counted | Aggregates that machine's local session logs into a `tokscale graph` document. | Does not know about quotas and does not talk to QuotaSight. |
| QuotaSight Collector (Linux x64, optional) | The same machine | Reads **one** `tokscale graph` document from stdin, normalizes it, and **sends** (`PUT`) a snapshot to the Usage API. | Does not scan logs itself, does not read credentials from files other than the systemd credential directory, and does not read the app's settings. |
| Usage API (your deployment) | Cloudflare Workers + D1, set up by the operator | Stores each collector's snapshots, enforces generation/revision monotonicity, and serves daily totals plus the breakdown. | Is not run by the app, and holds no prompt, response body, path, or account name. |
| QuotaSight desktop app | Windows or Linux x64 | **Reads** (`GET`) daily totals and the breakdown from the Usage API origin you configure. | Never sends usage anywhere, never reads TokScale or local logs, and contacts no other host. |

Sending is opt-in twice: a Collector has to be installed and scheduled, and `--send` is explicit (`--preview` is the default). The wire contract carries daily total tokens, and — only with `--wire-schema 2` — the normalized client/provider/model identifiers and five token components. Details are in [security](docs/security.md) and the [deployment runbook](docs/deployment.md).

## Dashboard: selected subscription quota

- The six-service picker starts empty. Only services added by the user are shown; old TokScale observation selections do not add quota cards.
- A selected service absent from the latest response stays visible as **Unavailable**, without a numeric value or ring, and can still be removed.
- Antigravity is one selectable service; API model groups remain separate cards. Percentages and reset periods come only from the quota API.
- The separate Usage page continues to show observed token counts and attribution from the Usage API. These are observations, not quota remaining, and must not be added to quota cards.

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
