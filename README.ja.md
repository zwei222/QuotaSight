# QuotaSight

[English](README.md) | [日本語](README.ja.md)

QuotaSightは、**観測されたAIのトークン使用量**を確認するWindows/Linux向けデスクトップアプリです。サブスクリプションの利用枠は取得せず、プロバイダーの請求APIも呼び出しません。API料金やトークン課金の支出を追跡するものでもありません。表示する値はすべて観測値です。利用を記録した端末のローカルCLIログを、その端末上の[TokScale](https://github.com/junhoyeo/tokscale)が集計し、運用者が管理する利用量APIへ送り、アプリが読み取ります。そのため各画面は、値の出所、対象期間と対象Collector、鮮度を必ず示します。

## 現在実装されている機能

- Avalonia製のダッシュボード、利用量ページ、設定画面。実行中の日本語・英語切り替えと、狭い画面幅への対応。
- ダッシュボード: 追加したプロバイダーの観測使用量カード。今日と直近7日間のトークン数を表示します。
- 利用量ページ: `Asia/Tokyo`区切りの直近30日間の日次トークン合計を、Collector別の行と鮮度の行付きで表示します。これとは別に、クライアント・プロバイダー・モデル別の利用内訳を表示します。
- 未受信、stale、遅延、サーバー未対応、読み取り失敗をそれぞれ別の固定状態として示します。更新に失敗しても前回の値を保持したままstaleとして表示し、ゼロには置き換えません。
- テーマ、更新間隔、常駐モード、利用量API接続（エンドポイントとAccess Client ID、Client Secret）の設定。
- 利用量APIの資格情報はWindows Credential ManagerとLinux Secret Service/`secret-tool`に保存し、安全なストアが使えない場合はセッション中だけ保持します。
- テレメトリーを送信しません。
- 任意の常駐モード。省スペースの観測使用量画面と、明示的な終了操作があります。

現在の画面には利用枠、リング、ゲージ、しきい値はありません。ローカルの利用枠履歴や書き出しもありません。アプリ自身が端末のログを走査することもなく、その端末の利用量は、同じ端末のCollectorが送信して初めて表示されます（[LinuxでのCollectorセットアップ](docs/collector-linux-setup.md)）。

## データの経路: 送信と読み取りは別

| 段階 | 動作する場所 | 行うこと | 行わないこと |
|---|---|---|---|
| TokScale（上流CLI） | 利用を記録した端末 | その端末のローカルセッションログを集計し、`tokscale graph`ドキュメントを作ります。 | 利用枠は扱わず、QuotaSightとも通信しません。 |
| QuotaSight Collector（Linux x64、任意） | 同じ端末 | 標準入力から`tokscale graph`ドキュメントを**1つ**読み、正規化してスナップショットを利用量APIへ**送信**（`PUT`）します。 | ログを自分で走査せず、systemdの資格情報ディレクトリ以外のファイルから資格情報を読みません。アプリの設定も読みません。 |
| 利用量API（運用者のデプロイ） | 運用者が構築するCloudflare Workers + D1 | Collectorごとにスナップショットを保存し、世代・revisionの単調性を検証し、日次合計と内訳を返します。 | アプリが起動するものではなく、プロンプト、応答本文、パス、アカウント名を保持しません。 |
| QuotaSightデスクトップアプリ | WindowsまたはLinux x64 | 設定した利用量APIのオリジンから、日次合計と内訳を**読み取り**（`GET`）ます。 | 利用量をどこかへ送信せず、TokScaleやローカルログを読みません。接続先は設定したAPIだけです。 |

送信は二重に任意です。Collectorを導入してスケジュールする必要があり、既定は`--preview`で、`--send`を明示したときだけ送ります。wire契約が運ぶのは日次の合計トークン数と、`--wire-schema 2`を選んだ場合だけ正規化済みのクライアント・プロバイダー・モデル識別子と5成分のトークン内訳です。詳しくは[セキュリティ](docs/security.md)と[デプロイ手順](docs/deployment.md)にあります。

## ダッシュボード: 観測使用量カード

- カードは、カタログ（TokScale 4.23.0に固定）から利用者が追加したプロバイダーにだけ表示します。内訳でまだ何も紐付いていないプロバイダーは、ゼロではなく**「まだ観測されていません」**と表示します。
- 複数のプロバイダーが同じ行を使った場合、その行はカンマ結合の1つの値として届き、名前に挙げられた**すべての**カードに加算されます。そのためカード間でトークンが重複し、該当カードには「他のプロバイダーと共有する分を含む」と表示します。セクションのバッジも同じ制限を示します（観測値・上限なし・推定なし・他端末分は含まない・カード間で共有分が重複）。
- **カードの合計を足し合わせてはいけません。** カードの数値は「そのプロバイダー経由として紐付いた使用量」であり、トークンの重複のない分割ではありません。
- トークンは数量であり、何かの上限に対する割合ではありません。カードに割合、リング、しきい値はありません。

## 利用量ページ: 日次合計と内訳

- **日次合計。** 今日を終点とする30日間を読み、期間、合計トークン数、Collectorと日付ごとの行、鮮度の行を表示します。鮮度は、その読み取りが対象としたCollectorのうち**最も古い**最終受信時刻です。合計は、最も長く報告していないCollectorと同じ古さにしかならないためです。12時間を超えて古いデータは、最新としてではなく遅延データとして示します。
- **内訳。** 同じ接続に対する2つ目の独立した読み取りで、クライアント・プロバイダー・モデル別に集計します。紐付いた合計、期間内の紐付け合計に対する構成比、日ごとの「その日の合計と紐付け分」、正確な未分類分（日合計 − 紐付け分）、「一部の日だけ内訳がある」状態、および補正せずにそのまま示す「内訳が日合計を超えている日」を表示します。カードには固定の注記を付けます。TokScaleがローカルログから集計した観測値であり、ログの削除（Claude Codeは既定30日）やAPI・キャッシュ型クライアントでは欠落や遅れが生じる、という内容です。
- 2つの読み取りは意図的に状態を分けています。日次合計が動いていても内訳だけが未対応・失敗であることはあり、片方の失敗が他方をstaleにすることはありません。集計単位の切り替えは再読み取りを発生させません。

## 利用量API接続、資格情報、失敗状態

設定画面が保存するのは、秘密ではないエンドポイント設定と、OS資格情報ストアだけに入るAccess Client ID／Client Secretです。エンドポイントは`https`のオリジンに限り、パス、クエリ、ユーザー情報、フラグメントを含められず、256文字以内です。保存後は資格情報を表示・コピーできず、保存済みかどうかだけを示します。資格情報ストアが利用不能またはロック中の場合は現在のセッション中だけ保持し、再起動後に再入力が必要です。接続を削除すると保存した2つの値を削除します。

読み取り失敗は分類され、固定文言で表示します。資格情報の拒否（401/403）、エンドポイントまたは応答の問題（404と契約違反）、追従しないリダイレクト、資格情報の欠落、一時的な失敗（timeout、接続失敗、429、5xx）です。内訳には、サーバーがまだ内訳を提供していない`not_found`と、collector名が正しくない`collector_not_found`が加わります。失敗の文言にステータスコード、URI、応答本文、識別子を含めることはありません。

## 通知と状態表示

現在のUIですべての通知は**アプリ内バナー**です。接続状態、読み取り失敗、stale、トレイ利用不可、常駐モードの保存失敗をここに表示します。しきい値通知はなく、画面に通知やしきい値の設定もありません。

デスクトップ通知のバックエンドはビルドに含まれています（Windows App SDK 2.7.0の`AppNotifications`、Linuxのsession D-Bus経由の`org.freedesktop.Notifications`）が、**現在のアプリ経路からは呼び出されません**。そのためこのビルドはOS通知を送信しません。実行できるのはWindowsの診断用`--notification-probe`だけで、これは登録ライフサイクルを確認し、成功時にexit code `0`を返します。登録が受け付けられたこととポップアップが表示されたことは別であり、実機Windowsでの表示は確認されていません。手順と記録のひな形は[Windows通知の受け入れ確認手順](docs/windows-notification-acceptance.ja.md)にあります。

## 必要条件

- ソースからビルドする場合のみ.NET SDK 10.0.x。リリースZIPは自己完結型です。
- Windows x64またはLinux x64。
- Linuxの画面表示にはGTKが必要です。トレイにはStatusNotifierホストが必要で、Secret Serviceは任意です（なければセッション限りの資格情報になります）。
- 設定した利用量APIのオリジンへのネットワーク接続。アプリが接続するホストはここだけです。
- 利用量を集める場合: Linux x86-64とsystemd 250以上、Node.js（TokScaleはnpmで配布）、書き込みスコープのCloudflare Accessサービストークン。[LinuxでのCollectorセットアップ](docs/collector-linux-setup.md)を参照してください。

## 配布

正式にサポートする配布物は、Windows x64またはLinux x64向けの自己完結型.NET 10 Native AOT ZIPです。MSIX、deb、AppImageは現在サポートしていません。Linux x64のリリースには`QuotaSight-Collector-<version>-linux-x64.zip`も添付します。これはローカルの`tokscale graph`ドキュメントを正規化して利用量APIへ送るCollectorで、ホスト側の設定（バイナリ、TokScale、systemdの資格情報、タイマー）は[LinuxでのCollectorセットアップ](docs/collector-linux-setup.md)にまとめています。

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

QuotaSightはローカル優先で、テレメトリーを送信しません。プロバイダーCLIの資格情報ファイルは読み取り・解析しません。アプリは利用量データをディスクに保持せず、書き込むのは設定だけです。設定に秘密は含みません。token、API key、cookie、password、生のプロバイダー応答、完全なCLI出力は、設定、ログ、クラッシュレポート、画面表示に到達しません。外部への接続先は、設定した利用量APIのオリジンだけであり、資格情報はWindows Credential ManagerまたはLinux Secret Serviceに保持します。ストアが利用不能またはロック中の場合は、現在のセッション中だけメモリに保持します。詳しくは[セキュリティの説明](docs/security.md)を参照してください。

## 利用枠表示があった版からの更新

設定ファイルは項目名ごとに読むため、以前の版が書いたファイルもそのまま読み込めます。現行版が使う設定はすべて保持され、利用枠時代の項目（しきい値、プロバイダー連携の値、参照クレジット）は次回保存時に落ちます。以前の版がOS資格情報ストアに保存したプロバイダー資格情報と、30日分の利用枠履歴ファイルは、現行版が**読み取らず**、更新時に**自動削除もしません**。使われなくなるだけです。

## Roadmap

- OS通知の表示経路を戻すか未使用の通知バックエンドを削除し、そのうえで実機Windowsで表示を検証します。
- TokScaleのリリースで紐付けIDが変わった場合は、プロバイダーカタログを意図的に更新します。
- 上流ツールが安定した契約を文書化した場合に限り、Collectorの対応範囲を広げます。

## 歴史的な文書

GitHub Appのガイドは、以前の設計の記録として残しています。Device Flowによる認証とCopilot Billing Usageの取得は、現行版では行いません。

- [QuotaSightのGitHub Appセットアップ](docs/github-app-setup.ja.md)（歴史的文書）
- [GitHub App setup for QuotaSight](docs/github-app-setup.md)（歴史的文書・英語）

## 公式リンク

- [TokScale（上流の利用量集計ツール）](https://github.com/junhoyeo/tokscale)
- [Cloudflare Accessのサービストークン](https://developers.cloudflare.com/cloudflare-one/access-controls/service-credentials/service-tokens/)
- [Cloudflare Workersの制限](https://developers.cloudflare.com/workers/platform/limits/)
- [.NET 10のダウンロード](https://dotnet.microsoft.com/download/dotnet/10.0)

[English](README.md) · [LinuxでのCollectorセットアップ](docs/collector-linux-setup.md) · [デプロイ手順](docs/deployment.md)
