# QuotaSight向けGitHub Appセットアップ

[English](github-app-setup.md) | [日本語](github-app-setup.ja.md)

QuotaSightは認証にGitHub Appを使います。認証とCopilot Billing Usageの取得は別の手順です。個人のAPI料金を追跡するのではなく、必要なorganization accessがある場合に、文書化されたCopilot AIクレジットの利用状況を読み取ります。

## 1. 認証だけの設定: Appを作成する

1. **GitHub Settings > Developer settings > GitHub Apps > New GitHub App**を開きます。
2. App nameとHomepage URLを入力します。
3. Device Flowではcallback URLを使わないため、callback設定は未使用のままにします。
4. 別の用途でWebhookを使わない場合は、無効にします。
5. 必要に応じて**Expire user authorization tokens**を有効にします。
6. **Enable Device Flow**を有効にします。
7. Appを作成し、App IDではなく**Client ID**をコピーします。
8. QuotaSightの設定画面にはClient IDだけを入力します。

QuotaSightはデスクトップ利用者にclient secretやprivate keyを要求しません。Device Flowにこれらは不要です。また、アプリ内でinstallation access tokenを発行することもありません。GitHub Appのユーザーアクセストークンには従来のOAuth scopeを使いません。利用できる範囲は、App permissionsと認証したユーザー自身の権限の両方で許可された部分に限られます。

Device Flowを完了するか、`gh auth status` probeを使います。Device Flowに成功したことが示すのは、GitHubが利用者を認証したということだけです。organization Billing Usageを参照できることまでは保証しません。

## 2. Billing Usage取得: 追加設定

usageを取得するには、次の設定も行います。

1. QuotaSightの設定画面に対象の**Organization slug**を設定します。
2. Appのorganization permissionsで**Administration: Read-only**を設定します。
3. GitHub Appを対象organizationにinstallします。
4. organization ownerにinstallと要求権限を承認してもらいます。このBilling Usage経路にはinstallとownerの承認が必要です。organizationのaccess restrictionとSAML SSOの承認も完了させます。
5. 対象organizationのownerなど、必要なorganization管理権限を持つユーザーでDevice Flowを完了します。QuotaSightはそのDevice Flow tokenを使います。別のtoken入力欄はありません。

QuotaSightが呼び出す接続先:

`GET /organizations/{org}/settings/billing/ai_credit/usage`

このBilling Usage requestには`X-GitHub-Api-Version: 2026-03-10`を指定します。現在のreporting yearとmonthを指定し、ログインユーザーのusageを取得します。Device Flowに成功していても、一般のseat tokenでは**403 Forbidden**になることがあります。organizationへのinstall、owner approval、管理者権限、organization slugはそれぞれ別に必要です。

公式OpenAPI exampleの`unitType`は`credits`です。一方、GitHub Billing Usageの実際のresponseでは`unitType: ai-credits`が返る場合もあります。QuotaSightは、どちらも大文字・小文字を区別せず同じAI Creditsとして正規化し、snapshotのunitは`credits`のまま保持します。未知のunit値はUnsupportedとして扱います。QuotaSightはgross、discount、net、unit、集計月、取得時刻を表示し、反映遅延の長さは不明であることも示します。billing entityは共有枠なので、個人別の残量は計算しません。GitHub側の利用状況の報告には遅れが生じることがあり、requestに成功してもリアルタイムのusageとは限りません。

## 3. トラブルシュートとfallback

- **Client IDがない:** App IDではなくGitHub App Client IDを入力します。client secretやprivate keyは絶対に貼り付けないでください。
- **Device Flowが無効:** GitHub Appの設定で**Enable Device Flow**を有効にします。
- **403 Forbidden:** organization slug、install、owner approval、SAML SSO、`Administration: read`を確認します。通常のseat tokenでは権限が足りない場合があります。
- **認証済みだがusageがない:** 認証成功はusage参照権限を意味しません。公式Copilot pageまたはQuotaSightのmanual fallbackを使います。
- **遅延または取得不能:** seat、organization metrics、billing totalから個人別の残量を推定しないでください。公式pageまたは手動入力を使い、sourceと鮮度を明示します。

## 公式リンク

- [GitHub Appを登録](https://github.com/settings/apps/new)
- [GitHub AppのユーザーアクセストークンとDevice Flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
- [GitHub Appをインストール](https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party)
- [OrganizationでGitHub Appのインストールを制限](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/limiting-oauth-app-and-github-app-access-requests-and-installations)
- [請求利用状況REST API](https://docs.github.com/en/rest/billing/usage?apiVersion=2026-03-10)
- [GitHub Copilot AI usage UI](https://github.com/settings/copilot)

[READMEへ戻る](../README.md) · [日本語版README](../README.ja.md) · [Englishセットアップガイド](github-app-setup.md)
