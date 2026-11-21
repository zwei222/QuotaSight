# アーキテクチャ

## レイヤー

- Core: 利用枠のスナップショット、利用枠期間、超過量、データの鮮度、取得元の信頼度、取得状態に関する副作用のない契約。
- Application: プロバイダーの統合、ダッシュボードの更新、30日分の履歴、設定、資格情報のライフサイクル、書き出しに関するユースケース。
- Infrastructure: ChatGPT Codexのdevice OAuthと非公開・実験的な`wham/usage`アダプター、Claudeの手動入力用URL、OpenCode Goの利用状況エンドポイント、Copilotの`gh`/device flow認証とBilling Usage API／手動入力への切り替え、OS資格情報バックエンド、JSONLリポジトリー。
- UI: Avaloniaによるダッシュボード、履歴、設定、トレイ、通知、および代替表示。ViewModelはApplicationの契約を呼び出します。

## データの流れ

1. UIは更新またはスケジューラーのtickをApplicationに依頼します。
2. アダプターはプロバイダーごとの方式で利用枠の値を取得し、取得元（`Manual`/`Official`/`Delayed`/`Experimental`）、取得時刻、リセット期間、`fresh-until`を付与します。Codexでは`used_percent`、`reset_at`、`limit_window_seconds`をprimary/secondaryの各期間に対応付け、used/limitは推測しません。2つの期間は独立して保持し、100%を超える値もそのまま保持します。
3. ApplicationはCoreの契約に基づいて超過量と古いデータの状態を計算し、UIモデルに反映します。
4. スナップショットにはtokenや生の応答を含めず、JSONL履歴に追記します。ダッシュボードは現在のスナップショットを、履歴画面は日付範囲内のデータを読み込みます。
5. トレイと通知は同じ要約を使用します。OSバックエンドが利用できない場合は、UI内の代替表示を使用します。

## データの意味

- 取得元の信頼度は、実際の取得方式を表します。`Manual`はユーザー入力、`Official`はプロバイダー公式接続先、`Delayed`はプロバイダーが遅延を示した値、`Experimental`は保証のないアダプターを意味します。
- `FreshUntil`を過ぎた値は古いデータとして表示します。値がない場合は不明として扱い、ゼロに置き換えません。
- 利用量が上限を超えた場合も実際の値を保持し、視覚的な進捗表示だけを上限に合わせて、超過していることを明示します。
- 利用期間は開始時刻を含み、終了時刻を含まない半開区間です。`reset-at`にはプロバイダーから返された時刻を保持します。
- プロバイダーによって期間、上限、使用量の一部が取得できない場合は、取得できた情報だけを表示し、欠けている値を推測しません。

## 保存先と設定

ApplicationのデータルートはOSのユーザー別アプリケーションデータ領域から決定し、マシン固有の絶対パスを設定に埋め込みません。設定にはテーマ、表示言語（ja/en）、更新間隔、通知の有効・無効、通知しきい値、常駐モード、GitHub App Client ID、GitHub Organization slugを保持します。プロバイダーURLは設定項目ではありません。Client IDは提供されていないため、コードに既定値をハードコードしません。履歴はデータルート配下の日付別JSONLファイルに保存し、30日分だけ保持します。

## セキュリティ境界

セキュアストアおよび外部プロセス／APIとの境界にアクセスできるのは、Infrastructureの資格情報アダプターだけです。ApplicationとUIが扱うのは不透明な資格情報参照と状態に限り、秘密値をログ、UI、履歴、エクスポートへ渡しません。CLI内部の資格情報を探索してはならず、平文への代替保存も禁止です。エクスポートには利用枠スナップショットの公開可能な値だけを出力します。

## AOTの制約

UIのcsprojを対象OS上でNativeAOT publishします。Avaloniaのcompiled bindingsを既定とし、JSONのポリモーフィック／リフレクションによるシリアル化にはsource-generated contextを使用します。動的なアセンブリ読み込み、実行時の型探索、トリミングに弱いリフレクション依存を新たに追加しません。InvariantGlobalizationや警告抑制によってAOTの問題を隠しません。

## プロバイダーアダプター

- CodexAdapterとCodexSessionManagerは、QuotaSight自身でOpenAI Codex device OAuthを実行し、OAuth public client ID、device endpoints、`wham/usage`を使用します。これらは公式Codex実装で確認できますが、第三者アプリ向けの公開された安定契約およびclient IDの流用許可は確認できていません。そのため非公開・実験的な方式であり、Officialではありません。client secretは要求も保持もしません。対象はChatGPT Plus/Pro全体ではなくCodex枠のみで、単一アカウントです。取得に失敗した場合は手動入力と公式ChatGPTサブスクリプションページへ切り替えます。実サービスでのlive loginは未検証です。
- ClaudeAdapterは公式URLを開く手動入力用アダプターです。請求APIや内部APIを模倣しません。
- OpenCodeGoAdapterは、セキュアストアから明示的に登録されたAPI keyを読み込み、利用状況エンドポイントを呼び出します。取得元は`Official`（旧形式の応答では`Experimental`）です。取得に失敗した場合や利用枠が返らない場合は、取得失敗または不明として扱い、`Delayed`とはしません。同じアカウントの前回値が残っている場合は古い値（`stale`）であり、現在の個人利用枠を示すものではありません。
- CopilotAdapterは`gh auth`またはdevice flowによる認証状態を使用します。Billing Usage APIで取得する利用量は、報告の遅れがあるため取得元を`Delayed`として示します。利用枠エンドポイントが存在しない場合や権限が不足している場合、認証成功を利用枠取得成功と誤表示せず、手動入力に切り替えます。
