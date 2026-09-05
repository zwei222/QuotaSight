# Security

## Threat model

保護対象は API key、OAuth/device-flow token、provider の認証済み CLI 状態、利用量の履歴です。脅威は、ローカルの別ユーザーや悪意あるプロセスによる読み取り、ログ・画面共有・export からの漏えい、provider の stale/誤った応答、通知の覗き見、ZIP への秘密混入です。QuotaSight はサーバーへ telemetry を送らず、取得不能を成功に見せません。OS が提供するユーザー境界を超える保護は保証しません。

## Credential lifecycle

credential は必要な時だけ secure store から取り出し、adapter のリクエストまたは認証処理の短いスコープで使います。Windows は Credential Manager、Linux は `secret-tool`/Secret Service を優先します。backend が利用不能な場合は session-only とし、平文ファイルや設定への fallback はしません。削除・logout は provider の credential と session cache を破棄します。credential value は history、export、通知、UI、ログへ渡しません。

## Exports and history

history は quota の表示値、source、timestamps、window、stale 状態だけを 30 日 JSONL に保存します。provider の生 response、header、cookie、token、内部 account identifier は保存しません。CSV/JSON export は内部 account identifier と秘密値を除外します。export 前に出力先と内容をユーザーが確認できるようにし、共有時は通常のファイル権限にも注意します。

## OAuth device flow

GitHub OAuth App Client ID は Settings の runtime 設定でのみ受け取り、コードや workflow に hardcode しません。client secret は要求しません。device code、user code、access token は画面・ログ・history に表示せず、認証完了後は secure store または session-only に置きます。device flow の承認 URL はユーザーが確認して開きます。認証できても quota endpoint が使えない場合は manual と表示します。

## CLI process safety

CLI adapter は固定した executable と引数を使用し、shell interpolation、`eval`、ユーザー入力を含む command string の組み立てを行いません。標準出力は許可した quota fields のみを構造化して parse し、stderr と exit code を安全に扱います。環境変数、process list、CLI の内部 credential cache の探索はしません。timeout、取消、最小権限、終了後の一時データ破棄を徹底します。

## Reporting guidance

不具合報告には OS、アプリ version、provider 名、source label、stale 状態、再現手順、終了コード、redacted な field 名だけを含めます。token、API key、device code、Authorization header、cookie、完全な provider response、個人アカウント識別子は送らないでください。ログを共有する前に秘密値を検索・削除し、必要なら最小の再現データへ置換します。
