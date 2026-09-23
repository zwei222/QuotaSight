# QuotaSight

[English](README.md) | [日本語](README.ja.md)

QuotaSight is a Windows/Linux desktop dashboard for subscription quotas. It is not an API billing, token-billing, or pay-as-you-go cost tracker. It shows observed values with source, confidence, freshness, and reporting limits; it does not turn estimates, stale data, manual entries, or experimental endpoints into authoritative real-time usage.

## Current capabilities

- Avalonia dashboard, history, settings, runtime English/Japanese switching, and narrow-width layout.
- Thirty days of quota snapshots in daily JSONL history.
- Windows Credential Manager and Linux Secret Service/`secret-tool`, with session-only storage when a secure store is unavailable.
- No telemetry. Tokens, plaintext credentials, and raw provider responses are not stored or displayed.
- Theme, refresh, notification, threshold, GitHub App Client ID, and GitHub organization slug settings.
- Optional resident mode with a compact quota window and an explicit exit action.

### Notifications

The notification threshold defaults to 80%, and notifications can be turned off in Settings. Threshold notifications are checked during periodic refreshes only while QuotaSight is running. Manual percentage snapshots can trigger a notification when they first reach the threshold; stale or unknown observations and quantity-only Copilot usage do not trigger threshold notifications. Error and status banners are independent of the threshold-notification toggle.

Notifications are best-effort, not guaranteed delivery. On Linux, QuotaSight attempts to send an `org.freedesktop.Notifications` request over the session D-Bus; acceptance by D-Bus does not guarantee that a notification daemon displays it. On Windows x64, the Windows App SDK 2.5.1 backend attempts to send OS notifications in the portable Native AOT ZIP build. API acceptance and an on-screen popup are separate outcomes. CI verifies restore, format, Release build/tests, and Native AOT publish/smoke; Windows notification registration and visible delivery require separate manual verification on a non-elevated Windows 11 x64 desktop and have not been reported as successful. See the [Windows notification acceptance procedure](docs/windows-notification-acceptance.md). The in-app banner remains available on both operating systems as a fallback (it is not visible while the window is hidden). Normal application shutdown attempts to unregister Windows notification registration, including `UnregisterAll`; abnormal termination or cleanup failure may leave registration behind.

## Provider matrix and limits

| Provider | Current method | What it does and does not promise |
|---|---|---|
| ChatGPT Codex | QuotaSight's OpenAI device OAuth and undocumented/experimental `wham/usage` endpoint | Covers one account's Codex allowance, not all ChatGPT Plus/Pro usage. It is never labeled Official; failures fall back to manual entry and the official subscription page. |
| Claude Pro | Manual entry plus the official URL | The user enters quota and reset information. QuotaSight does not call a billing API. |
| OpenCode Go | Explicit API key plus usage endpoint | Shows only the subscription quota returned by that endpoint; delayed or missing responses are stale/unknown. Internal OpenCode credential files are not read. |
| GitHub Copilot | GitHub App Device Flow or `gh auth status` probe, then optional Billing Usage retrieval | Authentication and usage access are separate. A configured organization slug and an administrator token with `Administration: read` are required for `GET /organizations/{org}/settings/billing/ai_credit/usage`. The official `credits` quantity is shown as gross, discount, and net; percentage and individual remaining are not calculated. |

## Copilot authentication and Billing Usage

Authentication only:

1. Create a GitHub App with Device Flow enabled.
2. Put only its Client ID in Settings. QuotaSight does not ask the desktop user for a private key or client secret.
3. Complete Device Flow, or use the safe `gh auth status` probe.

Billing Usage retrieval is a separate, optional step:

1. Set the target GitHub Organization slug in Settings.
2. In the GitHub App settings, grant the `Administration: read` organization permission.
3. Install the App in the target organization and obtain owner approval of the requested permission. This installation and approval are required for the Billing Usage path.
4. Complete Device Flow with a user who has the required organization administration permission. QuotaSight uses the Device Flow token; there is no separate token input field.
5. A general seat token may receive 403.
6. QuotaSight calls `GET /organizations/{org}/settings/billing/ai_credit/usage` for the current reporting month.

Device Flow success does not prove usage permission. When GitHub returns a refresh token, QuotaSight stores the expiring user-token bundle in the OS credential store and refreshes it before the access token expires. Existing legacy access-token entries have no refresh token; after they expire, the user must explicitly authenticate again. If a secure credential store is unavailable, credentials are session-only and authentication must be repeated after restarting the app. Usage is associated with a shared billing-entity pool, not an individual remaining balance. GitHub may report usage after a delay, so the card shows the aggregation period, retrieval time, and unknown reporting delay; after refresh failures, previously observed values are identified as stale rather than current. If the slug, installation, owner approval, permission, or endpoint is unavailable, use the official Copilot page or manual fallback. Do not infer personal remaining from seats, metrics, or billing totals. See the [GitHub App setup guide](docs/github-app-setup.md).

For Codex, QuotaSight runs its own device OAuth, does not read Hermes/Codex credential files, and does not request or retain a client secret. Provider states remain distinguishable as Manual, Official, Delayed, or Experimental.

## Requirements

- .NET SDK 10.0.x.
- Windows x64 or Linux x64.
- On Linux, the GUI requires GTK. OS notification attempts require a session D-Bus and notification daemon, and daemon acceptance does not guarantee display; Secret Service is optional.
- `gh` for an existing GitHub CLI status probe; a browser for Device Flow.

## Distribution

The supported release artifact is the self-contained .NET 10 Native AOT ZIP for Windows x64 or Linux x64. MSIX, deb, and AppImage packages are not currently supported.

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

QuotaSight is local-first and sends no telemetry. Provider CLI credential files are not read or parsed. Tokens, API keys, cookies, passwords, raw provider responses, and complete CLI output are not written to settings, history, logs, crash reports, exports, tests, or UI text. Persistent secrets use Windows Credential Manager or Linux Secret Service; if unavailable or locked, credentials stay in memory for the current session only. See [security details](docs/security.md).

Quota windows remain separate. The representative window is the tightest known window; unrelated windows are not added together. Over-100% values are preserved, with only visual progress indicators clamped. Reset times, freshness, source confidence, and stale/manual/experimental states remain explicit.

## Roadmap

- More provider packages when a documented service contract and public quota endpoint are available.
- Validate Windows OS notification display on physical Windows hardware; API acceptance does not establish that a popup was displayed.
- Broader Copilot organization coverage only when GitHub exposes a documented permission and response contract.

## Official references

- [Register a GitHub App](https://github.com/settings/apps/new)
- [GitHub App user access tokens and Device Flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
- [Refreshing GitHub App user access tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens)
- [Install a GitHub App](https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party)
- [Control GitHub App installation in an organization](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/limiting-oauth-app-and-github-app-access-requests-and-installations)
- [Billing usage REST API](https://docs.github.com/en/rest/billing/usage?apiVersion=2022-11-28)
- [GitHub Copilot AI usage UI](https://github.com/settings/copilot)

[日本語版](README.ja.md) · [GitHub App setup guide](docs/github-app-setup.md) · [日本語セットアップガイド](docs/github-app-setup.ja.md)
