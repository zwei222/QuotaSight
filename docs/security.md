# セキュリティ

## 脅威モデル

保護対象はAPI key、OAuth/device-flow token、プロバイダー認証済みCLIの状態、利用状況の履歴です。想定する脅威には、同じ端末上の別ユーザーや悪意あるプロセスによる読み取り、ログ・画面共有・書き出しを介した漏えい、プロバイダーからの古い／誤った応答、通知の覗き見、ZIPへの秘密情報の混入があります。QuotaSightはサーバーへテレメトリーを送信せず、データを取得できない状態を取得成功として見せません。OSが提供するユーザー境界を超える保護は保証しません。

## 資格情報のライフサイクル

資格情報は必要なときだけセキュアストアから取り出し、アダプターのリクエストまたは認証処理の短いスコープ内で使用します。WindowsではCredential Manager、Linuxでは`secret-tool`／Secret Serviceを優先します。バックエンドが利用できない場合は現在のセッション中だけ保持し、平文ファイルや設定への代替保存は行いません。削除またはログアウト時には、プロバイダーの資格情報とセッションキャッシュを破棄します。資格情報の値を履歴、エクスポート、通知、UI、ログへ渡しません。

## エクスポートと履歴

履歴には利用枠の表示値、取得元、時刻、期間、古いデータかどうかだけを、30日分のJSONLとして保存します。プロバイダーの生の応答、header、cookie、token、内部account identifierは保存しません。CSV／JSONエクスポートからも内部account identifierと秘密値を除外します。エクスポート前にユーザーが出力先と内容を確認できるようにします。共有する場合は、通常のファイルアクセス権限にも注意してください。

## OAuth device flow

GitHub App Client IDは設定画面の実行時設定からのみ受け取り、コードやworkflowにハードコードしません。client secretは要求しません。user codeは認証中に限り画面へ表示し、device code、device auth ID、access tokenは画面、ログ、履歴に出しません。GitHub Device Flowで期限付きuser tokenが利用可能な場合、新規ログイン時に返されるrefresh tokenをOS資格情報ストアに保存し、access tokenの期限前にrefresh token grantで更新します。refresh時にrotationされたtoken bundleも保存し直します。既存の旧形式access-token-only資格情報にはrefresh tokenがないため、期限切れ後は継続利用できません。利用を再開するには、ユーザーが明示的に再認証する必要があります。セキュアストアが利用できない場合、tokenは現在のセッション中だけ保持されるため、アプリの再起動後に再認証が必要です。device flowの承認URLは、ユーザーが確認してから開きます。GitHubの認証成功はBilling Usageを参照する権限を保証しません。参照に失敗した場合、前回値を最新として扱わず、古いデータであることを明示します。詳しくは[GitHub App user access tokenのrefresh](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens)を参照してください。

Windowsの通知バックエンドはWindows App SDK 2.5.1のApp Notifications APIを使用します。Windows x64向けportable Native AOT ZIPからOS通知の送信を試みますが、API呼び出しが成功してもポップアップが画面に表示されるとは限らず、Windows実機でのpixel-level表示は未検証です。アプリ内バナーはWindowsとLinuxの両方で代替手段として残ります。通常のアプリ終了経路では登録解除と`UnregisterAll`を呼び出しますが、終了処理が実行されない異常終了やcleanup自体の失敗時には、登録情報が残る可能性があります。配布artifactはWindows x64およびLinux x64向けのself-contained .NET 10 Native AOT ZIPのみです。MSIX、deb、AppImageは対象外です。

## OpenAI Codex device OAuth（非公開・実験的）

取得対象はChatGPT Plus/ProのChatGPT全体の利用量ではなく、単一アカウントのCodex枠だけです。QuotaSight自身がOpenAI Codex device OAuthを実行し、client secretは要求も保持もしません。また、Hermes/Codexの資格情報ファイルは読み取りません。OAuth public client ID、device endpoints、`wham/usage`は公式Codex実装で確認できますが、第三者アプリ向けの公開された安定契約とclient ID流用の許可は確認できていません。そのためOfficialとは表記しません。実サービスでのlive loginは未検証です。

Codexのaccess token、refresh token、id tokenはOSセキュアストアに保存し、セキュアストアが利用できない場合またはロック中の場合に限りsession-memoryに保持します。これらをsettings、history、export、log、生の応答、UIには出しません。user codeは認証中に限って画面に表示し、永続化しません。refresh rotationには排他制御を行い、ログアウト時には保存済み資格情報とセッションキャッシュを削除します。`used_percent`、`reset_at`、`limit_window_seconds`をprimary/secondaryの独立した期間へそのまま対応付け、used/limitは推測しません。100%を超える値も保持します。取得に失敗した場合は手動入力と公式ChatGPTサブスクリプションページへ切り替えます。

## CLIプロセスの安全性

