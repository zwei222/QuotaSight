using System.Globalization;
using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.UI;

// Explicit, strongly typed copy keeps localization trim/AOT safe and makes every UI string auditable.
public sealed class UiCopy
{
    private readonly UiLanguage language;
    public UiCopy(UiLanguage language) => this.language = language;
    public bool IsJapanese => language == UiLanguage.Japanese;
    public string UsageState(string state) => (state, IsJapanese) switch
    {
        ("Loading", true) => "利用量を読み込み中…",
        ("Loading", false) => "Loading usage…",
        ("Empty", true) => "指定期間の利用データはありません。",
        ("Empty", false) => "No usage data for this period.",
        ("Ready", true) => "利用データを表示しています。",
        ("Ready", false) => "Usage data loaded.",
        // Error is a neutral state sentence: the fix ("retry", "review the credential")
        // belongs to the failure banner, so the two never contradict each other.
        ("Error", true) => "利用量を読み込めませんでした。",
        ("Error", false) => "Could not load usage.",
        ("Stale", true) => "前回の利用データを表示しています（更新に失敗しました）。",
        ("Stale", false) => "Showing the last loaded usage data; the refresh failed.",
        ("Unconfigured", true) => "利用量 API は未設定です。",
        ("Unconfigured", false) => "Usage API is not configured.",
        _ => state
    };

    // Fixed failure sentences. Each one names a class of problem and an action, never a
    // status code, URL, host, header or identifier, so it is safe on screen. The kind is
    // decided by the read port from the status alone and never from a response body.
    public string UsageFailure(UsageFailureKind kind) => (kind, IsJapanese) switch
    {
        (UsageFailureKind.None, _) => string.Empty,
        (UsageFailureKind.Unauthorized, true) => "資格情報を確認してください。",
        (UsageFailureKind.Unauthorized, false) => "Check the credential.",
        (UsageFailureKind.Forbidden, true) => "資格情報を確認してください。",
        (UsageFailureKind.Forbidden, false) => "Check the credential.",
        (UsageFailureKind.NotFound, true) => "サーバー応答を確認してください。",
        (UsageFailureKind.NotFound, false) => "Check the server response.",
        (UsageFailureKind.InvalidResponse, true) => "サーバー応答を確認してください。",
        (UsageFailureKind.InvalidResponse, false) => "Check the server response.",
        (UsageFailureKind.Transient, true) => "一時的な失敗です。再試行できます。",
        (UsageFailureKind.Transient, false) => "Temporary failure. You can retry.",
        (UsageFailureKind.Redirected, true) => "利用量 API が別の場所へ転送しました。エンドポイントを確認してください。",
        (UsageFailureKind.Redirected, false) => "The usage API redirected the request. Check the endpoint.",
        (UsageFailureKind.CredentialMissing, true) => "利用量 API の資格情報がありません。設定画面で接続してください。",
        (UsageFailureKind.CredentialMissing, false) => "No usage API credential is stored. Connect the API in settings.",
        _ => string.Empty
    };

    // Freshness copy. The time is always formatted by the caller in the local time zone,
    // so this text never claims a zone or a relative age it cannot verify.
    public string UsageFreshnessLastReceived(DateTimeOffset local) => IsJapanese
        ? $"最終受信: {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}"
        : $"Last received: {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
    public string UsageFreshnessMissing => IsJapanese ? "未受信の collector あり" : "A collector has not reported yet";
    public string UsageFreshnessLastReceivedWithMissing(DateTimeOffset local) => IsJapanese
        ? $"{UsageFreshnessLastReceived(local)}（{UsageFreshnessMissing}）"
        : $"{UsageFreshnessLastReceived(local)} ({UsageFreshnessMissing})";
    public string UsageFreshnessNeverReceived => UsageFreshnessMissing;

    // Actions of the usage page's status banners.
    public string UsageRetry => IsJapanese ? "再試行" : "Retry";
    public string UsageOpenSettings => IsJapanese ? "設定画面で見直す" : "Review it in settings";

    // Delayed data is still shown, but never as if it were current.
    public string UsageDelayed => IsJapanese
        ? "遅延データ: 最終受信から12時間以上経過しています。"
        : "Delayed data: last received more than 12 hours ago.";

