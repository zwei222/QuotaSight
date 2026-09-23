# QuotaSight

[English](README.md) | [日本語](README.ja.md)

QuotaSightは、サブスクリプション契約の利用枠を確認するWindows/Linux向けデスクトップダッシュボードです。API料金、トークン課金、従量課金の支出を表示するアプリではありません。取得値には出典、確度、鮮度、反映上の制限を付け、推定値・古い値・手動入力・実験的endpointをリアルタイムの確定値として扱いません。

## 現在実装されている機能

- Avaloniaのdashboard、history、settings、実行中の日本語/英語切り替え、狭い幅のlayout。
- quota snapshotの日別JSONL保存（30日間）。
- Windows Credential Manager、Linux Secret Service/`secret-tool`。安全なcredential storeが使えない場合はセッション中だけ保持します。
- telemetryなし。token、平文credential、生のprovider responseは保存・表示しません。
- theme、更新間隔、通知、しきい値、GitHub App Client ID、GitHub Organization slugの設定。
- 任意の常駐モード、省スペースの利用枠画面、明示的な終了操作。

### 通知

通知しきい値の初期値は80%で、Settingsから通知を無効にできます。しきい値通知はQuotaSightの実行中に限り、定期更新時に確認されます。手動で入力した割合snapshotは、しきい値に初めて達したときに通知する場合があります。stale/unknownの観測値と、割合を持たないCopilotの数量データは、しきい値通知の対象外です。エラー・状態bannerは、しきい値通知のtoggleとは独立しています。

通知の配信はbest effortであり、表示を保証するものではありません。Linuxではsession D-Bus経由で`org.freedesktop.Notifications`への送信を試みますが、D-Busが要求を受け付けてもnotification daemonによる表示は保証されません。Windows x64ではWindows App SDK 2.5.1のbackendを使い、portable Native AOT ZIP buildからOS通知の送信を試みます。APIが受け付けたことと画面にpopupが表示されたことは別であり、Windows実機でのpixel-level表示確認は未実施です。両OSでfallbackとしてアプリ内bannerを残しています（ウィンドウが非表示の間はbannerを表示できません）。通常終了時にはWindows通知登録の解除（`UnregisterAll`を含む）を試みますが、異常終了やcleanup失敗時に登録が残る可能性があります。

## Providerの取得方式と限界

| Provider | 現在の方式 | できること・できないこと |
|---|---|---|
| ChatGPT Codex | QuotaSight独自のOpenAI device OAuthとundocumented/experimentalな`wham/usage` endpoint | 1アカウントのCodex枠のみで、ChatGPT Plus/Pro全体ではありません。Officialとは表示せず、失敗時は手動入力と公式subscription pageへfallbackします。 |
| Claude Pro | 手動入力と公式URL | 利用者が公式ページからquotaとresetを入力します。請求APIは呼びません。 |
| OpenCode Go | 明示的なAPI keyとusage endpoint | endpointが返すsubscription quotaだけを表示し、遅延・未提供はstale/unknownとします。内部credential fileは読みません。 |
| GitHub Copilot | GitHub App Device Flowまたは`gh auth status` probe、任意でBilling Usage取得 | 認証とusage参照権限は別です。設定したorganization slugと`Administration: read`を持つ管理者tokenが必要です。endpointは`GET /organizations/{org}/settings/billing/ai_credit/usage`です。公式`credits`数量をgross、discount、netで表示し、割合と個人remainingは算出しません。 |

## Copilotの認証とBilling Usage

認証だけの手順:

1. Device Flowを有効にしたGitHub Appを作成します。
2. SettingsにはClient IDだけを入力します。QuotaSightはdesktop利用者にprivate keyやclient secretを要求しません。
3. Device Flowを完了するか、安全な`gh auth status` probeを使います。

Billing Usage取得は別の任意手順です:

1. Settingsに対象GitHub Organization slugを設定します。
2. GitHub Appのorganization permissionsで`Administration: Read-only`を設定します。
3. 対象organizationへAppをinstallし、要求権限をorganization ownerに承認してもらいます。Billing Usage経路ではこのinstallationと承認が必須です。
4. 必要なorganization管理権限を持つユーザーでDevice Flowを完了します。QuotaSightはDevice Flow tokenを使い、別token入力欄はありません。
5. 一般のseat tokenでは403になる可能性があります。
6. QuotaSightは現在のreporting monthについて`GET /organizations/{org}/settings/billing/ai_credit/usage`を呼びます。