CLIアダプターは実行ファイルと引数を固定し、shell interpolationや`eval`を使わず、ユーザー入力を含むコマンド文字列を組み立てません。標準出力は許可したquota fieldsだけを構造化して解析し、stderrと終了コードを安全に扱います。環境変数、process list、CLI内部の資格情報キャッシュを探索しません。timeout、キャンセル、最小権限を徹底し、終了後は一時データを破棄します。

## 外部送信（日次の利用量スナップショット）

利用量を共有する構成では、ローカルで動作する任意のCollectorから、利用者が設定した利用量APIへ日次のスナップショットを送信します。明示的に構成した場合だけ有効になる任意機能であり、構成しなければ何も送信しません。QuotaSightのlocal-firstの原則は変わらず、この送信先は利用者が指定した自分のAPIです。

- 送信する内容: バケット日付（`YYYY-MM-DD`、`Asia/Tokyo`区切り）と、その日の合計トークン数だけです。どちらも集計値であり、内容を含みません。受信側は受理した書き込みごとに受信時刻を記録し、読み取り時にCollectorごとの最終受信時刻を返します。
- 送信しない内容: プロンプト、応答本文、ファイルパス、プロジェクト名、モデル名、アカウント表示名、provider名、セッション識別子、料金、クライアント情報、Collectorのホスト名やユーザー名を含みません。Collectorは上流のCLI出力から許可した項目だけを写像し、許可外の項目は破棄します。
- 認証: CollectorはCloudflare Accessのサービストークンで接続し、Workers側はAccessが発行するJWTを検証します。検証に必要な設定が欠けている場合、APIは全リクエストを拒否します（fail-closed）。書き込み用と読み取り用はスコープで分離し、Collectorに束縛された資格情報はオーナー全体の読み取りを行えません。未認証、権限不足、対象の不在、一時的な失敗は区別して扱い、どれも「データなし」として表示しません。
- 資格情報の保管場所: クライアントIDとシークレットはOSの資格情報ストア（Windows Credential Manager、Linux Secret Service）にのみ保存し、平文ファイルや設定へは保存しません。Collectorをサービスとして動かす場合は、systemdの資格情報渡し（`LoadCredential`）を使い、設定ファイルやコマンドライン引数に秘密を含めません。アプリの設定に保存するのは利用量APIのエンドポイント（オリジンのみで秘密ではない）だけです。
- 有効期限とローテーション: サービストークンには有効期限を設定し、期限の1週間前に通知されるようにします。ローテーションではSecretだけを更新し、Client IDは変わらないため、サーバー側のprincipal定義は変更しません。猶予期間（1時間〜30日）の間に新旧のSecretを入れ替え、Collectorは`LoadCredential`のファイル差し替えと`systemctl restart`、デスクトップアプリは設定画面からOS資格情報ストアを更新します。手順と確認項目は[デプロイ手順](deployment.md)にあります。
- 運用コストの境界: 受信側はWorkers Free、D1 Free、Zero Trust Freeの範囲で動かす前提であり、有料プランへのアップグレードを前提にしません。そのため認証・検証・応答生成にかかるWorker側のCPU時間が10 msの無料上限に収まるかを、デプロイ直後に実機で判定します（1102の件数とCPU時間のP99）。この判定は未実施であり、無料枠で収まらないと判明した場合の設計変更案は[デプロイ手順](deployment.md)に「適用しない案」として記載し、それでも収まらなければデプロイを取りやめます。無料枠の上限に達した場合は課金ではなくクエリの失敗として現れ、その間は値を取得できなかったものとして扱います。
- Collectorのローカル状態: 送信済みrevisionや送信待ちの差分はCollector側の状態ファイルにのみ保存し、利用量APIへは送りません。状態ファイルに秘密は含めません。
- エンドポイント: 利用量APIのエンドポイントは秘密ではなく、設定画面で入力するオリジンです。パス、クエリ、ユーザー情報、フラグメントを含む値は拒否し、平文HTTPも拒否します。
- 遅延と減少の意味: 送信はリアルタイムではありません。Collectorは1〜6時間間隔で動作するため、表示される値は遅延データです。また、上流の再スキャンで同じ日の合計が小さくなることがありますが、受信側は受理済みの値を下げず、無視した件数を受領応答に記録します。したがって減少は「利用が減った」ことを意味せず、再スキャン範囲の違いを意味します。前回値と最終受信時刻を保持し、12時間を超えて古い場合は遅延データとして明示し、権威あるリアルタイム値としては表示しません。

## 問題報告時の注意

不具合報告に含めるのは、OS、アプリversion、プロバイダー名、source label、古いデータかどうか、再現手順、終了コード、redactedされたfield名だけにしてください。token、API key、device code、Authorization header、cookie、プロバイダーからの完全な応答、個人アカウントを識別する情報は送らないでください。ログを共有する前に秘密値を検索して削除し、必要に応じて最小限の再現データに置き換えてください。
