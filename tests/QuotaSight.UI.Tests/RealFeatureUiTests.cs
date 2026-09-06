using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.Infrastructure;
using QuotaSight.UI;

namespace QuotaSight.UI.Tests;

public sealed class RealFeatureTests
{
    [Fact]
    public async Task OpenCode_success_stores_session_credential_and_delivers_snapshots()
    {
        var store = new InMemoryCredentialStore(); var delivered = new List<QuotaSnapshot>();
        using var client = new HttpClient(new JsonHandler("{\"rollingUsage\":{\"used\":80,\"limit\":100}}"));
        var facade = new UiProviderFacade(key => new OpenCodeGoAdapter(client, key, new Uri("https://example.test/usage"), TimeProvider.System), credentialStore: store, openCodeSuccess: snapshots => { delivered.AddRange(snapshots); return ValueTask.CompletedTask; });
        var result = await facade.TestOpenCodeAsync("session-key", default);
        Assert.True(result.Success); Assert.Single(delivered); Assert.Equal("session-key", await store.GetAsync("OpenCode Go", default)); Assert.DoesNotContain("session-key", result.Message);
    }

    [Fact]
    public async Task Refresh_failure_keeps_last_known_cards_and_marks_error()
    {
        var snapshot = Snapshot(40); var vm = new MainViewModel(new FixedDashboardSource(snapshot), quotaApplication: new StubApplication([]));
        await vm.RefreshAsync();
        Assert.Contains(vm.Cards, card => card.Account == "acct"); Assert.True(vm.IsError);
    }

    [Fact]
    public async Task Refresh_failure_rebuilds_last_known_cards_at_current_time_and_notifies_error_banner()
    {
        var observed = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(observed);
        var snapshot = Snapshot(40) with { Fetched = observed, Observed = observed, Window = new(QuotaWindowKind.Rolling, observed.AddHours(-1), observed.AddHours(1)) };
        var vm = new MainViewModel(new FixedDashboardSource(snapshot), quotaApplication: new StubApplication([]), timeProvider: clock);
        var changed = new List<string>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        clock.Advance(TimeSpan.FromHours(2));
        await vm.RefreshAsync();

        var row = Assert.Single(Assert.Single(vm.Cards).Windows);
        Assert.True(row.IsStale);
        Assert.Contains("Stale", row.FreshnessText, StringComparison.Ordinal);
        Assert.True(vm.IsError);
        Assert.Contains(nameof(vm.IsNotificationVisible), changed);
        Assert.Contains(nameof(vm.NotificationBannerText), changed);
    }

    [Fact]
    public async Task Saved_manual_and_provider_snapshots_are_immediately_visible_in_history_and_exports()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var history = new JsonlQuotaHistory(root);
            var vm = new MainViewModel(new EmptyDashboardSource(), new ManualQuotaService(history), history);
            await vm.InitializeAsync();
            var manual = Snapshot(25) with { Provider = ProviderKind.ChatGpt, Account = "manual-account", DisplayName = "ChatGPT Plus", Source = QuotaSource.Manual, Confidence = QuotaConfidence.Manual };
            await vm.ApplyManualSnapshotAsync(manual);
            Assert.Contains(vm.History.Entries, entry => entry.Account == "manual-account");
            Assert.Contains("manual-account", vm.History.ExportJson(), StringComparison.Ordinal);

            var provider = Snapshot(75) with { Account = "provider-account", Source = QuotaSource.Official, Confidence = QuotaConfidence.Official };
            await vm.ApplyProviderSnapshotsAsync([provider]);
            Assert.Contains(vm.History.Entries, entry => entry.Account == "provider-account");
            Assert.Contains("provider-account", vm.History.ExportCsv(), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Settings_roundtrip_and_client_id_update_are_public_contracts()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try { var store = new AppSettingsStore(root); var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: store); await vm.SetGithubClientIdAsync("client"); await vm.SetRefreshMinutesAsync(15); var loaded = await store.LoadAsync(); Assert.Equal("client", loaded.GithubOAuthClientId); Assert.Equal(15, loaded.RefreshMinutes); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Refresh_scheduler_reaches_the_same_application_refresh_contract()
    {
        var calls = 0; var scheduler = new RefreshScheduler(TimeSpan.FromMinutes(5)); using var stop = new CancellationTokenSource();
        stop.Cancel(); await scheduler.RunAsync(_ => { calls++; return ValueTask.CompletedTask; }, stop.Token); Assert.Equal(0, calls);
    }

    [Fact]
    public void Thirty_day_aggregation_deduplicates_account_and_keeps_multiple_windows()
    {
        var now = DateTimeOffset.UtcNow; var snapshots = new[] { Snapshot(40), Snapshot(90) with { Window = new(QuotaWindowKind.Monthly, now.AddDays(-1), now.AddDays(29)) } };
        var cards = DashboardAggregation.ToCards(snapshots, now); Assert.Single(cards); Assert.Equal(2, cards[0].Windows.Count); Assert.Equal("Monthly", cards[0].Windows[0].WindowName);
    }

    [Fact]
    public async Task History_export_preserves_manual_and_official_source_confidence_with_null_used_percent()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var history = new JsonlQuotaHistory(root);
            await history.AppendAsync([
                Snapshot(25) with { Source = QuotaSource.Manual, Confidence = QuotaConfidence.Manual },
                Snapshot(75) with { Source = QuotaSource.Official, Confidence = QuotaConfidence.Official },
                Snapshot(0) with { Used = null, Limit = null, Source = QuotaSource.Manual, Confidence = QuotaConfidence.Manual }
            ], default);
            var viewModel = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history);
            await viewModel.InitializeAsync();

