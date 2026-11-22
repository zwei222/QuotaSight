using System.Globalization;
using QuotaSight.Application;

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
        (UsageFailureKind.BreakdownUnsupported, true) => "サーバーが内訳に未対応です。",
        (UsageFailureKind.BreakdownUnsupported, false) => "The server does not provide the usage breakdown yet.",
        (UsageFailureKind.CollectorNotFound, true) => "collector の指定が正しくありません。設定画面で確認してください。",
        (UsageFailureKind.CollectorNotFound, false) => "The collector name is not valid. Review the settings.",
        _ => string.Empty
    };

    // Fixed state sentences for the breakdown card. "Unsupported" is its own state, not
    // an error: the daily totals still work, only this capability is missing server-side.
    public string UsageBreakdownState(string state) => (state, IsJapanese) switch
    {
        ("Loading", true) => "利用内訳を読み込み中…",
        ("Loading", false) => "Loading the usage breakdown…",
        ("Ready", true) => "利用内訳を表示しています。",
        ("Ready", false) => "Usage breakdown loaded.",
        ("Empty", true) => "この期間の内訳はまだありません。",
        ("Empty", false) => "No attributed breakdown for this period yet.",
        ("Unsupported", true) => "サーバーが内訳に未対応です。",
        ("Unsupported", false) => "The server does not provide the usage breakdown yet.",
        ("Stale", true) => "前回の内訳を表示しています（更新に失敗しました）。",
        ("Stale", false) => "Showing the last loaded breakdown; the refresh failed.",
        ("Error", true) => "利用内訳を読み込めませんでした。",
        ("Error", false) => "Could not load the usage breakdown.",
        ("Unconfigured", true) => "利用量 API は未設定です。",
        ("Unconfigured", false) => "Usage API is not configured.",
        _ => state
    };

    // The fixed disclaimer the breakdown card always carries with values on screen. It
    // names the limits of the observation instead of implying a complete measurement.
    public string UsageBreakdownNote => IsJapanese
        ? "TokScale がローカルログから集計した観測値です。ログの削除（Claude Code は既定30日）や API・キャッシュ型クライアントでは欠落・遅れがあります。"
        : "Observed values aggregated by TokScale from local logs. Log deletion (Claude Code keeps 30 days by default) and API or cache-type clients can leave gaps or delays.";

    public string UsageBreakdownTitle => IsJapanese ? "利用内訳" : "Usage breakdown";
    public string UsageBreakdownDimensionName(UsageBreakdownDimension dimension) => (dimension, IsJapanese) switch
    {
        (UsageBreakdownDimension.Client, true) => "クライアント",
        (UsageBreakdownDimension.Client, false) => "Client",
        (UsageBreakdownDimension.Provider, true) => "プロバイダー",
        (UsageBreakdownDimension.Provider, false) => "Provider",
        (UsageBreakdownDimension.Model, true) => "モデル",
        (UsageBreakdownDimension.Model, false) => "Model",
        _ => dimension.ToString()
    };
    /// <summary>The dimension switcher's own name for screen readers, distinct from the card title.</summary>
    public string UsageBreakdownDimensionSelector => IsJapanese ? "内訳の集計単位" : "Breakdown grouping";
    public string UsageBreakdownUnknown => IsJapanese ? "（不明）" : "(unknown)";
    public string UsageBreakdownTotal => IsJapanese ? "合計" : "Total";
    public string UsageBreakdownShare(decimal share) => IsJapanese
        ? $"構成比 {share:0.#}%"
        : $"{share:0.#}% of total";
    public string UsageBreakdownComponentInput => IsJapanese ? "入力" : "Input";
    public string UsageBreakdownComponentOutput => IsJapanese ? "出力" : "Output";
    public string UsageBreakdownComponentCacheRead => IsJapanese ? "キャッシュ読取" : "Cache read";
    public string UsageBreakdownComponentCacheWrite => IsJapanese ? "キャッシュ書込" : "Cache write";
    public string UsageBreakdownComponentReasoning => IsJapanese ? "推論" : "Reasoning";
    public string UsageBreakdownTokens(long tokens) => IsJapanese
        ? $"{tokens:N0} トークン"
        : $"{tokens:N0} tokens";
    public string UsageBreakdownUnclassified(long tokens) => IsJapanese
        ? $"未分類 {tokens:N0} トークン（内訳のない利用）"
        : $"Unclassified: {tokens:N0} tokens (usage without a breakdown)";
    public string UsageBreakdownPartial => IsJapanese
        ? "一部の日だけ内訳があります。"
        : "Only some days have a breakdown.";
    public string UsageBreakdownOverAttributionRow(string date, long breakdown, long total) => IsJapanese
        ? $"{date} の内訳が日合計を超えています（{breakdown:N0} > {total:N0}）"
        : $"{date}: the breakdown exceeds the day total ({breakdown:N0} > {total:N0})";

    /// <summary>
    /// Display name of a TokScale client id. The versioned catalog is the source of
    /// truth; ids outside it keep their known names, and an unknown id is shown as-is
    /// rather than invented. Product names are stable and identical in both languages.
    /// </summary>
    public string TokScaleClientName(string id)
    {
        if (TokScaleClientCatalog.Find(id) is { } client) return client.DisplayName;
        return id switch
        {
            "aider" => "Aider",
            "iflow" => "iFlow CLI",
            _ => id
        };
    }

    // The dashboard's observed-usage section. It is deliberately named as an observation:
    // TokScale has no quota, so no ring, no gauge and no threshold ever appears here.
    public string TokScaleSectionTitle => IsJapanese ? "観測使用量（TokScale）" : "Observed usage (TokScale)";
    // The badge also carries the cards-may-overlap note: a combination row feeds every
    // provider it contains, so card totals are not meant to add up.
    public string TokScaleBadge => IsJapanese
        ? "観測値・上限なし・推定・他端末分は含まない・カード間で共有分が重複します"
        : "Observed · no limit · no estimates · other devices excluded · cards may share amounts";
    public string TokScaleToday => IsJapanese ? "今日" : "Today";
    public string TokScaleWeek => IsJapanese ? "7日" : "7 days";
    /// <summary>A tracked provider the breakdown has not attributed anything to yet.</summary>
    public string TokScaleNotObserved => IsJapanese ? "まだ観測されていません" : "Not observed yet";
    /// <summary>A card whose tokens include a combination row other providers also carry.</summary>
    public string TokScaleSharedNotice => IsJapanese
        ? "他のプロバイダーと共有する分を含む"
        : "Includes tokens shared with other providers";
    // The add-provider panel for a catalog provider: a usage-only card, never a quota.
    public string TokScalePanelTitle => IsJapanese ? "使用量のみのカード" : "Usage-only card";
    public string TokScalePanelDescription => IsJapanese
        ? "このプロバイダーのカードを観測使用量セクションに追加します。カードは使用量のみを表示し、上限やリングはありません。利用量 API に接続しているときに観測されます。"
        : "Adds this provider's card to the observed usage section. The card shows usage only — no limit and no ring. Observations require a usage API connection.";
    public string TokScalePanelAdd => IsJapanese ? "カードを追加" : "Add card";
    public string TokScalePanelRemove => IsJapanese ? "カードを削除" : "Remove card";
    public string TokScalePanelTracked => IsJapanese
        ? "このプロバイダーのカードは追加済みです。"
        : "This provider's card is added.";
    public string TokScalePanelNotTracked => IsJapanese
        ? "このプロバイダーのカードはまだ追加されていません。"
        : "This provider's card is not added yet.";
    public string TokScaleEmptyTitle => IsJapanese ? "まだ観測されたデータはありません" : "Nothing observed yet";
    public string TokScaleEmptyBodyUnconfigured => IsJapanese
        ? "利用量 API を設定して、プロバイダーを追加しましょう。"
        : "Set up the usage API, then add a provider.";
    public string TokScaleEmptyBody => IsJapanese
        ? "「追加」からプロバイダーを選ぶと、そのプロバイダーの観測使用量がここに表示されます。"
        : "Choose a provider under Add to show its observed usage here.";
    /// <summary>
    /// The empty state when nothing is selected but the usage API did observe providers:
    /// the next step is still to add one, and the sentence says observations exist.
    /// </summary>
    public string TokScaleEmptyBodyWithObservations(int count) => IsJapanese
        ? $"「追加」からプロバイダーを選ぶと、そのプロバイダーの観測使用量がここに表示されます。利用量 API から {count} 件のプロバイダーが観測されています。"
        : $"Choose a provider under Add to show its observed usage here. {count} provider(s) were observed through the usage API.";
    public string TokScaleOpenSettings => IsJapanese ? "設定を開く" : "Open settings";
    public string TokScaleState(string state) => (state, IsJapanese) switch
    {
        ("Unconfigured", true) => "利用量 API に未接続",
        ("Unconfigured", false) => "Not connected to the usage API",
        ("Loading", true) => "観測使用量を読み込み中…",
        ("Loading", false) => "Loading observed usage…",
        ("Ready", true) => "観測使用量を表示しています。",
        ("Ready", false) => "Observed usage loaded.",
        ("Empty", true) => "表示するプロバイダーが選択されていません。",
        ("Empty", false) => "No provider is selected yet.",
        ("Stale", true) => "前回の観測使用量を表示しています（更新に失敗しました）。",
        ("Stale", false) => "Showing the last observed usage; the refresh failed.",
        ("Error", true) => "観測使用量を読み込めませんでした。",
        ("Error", false) => "Could not load observed usage.",
        _ => state
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
        (UsageConnectionError.InvalidCredential, true) => "クライアント ID とクライアントシークレットは、空白を含まない印字可能なASCII文字で入力してください。",
        (UsageConnectionError.InvalidCredential, false) => "Enter a client id and client secret with no spaces.",
        (UsageConnectionError.CredentialStoreUnavailable, true) => "資格情報ストアを利用できないため、保存していません。",
        (UsageConnectionError.CredentialStoreUnavailable, false) => "The credential store is unavailable, so nothing was saved.",
        _ => string.Empty
    };
    public string Dashboard => IsJapanese ? "ダッシュボード" : "Dashboard";
    public string NavUsage => IsJapanese ? "利用量" : "Token usage";
    public string UsageTotalTokens => IsJapanese ? "合計トークン" : "Total tokens";
    public string AddProvider => IsJapanese ? "追加" : "Add";
    public string ProviderPicker => IsJapanese ? "プロバイダーを選択" : "Choose a provider";
    public string Close => IsJapanese ? "閉じる" : "Close";
    public string Settings => IsJapanese ? "設定" : "Settings";
    public string Refresh => IsJapanese ? "更新" : "Refresh";
    public string RefreshError => IsJapanese ? "更新できません。最後に取得した状態を表示しています。" : "Unable to refresh. Showing the last known state.";
    public string Subtitle => IsJapanese
        ? "契約サービスの残量をひと目で確認できます。"
        : "See remaining quota across your selected services.";
    public string LocalFirst => "LOCAL-FIRST";
    public string NoSecretsLeave => IsJapanese ? "テレメトリーは送信しません。認証情報は連携先への認証に使用します。" : "No telemetry is sent. Credentials are used to authenticate with providers.";
    public string TrayUnavailableTitle => IsJapanese ? "トレイを利用できません" : "Tray unavailable";
    public string TrayUnavailable => IsJapanese ? "トレイを利用できません。アプリ内で操作してください。" : "Tray unavailable. Use the app window instead.";
    public string Appearance => IsJapanese ? "表示" : "Appearance";
    public string Theme => IsJapanese ? "テーマ" : "Theme";
    public string Language => IsJapanese ? "言語" : "Language";
    public string ReduceMotion => IsJapanese ? "動きを減らす" : "Reduce motion";
    public string HighContrast => IsJapanese ? "ハイコントラスト" : "High contrast";
    public string SupportStatus => IsJapanese ? "対応状況: 動きを減らす、ハイコントラスト" : "Support status: Reduce motion · High contrast";
    public string RefreshAlerts => IsJapanese ? "更新" : "Refresh";
    public string RefreshInterval => IsJapanese ? "更新間隔（5〜15分）" : "Refresh interval (5–15 minutes)";
    public string ResidentMode => IsJapanese ? "常駐モードを有効にする" : "Enable resident mode";
    public string ResidentModeDescription => IsJapanese ? "対応環境ではアイコンをクリックして観測使用量をすばやく確認できます。ウィンドウを閉じてもトレイに残ります。" : "On supported platforms, click the icon for a quick look at observed usage; closing the window keeps QuotaSight in the tray.";
    public string ResidentSaveFailure => IsJapanese ? "常駐モードの設定を保存できませんでした。変更は保存されていません。" : "Unable to save the resident mode setting. Your change was not saved.";
    public string OpenFullWindow => IsJapanese ? "メインウィンドウを開く" : "Open full window";
    public string TrayUpdate => IsJapanese ? "更新" : "Refresh";
    public string TrayExit => IsJapanese ? "終了" : "Exit";
    public string QuotaAddService => IsJapanese ? "＋ サービスを追加" : "＋ Add a service";
    public string QuotaManageServices => IsJapanese ? "サービスを管理" : "Manage services";
    public string QuotaEmptyTitle => IsJapanese ? "表示するサービスがありません" : "No services on your dashboard";
    public string QuotaRemoveService => IsJapanese ? "表示から外す" : "Hide from dashboard";
    public string QuotaEmptySelection => IsJapanese ? "残量を確認したい契約を追加してください。" : "Add the subscriptions whose remaining quota you want to see.";
    public string QuotaCompactEmptyAdd => IsJapanese ? "メインウィンドウで追加" : "Add in the main window";
    public string QuotaEmptyAdd => IsJapanese ? "サービスを追加" : "Add a service";
    public string QuotaPanelTitle => IsJapanese ? "ダッシュボードに表示するサービス" : "Services shown on the dashboard";
    public string QuotaAddAction => IsJapanese ? "追加" : "Add";
    public string QuotaAntigravityNote => IsJapanese ? "Gemini系とClaude/GPT系の2枚" : "Two cards: Gemini and Claude/GPT models";
    public string QuotaNoneYet => IsJapanese ? "まだありません" : "None yet";
    public string QuotaAllAdded => IsJapanese ? "すべて追加済みです" : "All services are added";
    public string QuotaShownHeader(int count) => IsJapanese ? $"表示中（{count}）" : $"Shown ({count})";
    public string QuotaAddableHeader(int count) => IsJapanese ? $"追加できるサービス（{count}）" : $"Available to add ({count})";
    public string QuotaRemoveServiceNamed(string name) => IsJapanese ? $"{name} を表示から外す" : $"Hide {name} from dashboard";
    public string QuotaAddServiceNamed(string name) => IsJapanese ? $"{name} を追加" : $"Add {name}";
    public string CompactTitle => IsJapanese ? "契約残量" : "Quota";
    public string CompactSubtitle => IsJapanese ? "選択したサービスの残量" : "Remaining quota for selected services";
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
    public string ThemeSystem => IsJapanese ? "システム" : "System";
    public string ThemeLight => IsJapanese ? "ライト" : "Light";
    public string ThemeDark => IsJapanese ? "ダーク" : "Dark";
}
