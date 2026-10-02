# QuotaSight コントリビューター向け指示

## プロダクト契約

QuotaSightは、AIの**サブスクリプション利用枠**に対する消費率と残量率を可視化します。API料金、トークン課金、従量課金APIの支出を表示するものではありません。推定値、遅延データ、手動入力、実験的エンドポイントを、権威あるリアルタイム利用量として表示してはいけません。

初期対応対象はWindows x64とLinux x64です。リリース成果物は自己完結型の.NET 10 Native AOT ZIPです。MSIX、deb、AppImageなどのパッケージ形式は、明示的に追加されるまで対象外です。

## リポジトリ構成

- `src/QuotaSight.Core`: 依存関係を持たない利用枠モデルと計算。
- `src/QuotaSight.Application`: ユースケースと外部サービスの契約。
- `src/QuotaSight.Infrastructure`: プロバイダーアダプター、永続化、スケジューリング、設定、OS資格情報サービス。
- `src/QuotaSight.UI`: Avaloniaアプリケーション、プレゼンテーションロジック、ローカライズ、トレイ動作、composition root。
- `tests/QuotaSight.Tests`: ドメイン、インフラストラクチャ、セキュリティ、workflowの契約テスト。
- `tests/QuotaSight.UI.Tests`: プレゼンテーションとAvalonia Headlessの動作テスト。
- `docs`: アーキテクチャとセキュリティの決定事項。

依存方向は `UI -> Application/Infrastructure`、`Infrastructure -> Application/Core`、`Application -> Core` を維持してください。CoreにはUI、ネットワーク、ファイルシステム、OS固有の依存関係を持ち込まないでください。

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

## 利用枠のセマンティクス

- rolling、daily、weekly、monthly、customの各独立windowをすべて保持してください。
- カードの代表値は、判明しているうち最も逼迫したwindowです。無関係なwindowを合算してはいけません。
- 100%を超える値を保持してください。視覚的なprogress indicatorだけをclampし、残量0%と超過量を表示します。
- プロバイダーのreset時刻は絶対時刻で保存し、ローカル時刻と相対時刻を併記してください。
- freshnessとsource confidenceを分離してください。更新失敗時は、最後に成功した値と元の観測時刻を保持し、stale状態を明示します。
- UIとexportでは `Official`、`Delayed`、`Manual`、`Experimental` を常に区別してください。
- 上限不明、無制限プラン、未認証、権限不足、非対応プラン、rate limit、一時的失敗は、それぞれ異なる状態です。

## プロバイダー境界

- ChatGPT Plus/ProのCodex枠は、QuotaSight独自のOpenAI Codex device OAuthと`wham/usage`を使用できます。ただし第三者アプリ向けの公開安定契約と公式Client ID流用許諾は未確認のため、常に`Experimental`として表示し、実サービスでのlive login未検証を明記してください。ChatGPT全体の利用量と混同せず、取得失敗時は手動入力と公式ChatGPTサブスクリプションページへfallbackします。Hermes/Codexの資格情報ファイルは読み取らず、client secretを持たず、単一アカウントとして扱います。
- Claude Proは、対応する公開契約が存在するまで、利用率/resetの手動入力と公式Usageページへのリンクを使用します。
- OpenAI Codexのaccess/refresh/id tokenはOS資格情報ストアだけへ永続化し、利用不能時はsession-memoryに限定します。refresh token rotationは排他し、logout時に専用資格情報を削除してください。`used_percent`、`reset_at`、`limit_window_seconds`だけを写像し、`used`/`limit`を推測せず、primary/secondary windowと100%超の値を保持してください。
- OpenCode Goは明示的に入力されたAPI keyとusage endpoint adapterを使用します。OpenCode内部の認証ファイルを読み取ってはいけません。
- GitHub Copilot Businessでは、安全な `gh` status probeまたはQuotaSight用GitHub AppのDevice Flowを使用できます。権限を持つ組織管理者はBilling Usage APIからユーザー別AI Credits使用量を取得できますが、認証成功は参照権限を保証しません。1席あたり月1,900 AI Creditsは請求主体単位の共有プールであり、個人残量として計算してはいけません。取得できない場合は手動入力へfallbackします。
- GitHub App Client IDは実行時設定です。App IDと混同せず、デスクトップアプリにclient secretやprivate keyを要求または埋め込んではいけません。
- その他のプランはcapability detectionに基づくbest effort対応です。上限を推測せず、非対応状態を表示してください。

## 秘密情報とプライバシー

- プロバイダーCLI内部の資格情報ファイルを読み取ったり解析したりしないでください。
- token、API key、cookie、password、生のprovider response、完全なCLI出力を、settings、history、log、crash report、export、test、UI textへ書き込まないでください。
- 秘密情報はWindows Credential ManagerまたはLinux Secret Serviceでのみ永続化します。secure storeが利用不能またはlockedなら、そのsession中だけメモリに保持します。平文ファイルへfallbackしてはいけません。
- subprocessの引数は `ProcessStartInfo.ArgumentList` で渡してください。shellを起動したり、ユーザー入力をcommand stringへ埋め込んだりしないでください。
- Linux Secret Serviceへ渡す値はcommand-line argumentではなくredirected standard inputを使用してください。
- exportに含めてよいのはprovider、accountの表示名、quota window、値、source、confidence、timestampsです。internal IDとsecretは除外してください。
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

UIコピーは簡潔で自然な表現にし、プロバイダー対応範囲やデータ鮮度を誇張してはいけません。

## 永続化

履歴はschema versionとevent IDを持つUTC日別JSONLで、30日間保持します。writerは1つに制限し、切り詰められた末尾行だけを回復対象とし、それ以前の破損行は報告してください。期限切れファイルと境界日の期限切れentryをpruneします。settingsとhistoryは実行ファイルの隣ではなく、OSのユーザー別データディレクトリに保存してください。

settingsにはtheme、language、refresh interval、notification preference、threshold、GitHub OAuth Client ID、GitHub Organization slugを保存できます。settings DTOにsecretを保持するfieldを含めてはいけません。

## Gitと配布

commit前にformat、関連test、Release build、security review、ホストOSで実行可能なNative AOT smoke testを必須とします。CIはfail-closedを維持してください。`continue-on-error`、`|| true`、fake test、warning suppression、環境固有のbypassを追加してはいけません。

リリース公開は手動で行い、新しいsemantic version tagを必須とします。既存のtagまたはreleaseを上書きしてはいけません。各RIDは対応するOS上でbuildし、publish済みbinaryを `--smoke-test` で実行してからarchiveしてください。