Device Flow成功はusage権限を意味しません。GitHubからrefresh tokenを受け取った場合、期限付きuser token bundleをOS資格情報ストアに保存し、access tokenの期限前に更新します。既存の旧形式access tokenにはrefresh tokenがないため、期限切れ後は利用者が明示的に再認証する必要があります。secure storeを利用できない場合はcredentialをセッション中だけ保持するため、アプリ再起動後に再認証が必要です。usageは個人のremainingではなく、請求主体単位のshared billing-entity poolに関する値です。GitHub側のreporting delayがあるため、cardには集計期間、取得時刻、反映遅延不明を表示します。更新に失敗した場合、前回取得値は最新値ではなくstaleとして示します。slug、installation、owner approval、権限、endpointのいずれかが利用できない場合は、公式Copilot pageまたはmanual fallbackを使います。seat、metrics、billing totalから個人remainingを推定しないでください。詳しくは[GitHub Appセットアップガイド](docs/github-app-setup.ja.md)を参照してください。

CodexではQuotaSight自身がdevice OAuthを実行し、Hermes/Codexのcredential fileを読みません。providerの状態はManual、Official、Delayed、Experimentalを区別します。

## 必要条件

- .NET SDK 10.0.x。
- Windows x64またはLinux x64。
- LinuxのGUIにはGTKが必要です。OS通知の送信試行にはsession D-Busとnotification daemonが必要ですが、daemonが受け付けても表示は保証されません。Secret Serviceは任意です。
- 既存のGitHub CLI状態確認には`gh`、Device Flowにはbrowserが必要です。

## 配布

正式なrelease artifactはWindows x64またはLinux x64向けのself-contained .NET 10 Native AOT ZIPです。MSIX、deb、AppImageは現在サポート対象外です。

## buildと実行

```bash
dotnet restore QuotaSight.slnx
dotnet build QuotaSight.slnx -c Release --no-restore
dotnet test QuotaSight.slnx -c Release --no-build --no-restore
dotnet format QuotaSight.slnx --verify-no-changes --no-restore
dotnet run --project src/QuotaSight.UI/QuotaSight.UI.csproj
# headless起動契約の確認
dotnet run --project src/QuotaSight.UI/QuotaSight.UI.csproj -- --smoke-test
```

## securityとprivacy

QuotaSightはlocal-firstで、外部telemetryを送信しません。provider CLIのcredential fileは読み取り・解析しません。token、API key、cookie、password、生のprovider response、完全なCLI出力をsettings、history、log、crash report、export、test、UI textへ書き込みません。永続化するsecretはWindows Credential ManagerまたはLinux Secret Serviceのみを使い、利用不能・ロック時は現在のセッション中だけメモリに保持します。詳しくは[securityの説明](docs/security.md)を参照してください。

quota windowは混ぜずに保持します。dashboardの代表値は最も逼迫したwindowで、無関係なwindowを合算しません。100%超の値も保持し、clampするのは視覚的なprogress indicatorだけです。reset時刻、freshness、source confidence、stale/manual/experimental状態を明示します。

## Roadmap

- 公開されたサービス契約とquota endpointが確認できたproviderの追加。
- Windows実機でOS通知が画面に表示されることを検証します。APIが受け付けたことだけではpopup表示を確認したことになりません。
- GitHubが文書化されたpermissionとresponse contractを提供した場合のCopilot organization対応拡張。

## 公式リンク

- [GitHub Appを登録](https://github.com/settings/apps/new)
- [GitHub AppのユーザーアクセストークンとDevice Flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
- [GitHub Appのユーザーアクセストークンを更新する](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens)
- [GitHub Appをインストール](https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party)
- [OrganizationでGitHub Appのインストールを制限](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/limiting-oauth-app-and-github-app-access-requests-and-installations)
- [請求利用状況REST API](https://docs.github.com/en/rest/billing/usage?apiVersion=2022-11-28)
- [GitHub Copilot AI usage UI](https://github.com/settings/copilot)

[English](README.md) · [英語セットアップガイド](docs/github-app-setup.md) · [日本語セットアップガイド](docs/github-app-setup.ja.md)