    // Fixed state sentences for the usage API connection. They deliberately name no endpoint,
    // no host and no identifier, so they are safe on screen and in a screenshot.
    public string UsageConnectionStatus(UsageConnectionState state) => (state, IsJapanese) switch
    {
        (UsageConnectionState.Unconfigured, true) => "利用量 API は未設定です。",
        (UsageConnectionState.Unconfigured, false) => "Usage API is not configured.",
        (UsageConnectionState.Configured, true) => "利用量 API に接続する設定が保存されています。",
        (UsageConnectionState.Configured, false) => "The usage API connection is saved.",
        (UsageConnectionState.SessionOnly, true) => "認証情報はこのセッション中のみ保持されます。再起動後は設定し直してください。",
        (UsageConnectionState.SessionOnly, false) => "The credential is kept for this session only; configure it again after restarting.",
        (UsageConnectionState.StoreUnavailable, true) => "資格情報ストアを利用できないため、接続設定を保存できません。",
        (UsageConnectionState.StoreUnavailable, false) => "The credential store is unavailable, so this connection cannot be saved.",
        (UsageConnectionState.Invalid, true) => "保存された利用量 API の設定が正しくありません。設定し直してください。",
        (UsageConnectionState.Invalid, false) => "The saved usage API setting is not valid. Configure it again.",
        _ => state.ToString()
    };
    // Validation copy is fixed and never echoes the entered text.
    public string UsageConnectionErrorText(UsageConnectionError error) => (error, IsJapanese) switch
    {
        (UsageConnectionError.None, _) => string.Empty,
        (UsageConnectionError.Missing, true) => "利用量 API のエンドポイントを入力してください。",
        (UsageConnectionError.Missing, false) => "Enter the usage API endpoint.",
        (UsageConnectionError.Malformed, true) => "エンドポイントの形式が正しくありません。",
        (UsageConnectionError.Malformed, false) => "The endpoint is not a valid address.",
        (UsageConnectionError.NotHttps, true) => "エンドポイントは https で指定してください。",
        (UsageConnectionError.NotHttps, false) => "The endpoint must use https.",
        (UsageConnectionError.UserInformation, true) => "エンドポイントにユーザー情報を含めないでください。",
        (UsageConnectionError.UserInformation, false) => "The endpoint must not contain user information.",
        (UsageConnectionError.Query, true) => "エンドポイントにクエリを含めないでください。",
        (UsageConnectionError.Query, false) => "The endpoint must not contain a query.",
        (UsageConnectionError.Fragment, true) => "エンドポイントにフラグメントを含めないでください。",
        (UsageConnectionError.Fragment, false) => "The endpoint must not contain a fragment.",
        (UsageConnectionError.Path, true) => "エンドポイントにパスを含めないでください。",
        (UsageConnectionError.Path, false) => "The endpoint must not contain a path.",
        (UsageConnectionError.HostMissing, true) => "エンドポイントのホスト名を指定してください。",
        (UsageConnectionError.HostMissing, false) => "The endpoint must include a host name.",
        (UsageConnectionError.TooLong, true) => "エンドポイントが長すぎます（256文字以内）。",
        (UsageConnectionError.TooLong, false) => "The endpoint is too long (256 characters at most).",
        (UsageConnectionError.InvalidCharacters, true) => "エンドポイントに空白や制御文字は使えません。",
        (UsageConnectionError.InvalidCharacters, false) => "The endpoint must not contain spaces or control characters.",
        (UsageConnectionError.InvalidCredential, true) => "クライアント ID とクライアントシークレットは空白を含まない半角英数字で入力してください。",
        (UsageConnectionError.InvalidCredential, false) => "Enter a client id and client secret with no spaces.",
        (UsageConnectionError.CredentialStoreUnavailable, true) => "資格情報ストアを利用できないため、保存していません。",
        (UsageConnectionError.CredentialStoreUnavailable, false) => "The credential store is unavailable, so nothing was saved.",
        _ => string.Empty
    };
    public string Dashboard => IsJapanese ? "ダッシュボード" : "Dashboard";
    public string UsageOverview => IsJapanese ? "使用量" : "Usage";
    public string UsageTotalTokens => IsJapanese ? "合計トークン" : "Total tokens";
    public string AddProvider => IsJapanese ? "追加" : "Add provider";
    public string ProviderPicker => IsJapanese ? "プロバイダーを選択" : "Choose a provider";
    public string Close => IsJapanese ? "閉じる" : "Close";
    public string History => IsJapanese ? "履歴" : "History";
    public string Settings => IsJapanese ? "設定" : "Settings";
    public string Refresh => IsJapanese ? "更新" : "Refresh";
    public string QuotaRefreshing => IsJapanese ? "利用枠データを更新しています…" : "Refreshing quota data…";
    public string ResetPending => IsJapanese ? "リセット後の最新値は未確認" : "Post-reset value not yet confirmed";
    public string LastObserved(string value) => IsJapanese ? $"最後に確認された値: {value}" : $"Last observed: {value}";
    public string LastObservedBeforeReset(string value) => IsJapanese ? $"リセット前の最後に確認された値: {value}" : $"Last observed before reset: {value}";
    public string QuotaExhausted => IsJapanese ? "上限に到達・残量 0%" : "Exhausted · 0% remaining";
    public string FactRemaining(decimal percent) => IsJapanese ? $"{percent:0.#}% 残り" : $"{percent:0.#}% remaining";
    public string PillNormal => IsJapanese ? "通常" : "Normal";
    public string PillAttention => IsJapanese ? "注意" : "Caution";
    public string PillDanger => IsJapanese ? "危険" : "Danger";
    // Dashboard pill words stay as short as the compact badges so the widest English pill
    // still fits (or wraps cleanly) inside a narrow tile; Japanese keeps the same meaning.
    public string PillExhausted => IsJapanese ? "上限到達" : "Exhausted";
    public string PillStale => IsJapanese ? "古い情報" : "Stale";
    public string PillUnknown => IsJapanese ? "不明" : "Unknown";
    public string PillOver(decimal by) => IsJapanese ? $"{by:0.#}%超過" : $"Over {by:0.#}%";
    public string PillResetPending => IsJapanese ? "未確認" : "Pending";
    public string ConnectedAccounts => IsJapanese ? "接続済みアカウント" : "Connected accounts";
    public string SummaryAccounts(int count) => IsJapanese ? $"{count}アカウント" : $"{count} accounts";
    public string SummaryReview(int count) => IsJapanese ? $"{count}件 要確認" : $"{count} need review";
    public string CreditsPeriod => IsJapanese ? "クレジット" : "Credits";
    public string CopilotCenterPercent(decimal? percent) => percent is null ? CompactUnknownValue : percent.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    public string CompactQuotaRemaining(decimal percent) => $"{percent:0.#}%";
    public string CompactUnknownValue => "—";
    public string CompactBadgeStale => IsJapanese ? "古い" : "Stale";
    public string CompactBadgeUnknown => IsJapanese ? "不明" : "Unknown";
    public string CompactBadgeResetPending => IsJapanese ? "未確認" : "Pending";
    public string CompactBadgeOver => IsJapanese ? "上限超過" : "Over";
    public string CompactExhausted => IsJapanese ? "上限到達" : "Exhausted";
    public string CompactResetUnavailable => IsJapanese ? "リセット時刻不明" : "Reset time unknown";
    public string CompactResetPassed => IsJapanese ? "リセット済み" : "Reset passed";
    public string CompactResetDays(int days) => IsJapanese ? $"あと{days}日" : $"in {days}d";
    public string CompactResetHours(int hours, int minutes) => IsJapanese ? $"あと{hours}時間" : minutes > 0 ? $"in {hours}h {minutes}m" : $"in {hours}h";
    public string CompactResetMinutes(int minutes) => IsJapanese ? $"あと{minutes}分" : $"in {minutes}m";
    public string CopilotUsageVsReference(decimal percent) => IsJapanese ? $"参考枠の{percent:0.#}%" : $"{percent:0.#}% of reference";
    public string AdditionalRingWindows(int count) => IsJapanese ? $"+{count}件はリング外に表示" : $"+{count} more windows outside rings";
    public string RefreshError => IsJapanese ? "更新できません。最後に取得した状態を表示しています。" : "Unable to refresh. Showing the last known state.";
    public string Subtitle => IsJapanese ? "次回のリセットまでの利用状況を確認できます。" : "A calm view of what is left before your next reset.";
    public string EmptyTitle => IsJapanese ? "利用枠データがありません" : "No quota data yet";
    public string EmptyDescription => IsJapanese ? "プロバイダーを接続するか、利用枠を手動で追加してください。" : "Connect a provider or add a manual quota to get started.";
    public string LocalFirst => "LOCAL-FIRST";
    public string NoSecretsLeave => IsJapanese ? "テレメトリーは送信しません。認証情報は連携先への認証に使用します。" : "No telemetry is sent. Credentials are used to authenticate with providers.";
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
    public string CopilotDescription => IsJapanese ? "Copilotはghの状態確認またはデバイス認証を使用します。デバイス認証には実行時に設定したGitHub App Client IDが必要ですが、クライアントシークレットは不要です。ghの状態確認にはClient IDは不要です。組織の利用状況を取得できない場合は手動入力を使えます。" : "Copilot uses gh status or device flow. Device flow requires a runtime-configured GitHub App Client ID, with no client secret. gh status does not require a Client ID. Organization quota may be unavailable, so manual fallback is supported.";
    public string CopilotNoUsageDetails => IsJapanese ? "利用明細なし" : "No usage details";
    public string CopilotNoUsageValue => IsJapanese ? "使用量不明" : "usage unavailable";
    public string CopilotNoUsageState => IsJapanese ? "明細なし" : "No usage details";
    public string CopilotNoDataDetails => IsJapanese ? "明細なし・使用量不明" : "No usage details · usage unavailable";
    public string CopilotNoDataAutomation(string period) => $"{period} · {CopilotNoDataDetails}";
    public string CopilotMonthPeriod(int year, int month) => IsJapanese ? $"{year}年{month}月 · UTC" : $"{new DateTime(year, month, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)} · UTC";

