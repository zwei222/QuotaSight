using Avalonia.Controls;
using QuotaSight.Core;
using QuotaSight.UI;

namespace QuotaSight.UI.Tests.Support;

/// <summary>
/// Test host that builds the synthetic quota-rings window from fixed sample data, a fixed
/// clock, and theme/language chosen through settings, so tests and any external capture
/// harness that links this file render the same window.
/// </summary>
public static class QuotaRingsHost
{
    public static readonly DateTimeOffset FixtureNow = DateTimeOffset.Parse("2026-11-20T12:00:00+09:00");

    public static (Window Window, MainViewModel ViewModel) Create(string[] args)
    {
        var widget = args.Contains("--widget", StringComparer.OrdinalIgnoreCase);
        var light = args.Contains("--light", StringComparer.OrdinalIgnoreCase);
        // Theme must be passed explicitly through settings: SetSettings applies dto.Theme and a
        // default "System" value would override the --dark variant on the host.
        var theme = light ? "Light" : "Dark";
        var viewModel = new MainViewModel(new SyntheticDashboardSource(),
            timeProvider: new FixedTimeProvider(FixtureNow));
        viewModel.SetSettings(viewModel.CurrentSettingsForTests with { Theme = theme, CopilotReferenceCredits = 1500m }, persist: false);
        if (args.Contains("--ja", StringComparer.OrdinalIgnoreCase))
            viewModel.Language = UiLanguage.Japanese;
        Window window = widget
            ? new CompactQuotaWindow(viewModel) { Width = 410, Height = 620 }
            : new MainWindow(viewModel) { Width = 1100, Height = 740 };
        return (window, viewModel);
    }
}

public sealed class SyntheticDashboardSource : IDashboardSource
{
    private readonly IReadOnlyList<ProviderCardViewModel> cards;

    public SyntheticDashboardSource()
    {
        var now = QuotaRingsHost.FixtureNow;
        cards =
        [
            Card(ProviderKind.Claude, "Claude Max", "alice", "Connected",
                [Sample(ProviderKind.Claude, "alice", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 62, now, now.AddHours(1).AddMinutes(12)),
                 Sample(ProviderKind.Claude, "alice", "Messages", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 29, now, now.AddDays(3))]),
            Card(ProviderKind.ChatGpt, "ChatGPT Plus", "work", "Connected",
                [Sample(ProviderKind.ChatGpt, "work", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 0, now, now.AddMinutes(22)),
                 Sample(ProviderKind.ChatGpt, "work", "Messages", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 55, now, now.AddDays(2))]),
            Card(ProviderKind.OpenCode, "OpenCode Go", "dev", "Connected",
                [Sample(ProviderKind.OpenCode, "dev", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 88, now, now.AddHours(2)),
                 Sample(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Daily, TimeSpan.FromDays(1), 72, now, now.AddHours(9)),
                 Sample(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), 40, now, now.AddDays(2)),
                 Sample(ProviderKind.OpenCode, "dev", "Requests", QuotaWindowKind.Monthly, TimeSpan.FromDays(30), 12, now, now.AddDays(10))]),
            Card(ProviderKind.Claude, "Claude Pro", "personal", "Stale · refresh failed",
                [Sample(ProviderKind.Claude, "personal", "5h", QuotaWindowKind.Rolling, TimeSpan.FromHours(5), 75, now.AddHours(-3), now.AddHours(1), now.AddHours(-2)),
                 Sample(ProviderKind.Claude, "personal", "Messages", QuotaWindowKind.Weekly, TimeSpan.FromDays(7), null, now.AddHours(-3), now.AddDays(3), now.AddHours(-2)),
                 Sample(ProviderKind.Claude, "personal", "Messages", QuotaWindowKind.Monthly, TimeSpan.FromDays(30), 64, now.AddHours(-1), now.AddMinutes(-30), now.AddHours(2))]),
            CopilotCard(now)
        ];
    }

    public bool IsDemo => false;

    public IReadOnlyList<ProviderCardViewModel> Load() => cards;

    private static ProviderCardViewModel Card(ProviderKind provider, string name, string account, string state,
        IReadOnlyList<QuotaRowViewModel> rows) =>
        new(provider, name, account, "#73D6B1", state, false, rows) { CurrentLanguage = UiLanguage.English };

    private static QuotaRowViewModel Sample(ProviderKind provider, string account, string metric,
        QuotaWindowKind kind, TimeSpan duration, decimal? remaining, DateTimeOffset fetched,
        DateTimeOffset resetAt, DateTimeOffset? freshUntil = null)
    {
        var used = remaining is { } value ? 100m - value : (decimal?)null;
        var snapshot = new QuotaSnapshot(provider, account, metric,
            new QuotaWindow(kind, fetched.AddDays(-1), fetched.Add(duration), duration, resetAt),
            used, remaining is null ? null : 100m, null, "requests", fetched, fetched,
            QuotaSource.Official, QuotaConfidence.Reported, freshUntil ?? fetched.AddHours(4),
            provider == ProviderKind.Claude ? "Claude" : provider == ProviderKind.ChatGpt ? "ChatGPT" : "OpenCode Go");
        var row = QuotaPresentationFormatter.Format(snapshot, QuotaRingsHost.FixtureNow, UiLanguage.English);
        return kind == QuotaWindowKind.Rolling ? row with { WindowName = metric, Metric = string.Empty } : row;
    }

    private static ProviderCardViewModel CopilotCard(DateTimeOffset now)
    {
        var snapshot = new QuotaSnapshot(ProviderKind.Copilot, "corp", "AI credits",
            new QuotaWindow(QuotaWindowKind.Monthly, now.AddDays(-10), now.AddDays(20), TimeSpan.FromDays(30), now.AddDays(20)),
            null, null, 83, "credits", now, now, QuotaSource.Delayed, QuotaConfidence.Reported,
            now.AddHours(2), "GitHub Copilot Business", new CopilotUsageBreakdown(1240, 900, 340));
        var row = QuotaPresentationFormatter.Format(snapshot, now, UiLanguage.English);
        return new ProviderCardViewModel(ProviderKind.Copilot, "GitHub Copilot Business", "corp",
            "#73D6B1", "Connected · reference values", false, [row])
        {
            CurrentLanguage = UiLanguage.English
        };
    }
}

public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
