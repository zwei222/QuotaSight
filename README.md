# QuotaSight

QuotaSight は API 請求額ではなく、subscription quota（契約プランの利用枠）を一つの dashboard で確認する Windows/Linux デスクトップアプリです。取得できない値は推測せず、取得方式と確度を表示します。

## 実装済み

- Demo mode による安全なサンプル dashboard。
- dashboard、history、settings のAvalonia UI。日本語/英語の実行時切替と狭幅レイアウトに対応。
- tray常駐と、native通知が利用できない場合のin-app通知fallback。trayが利用できない環境では通常のウィンドウ終了を維持。
- 30 日分の quota snapshot を JSONL で保持。
- Windows Credential Manager、Linux `secret-tool`/Secret Service、利用不能時の session-only credential storage。
- telemetry なし。token、平文 credential、生 response は保存・表示しません。

## Provider の取得方式と限界

| Provider | 方式 | 表示上の限界 |
|---|---|---|
| ChatGPT Codex | OpenAI device OAuth + Undocumented/Experimental `wham/usage` | ChatGPT Plus/Pro全体ではなくCodex枠のみ。public client IDとdevice endpointは公式Codex実装で確認できるが、第三者アプリ向け公開安定契約・client ID流用許諾は未確認。失敗時はmanual + 公式ChatGPTサブスクリプションページ。live loginは未検証。 |
| Claude | manual + official URL | quota は公式 URL を開いて手動入力。請求額 API ではない。 |
| OpenCode Go | API key + usage endpoint | usage endpoint の返す subscription quota の範囲のみ。応答が遅延・未提供なら stale/unknown。 |
| Copilot | `gh auth`/device flow | GitHub CLI 認証は使えるが、quota endpoint が利用できない場合は manual。請求額 API ではない。 |

CodexのOAuth public client ID、device endpoints、`wham/usage`は公式Codex実装で確認できるものの、第三者アプリ向けの公開安定契約やclient ID流用許諾は確認できないため、Officialとは扱いません。QuotaSight自身がdevice OAuthを実行し、client secretは要求・保持せず、Hermes/Codexのcredentialファイルも読みません。Codexは単一アカウントのCodex枠だけを対象にし、失敗時はmanual + 公式ChatGPTサブスクリプションページへfallbackします。未検証の実サービス応答およびlive loginを、実装済み・保証済みとは扱いません。provider の状態は manual / official / delayed / experimental のtruthfulnessを維持します。

## 必要条件

- .NET SDK 10.0.x
- Windows x64 または Linux x64
- Linux GUI/tray/notification: 実行環境に応じた GTK、D-Bus、notification daemon、Secret Service（任意。無い場合は fallback）。
- Copilot の既存 CLI 認証を確認する場合は `gh`。device flow を使う場合はブラウザ。

GitHub OAuth App の Client ID は現時点で提供されていないため hardcode していません。利用時に Settings へ runtime 設定します。client secret は不要です。

## 開発・実行

```bash
dotnet restore QuotaSight.slnx
dotnet build QuotaSight.slnx -c Release --no-restore
dotnet test QuotaSight.slnx -c Release --no-build --no-restore
dotnet format QuotaSight.slnx --verify-no-changes --no-restore
dotnet run --project src/QuotaSight.UI/QuotaSight.UI.csproj
# headless 起動契約の確認
dotnet run --project src/QuotaSight.UI/QuotaSight.UI.csproj -- --smoke-test
```

`--smoke-test` は UI を表示せず、配布 binary の起動・基本依存を確認して終了します。

## 配布 ZIP の実行

Release workflow が OS 上で NativeAOT self-contained binary を作り、`QuotaSight-<version>-<rid>.zip`（`linux-x64` または `win-x64`）として公開します。ZIP を展開し、Windows は `QuotaSight.UI.exe`、Linux は `QuotaSight.UI` を実行してください。Linux では必要に応じて実行権限を付与し、GUI/tray/notification 依存をインストールします。

## プライバシー

外部 telemetry はありません。history は既定で 30 日の JSONL です。credential の取得・削除・export の境界は `docs/security.md` を参照してください。CLI 内部 credential の読取、平文 fallback、token の log/UI/history 出力、生 response の保存は行いません。

## Roadmap

- provider packages の追加（実サービス仕様と公開 quota endpoint が確認できたものから）。
- より多くの OS 通知バックエンド。

## English short section

QuotaSight tracks subscription quotas, not API billing. For ChatGPT Plus/Pro it targets the Codex quota only, not all ChatGPT usage. QuotaSight runs its own OpenAI Codex device OAuth for one account and does not read Hermes/Codex credential files. The public client ID, device endpoints, and `wham/usage` are visible in the official Codex implementation, but no public stable third-party contract or permission to reuse the client ID has been confirmed; this integration is therefore Undocumented/Experimental, never Official. Tokens stay in the OS secure store or session memory only; failures fall back to manual entry and the official ChatGPT subscription page. Live login is unverified.