    public string CopilotReferenceDetail(decimal? used, decimal reference) => used is { } value
        ? IsJapanese ? $"使用済み {value:#,0.##} / {reference:#,0.##} credits" : $"Used {value:#,0.##} / {reference:#,0.##} credits"
        : IsJapanese ? $"使用量不明 / {reference:#,0.##} credits" : $"Usage unavailable / {reference:#,0.##} credits";
    public string CopilotManualReference(decimal? used, decimal reference) => used is { } value
        ? IsJapanese ? $"使用済み {value:#,0.##} / 手動参考枠 {reference:#,0.##} credits" : $"Used {value:#,0.##} / manual reference {reference:#,0.##} credits"
        : IsJapanese ? $"使用量不明 / 手動参考枠 {reference:#,0.##} credits" : $"Usage unavailable / manual reference {reference:#,0.##} credits";
    public string CopilotUsagePercent(decimal? percent) => percent is { } value
        ? IsJapanese ? $"参考枠に対する使用率 {value:0.#}%" : $"Usage vs manual reference: {value:0.#}%"
        : CopilotUsagePercentUnavailable;
    public string CopilotUsagePercentUnavailable => IsJapanese ? "参考枠に対する使用率を表示できません" : "Usage vs manual reference unavailable";
    public string CopilotRateDisclaimer => IsJapanese ? "参考値・残量ではありません" : "Reference only · not remaining";
    public string CopilotReferencePercentDisplay(decimal? percent) => percent is { } value ? $"{value:0.#}%" : IsJapanese ? "—" : "Unavailable";
    public string CopilotManualReferenceBadge => IsJapanese ? "手動参考枠" : "MANUAL REFERENCE";
    public string CopilotReferenceShortCaption => IsJapanese ? "参考枠に対する使用率" : "of manual reference";
    public string CopilotReferenceDisclaimer => IsJapanese ? "参考値・残量ではありません。個人の残高・契約上限ではありません。" : "Reference only · not remaining. Not a personal balance or plan limit.";
    public string CopilotReferenceLabel => IsJapanese ? "Copilot手動参考枠（クレジット）" : "Copilot manual reference credits";
    public string CopilotReferenceHint => IsJapanese ? "比較用の設定値です。個人の残高・契約上限ではありません。空欄で解除できます。" : "A comparison preference only—not a personal balance or plan limit. Leave blank to clear.";
    public string CopilotReferenceSave => IsJapanese ? "保存" : "Save";
    public string CopilotReferenceInvalid => IsJapanese ? "正の数値を入力してください。空欄にすると解除されます。" : "Enter a positive number, or leave blank to clear.";
    public string CopilotReferenceSaveFailed => IsJapanese ? "設定を保存できませんでした。" : "Unable to save this setting.";