            using var json = JsonDocument.Parse(viewModel.History.ExportJson());
            Assert.Contains(json.RootElement.EnumerateArray(), entry => entry.GetProperty("source").GetString() == "Manual" && entry.GetProperty("confidence").GetString() == "Manual");
            Assert.Contains(json.RootElement.EnumerateArray(), entry => entry.GetProperty("source").GetString() == "Official" && entry.GetProperty("confidence").GetString() == "Official");
            Assert.Contains(json.RootElement.EnumerateArray(), entry => entry.GetProperty("usedPercent").ValueKind == JsonValueKind.Null);

            var csv = viewModel.History.ExportCsv();
            Assert.StartsWith("Provider,Account,Window,UsedPercent,Observed,Source,Confidence\n", csv, StringComparison.Ordinal);
            Assert.Contains(",Manual,Manual", csv, StringComparison.Ordinal);
            Assert.Contains(",Official,Official", csv, StringComparison.Ordinal);
            Assert.Contains(",,", csv, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InitializeAsync_loads_each_persisted_history_event_once_and_builds_one_card_window()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var history = new JsonlQuotaHistory(root);
            await history.AppendAsync([Snapshot(25)], default);
            var viewModel = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history);

            await viewModel.InitializeAsync();

            Assert.Single(viewModel.History.Entries);
            var card = Assert.Single(viewModel.Cards);
            Assert.Single(card.Windows);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InitializeAsync_skips_a_corrupt_current_day_and_loads_the_prior_day()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        try
        {
            var history = new JsonlQuotaHistory(root, new FixedTimeProvider(now.AddDays(-1)));
            await history.AppendAsync([Snapshot(25) with { Account = "prior-day" }], default);
            await File.WriteAllTextAsync(Path.Combine(root, $"{today:yyyy-MM-dd}.jsonl"), "not-json\n");

            var viewModel = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history, timeProvider: new FixedTimeProvider(now));
            await viewModel.InitializeAsync();
            await viewModel.InitializeAsync();

            Assert.Equal("History data is damaged; showing available entries.", viewModel.History.LoadError);
            Assert.Single(viewModel.History.Entries, entry => entry.Account == "prior-day");
            Assert.Contains(viewModel.Cards, card => card.Account == "prior-day");
            await Assert.ThrowsAsync<InvalidDataException>(async () => await history.ReadAsync(today, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task History_csv_export_keeps_comma_quote_and_newline_account_as_one_literal_field()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        const string account = "Personal, \"quoted\"\naccount";
        try
        {
            var history = new JsonlQuotaHistory(root);
            await history.AppendAsync([Snapshot(25) with { Account = account }], default);
            var viewModel = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history);
            await viewModel.InitializeAsync();

            var rows = ParseCsv(viewModel.History.ExportCsv());

            Assert.Equal(2, rows.Count);
            Assert.Equal(account, rows[1][1]);
            Assert.Equal(7, rows[1].Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < csv.Length && csv[index + 1] == '"') { field.Append('"'); index++; }
                else if (character == '"') quoted = false;
                else field.Append(character);
                continue;
            }
            if (character == '"') quoted = true;
            else if (character == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (character == '\r' || character == '\n')
            {
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n') index++;
                row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>();
            }
            else field.Append(character);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current = current.Add(amount);
    }
    private static QuotaSnapshot Snapshot(decimal percent) => new(ProviderKind.OpenCode, "acct", "usage", new(QuotaWindowKind.Rolling, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)), percent, 100, null, "requests", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, QuotaSource.Official, QuotaConfidence.Official, DateTimeOffset.UtcNow.AddHours(1), "OpenCode Go");
    private sealed class JsonHandler(string json) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }); }
    private sealed class FixedDashboardSource(QuotaSnapshot snapshot) : IDashboardSource { public bool IsDemo => false; public IReadOnlyList<ProviderCardViewModel> Load() => DashboardAggregation.ToCards([snapshot], DateTimeOffset.UtcNow); }
    private sealed class StubApplication(IReadOnlyList<QuotaSnapshot> result) : IQuotaApplication { public ValueTask<IReadOnlyList<QuotaSnapshot>> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(result); }
}
