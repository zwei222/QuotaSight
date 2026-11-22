# QuotaSight コントリビューター向け指示

## プロダクト契約

QuotaSightの主目的は、AIサブスクリプションの実際の残り枠を円形インジケーターで確認できるようにすることです。取得元はTokScaleに統一します。`tokscale usage` が返す契約残量と、`tokscale graph` が集計する観測トークン数は、別のデータとして扱います。API料金、トークン課金、従量課金APIの支出は表示しません。

初期対応対象はWindows x64とLinux x64です。リリース成果物は自己完結型の.NET 10 Native AOT ZIPです。CollectorはLinux x64向けにのみ配布します。MSIX、deb、AppImageなどのパッケージ形式は、明示的に追加されるまで対象外です。

## リポジトリ構成

- `src/QuotaSight.Application`: 資格情報ストアと更新スケジューラーの契約、インメモリ実装。
- `src/QuotaSight.Infrastructure`: 設定ストア、OS資格情報ストア、スケジューリング、パス解決。
- `src/QuotaSight.UI`: Avaloniaアプリケーション、プレゼンテーションロジック、利用量API読み取りポート、ローカライズ、トレイ動作、composition root。
- `src/QuotaSight.Collector`: 標準入力の`tokscale graph`ドキュメントを正規化し、利用量APIへ送信する単体のNative AOT実行ファイル。
- `src/QuotaSight.Usage.Api`: Cloudflare Workers + D1のTypeScriptパッケージ。.NETソリューションの外にあり、UIとはHTTP契約だけで結びつきます。
- `tests/QuotaSight.Tests`: インフラストラクチャ、セキュリティ、workflowの契約テスト。
- `tests/QuotaSight.Collector.Tests`: Collectorの正規化、wire契約、送信フロー、systemd連携のテスト。
- `tests/QuotaSight.UI.Tests`: プレゼンテーションとAvalonia Headlessの動作テスト。
- `docs`: アーキテクチャ、セキュリティ、デプロイ、Collector、通知受け入れの決定事項。

依存方向は `UI -> Application`、`UI -> Infrastructure`、`Infrastructure -> Application` です。Applicationは他プロジェクトを参照しません。Collectorはどのプロジェクトも参照せず、UIからも参照されません。`QuotaSight.Core`は利用枠モデルとともに削除済みで、ドメイン層を新設する場合もUI、ネットワーク、ファイルシステム、OS固有の依存関係を持ち込まないでください。

## 必須ワークフロー

動作変更には厳格なテスト駆動開発を使用します。

1. まず焦点を絞ったテストを1件追加する。
2. 実行し、目的の未実装動作が原因で失敗することを確認する。
3. 最小限の実装を追加する。
4. 対象テストを実行し、その後に関連テスト群を実行する。
5. GREENの間だけリファクタリングする。

標準検証コマンド:

```bash
dotnet restore QuotaSight.slnx
dotnet format QuotaSight.slnx --verify-no-changes --no-restore
dotnet build QuotaSight.slnx -c Release --no-restore
dotnet test QuotaSight.slnx -c Release --no-build --no-restore
```

Native AOT検証では、RIDを指定したrestoreが必要です。

```bash
dotnet restore QuotaSight.slnx --runtime linux-x64
dotnet publish src/QuotaSight.UI/QuotaSight.UI.csproj -c Release -r linux-x64 --self-contained true -p:PublishAot=true --no-restore
src/QuotaSight.UI/bin/Release/net10.0/linux-x64/publish/QuotaSight.UI --smoke-test
```

Windows Native AOTはWindows runner、Linux Native AOTはLinux runnerでpublishしてください。リリースレーンを通すためにAOTを無効化してはいけません。

## Native AOTの制約

- Avaloniaのcompiled bindingと `x:DataType` を使用し、reflection bindingを追加しないでください。
- `System.Text.Json` のsource-generated contextと、明示的に生成した型メタデータを使用してください。
- ユーザーコンテンツの実行時XAML読み込み、`Reflection.Emit`、実行時コード生成、動的assembly/plugin読み込み、assembly scan型DI、未検証のreflection serializerを使用しないでください。
- trimming、AOT、analyzer、脆弱性の警告を、根本原因の修正を文書化せずに抑制しないでください。
- `InvariantGlobalization` を有効化しないでください。日本語UIと英語UIが必要です。
- reflection主体のDIコンテナーより、明示的なconstructor compositionを優先してください。

## 使用量のセマンティクス

- 円形インジケーターは、`tokscale usage` が返した契約枠の残量％を、対応する契約期間ごとに表示します。取得できない場合は不明とし、観測トークン数、推定値、任意の上限で代用しないでください。古い値を現在の残量として扱わないでください。
- 日次合計はCollectorと日付の組で保持してください。同じ日の複数Collectorの行を統合して一方だけを残してはいけません。
- ダッシュボードのカードはプロバイダー別の観測値です。複数プロバイダーが関与した行はカンマ結合の1キーとして届き、関与するすべてのカードに加算されます。カード間で共有分が重複するため、カードの合計を足し合わせて全体として提示してはいけません。
- 利用内訳は日次合計とは独立した読み取りです。片方の失敗が他方をstaleにしない状態機械を維持してください。
- 内訳は `attributed ≤ total` を検証し、未分類分は正確に合計してください。過剰紐付けの日は補正せず、生の数値で示してください。
- 空のキーは「不明」というデータであり、統合も破棄もしないでください。未知のIDは名称を発明せず、そのまま表示してください。
- 鮮度はCollectorの最終受信時刻のうち最も古いものです。12時間を超えたら遅延として示してください。更新失敗時は、最後に成功した値と元の観測時刻を保持し、stale状態を明示してください。
- 値がない場合は不明として扱い、ゼロに置き換えないでください。上限不明、未認証、権限不足、一時的失敗は、それぞれ異なる状態です。

