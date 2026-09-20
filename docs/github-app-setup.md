# GitHub App setup for QuotaSight

[English](github-app-setup.md) | [日本語](github-app-setup.ja.md)

QuotaSight uses a GitHub App for authentication. Authentication is separate from Copilot Billing Usage retrieval. QuotaSight does not call an API-billing endpoint for personal cost tracking; it reads the documented Copilot AI-credit usage response when the required organization access is available.

## 1. Authentication only: create the App

1. Open **GitHub Settings > Developer settings > GitHub Apps > New GitHub App**.
2. Enter an app name and homepage URL.
3. Leave callback settings unused; Device Flow does not need a callback URL.
4. Disable webhooks unless you need them for another product.
5. Enable **Expire user authorization tokens** if appropriate.
6. Enable **Enable Device Flow**.
7. Create the app and copy its **Client ID**, not its App ID.
8. In QuotaSight Settings, enter only the Client ID.

QuotaSight does not request a client secret or private key from the desktop user. Device Flow does not require either, and QuotaSight does not issue installation access tokens in the app.

Complete Device Flow, or use the `gh auth status` probe. A successful Device Flow proves that GitHub authenticated the user; it does not prove that the user can read organization Billing Usage.

## 2. Billing Usage retrieval: additional setup

To retrieve usage, also:

1. Set the target **Organization slug** in QuotaSight Settings.
2. In the App's organization permissions, set **Administration: Read-only**.
3. Install the GitHub App in the target organization.
4. Ask an organization owner to approve the installation and requested access. This installation and owner approval are required for the Billing Usage path; also complete organization access restrictions and SAML SSO approval.
5. Complete Device Flow with a user who has the required organization administration permission. QuotaSight uses that Device Flow token and has no separate token input field.

QuotaSight calls:

`GET /organizations/{org}/settings/billing/ai_credit/usage`

The response is requested for the current reporting year and month and is filtered to the signed-in user. A general seat token may receive **403 Forbidden** even when Device Flow succeeded. Organization installation, owner approval, administrator access, and the organization slug are all separate requirements.

The supported response uses official `unitType: credits`. QuotaSight displays gross, discount, net, unit, aggregation month, retrieval time, and the fact that reporting delay is unknown. The billing entity is a shared pool; QuotaSight does not calculate a personal remaining balance. GitHub reporting can lag, so a successful request is not proof of real-time usage.

## 3. Troubleshooting and fallback

- **Client ID missing:** enter the GitHub App Client ID, not App ID. Never paste a client secret or private key.
- **Device Flow disabled:** enable **Enable Device Flow** in the GitHub App settings.
- **403 Forbidden:** verify the organization slug, installation, owner approval, SAML SSO, and `Administration: read`. A normal seat token may not be sufficient.
- **Authenticated but no usage:** authentication success is not usage authorization. Use the official Copilot page or QuotaSight manual fallback.
- **Delayed or unavailable data:** do not infer personal remaining from seats, organization metrics, or billing totals. Use the official page or manual entry and keep source and freshness visible.

## Official references

- [Register a GitHub App](https://github.com/settings/apps/new)
- [GitHub App user access tokens and Device Flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
- [Install a GitHub App](https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party)
- [Control GitHub App installation in an organization](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/limiting-oauth-app-and-github-app-access-requests-and-installations)
- [Billing usage REST API](https://docs.github.com/en/rest/billing/usage?apiVersion=2022-11-28)
- [GitHub Copilot AI usage UI](https://github.com/settings/copilot)

[Back to README](../README.md) · [日本語版README](../README.ja.md) · [日本語セットアップガイド](github-app-setup.ja.md)
