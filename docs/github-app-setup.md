# GitHub App setup for QuotaSight

[English](github-app-setup.md) | [日本語](github-app-setup.ja.md)

This guide configures the GitHub App used by QuotaSight's current GitHub authentication path. QuotaSight handles subscription quota, not API billing. For Copilot, the current implementation can probe `gh auth status` or complete GitHub App Device Flow, but `CopilotAdapter` does not retrieve quota and returns Unsupported. Authentication success must not be read as quota API success; use manual fallback when quota is unavailable.

## 1. Create an app for your own use

1. Open **GitHub Settings > Developer settings > GitHub Apps > New GitHub App**.
2. Enter an **App name** that identifies this QuotaSight installation.
3. Set a **Homepage URL**. Use the repository URL or another page you control.
4. **Callback URL is not used by Device Flow.** It may remain unused for this setup.
5. If you do not need webhooks, clear/disable webhook activation. QuotaSight does not require webhooks.
6. Enable **Expire user authorization tokens** (recommended).
7. Under **Where can this GitHub App be installed?**, choose the scope deliberately:
   - Choose **Any account** when a personally owned app must later be installed in a different organization.
   - Choose **Only on this account** when the app is owned by the target organization and will stay there, or when you only need the current authentication/status check and manual quota entry.
8. Enable **Enable Device Flow**. Without this setting, QuotaSight cannot start the device authorization flow.
9. Create the app and copy its **Client ID**. This is different from the App ID. In QuotaSight Settings, enter only:

   `YOUR_GITHUB_APP_CLIENT_ID`

Do not enter a client secret or private key. Device Flow does not require either. A private key does not need to be generated; if you generate one accidentally, never put it in this repository, a ZIP, logs, or screenshots.

## 2. Organization access and approval

The current authentication/status check and manual quota entry do not require organization installation. If a future QuotaSight version reads Copilot information associated with a target organization, the GitHub App must be installed for that organization and the organization owner must approve the installation/access. The user completing Device Flow must also have the GitHub permissions required by the organization's policy and the relevant Copilot access.

If the organization restricts GitHub App access, an owner or administrator must allow this app under the organization's access restrictions. For SAML SSO, authorize the app/session for the organization when GitHub prompts for SSO approval; an otherwise successful sign-in may still lack organization access until SSO is approved.

The current QuotaSight implementation sends `read:user read:org` in the Device Flow request. For a GitHub App, that OAuth-style scope string does not grant fine-grained permissions. Effective access is limited by the app's registered permissions, its installation, and the signed-in user's access. QuotaSight does not currently call an organization-context API and does not claim that login alone grants Copilot metrics or seat access.

## 3. What is deliberately not configured yet

If official Copilot data retrieval is implemented in the future, candidate read-only permissions should be evaluated as the minimum needed and clearly documented:

- **GitHub Copilot Business: read**
- **Organization Copilot metrics: read**

These are future candidates and are **currently unused**. **Administration: read** and **AI credit billing report** are administrator-oriented permissions/data paths and are not implemented by QuotaSight. Do not grant them just to make the current Device Flow work.

Copilot seats, organization metrics, and AI-credit usage do not equal one person's real-time remaining quota. Business credits are a shared pool, so a fixed personal allowance must not be hardcoded. Until an official read-only data contract is implemented, record the value manually and label the source, confidence, and freshness.

## Troubleshooting

### Client ID is missing

The Device Flow cannot start when the Client ID is empty or whitespace. Copy the GitHub App's **Client ID**, not its App ID, into QuotaSight Settings. Do not paste a secret or private key.

### Device Flow is not enabled

Return to the GitHub App settings and enable **Enable Device Flow**. Save the app settings, then start authentication again.

### The organization is not installed or approved

This applies only to future organization-data integrations, not the current authentication/status check and manual fallback. If the app is personally owned, confirm that **Where can this GitHub App be installed?** is set to **Any account**. Then ask an organization owner to install the app for the target organization and approve the requested access. Check the organization's GitHub App access restrictions. If SAML SSO is enabled, complete the SSO authorization for the organization.

### Authentication succeeds but access is denied

Confirm that the signed-in user has the required organization/Copilot access and that SAML SSO approval is current. A successful Device Flow token proves authentication only; it does not prove permission to read organization data.

### Copilot quota is unavailable

This is expected for the current adapter: quota retrieval is Unsupported. Use QuotaSight's manual fallback and the official Copilot UI or organization usage pages. Do not infer a personal remaining quota from seats, metrics, or billing totals.

## Official references

- [Register a GitHub App](https://github.com/settings/apps/new)
- [GitHub App user access tokens and Device Flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
- [Install a GitHub App](https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party)
- [Control GitHub App installation in an organization](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/limiting-oauth-app-and-github-app-access-requests-and-installations)
- [Copilot user and seat REST API](https://docs.github.com/en/rest/copilot/copilot-user-management?apiVersion=2022-11-28)
- [Copilot usage metrics REST API](https://docs.github.com/en/rest/copilot/copilot-usage-metrics?apiVersion=2022-11-28)
- [Billing usage REST API](https://docs.github.com/en/rest/billing/usage?apiVersion=2022-11-28)
- [GitHub Copilot AI usage UI](https://github.com/settings/copilot)

[Back to README](../README.md) · [日本語版README](../README.ja.md) · [日本語セットアップガイド](github-app-setup.ja.md)
