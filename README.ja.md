# QuotaSight

[English](README.md) | [日本語](README.ja.md)

QuotaSightは、サブスクリプションの利用枠を確認するWindows/Linux向けデスクトップアプリです。API料金、トークン課金、従量課金の支出を追跡するものではありません。各値に出典、確度、鮮度、反映上の制限を示します。推定値、古い値、手動入力値、実験的な接続先を、リアルタイムの確定値として扱うことはありません。

## 現在実装されている機能

- Avalonia製の画面で利用状況と履歴を確認し、設定を変更できます。実行中に日本語と英語を切り替えられ、狭い画面幅にも対応します。
- 利用枠の記録を日別のJSONL形式で30日間保存します。
- Windows Credential Manager、Linux Secret Service/`secret-tool`に対応します。安全な資格情報ストアが使えない場合は、セッション中だけメモリに保持します。
- テレメトリー（利用状況や診断情報を含む）を送信しません。機密値を設定、履歴、ログ、書き出し、画面表示に書き込みません。tokenは、後述のOSの安全な資格情報ストアまたはセッション中のメモリに保持される場合があります。
- テーマ、更新間隔、通知、しきい値、GitHub App Client ID、GitHub Organization slugを設定できます。
- 任意で常駐モードを使えます。省スペースの利用枠画面と、明示的な終了操作があります。

### 通知

通知しきい値の初期値は80%です。設定画面から通知を無効にできます。しきい値通知はQuotaSightの実行中に限り、定期更新の際に確認されます。手動入力した割合のスナップショットは、しきい値に初めて達したときに通知される場合があります。stale/unknownの観測値と、割合のないCopilotの数量データは、しきい値通知の対象外です。エラーや状態を知らせるバナーは、しきい値通知の設定とは別に表示されます。

通知は可能な範囲で送信を試みますが、表示を保証しません。Linuxではsession D-Bus経由で`org.freedesktop.Notifications`への送信を試みます。D-Busが要求を受け付けても、通知サービスが画面に表示するとは限りません。

Windows x64ではWindows App SDK 2.5.1の基盤を使い、portable Native AOT ZIPからOS通知の送信を試みます。APIが要求を受け付けたことと、画面にポップアップが表示されたことは別です。CIで確認するのはrestore、format、Release build/test、Native AOT publish/smokeまでです。Windows通知の登録と画面表示は、非昇格のWindows 11 x64デスクトップで別途手動確認が必要です。成功したという確認記録はまだありません。手順は[Windows通知の受け入れ確認](docs/windows-notification-acceptance.ja.md)を参照してください。

両OSとも、代替としてアプリ内バナーを表示します。ただし、ウィンドウが隠れている間は見えません。通常終了時には、`UnregisterAll`を含むWindows通知登録の解除を試みます。これは画面上にすでに表示された通知を消す操作ではありません。異常終了や後片付けの失敗時には、登録が残ることがあります。

## プロバイダーの取得方式と限界

| プロバイダー | 現在の方式 | できること・できないこと |
|---|---|---|
| ChatGPT Codex | QuotaSight独自のOpenAI device OAuthと、非公開・実験的な`wham/usage`接続先 | 1アカウントのCodex利用枠だけを扱い、ChatGPT Plus/Pro全体は対象にしません。Officialとは表示しません。取得に失敗した場合は手動入力と公式サブスクリプションページを案内します。 |
| Claude Pro | 手動入力と公式URL | 利用者が公式ページを見て利用枠とリセット時刻を入力します。請求APIは呼び出しません。 |
| OpenCode Go | 明示的に入力したAPI keyと利用状況の接続先 | 接続先が返すsubscription quotaだけを表示します。反映が遅れている値や取得できない値はstale/unknownとして扱います。OpenCode内部のcredential fileは読みません。 |
| GitHub Copilot | GitHub App Device Flowまたは`gh auth status`の確認。Billing Usageの取得は任意です。 | 認証と利用状況の参照権限は別です。`GET /organizations/{org}/settings/billing/ai_credit/usage`には、設定画面に登録したorganization slugと、`Administration: read`権限のある管理者のtokenが必要です。公式の`credits`数量をgross、discount、netに分けて表示し、割合や個人別の残量は計算しません。 |

## Copilotの認証とBilling Usage

認証だけを行う場合:

1. Device Flowを有効にしたGitHub Appを作成します。
2. 設定画面にはClient IDだけを入力します。QuotaSightはデスクトップ利用者にprivate keyやclient secretを要求しません。
3. Device Flowを完了するか、安全な`gh auth status`の確認を使います。

Billing Usageの取得は、これとは別の任意の手順です:

1. 設定画面に対象GitHub Organization slugを設定します。
2. GitHub Appのorganization permissionsで`Administration: Read-only`を設定します。
3. 対象organizationにAppをインストールし、要求した権限をorganization ownerに承認してもらいます。このBilling Usage経路では、インストールと承認の両方が必要です。
4. 必要なorganization管理権限を持つユーザーでDevice Flowを完了します。QuotaSightはDevice Flow tokenを使います。別のtoken入力欄はありません。
5. 一般のseat tokenでは403になることがあります。
6. QuotaSightは現在のreporting monthについて`GET /organizations/{org}/settings/billing/ai_credit/usage`を呼び出します。

Device Flowに成功しても、usageを参照できるとは限りません。GitHubからrefresh tokenが返された場合、期限付きuser token bundleをOS資格情報ストアに保存し、access tokenの期限前に更新します。既存の旧形式access tokenにはrefresh tokenがありません。そのtokenの期限が切れた後は、利用者が明示的に再認証する必要があります。安全な資格情報ストアが使えない場合、資格情報はセッション中だけ保持されるため、アプリの再起動後に再認証が必要です。

usageは請求主体ごとの共有枠に関する値であり、個人別の残量ではありません。カードには集計期間と取得時刻を示し、反映遅延の長さは不明であることも明記します。GitHubからの報告には遅れが生じることがあるため、表示値がいつの利用状況を反映しているかは確定できません。更新に失敗した場合、前回取得した値は最新ではなくstale（古い値）として示します。Organization slug、installation、owner approval、権限、接続先のいずれかを利用できない場合は、公式Copilotページを参照するか、手動入力を使います。seat、metrics、billing totalから個人別の残量を推定してはいけません。詳しくは[GitHub Appセットアップガイド](docs/github-app-setup.ja.md)を参照してください。

CodexではQuotaSight自身がdevice OAuthを実行し、Hermes/Codexの認証情報ファイルを読みません。また、client secretを要求・保持しません。プロバイダーの状態はManual、Official、Delayed、Experimentalを区別して表示します。

## 必要条件

- .NET SDK 10.0.x。
- Windows x64またはLinux x64。
- Linuxの画面表示にはGTKが必要です。OS通知の送信にはsession D-Busと通知サービスが必要ですが、通知サービスが要求を受け付けても画面表示は保証されません。Secret Serviceは任意です。
- 既存のGitHub CLI状態確認には`gh`、Device Flowにはbrowserが必要です。

## 配布

正式にサポートする配布物は、Windows x64またはLinux x64向けの自己完結型.NET 10 Native AOT ZIPです。MSIX、deb、AppImageは現在サポートしていません。

## ビルドと実行

```bash
dotnet restore QuotaSight.slnx
dotnet build QuotaSight.slnx -c Release --no-restore
dotnet test QuotaSight.slnx -c Release --no-build --no-restore
dotnet format QuotaSight.slnx --verify-no-changes --no-restore
dotnet run --project src/QuotaSight.UI/QuotaSight.UI.csproj
# 画面を表示しない起動契約の確認
dotnet run --project src/QuotaSight.UI/QuotaSight.UI.csproj -- --smoke-test
```

## セキュリティとプライバシー

QuotaSightはローカル優先で、テレメトリーを送信しません。プロバイダーCLIの資格情報ファイルは読み取り・解析しません。token、API key、cookie、password、生のプロバイダー応答、完全なCLI出力を、設定、履歴、ログ、クラッシュレポート、書き出し、テスト、画面表示に書き込みません。tokenなどの機密値は、Windows Credential ManagerまたはLinux Secret Serviceに保持される場合があります。安全なストアが利用不能またはロック中の場合、資格情報は現在のセッション中だけメモリに保持します。詳しくは[セキュリティの説明](docs/security.md)を参照してください。

利用期間はそれぞれ分けて保持します。代表値には、判明している期間のうち最も逼迫したものを使います。無関係な期間同士は合算しません。100%を超える値も保持し、視覚的な進捗表示だけを100%に合わせます。リセット時刻、データの鮮度、取得元の確度、stale/manual/experimentalの状態を明示します。

## Roadmap

- 公開されたサービス契約と利用枠の接続先を確認できたプロバイダーを追加します。
- Windows実機でOS通知が画面に表示されることを検証します。APIが要求を受け付けただけではポップアップ表示を確認したことになりません。
- GitHubが文書化されたpermissionとresponse contractを提供した場合に限り、Copilot organization対応を拡張します。

## 公式リンク

- [GitHub Appを登録](https://github.com/settings/apps/new)
- [GitHub AppのユーザーアクセストークンとDevice Flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
- [GitHub Appのユーザーアクセストークンを更新する](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens)
- [GitHub Appをインストール](https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party)
- [OrganizationでGitHub Appのインストールを制限](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/limiting-oauth-app-and-github-app-access-requests-and-installations)
- [請求利用状況REST API](https://docs.github.com/en/rest/billing/usage?apiVersion=2022-11-28)
- [GitHub Copilot AI usage UI](https://github.com/settings/copilot)

[English](README.md) · [英語セットアップガイド](docs/github-app-setup.md) · [日本語セットアップガイド](docs/github-app-setup.ja.md)
