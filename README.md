# QuotaSight

[English](README.md) | [日本語](README.ja.md)

QuotaSight is a Windows/Linux desktop dashboard for subscription quotas. It is not an API billing, token-billing, or pay-as-you-go cost tracker. It shows observed quota values together with their source and confidence; it does not turn estimates, stale data, manual entries, or experimental endpoints into authoritative real-time usage.

## Current capabilities

- Safe demo mode with sample dashboard data.
- Avalonia dashboard, history, and settings screens.
- Runtime English/Japanese switching and a narrow-width layout.
- Tray support and in-app notification fallback when native notifications are unavailable. If the tray is unavailable, the normal application window remains usable.
- Thirty days of quota snapshots stored as daily JSONL history.
- Windows Credential Manager and Linux Secret Service/`secret-tool` integration, with session-only credential storage when a secure store is unavailable.
- No telemetry. Tokens, plaintext credentials, and raw provider responses are not stored or displayed.
- Theme selection (System, Light, Dark), refresh interval, notification preferences, thresholds, and a runtime GitHub App Client ID in settings. The Client ID is not a secret.

## Provider matrix and limits

| Provider | Current method | What it does and does not promise |
|---|---|---|
| ChatGPT Codex | QuotaSight's OpenAI device OAuth and undocumented/experimental `wham/usage` endpoint | Covers the Codex allowance for one ChatGPT account, not all ChatGPT Plus/Pro usage. The public client ID and endpoints are visible in the official Codex implementation, but a stable third-party contract and permission to reuse that client ID have not been confirmed. It is therefore never labeled Official; live login is unverified. Failures fall back to manual entry and the official ChatGPT subscription page. |
| Claude Pro | Manual entry plus the official URL | The user enters quota and reset information from the official page. QuotaSight does not call a billing API. |
| OpenCode Go | Explicit API key plus a usage endpoint | Shows only the subscription quota returned by that endpoint. Delayed or missing responses are represented as stale/unknown rather than guessed. QuotaSight does not read OpenCode's internal credential files. |
| GitHub Copilot | `gh auth status` probe or GitHub App Device Flow | Current implementation authenticates/checks status only. `CopilotAdapter` returns Unsupported for quota retrieval, so Copilot uses manual fallback. Successful GitHub login is not successful quota API retrieval. See the [GitHub App setup guide](docs/github-app-setup.md). |

For Codex, QuotaSight runs its own device OAuth, does not read Hermes/Codex credential files, does not request or retain a client secret, and stores tokens only in the OS secure store or session memory. Provider states remain distinguishable as Manual, Official, Delayed, or Experimental.

### Copilot terminology and manual fallback

Copilot seats, organization metrics, and AI-credit usage are not the same thing as an individual's real-time remaining quota. Business credits are a shared pool; QuotaSight must not hardcode a fixed personal allowance. Until an official Copilot data contract is implemented, enter the relevant value manually and keep its source and freshness explicit.

The GitHub App guide explains installation, owner approval, organization access, SAML SSO approval, and the security boundary. The app currently needs only the Client ID for Device Flow. Do not enter a client secret or private key; a private key is not needed for this flow.

## Requirements

- .NET SDK 10.0.x.
- Windows x64 or Linux x64.
- On Linux, GUI/tray/notification support depends on the environment's GTK, D-Bus, notification daemon, and optionally Secret Service. Missing components use the documented fallback where possible.
- `gh` to check existing GitHub CLI authentication; a browser for Device Flow.

## Build and run

```bash
dotnet restore QuotaSight.slnx
dotnet build QuotaSight.slnx -c Release --no-restore
dotnet test QuotaSight.slnx -c Release --no-build --no-restore
dotnet format QuotaSight.slnx --verify-no-changes --no-restore
dotnet run --project src/QuotaSight.UI/QuotaSight.UI.csproj
# Check the headless startup contract
dotnet run --project src/QuotaSight.UI/QuotaSight.UI.csproj -- --smoke-test
```

`--smoke-test` starts without showing the UI, checks startup and basic dependencies, and exits.

## Release ZIP

The release workflow builds a self-contained .NET 10 Native AOT binary on the target OS and publishes `QuotaSight-<version>-<rid>.zip` for `linux-x64` or `win-x64`. Extract the ZIP and run `QuotaSight.UI` on Linux or `QuotaSight.UI.exe` on Windows. On Linux, grant execute permission if needed and install the GUI/tray/notification dependencies required by the environment. MSIX, deb, and AppImage packages are not currently provided.

## Security and privacy

QuotaSight is local-first and sends no external telemetry. History is retained for 30 days as daily JSONL. Provider CLI credential files are not read or parsed. Tokens, API keys, cookies, passwords, raw provider responses, and complete CLI output are not written to settings, history, logs, crash reports, exports, tests, or UI text. Persistent secrets use Windows Credential Manager or Linux Secret Service; if unavailable or locked, credentials stay in memory for the current session only. See [security details](docs/security.md).

Quota windows remain separate (rolling, daily, weekly, monthly, or custom). The dashboard representative is the tightest known window; unrelated windows are not added together. Over-100% values are preserved, with only visual progress indicators clamped. Reset times, freshness, source confidence, and stale/manual/experimental states remain explicit.

## Roadmap

- More provider packages when a documented service contract and public quota endpoint are available.
- More native notification backends.
- Official Copilot data retrieval is not part of the current implementation.

## Official references

- [Register a GitHub App](https://github.com/settings/apps/new)
- [GitHub App user access tokens and Device Flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
- [Install a GitHub App](https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party)
- [Control GitHub App installation in an organization](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/limiting-oauth-app-and-github-app-access-requests-and-installations)
- [Copilot user and seat REST API](https://docs.github.com/en/rest/copilot/copilot-user-management?apiVersion=2022-11-28)
- [Copilot usage metrics REST API](https://docs.github.com/en/rest/copilot/copilot-usage-metrics?apiVersion=2022-11-28)
- [Billing usage REST API](https://docs.github.com/en/rest/billing/usage?apiVersion=2022-11-28)
- [GitHub Copilot AI usage UI](https://github.com/settings/copilot)

[日本語版](README.ja.md) · [GitHub App setup guide](docs/github-app-setup.md) · [日本語セットアップガイド](docs/github-app-setup.ja.md)
