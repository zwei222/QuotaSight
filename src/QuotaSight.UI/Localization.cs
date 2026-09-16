using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.UI;

// Explicit, strongly typed copy keeps localization trim/AOT safe and makes every UI string auditable.
public sealed class UiCopy
{
    private readonly UiLanguage language;
    public UiCopy(UiLanguage language) => this.language = language;
    public bool IsJapanese => language == UiLanguage.Japanese;
    public string Dashboard => IsJapanese ? "ダッシュボード" : "Dashboard";
    public string UsageOverview => IsJapanese ? "使用量" : "Usage";
    public string AddProvider => IsJapanese ? "追加" : "Add provider";
    public string ProviderPicker => IsJapanese ? "プロバイダーを選択" : "Choose a provider";
    public string Close => IsJapanese ? "閉じる" : "Close";
    public string History => IsJapanese ? "履歴" : "History";
    public string Settings => IsJapanese ? "設定" : "Settings";
    public string Refresh => IsJapanese ? "更新" : "Refresh";
    public string QuotaRefreshing => IsJapanese ? "利用枠データを更新しています…" : "Refreshing quota data…";
    public string RefreshError => IsJapanese ? "更新できません。最後に取得した状態を表示しています。" : "Unable to refresh. Showing the last known state.";
    public string Subtitle => IsJapanese ? "次回のリセットまでの利用状況を確認できます。" : "A calm view of what is left before your next reset.";
    public string EmptyTitle => IsJapanese ? "利用枠データがありません" : "No quota data yet";
    public string EmptyDescription => IsJapanese ? "プロバイダーを接続するか、手動の利用枠を追加してください。" : "Connect a provider or add a manual quota to get started.";
    public string LocalFirst => "LOCAL-FIRST";
    public string NoSecretsLeave => IsJapanese ? "秘密情報はこのアプリの外へ出ません" : "No secrets leave this app";
    public string DemoBanner => IsJapanese ? "デモモード · サンプル値のみ" : "DEMO MODE · Sample values only";
    public string ConnectManage => IsJapanese ? "プロバイダーを接続・管理" : "Connect or manage providers";
    public string ManualDescription => IsJapanese ? "ChatGPT Plus と Claude Pro の利用枠を手動で入力できます。公式の利用状況ページもブラウザーで開けます。" : "Manual form for ChatGPT Plus and Claude Pro; official usage links open in your browser.";
    public string Provider => IsJapanese ? "プロバイダー" : "Provider";
    public string AccountName => IsJapanese ? "アカウント表示名" : "Account display name";
    public string Window => IsJapanese ? "利用枠" : "Window";
    public string UsedPercent => IsJapanese ? "使用率（%）" : "Used %";
    public string ResetOptional => IsJapanese ? "リセット日時（任意）" : "Reset (optional)";
    public string OpenChatGpt => IsJapanese ? "ChatGPTの利用状況を開く" : "Open ChatGPT usage";
    public string OpenClaude => IsJapanese ? "Claudeの利用状況を開く" : "Open Claude usage";
    public string AddAccount => IsJapanese ? "アカウントを追加" : "Add account";
    public string OpenCodeDescription => IsJapanese ? "APIキーは安全な資格情報ストアを利用できる場合は保存し、利用できない場合はセッション中のみ保持します。キーは表示・記録しません。" : "API key is stored in the secure credential store when available; otherwise it is kept for this session only. It is never displayed or logged.";
    public string TestConnection => IsJapanese ? "接続をテスト" : "Test connection";
    public string CopilotDescription => IsJapanese ? "Copilotはghの状態確認またはデバイス認証を使用します。デバイス認証には実行時に設定したGitHub App Client IDが必要ですが、クライアントシークレットは不要です。ghの状態確認にはClient IDは不要です。組織の利用枠を取得できない場合は手動入力を使えます。" : "Copilot uses gh status or device flow. Device flow requires a runtime-configured GitHub App Client ID, with no client secret. gh status does not require a Client ID. Organization quota may be unavailable, so manual fallback is supported.";
    public string CopilotFlowWaiting => IsJapanese ? "認証処理中です。ブラウザーでコードを入力するまで待っています。" : "Authentication is in progress. Waiting for you to enter the code in your browser.";
    public string CodexBadge => IsJapanese ? "実験的" : "Experimental";
    public string CodexDescription => IsJapanese ? "OpenAI Codexの実験的な連携です。ChatGPT Plus/ProのCodex枠だけを取得します。Codex CLIは不要で、QuotaSightがブラウザーでデバイス認証を開始します。ブラウザーを開けない場合は、表示されたURLとコードで手動で続行できます。" : "Experimental OpenAI Codex integration for the ChatGPT Plus/Pro Codex allowance only. No Codex CLI installation is required; QuotaSight starts the device login in your browser. If the browser cannot open, continue manually with the displayed URL and code.";
    public string CodexExpires => IsJapanese ? "期限" : "Expires";
    public string CodexConnect => IsJapanese ? "ChatGPTに接続" : "Connect to ChatGPT";
    public string CodexConfirm => IsJapanese ? "認証状態を確認" : "Confirm connection";
    public string CodexLogout => IsJapanese ? "ログアウト" : "Log out";
    public string CodexOpenBrowser => IsJapanese ? "認証ページを開く" : "Open verification page";
    public string CodexCodeLabel => IsJapanese ? "認証コード" : "User code";
    public string CodexBrowserOpened => IsJapanese ? "既定のブラウザーで認証ページを開きました。" : "The verification page was opened in your default browser.";
    public string CodexBrowserFailed => IsJapanese ? "ブラウザーを開けませんでした。表示されたURLを手動で開き、コードを入力して続行してください。" : "We could not open your browser. Open the displayed URL manually, then enter the code to continue.";
    public string StartDevice => IsJapanese ? "デバイス認証を開始" : "Start device flow";
    public string CopyCode => IsJapanese ? "コードをコピー" : "Copy code";
    public string OpenVerification => IsJapanese ? "認証ページを開く" : "Open verification";
    public string Poll => IsJapanese ? "認証状態を確認" : "Poll";
    public string ProbeGh => IsJapanese ? "ghの状態を確認" : "Probe gh status";
    public string OpenOfficialUsage => IsJapanese ? "公式の利用状況を開く" : "Open official usage";
    public string HistoryDescription => IsJapanese ? "ローカルに保存した30日分の利用状況。エクスポートには安全な表示項目のみ含まれます。" : "30 days of local snapshots. Export contains UI-safe fields only.";
    public string ExportCsv => "Export CSV";
    public string ExportJson => "Export JSON";
    public string DeleteAll => IsJapanese ? "すべて削除" : "Delete all";
    public string UsageTrend => IsJapanese ? "使用状況の推移" : "Usage trend";
    public string HistoryPrevious => IsJapanese ? "前へ" : "Previous";
    public string HistoryNext => IsJapanese ? "次へ" : "Next";
    public string Delete => IsJapanese ? "削除" : "Delete";
    public string HistoryNotificationTitle => History;
    public string HistoryDeleteFailure => IsJapanese ? "この履歴を削除できませんでした。" : "Unable to delete this history entry.";
    public string HistoryDeleteAllFailure => IsJapanese ? "履歴を削除できませんでした。" : "Unable to delete history.";
    public string HistoryCorrupt => IsJapanese ? "履歴データが破損しています。読み込める項目を表示しています。" : "History data is damaged; showing available entries.";
    public string TrayUnavailableTitle => IsJapanese ? "トレイ" : "Tray unavailable";
    public string TrayUnavailable => IsJapanese ? "トレイを利用できません。アプリ内で操作してください。" : "Tray unavailable. Use the app window instead.";
    public string ProviderName(ProviderKind provider) => provider switch
    {
        ProviderKind.ChatGpt => "ChatGPT Codex",
        ProviderKind.OpenCode => "OpenCode Go",
        ProviderKind.Claude => "Claude",
        ProviderKind.Copilot => "GitHub Copilot",
        _ => provider.ToString()
    };
    private string RefreshProviderName(ProviderKind provider) => provider == ProviderKind.ChatGpt ? "Codex" : ProviderName(provider);
    public string RefreshMissingProviders(IEnumerable<ProviderKind> providers)
    {
        var names = providers.Distinct().Select(RefreshProviderName).ToList();
        var joined = IsJapanese ? string.Join("、", names) : names.Count switch { 0 => string.Empty, 1 => names[0], _ => string.Join(", ", names[..^1]) + " and " + names[^1] };
        return IsJapanese ? $"今回は{joined}のデータを取得できませんでした。接続状態を確認してください。" : $"{joined} data could not be retrieved this time. Check the connection.";
    }
    public string QuotaThresholdTitle(ProviderKind provider) => IsJapanese ? $"{provider switch { ProviderKind.ChatGpt => "ChatGPT", ProviderKind.OpenCode => "OpenCode Go", ProviderKind.Claude => "Claude", ProviderKind.Copilot => "GitHub Copilot", _ => provider.ToString() }}の利用枠のしきい値" : $"{provider} quota threshold";
    public string QuotaThresholdReason(decimal percent) => IsJapanese ? $"使用率 {percent:0.#}%" : $"{percent:0.#}% used";
    public string Appearance => IsJapanese ? "表示" : "Appearance";
    public string Theme => IsJapanese ? "テーマ" : "Theme";
    public string Language => IsJapanese ? "言語" : "Language";
    public string ReduceMotion => IsJapanese ? "動きを減らす" : "Reduce motion";
    public string HighContrast => IsJapanese ? "ハイコントラスト" : "High contrast";
    public string SupportStatus => IsJapanese ? "対応状況: 動きを減らす、ハイコントラスト" : "Support status: Reduce motion · High contrast";
    public string RefreshAlerts => IsJapanese ? "更新と通知" : "Refresh and alerts";
    public string RefreshInterval => IsJapanese ? "更新間隔（5〜15分）" : "Refresh interval (5–15 minutes)";
    public string EnableNotifications => IsJapanese ? "通知を有効にする" : "Enable notifications";
    public string ResidentMode => IsJapanese ? "常駐モードを有効にする" : "Enable resident mode";
    public string ResidentModeDescription => IsJapanese ? "対応環境ではアイコンをクリックして利用枠をすばやく確認できます。ウィンドウを閉じてもトレイに残ります。" : "On supported platforms, click the icon for a quick quota check; closing the window keeps QuotaSight in the tray.";
    public string OpenFullWindow => IsJapanese ? "正式なウィンドウを開く" : "Open full window";
    public string TrayUpdate => IsJapanese ? "更新" : "Refresh";
    public string TrayExit => IsJapanese ? "終了" : "Exit";
    public string CompactTitle => IsJapanese ? "利用枠の確認" : "Quota check";
    public string CompactSubtitle => IsJapanese ? "次のリセットまでの概要" : "A quick view before the next reset";
    public string CompactProviders => IsJapanese ? "プロバイダー" : "Quota providers";
    public string OverallThreshold => IsJapanese ? "通知する使用率（%）" : "Overall threshold (%)";
    public string ProviderOverrides => IsJapanese ? "プロバイダーごとにしきい値を設定できます。" : "Provider overrides can be configured per account.";
    public string Integrations => IsJapanese ? "連携" : "Integrations";
    public string GithubClientId => IsJapanese ? "GitHub App Client ID（秘密情報ではありません）" : "GitHub App Client ID (not a secret)";
    public string Autostart => IsJapanese ? "自動起動: 現在利用できません（プラットフォーム機能が未導入）" : "Autostart: Unsupported · platform backend not installed";
    public string AccountWatermark => IsJapanese ? "個人" : "Personal";
    public string UsedPercentWatermark => IsJapanese ? "0以上" : "0 or more";
    public string ResetAtWatermark => "2026-01-15 12:00";
    public string ProviderFieldName => IsJapanese ? "プロバイダー" : "Provider";
    public string AccountFieldName => IsJapanese ? "アカウント表示名" : "Account display name";
    public string WindowFieldName => IsJapanese ? "利用枠" : "Quota window";
    public string UsedPercentFieldName => IsJapanese ? "使用率（%）" : "Used percent";
    public string ResetAtFieldName => IsJapanese ? "リセット日時（任意）" : "Optional reset time";
    public string ThemeSystem => IsJapanese ? "システム" : "System";
    public string ThemeLight => IsJapanese ? "ライト" : "Light";
    public string ThemeDark => IsJapanese ? "ダーク" : "Dark";
    public string CredentialUnavailable(string availability) => IsJapanese ? $"OpenCode Goの資格情報ストアを利用できないため、セッション中のみ保持します。キーは表示・記録しません。" : $"OpenCode Go credential store: {availability}; session-only fallback; key is never displayed or logged.";
    public string CodexStatus(CodexAuthorizationState state, bool success, FetchStatus status = FetchStatus.Success) => IsJapanese ? status switch
    {
        FetchStatus.Unauthorized => "Codexの認証が拒否されました。",
        FetchStatus.Forbidden => "Codexの認証が許可されませんでした。",
        FetchStatus.RateLimited => "Codexの認証は一時的に制限されています。",
        FetchStatus.TransientFailure => "Codexの認証に一時的な問題が発生しました。",
        _ => (state, success) switch
        {
            (CodexAuthorizationState.Disconnected, _) => "Codexは未接続です。",
            (CodexAuthorizationState.AwaitingAuthorization, true) => "認証を開始しました。",
            (CodexAuthorizationState.Pending, _) => "認証を待っています。しばらくしてから再確認してください。",
            (CodexAuthorizationState.Connected, true) => "Codexに接続しました。",
            (CodexAuthorizationState.Error, _) => "Codexの認証に失敗しました。",
            _ => "Codexの状態を確認できません。"
        }
    } : string.Empty;
    public string OpenCodeStatus(ProviderConnectionResult result) => IsJapanese ? result.Success ? "OpenCode Goに接続しました。資格情報は安全に保存しました。" : result.Status switch
    {
        FetchStatus.Unauthorized => "OpenCode GoのAPIキーが拒否されました（401）。",
        FetchStatus.Forbidden => "OpenCode Goへのアクセスが拒否されました（403）。",
        FetchStatus.Unsupported => "OpenCode Goの利用状況エンドポイントが見つかりません（404）。",
        FetchStatus.RateLimited => "OpenCode Goで一時的な利用制限が発生しています（429）。",
        _ => "OpenCode Goからの応答を処理できませんでした。"
    } : result.Message;
    public string CopilotQuotaNotice => IsJapanese
        ? "認証に成功しても利用枠の取得には成功していません。Copilot Business個人の月次残量を返す公式APIは確認できないため、公式画面または手動入力を利用してください。"
        : "Authorization does not mean quota was retrieved. GitHub has no confirmed official API for an individual Copilot Business monthly remaining balance; use the official page or enter it manually.";
    public string CopilotStatus(CopilotUiState state) => IsJapanese ? state switch
    {
        CopilotUiState.Idle => "Copilotの認証はまだ開始されていません。",
        CopilotUiState.Started => "認証を開始しました。ブラウザーでコードを入力するまで待っています。入力後は自動で確認します。",
        CopilotUiState.Pending => "認証を待っています。ブラウザーでコードを入力してください。",
        CopilotUiState.Completed => "GitHubの認証が完了しました。",
        CopilotUiState.Denied => "GitHubの認証が拒否されました。",
        CopilotUiState.Expired => "GitHubのデバイス認証コードの期限が切れました。もう一度開始してください。",
        CopilotUiState.ConfigurationError => "GitHub App Client IDが設定されていません。設定を確認してください。",
        CopilotUiState.DeviceFlowDisabled => "GitHubのデバイス認証は無効になっています。別の認証方法または公式ページを利用してください。",
        CopilotUiState.Unsupported => "このCopilot連携は非対応です。公式ページまたは手動入力を利用してください。",
        CopilotUiState.Failed => "GitHubの認証を完了できませんでした。詳細を確認して再試行してください。",
        CopilotUiState.GhProbe => "Copilotのgh状態を確認しました。",
        _ => "Copilotの状態を確認できません。"
    } : state switch
    {
        CopilotUiState.Idle => "Copilot authentication has not started yet.",
        CopilotUiState.Started => "Device authentication started. Waiting for you to enter the code in your browser; checking continues automatically afterward.",
        CopilotUiState.Pending => "Waiting for GitHub authorization. Enter the code in your browser.",
        CopilotUiState.Completed => "GitHub authorization completed.",
        CopilotUiState.Denied => "GitHub authorization was denied.",
        CopilotUiState.Expired => "The GitHub device code expired. Start again to request a new code.",
        CopilotUiState.ConfigurationError => "GitHub App Client ID is not configured. Check Settings.",
        CopilotUiState.DeviceFlowDisabled => "GitHub device flow is disabled. Use another sign-in method or the official page.",
        CopilotUiState.Unsupported => "This Copilot integration is unsupported. Use the official page or manual entry.",
        CopilotUiState.Failed => "GitHub authorization could not be completed. Review the details and try again.",
        CopilotUiState.GhProbe => "Copilot gh status checked.",
        _ => "Copilot status unavailable."
    };
    public string SecureCredential => IsJapanese ? "資格情報ストア: 安全なストアを利用可能。キーは表示・記録しません。" : "Credential store: secure store available; key is never displayed or logged.";
    public string RefreshIncompleteTitle => IsJapanese ? "一部更新できません" : "Quota refresh incomplete";
    public string RefreshFailure(ProviderFailure failure, bool mixedResult)
    {
        var provider = failure.Provider switch
        {
            ProviderKind.ChatGpt => "Codex",
            ProviderKind.OpenCode => "OpenCode Go",
            ProviderKind.Claude => "Claude",
            ProviderKind.Copilot => "GitHub Copilot",
            _ => failure.Provider.ToString()
        };
        var detail = failure.Status switch
        {
            FetchStatus.Unauthorized => IsJapanese ? "再接続が必要です。手動入力または公式ページを利用してください。" : "Reconnect is required. Use manual input or the official page.",
            FetchStatus.Forbidden => IsJapanese ? "権限が不足しています。権限を確認してください。" : "Permission is insufficient. Check the account permissions.",
            FetchStatus.RateLimited => IsJapanese ? "一時的なレート制限です。" : "Temporary rate limit.",
            FetchStatus.TransientFailure => IsJapanese ? "一時的な取得失敗です。" : "Temporary fetch failure.",
            FetchStatus.Unsupported => IsJapanese ? "この連携は対応していません。手動入力または公式ページを利用してください。" : "This integration is unsupported. Use manual input or the official page.",
            _ => IsJapanese ? "取得できませんでした。" : "Could not fetch quota data."
        };
        if (failure.RetryAfter is { } retry)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(retry.TotalMinutes));
            detail += IsJapanese ? $" 約{minutes}分待ってから再試行してください。" : $" Try again in about {minutes}m.";
        }
        return $"{provider}: {detail}";
    }
    public string RefreshFailures(IReadOnlyList<ProviderFailure> failures, bool mixedResult) =>
        (mixedResult ? (IsJapanese ? "一部更新できません。" : "Some quotas could not be updated. ") : string.Empty) +
        string.Join(IsJapanese ? "\n" : "\n", failures.Select(failure => RefreshFailure(failure, mixedResult)));
}
