# QuotaSight向けGitHub Appセットアップ

[English](github-app-setup.md) | [日本語](github-app-setup.ja.md)

QuotaSightは認証にGitHub Appを使います。認証とCopilot Billing Usage取得は別の手順です。個人のAPI料金を追跡するのではなく、必要なorganization accessがある場合に文書化されたCopilot AIクレジットusage responseを読み取ります。

## 1. 認証だけの設定: Appを作成する

1. **GitHub Settings > Developer settings > GitHub Apps > New GitHub App**を開きます。
2. App nameとHomepage URLを入力します。
3. Device Flowではcallback URLを使わないため、callback設定は未使用のままにします。
4. 別用途でWebhookを使わない場合は無効にします。
5. 必要に応じて**Expire user authorization tokens**を有効にします。
6. **Enable Device Flow**を有効にします。
7. Appを作成し、App IDではなく**Client ID**をコピーします。
8. QuotaSight SettingsにはClient IDだけを入力します。

QuotaSightはdesktop利用者にclient secretやprivate keyを要求しません。Device Flowにはどちらも不要で、アプリ内でinstallation access tokenを発行することもありません。GitHub Appのユーザーアクセストークンは従来のOAuth scopeを使わず、App permissionsと認証ユーザー自身の権限の共通部分で制限されます。

Device Flowを完了するか、`gh auth status` probeを使います。Device Flow成功はGitHubが利用者を認証したことを示すだけで、organization Billing Usageの参照権限を示しません。

## 2. Billing Usage取得: 追加設定

usageを取得するには、さらに次を行います。

1. QuotaSight Settingsに対象の**Organization slug**を設定します。
2. Appのorganization permissionsで**Administration: Read-only**を設定します。
3. GitHub Appを対象organizationへinstallします。
4. organization ownerにinstallationと要求権限を承認してもらいます。Billing Usage経路ではinstallationとowner承認が必須です。organizationのaccess restrictionやSAML SSO承認も完了します。
5. 対象organizationのownerなど、必要なorganization管理権限を持つユーザーでDevice Flowを完了します。QuotaSightはそのDevice Flow tokenを使い、別token入力欄はありません。

QuotaSightが呼ぶendpointは次です。

`GET /organizations/{org}/settings/billing/ai_credit/usage`

このBilling Usage requestには`X-GitHub-Api-Version: 2026-03-10`を指定します。現在のyear/monthを指定し、ログインユーザーのusageを取得します。Device Flowが成功していても、一般のseat tokenでは**403 Forbidden**になる可能性があります。organization installation、owner approval、管理者権限、organization slugはそれぞれ別の要件です。

公式OpenAPI exampleの`unitType`は`credits`ですが、live GitHub Billing Usage responseでは`unitType: ai-credits`も観測されます。QuotaSightは両方のvariantを大文字小文字を区別せず同じAI Creditsとして正規化し、snapshotのunitは`credits`を維持します。未知のunit値はUnsupportedのままです。QuotaSightはgross、discount、net、unit、集計月、取得時刻、反映遅延不明を表示します。billing entityはshared poolなので、個人のremainingは計算しません。GitHub側のreportingには遅延があり、request成功はリアルタイムusageを保証しません。

## 3. トラブルシュートとfallback

- **Client IDがない:** App IDではなくGitHub App Client IDを入力します。client secretやprivate keyは貼り付けません。
- **Device Flowが無効:** GitHub App設定で**Enable Device Flow**を有効にします。
- **403 Forbidden:** organization slug、installation、owner approval、SAML SSO、`Administration: read`を確認します。通常のseat tokenでは不足する可能性があります。
- **認証済みだがusageがない:** 認証成功はusage参照権限を意味しません。公式Copilot pageまたはQuotaSightのmanual fallbackを使います。
- **遅延または取得不能:** seat、organization metrics、billing totalから個人remainingを推定しません。公式pageまたは手動入力を使い、sourceとfreshnessを明示します。

## 公式リンク

- [GitHub Appを登録](https://github.com/settings/apps/new)
- [GitHub AppのユーザーアクセストークンとDevice Flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
- [GitHub Appをインストール](https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party)
- [OrganizationでGitHub Appのインストールを制限](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/limiting-oauth-app-and-github-app-access-requests-and-installations)
- [請求利用状況REST API](https://docs.github.com/en/rest/billing/usage?apiVersion=2026-03-10)
- [GitHub Copilot AI usage UI](https://github.com/settings/copilot)

[READMEへ戻る](../README.md) · [日本語版README](../README.ja.md) · [Englishセットアップガイド](github-app-setup.md)
