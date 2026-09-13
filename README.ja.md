# QuotaSight

[English](README.md) | [日本語](README.ja.md)

QuotaSightは、サブスクリプション契約の利用枠を確認するWindows/Linux向けデスクトップダッシュボードです。API料金、トークン課金、従量課金の支出を表示するアプリではありません。取得値には出典と確度を付け、推定値・古い値・手動入力・実験的エンドポイントをリアルタイムの確定値として扱いません。

## 現在実装されている機能

- 安全なサンプルデータを表示するDemo mode。
- Avaloniaによるdashboard、history、settings画面。
- 実行中の日本語/英語切り替えと狭い幅のレイアウト。
- 選択式の常駐モード。トレイアイコンの通常クリックで省スペースな利用枠画面を開き、コンテキストメニューから正式なウィンドウ、更新、明示的な終了を選べます。native tray hostが使えない、または途中で消失した場合は、復帰不能な非表示状態にせず通常のウィンドウを維持・復元します。
- quota snapshotを日別JSONLで30日間保持。
- Windows Credential Manager、Linux Secret Service/`secret-tool`を利用。安全な資格情報ストアが使えない場合はセッション中だけメモリに保持。
- telemetryなし。token、平文credential、生のprovider responseは保存・表示しません。
- テーマ（System/Light/Dark）、更新間隔、通知設定、しきい値、実行時設定のGitHub App Client IDに対応。Client IDは秘密情報ではありません。

## Providerの取得方式と限界

| Provider | 現在の方式 | できること・できないこと |
|---|---|---|
| ChatGPT Codex | QuotaSight独自のOpenAI device OAuthと、undocumented/experimentalな`wham/usage` | 1つのChatGPTアカウントのCodex枠のみで、ChatGPT Plus/Pro全体の利用量ではありません。公式Codex実装からpublic client IDとendpointは確認できますが、第三者アプリ向けの安定した契約とclient ID再利用の許可は確認できないため、Officialとは表示しません。live loginも未検証です。失敗時は手動入力と公式ChatGPTサブスクリプションページへfallbackします。 |
| Claude Pro | 手動入力と公式URL | 公式ページを開き、quotaとreset情報を利用者が入力します。請求APIは呼びません。 |
| OpenCode Go | 明示的なAPI keyとusage endpoint | endpointが返す範囲のsubscription quotaだけを表示します。遅延・未提供なら推測せずstale/unknownとします。OpenCode内部のcredential fileは読みません。 |
| GitHub Copilot | `gh auth status` probeまたはGitHub App Device Flow | 現時点の実装は認証・状態確認までです。`CopilotAdapter`のquota取得はUnsupportedで、Copilotはmanual fallbackになります。GitHubへのログイン成功はquota API取得成功を意味しません。[GitHub Appセットアップガイド](docs/github-app-setup.ja.md)を参照してください。 |

CodexではQuotaSight自身がdevice OAuthを実行し、Hermes/Codexのcredential fileを読みません。client secretは要求・保持せず、tokenはOSの安全なcredential storeまたはセッションメモリだけに置きます。providerの状態はManual、Official、Delayed、Experimentalを区別します。

### Copilotの用語とmanual fallback

Copilotのseat、organization metrics、AI credit usageは、個人のリアルタイム残りquotaと同じではありません。Businessのcreditは共有プールです。個人用の固定残量をhardcodeしてはいけません。公式のCopilotデータ契約が実装されるまでは、該当する値を手動入力し、出典と鮮度を明示してください。

GitHub Appガイドでは、installation、owner approval、organization access、SAML SSO承認と安全上の境界を説明しています。現在のDevice FlowでQuotaSightに入力するのはClient IDだけです。client secretやprivate keyは入力しません。このflowにprivate keyは不要です。

## 必要条件

- .NET SDK 10.0.x。
- Windows x64またはLinux x64。
- LinuxのGUI/tray/notificationは、実行環境のGTK、D-Bus、notification daemon、必要に応じてSecret Serviceに依存します。未導入時は可能な範囲でfallbackします。
- 既存のGitHub CLI認証を確認する場合は`gh`、Device Flowにはブラウザーが必要です。

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

`--smoke-test`はUIを表示せず、起動と基本依存を確認して終了します。

### 選択式の常駐モード

**設定 → 更新と通知**で**常駐モードを有効にする**を選びます。以後、正式なウィンドウを閉じてもQuotaSightはトレイに残ります。トレイアイコンの通常クリックでは利用枠確認用の省スペース画面を開き、コンテキストメニューから正式なウィンドウの表示、利用枠の更新、アプリの終了を実行できます。2つの画面は同じアプリ状態と更新schedulerを共有します。常駐モードは初期状態では無効で、動作するnative tray hostがない環境では有効になりません。

## Release ZIP

Release workflowは対象OS上で自己完結型の.NET 10 Native AOT binaryを作り、`linux-x64`または`win-x64`向けに`QuotaSight-<version>-<rid>.zip`を公開します。ZIPを展開し、Linuxでは`QuotaSight.UI`、Windowsでは`QuotaSight.UI.exe`を実行してください。Linuxでは必要に応じて実行権限を付け、GUI/tray/notificationの依存を導入します。MSIX、deb、AppImageは現在提供していません。

## securityとprivacy

QuotaSightはlocal-firstで、外部telemetryを送信しません。historyは日別JSONLとして30日間保持します。provider CLIのcredential fileは読み取り・解析しません。token、API key、cookie、password、生のprovider response、完全なCLI出力をsettings、history、log、crash report、export、test、UI textへ書き込みません。永続化するsecretはWindows Credential ManagerまたはLinux Secret Serviceのみを使い、利用不能・ロック時は現在のセッション中だけメモリに保持します。詳しくは[securityの説明](docs/security.md)を参照してください。

quota window（rolling、daily、weekly、monthly、custom）は混ぜずに保持します。dashboardの代表値は判明している中で最も逼迫したwindowで、無関係なwindowを合算しません。100%超の値も保持し、clampするのは視覚的なprogress indicatorだけです。reset時刻、freshness、source confidence、stale/manual/experimental状態を明示します。

## Roadmap

- 公開されたサービス契約とquota endpointが確認できたproviderの追加。
- native notification backendの追加。
- 公式Copilotデータ取得は現時点では未実装です。

## 公式リンク

- [GitHub Appを登録](https://github.com/settings/apps/new)
- [GitHub AppのユーザーアクセストークンとDevice Flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
- [GitHub Appをインストール](https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party)
- [OrganizationでGitHub Appのインストールを制限](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/limiting-oauth-app-and-github-app-access-requests-and-installations)
- [Copilotユーザー・シート管理REST API](https://docs.github.com/en/rest/copilot/copilot-user-management?apiVersion=2022-11-28)
- [Copilot利用状況メトリクスREST API](https://docs.github.com/en/rest/copilot/copilot-usage-metrics?apiVersion=2022-11-28)
- [請求利用状況REST API](https://docs.github.com/en/rest/billing/usage?apiVersion=2022-11-28)
- [GitHub Copilot AI usage UI](https://github.com/settings/copilot)

[English](README.md) · [GitHub App setup guide](docs/github-app-setup.md) · [日本語セットアップガイド](docs/github-app-setup.ja.md)
