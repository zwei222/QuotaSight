# QuotaSight向けGitHub Appセットアップ

[English](github-app-setup.md) | [日本語](github-app-setup.ja.md)

このガイドでは、QuotaSightの現在のGitHub認証経路に使うGitHub Appを設定します。QuotaSightが扱うのはAPI料金ではなく、サブスクリプションの利用枠です。Copilotについて、現実装でできるのは`gh auth status`のprobeまたはGitHub App Device Flowによる認証までです。`CopilotAdapter`のquota取得はUnsupportedで、取得できない場合はmanual fallbackになります。ログイン成功をquota API取得成功と解釈しないでください。

## 1. 自分用のAppを作成する

1. **GitHub Settings > Developer settings > GitHub Apps > New GitHub App**を開きます。
2. このQuotaSight用Appだと分かる**App name**を入力します。
3. **Homepage URL**を設定します。リポジトリURLなど、自分が管理するページを指定してください。
4. **Callback URLはDevice Flowでは使用しません。**この用途では未使用のままで構いません。
5. Webhookが不要ならWebhookの有効化を解除します。QuotaSightはWebhookを必要としません。
6. **Expire user authorization tokens**を有効にすることを推奨します。
7. **Where can this GitHub App be installed?**では、用途に合う公開範囲を選びます。
   - 個人所有のAppを別のOrganizationへインストールする場合は、**Any account**を選びます。
   - 対象Organizationが所有し、そのOrganization内だけで使う場合、または現行の認証・状態確認と手動入力だけを使う場合は、**Only on this account**を選べます。
8. **Enable Device Flow**を必ず有効にします。これを有効にしないとQuotaSightはデバイス認証を開始できません。
9. Appを作成し、**Client ID**をコピーします。これはApp IDとは別の値です。QuotaSightの設定画面には次の値だけを入力します。

   `YOUR_GITHUB_APP_CLIENT_ID`

client secretやprivate keyは入力しません。Device Flowにはどちらも不要です。private keyを生成する必要もありません。誤って生成した場合も、repository、ZIP、log、screenshotへ置かないでください。

## 2. Organizationのアクセスと承認

現行の認証・状態確認と手動入力には、Organizationへのインストールは不要です。将来のQuotaSightが対象OrganizationのCopilot情報を取得する場合は、そのOrganizationへGitHub Appをインストールし、Organization ownerがインストールとアクセスを承認する必要があります。デバイス認証を行うユーザーにも、OrganizationのポリシーとCopilot設定に応じたGitHub権限が必要です。

OrganizationがGitHub Appのアクセスを制限している場合、ownerまたはadministratorがorganizationのアクセス制限でこのAppを許可してください。SAML SSOを使っている場合は、GitHubが表示する案内に従ってorganization向けのSSO承認を完了します。サインイン自体が成功していても、SSO承認が終わるまでorganizationへアクセスできないことがあります。

現在のQuotaSightはデバイス認証のリクエストで`read:user read:org`を送信します。ただし、GitHub Appでは、このOAuth形式のscope文字列によって細粒度の権限が付与されるわけではありません。実際のアクセス範囲は、Appに登録した権限、インストール先、ログインしたユーザーの権限で決まります。現行版はOrganization情報を確認するAPIを呼び出しておらず、ログインだけでCopilotのメトリクスやシートを読めるとは説明していません。

## 3. 現時点で設定しないもの

将来、公式のCopilotデータ取得を実装する場合は、必要最小限のread-only権限候補として次を検討します。

- **GitHub Copilot Business: read**
- **Organization Copilot metrics: read**

これらは将来候補であり、**現在は未使用**です。**Administration: read**や**AI credit billing report**は管理者向けの権限・データ経路で、QuotaSightでは未実装です。現在のDevice Flowを動かすためだけに付与しないでください。

Copilotのseat、organization metrics、AI credit usageは、個人のリアルタイム残りquotaと同じではありません。Businessのcreditは共有プールなので、個人の固定残量をhardcodeしてはいけません。公式のread-onlyデータ契約が実装されるまでは、値を手動入力し、出典、確度、鮮度を表示してください。

## トラブルシュート

### Client IDが未設定

Client IDが空または空白だとDevice Flowは開始できません。App IDではなく、GitHub Appの**Client ID**をQuotaSight Settingsへ入力してください。secretやprivate keyは貼り付けません。

### Device Flowが有効になっていない

GitHub Appの設定へ戻り、**Enable Device Flow**を有効にして保存します。その後、認証を再実行してください。

### Organizationにinstallation/承認がない

これは将来のOrganizationデータ連携に関する項目で、現行の認証・状態確認と手動入力には影響しません。個人所有のAppなら、**Where can this GitHub App be installed?**が**Any account**になっていることを確認します。そのうえで、対象OrganizationのownerにAppのインストールとアクセス承認を依頼してください。OrganizationのGitHub Appアクセス制限も確認します。SAML SSOが有効なら、Organization向けのSSO承認を完了します。

### 認証は成功するがアクセスが拒否される

ログインしたユーザーが必要なorganization/Copilot権限を持っているか、SAML SSOの承認が有効かを確認します。Device Flow tokenが取得できても、それは認証の証明にすぎず、organization dataを読める証明ではありません。

### Copilot quotaが利用できない

現行adapterでは想定された状態です。quota取得はUnsupportedなので、QuotaSightのmanual fallbackと公式Copilot UIまたはorganization usage pageを使ってください。seat、metrics、billing合計から個人の残りquotaを推定しないでください。

## 公式リンク

- [GitHub Appを登録](https://github.com/settings/apps/new)
- [GitHub AppのユーザーアクセストークンとDevice Flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
- [GitHub Appをインストール](https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party)
- [OrganizationでGitHub Appのインストールを制限](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/limiting-oauth-app-and-github-app-access-requests-and-installations)
- [Copilotユーザー・シート管理REST API](https://docs.github.com/en/rest/copilot/copilot-user-management?apiVersion=2022-11-28)
- [Copilot利用状況メトリクスREST API](https://docs.github.com/en/rest/copilot/copilot-usage-metrics?apiVersion=2022-11-28)
- [請求利用状況REST API](https://docs.github.com/en/rest/billing/usage?apiVersion=2022-11-28)
- [GitHub Copilot AI usage UI](https://github.com/settings/copilot)

[READMEへ戻る](../README.md) · [日本語版README](../README.ja.md) · [Englishセットアップガイド](github-app-setup.md)