    public string CopilotReauthenticate => IsJapanese ? "再認証をお試しください" : "Try re-authenticating";
    public string CopilotSessionOnlyStorage => IsJapanese ? "認証情報はこのセッション中のみ保持されます。アプリを再起動すると、再認証が必要です。" : "Your sign-in is kept for this session only. You’ll need to authenticate again after restarting the app.";
    public string CopilotFlowWaiting => IsJapanese ? "認証中です。ブラウザーでコードを入力してください。" : "Authentication is in progress. Waiting for you to enter the code in your browser.";
    public string CodexBadge => IsJapanese ? "実験的" : "Experimental";
    public string CodexDescription => IsJapanese ? "OpenAI Codexとの実験的な連携です。ChatGPT Plus/ProのCodex利用枠のみ取得します。Codex CLIは不要です。QuotaSightがブラウザーでデバイス認証を開始します。ブラウザーが開かない場合は、表示されたURLを開いてコードを入力してください。" : "Experimental OpenAI Codex integration for the ChatGPT Plus/Pro Codex allowance only. No Codex CLI installation is required; QuotaSight starts the device login in your browser. If the browser cannot open, continue manually with the displayed URL and code.";
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
    public string HistoryDescription => IsJapanese ? "利用状況の記録を30日分、端末内に保存します。エクスポートには表示用の情報のみ含まれます。" : "30 days of local snapshots. Export contains UI-safe fields only.";
    public string ExportCsv => IsJapanese ? "CSVをエクスポート" : "Export CSV";
    public string ExportJson => IsJapanese ? "JSONをエクスポート" : "Export JSON";
    public string DeleteAll => IsJapanese ? "すべて削除" : "Delete all";
    public string UsageTrend => IsJapanese ? "使用状況の推移" : "Usage trend";
    public string HistoryPrevious => IsJapanese ? "前へ" : "Previous";
    public string HistoryNext => IsJapanese ? "次へ" : "Next";
    public string Delete => IsJapanese ? "削除" : "Delete";
    public string HistoryNotificationTitle => History;
    public string HistoryDeleteFailure => IsJapanese ? "この履歴を削除できませんでした。" : "Unable to delete this history entry.";
    public string HistoryDeleteAllFailure => IsJapanese ? "履歴を削除できませんでした。" : "Unable to delete history.";
    public string HistoryExportFailure => IsJapanese ? "履歴をエクスポートできませんでした。" : "Unable to export history.";
    public string ResidentSaveFailure => IsJapanese ? "常駐モードの設定を保存できませんでした。変更は保存されていません。" : "Unable to save the resident mode setting. Your change was not saved.";
    public string HistoryCorrupt => IsJapanese ? "履歴データが破損しています。読み込める項目を表示しています。" : "History data is damaged; showing available entries.";
    public string TrayUnavailableTitle => IsJapanese ? "トレイを利用できません" : "Tray unavailable";
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
    public string RefreshNoData(IEnumerable<ProviderKind> providers, IReadOnlySet<ProviderKind> providersWithPreviousValue)
    {
        var messages = providers.Distinct().Select(provider => providersWithPreviousValue.Contains(provider)
            ? (IsJapanese ? $"{RefreshProviderName(provider)}は現在の期間のデータがありません。前回正常に取得した値を表示しています。" : $"{RefreshProviderName(provider)}: no data for the current period. Showing the last successful value.")
            : (IsJapanese ? $"{RefreshProviderName(provider)}は現在の期間のデータがありません。" : $"{RefreshProviderName(provider)}: no data for the current period.")).ToList();
        return string.Join(" ", messages);
    }
    public string QuotaThresholdTitle(ProviderKind provider) => IsJapanese ? $"{provider switch { ProviderKind.ChatGpt => "ChatGPT", ProviderKind.OpenCode => "OpenCode Go", ProviderKind.Claude => "Claude", ProviderKind.Copilot => "GitHub Copilot", _ => provider.ToString() }}の利用率がしきい値に達しました" : $"{provider} quota threshold";
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
    public string NotificationDeliveryDescription => OperatingSystem.IsWindows()
        ? (IsJapanese
            ? "しきい値に達するとWindowsの通知を試み、アプリ内バナーも表示します。通知はOSの設定などで表示されない場合があります。ウィンドウを閉じている間はバナーを確認できません。"
            : "At the threshold, QuotaSight tries a Windows system notification and also shows an in-app banner. The OS may suppress notifications; banners cannot be seen while the window is closed.")
        : OperatingSystem.IsLinux()
            ? (IsJapanese
                ? "Linuxでは、アプリの実行中にしきい値へ達するとデスクトップ通知を試み、アプリ内にも表示します。OSの設定や集中モードでデスクトップ通知が表示されない場合があります。"
                : "On Linux, QuotaSight tries a desktop notification at the threshold while running and also shows an in-app banner. OS settings or Focus may suppress the desktop notification.")
            : (IsJapanese
                ? "しきい値に達すると、アプリの実行中にアプリ内で表示します。ウィンドウが非表示の間は確認できません。"
                : "Threshold alerts appear in-app while QuotaSight is running; they cannot be seen while the window is hidden.");
    public string ResidentMode => IsJapanese ? "常駐モードを有効にする" : "Enable resident mode";
    public string ResidentModeDescription => IsJapanese ? "対応環境ではアイコンをクリックして利用枠をすばやく確認できます。ウィンドウを閉じてもトレイに残ります。" : "On supported platforms, click the icon for a quick quota check; closing the window keeps QuotaSight in the tray.";
    public string OpenFullWindow => IsJapanese ? "メインウィンドウを開く" : "Open full window";
    public string TrayUpdate => IsJapanese ? "更新" : "Refresh";
    public string TrayExit => IsJapanese ? "終了" : "Exit";
    public string CompactTitle => IsJapanese ? "利用枠の確認" : "Quota check";
    public string CompactSubtitle => IsJapanese ? "次のリセットまでの概要" : "A quick view before the next reset";
    public string CompactProviders => IsJapanese ? "プロバイダー" : "Quota providers";
    public string OverallThreshold => IsJapanese ? "通知する使用率（%）" : "Overall threshold (%)";
    public string ProviderOverrides => IsJapanese ? "通知しきい値は使用率を計算できる利用枠に適用されます。CopilotのAIクレジット総量には個人別の上限がないため、対象外です。" : "The threshold applies to percentage-based quotas. Copilot Gross AI credits have no personal limit and are excluded.";
    public string Integrations => IsJapanese ? "連携" : "Integrations";
    public string GithubClientId => IsJapanese ? "GitHub App Client ID（秘密情報ではありません）" : "GitHub App Client ID (not a secret)";
    public string GithubOrganization => IsJapanese ? "GitHub Organization slug（組織名のURL用表記・秘密情報ではありません）" : "GitHub organization slug (not a secret)";
    public string GithubOrganizationHint => IsJapanese ? "入力内容は変更時に自動保存されます。保存後はダッシュボードで更新してください。例: acme-engineering" : "Changes are auto-saved. After saving, refresh the dashboard. Example: acme-engineering";
    // Usage connection section. Credential values are never part of this copy: it is fixed
    // text that is safe to show, log or screenshot next to masked inputs.
    public string UsageConnectionTitle => IsJapanese ? "利用量 API 接続" : "Usage API connection";
    public string UsageConnectionDescription => IsJapanese
        ? "利用量 API のエンドポイントと Access Client ID、Client Secret を保存します。認証情報は安全な資格情報ストアにのみ保存され、保存後は表示・コピーできません。"
        : "Saves the usage API endpoint with its Access Client ID and Client Secret. Credentials live only in the secure credential store and cannot be viewed or copied after saving.";
    public string UsageEndpointLabel => IsJapanese ? "利用量 API エンドポイント" : "Usage API endpoint";
    public string UsageEndpointHint => IsJapanese ? "https のオリジンを指定します（例: https://usage.example.test）。" : "Enter an https origin (for example, https://usage.example.test).";
    public string UsageClientIdLabel => IsJapanese ? "Access Client ID" : "Access Client ID";
    public string UsageClientSecretLabel => IsJapanese ? "Client Secret（保存後に表示・コピーできません）" : "Client secret (cannot be viewed or copied after saving)";
    public string UsageConnectionSave => IsJapanese ? "接続を保存" : "Save connection";
    public string UsageConnectionRemove => IsJapanese ? "接続を削除" : "Remove connection";
    public string UsageConnectionRemoveConfirm => IsJapanese ? "保存した利用量 API の接続設定を削除しますか？この操作は取り消せません。" : "Remove the saved usage API connection? This cannot be undone.";
    public string UsageConnectionConfirmRemove => IsJapanese ? "削除する" : "Remove";
    public string UsageConnectionCancelRemove => IsJapanese ? "キャンセル" : "Cancel";
    public string UsageConnectionSaving => IsJapanese ? "保存中…" : "Saving…";
    public string UsageConnectionRemoving => IsJapanese ? "削除中…" : "Removing…";
    public string UsageConnectionSaveFailure => IsJapanese ? "接続設定を保存できませんでした。" : "Unable to save the connection.";
    public string UsageCredentialSaved => IsJapanese ? "認証情報: 保存済み" : "Credential: saved";
    public string UsageCredentialNone => IsJapanese ? "認証情報: 未保存" : "Credential: not saved";
    public string UsageSessionOnlyBadge => IsJapanese ? "このセッション限り" : "This session only";
    public string UsageStoreUnavailableBadge => IsJapanese ? "資格情報ストア利用不可のため保存できません" : "Cannot save: the credential store is unavailable";
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
    public string OpenCodeStatus(ProviderConnectionResult result) => IsJapanese ? result.Success ? "OpenCode Goに接続しました。資格情報は安全なストアを利用できる場合は保存し、それ以外はこのセッション中のみ保持します。" : result.Status switch
    {
        FetchStatus.Unauthorized => "OpenCode GoのAPIキーが拒否されました（401）。",
        FetchStatus.Forbidden => "OpenCode Goへのアクセスが拒否されました（403）。",
        FetchStatus.Unsupported => "OpenCode Goの利用状況エンドポイントが見つかりません（404）。",
        FetchStatus.RateLimited => "OpenCode Goで一時的な利用制限が発生しています（429）。",
        _ => "OpenCode Goからの応答を処理できませんでした。"
    } : result.Message;
    public string CopilotQuotaNotice => IsJapanese
        ? "権限を持つ組織管理者はBilling Usage API（/organizations/{org}/settings/billing/ai_credit/usage）から使用量の詳細を取得できます。表示する数値は請求主体で共有される総量の内訳（含まれる分・追加分）であり、個人の利用枠や残量、割合ゲージではありません。設定または権限が不足する場合は公式画面または手動入力を利用してください。反映には遅延があります。"
        : "An organization administrator with the required access can retrieve usage details from the Billing Usage API (/organizations/{org}/settings/billing/ai_credit/usage). The figures show the shared billing-entity Gross composition (Included and Additional), not a personal quota, remaining balance, or percentage gauge. If configuration or permissions are insufficient, use the official page or manual entry; GitHub may report usage with delay.";
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
            FetchStatus.ConfigurationError when failure.Provider == ProviderKind.Copilot => IsJapanese ? "設定画面でGitHub Organizationを設定してください。" : "Set the GitHub Organization in Settings.",
            FetchStatus.Unauthorized when failure.Provider == ProviderKind.Copilot => IsJapanese ? "GitHub認証が必要です。" : "GitHub authentication is required.",
            FetchStatus.Forbidden when failure.Provider == ProviderKind.Copilot => IsJapanese ? "Billing権限が不足しています。AppのAdministration: Read-only、対象Organizationへのインストール/承認、認証ユーザーのOrganization管理権限を確認してください。" : "GitHub Billing permission is insufficient. Check the App's Administration: Read-only permission, installation and approval for the target Organization, and the authenticated user's Organization management permission.",
            FetchStatus.RateLimited when failure.Provider == ProviderKind.Copilot => IsJapanese ? "GitHub Copilotのレート制限です。" : "GitHub Copilot is rate-limited.",
            FetchStatus.ConfigurationError => IsJapanese ? "設定を確認してください。" : "Check the provider configuration.",
            FetchStatus.Unauthorized => IsJapanese ? "再接続が必要です。手動入力または公式ページを利用してください。" : "Reconnect is required. Use manual input or the official page.",
            FetchStatus.Forbidden => IsJapanese ? "権限が不足しています。" : "Permission is insufficient.",
            FetchStatus.RateLimited => IsJapanese ? "一時的なレート制限です。" : "Temporary rate limit.",
            FetchStatus.TransientFailure => IsJapanese ? "一時的な取得失敗です。" : "Temporary fetch failure.",
            FetchStatus.Unsupported when failure.Provider == ProviderKind.Copilot => IsJapanese ? "Billing Usageを取得できません。Organization名、アクセス権、対象の対応状況を確認してください。" : "Unable to retrieve Billing Usage. Check the organization name, access permissions, and whether the target is supported.",
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