## 送信と読み取りの境界

- 送信するのはCollectorだけです。`tokscale graph`1ドキュメントを標準入力から読み、日次合計（既定のwire v1）と、`--wire-schema 2`を選んだ場合だけ正規化済み識別子と5成分の内訳を利用量APIへ`PUT`します。
- デスクトップアプリは利用量APIの読み取り専用とし、TokScaleやローカルログを直接読みません。契約残量を連携する場合は観測トークン数とは別の契約として扱い、既存の認証・送信先の境界を維持してください。TokScale以外の独自プロバイダー照会は追加しないでください。
- 送信内容に、プロンプト、応答本文、ファイルパス、プロジェクト名、セッション識別子、アカウント表示名、ホスト名、コスト・料金、メッセージ数を含めないでください。パスらしい値はモデル識別子として送らず、空文字に置き換えてください。
- 利用量APIのエンドポイントはhttpsのオリジンのみを受け付けます。パス、クエリ、ユーザー情報、フラグメント、平文HTTPを許可しないでください。
- 受信側の世代・revision・日次合計の単調性、active generationだけを読む規則、受理済みの値を下げない規則を変更しないでください。

## 秘密情報とプライバシー

- プロバイダーCLI内部の資格情報ファイルを読み取ったり解析したりしないでください。
- token、API key、cookie、password、生のprovider response、完全なCLI出力を、settings、log、crash report、UI textへ書き込まないでください。
- 秘密情報はWindows Credential ManagerまたはLinux Secret Serviceでのみ永続化します。secure storeが利用不能またはlockedなら、そのsession中だけメモリに保持します。平文ファイルへfallbackしてはいけません。
- 利用量APIのエンドポイントだけが秘密ではない設定値です。client idとclient secretをsettings DTOへ追加しないでください。
- Collectorは資格情報を`CREDENTIALS_DIRECTORY`の2ファイルからのみ読み、コマンドライン引数や環境変数では受け取りません。systemdの`LoadCredential`／`LoadCredentialEncrypted`を使う前提を崩さないでください。
- subprocessを起動する場合、引数は `ProcessStartInfo.ArgumentList` で渡し、shellを起動したり、ユーザー入力をcommand stringへ埋め込んだりしないでください。Linux Secret Serviceへ渡す値はcommand-line argumentではなくredirected standard inputを使用してください。
- ログ、stderr、UIに出すのは固定の理由クラスと表示名だけにしてください。内部IDとsecretを含めないでください。
- QuotaSightはlocal-firstであり、telemetryを送信しません。

## UIとアクセシビリティ

既存のlayout、spacing、hierarchy、semantic color、responsive behavior、interaction affordanceは、意図的なデザインとして扱ってください。UI/UXの変更にはデザインレビューが必要です。機械的な後続変更では、その意図を維持してください。

以下を維持します。

- OS theme追従、およびSystem/Light/Darkの明示的選択
- 日本語と英語の実行時切り替え
- keyboard navigationと見えるfocus
- 意味のある `AutomationProperties.Name`
- high contrastでも読み取れる状態表示
- animationを必須としないreduced-motion対応
- 色だけに依存しないtextまたはiconのstatus label
- close-to-tray動作と明示的なExit
- native trayまたはnotificationが使えない場合の、正直なin-app fallback

UIコピーは簡潔で自然な表現にし、観測範囲やデータ鮮度を誇張してはいけません。観測使用量を上限に対する割合として示唆する表現を追加しないでください。

## 永続化

アプリがディスクへ書くのは`settings.json`だけです。利用量の履歴、内訳、受信時刻を保存しないでください。旧版が書いた日別JSONL履歴は読み取りも削除もせず、そのまま残します。設定とCollectorの状態は実行ファイルの隣ではなく、OSのユーザー別データ領域／状態ディレクトリに保存してください。

settingsにはtheme、language、refresh interval、通知の有効・無効（ファイル互換のため保持、現行UIに切り替えなし）、常駐モード、利用量APIのエンドポイント（オリジンのみで秘密ではない）、追加済みプロバイダー、旧クライアント一覧（保持のみ、カードの情報源にしない）を保存できます。settings DTOにsecretを保持するfieldを含めてはいけません。既存の設定項目名と型を変更しないでください。名前が変わると、既存ファイルの値が読み込み時に無言で既定値へ戻ります。

## Gitと配布

commit前にformat、関連test、Release build、security review、ホストOSで実行可能なNative AOT smoke testを必須とします。CIはfail-closedを維持してください。`continue-on-error`、`|| true`、fake test、warning suppression、環境固有のbypassを追加してはいけません。

リリース公開は手動で行い、新しいsemantic version tagを必須とします。既存のtagまたはreleaseを上書きしてはいけません。各RIDは対応するOS上でbuildし、publish済みbinaryを `--smoke-test` で実行してからarchiveしてください。
