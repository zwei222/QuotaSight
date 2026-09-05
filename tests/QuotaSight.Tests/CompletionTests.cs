using System.Reflection;
using System.Runtime.InteropServices;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.Infrastructure;
using QuotaSight.UI;

namespace QuotaSight.Tests;

public sealed class CompletionTests
{
    private sealed class FakeSecretRunner(int exitCode) : ISecretToolProcessRunner
    {
        public ValueTask<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? stdin, CancellationToken cancellationToken)
            => ValueTask.FromResult(new SecretToolResult(exitCode, string.Empty, "locked"));
    }

    [Fact]
    public async Task Linux_secret_store_exit_one_falls_back_to_session_value()
    {
        var primary = new LinuxSecretToolCredentialStore(new FakeSecretRunner(1));
        var fallback = new InMemoryCredentialStore();
        var store = new FallbackCredentialStore(primary, fallback);
        await store.SetAsync("key", "value", default);
        Assert.Equal("value", await store.GetAsync("key", default));
        Assert.Equal(CredentialStoreAvailability.Locked, store.Availability);
    }
    [Fact]
    public async Task Windows_credentials_write_uses_target_and_utf16_blob()
    {
        var api = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(api);
        await store.SetAsync("alice", "secret", default);
        Assert.Equal("QuotaSight:alice", api.Target);
        Assert.Equal("secret", api.WrittenSecret);
        Assert.Equal(1u, api.Type); Assert.Equal(2u, api.Persist);
    }

    [Fact]
    public async Task Windows_credentials_read_success_frees_native_pointer_and_zeroes_managed_blob()
    {
        var api = new FakeWindowsCredentialApi { ReadValue = "secret" };
        var store = new WindowsCredentialStore(api);
        Assert.Equal("secret", await store.GetAsync("alice", default));
        Assert.True(api.FreeCalled);
        Assert.True(api.ZeroingContractObserved);
    }

    [Fact]
    public async Task Windows_credentials_not_found_is_null()
    {
        var api = new FakeWindowsCredentialApi { ReadError = WindowsCredentialError.NotFound };
        Assert.Null(await new WindowsCredentialStore(api).GetAsync("alice", default));
    }

    [Fact]
    public async Task Windows_credentials_error_is_unavailable_without_secret_logging()
    {
        var api = new FakeWindowsCredentialApi { ReadError = WindowsCredentialError.Other };
        var store = new WindowsCredentialStore(api);
        Assert.Null(await store.GetAsync("alice", default));
        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
        Assert.DoesNotContain("secret", api.Log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Windows_credentials_delete_uses_target()
    {
        var api = new FakeWindowsCredentialApi();
        await new WindowsCredentialStore(api).RemoveAsync("alice", default);
        Assert.Equal("QuotaSight:alice", api.Target);
        Assert.True(api.DeleteCalled);
    }

    [Fact]
    public async Task Fallback_credential_store_keeps_secret_in_session_when_primary_write_fails()
    {
        var primary = new FailingCredentialStore(); var fallback = new InMemoryCredentialStore(); var store = new FallbackCredentialStore(primary, fallback);
        await store.SetAsync("alice", "secret", default); Assert.Equal("secret", await store.GetAsync("alice", default)); Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
    }

    [Fact]
    public void Ui_composition_root_selects_linux_secret_service_store_on_linux()
    {
        if (!OperatingSystem.IsLinux()) return;

        var facade = Assert.IsType<UiProviderFacade>(CompositionRoot.CreateProviderFacade(clientId: "test"));
        var field = typeof(UiProviderFacade).GetField("credentialStore", BindingFlags.Instance | BindingFlags.NonPublic);
        var store = field?.GetValue(facade);

        Assert.IsType<FallbackCredentialStore>(store);
    }

    [Fact]
    public async Task Gh_probe_distinguishes_available_authenticated_from_quota()
    {
        var result = await new GhCliProbe("/bin/true").ProbeAsync(default);
        Assert.Equal(GhProbeStatus.AvailableAuthenticated, result.Status);
        Assert.False(result.QuotaAvailable);
    }

    [Fact]
    public async Task Gh_probe_distinguishes_unauthorized()
    {
        var result = await new GhCliProbe("/bin/false").ProbeAsync(default);
        Assert.Equal(GhProbeStatus.Unauthorized, result.Status);
    }

    [Fact]
    public async Task Gh_probe_distinguishes_missing_and_timeout()
    {
        Assert.Equal(GhProbeStatus.Missing, (await new GhCliProbe("/missing/gh").ProbeAsync(default)).Status);
        Assert.Equal(GhProbeStatus.Timeout, (await new GhCliProbe("/bin/sleep", ["2"], TimeSpan.FromMilliseconds(10)).ProbeAsync(default)).Status);
    }

    [Fact]
    public async Task Provider_facade_exposes_gh_probe_status_without_output()
    {
        var result = await new UiProviderFacade(ghProbe: new GhCliProbe("/bin/true")).ProbeGitHubCliAsync(default);
        Assert.Contains("authenticated", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stdout", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Settings_store_save_async_roundtrips()
    {
        var store = new AppSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        await store.SaveAsync(new AppSettingsDto(Theme: "Dark", Language: "Japanese", RefreshMinutes: 5, OverallThreshold: 70, NotificationsEnabled: false, GithubOAuthClientId: "id"));
        var loaded = await store.LoadAsync();
        Assert.Equal("Dark", loaded.Theme); Assert.Equal("id", loaded.GithubOAuthClientId);
    }

    [Fact]
    public async Task Settings_store_corrupt_file_is_backed_up()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = new AppSettingsStore(root); await File.WriteAllTextAsync(store.Path, "not-json");
        _ = await store.LoadAsync(); Assert.True(File.Exists(store.Path + ".bak"));
    }

    [Fact]
    public async Task Settings_changes_are_enabled_and_saved_by_view_model()
    {
        var store = new AppSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: store);
        vm.SetSettings(new AppSettingsDto(Theme: "Light", Language: "English", RefreshMinutes: 10, OverallThreshold: 80, NotificationsEnabled: true, GithubOAuthClientId: "changed"));
        Assert.Equal("changed", (await store.LoadAsync()).GithubOAuthClientId);
    }

    [Fact]
    public async Task Settings_save_recreates_github_factory_with_current_client_id()
    {
        var factory = new RecordingGitHubFactory(); var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: new AppSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), githubFactory: factory);
        vm.SetSettings(new AppSettingsDto(GithubOAuthClientId: "current")); await Task.Delay(20); Assert.Equal("current", factory.LastClientId);
    }

    [Fact]
    public async Task Refresh_prunes_history_at_thirty_days()
    {
        var history = new RecordingHistory(); var app = new QuotaApplication([], history, new FixedTimeProvider(new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero)));
        await app.RefreshAsync(default); Assert.Equal(new DateOnly(2026, 8, 6), history.PrunedBefore);
    }

    [Fact]
    public async Task Startup_prunes_history()
    {
        var history = new RecordingHistory(); var vm = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history, timeProvider: new FixedTimeProvider(new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero))); await vm.InitializeAsync(); Assert.NotNull(history.PrunedBefore);
    }

    [Fact]
    public async Task Manual_add_reloads_jsonl_into_dashboard_and_history()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); var history = new JsonlQuotaHistory(root, new FixedTimeProvider(new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero))); var vm = new MainViewModel(new EmptyDashboardSource(), new ManualQuotaService(history), history, new FixedTimeProvider(new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero)));
        var snapshot = new QuotaSnapshot(ProviderKind.ChatGpt, "acct", "Messages", new(QuotaWindowKind.Weekly, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)), 40, 100, null, "%", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, QuotaSource.Manual, QuotaConfidence.Manual, null);
        await vm.ApplyManualSnapshotAsync(snapshot); await Task.Delay(30); var reloaded = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history, timeProvider: new FixedTimeProvider(new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero))); await reloaded.InitializeAsync(); Assert.Contains(reloaded.Cards, c => c.Account == "acct"); Assert.Contains(reloaded.History.Entries, e => e.Account == "acct");
    }

    private sealed class FakeWindowsCredentialApi : IWindowsCredentialApi
    {
        public string? Target; public string? WrittenSecret; public string Log = ""; public string? ReadValue; public WindowsCredentialError ReadError; public bool FreeCalled; public bool DeleteCalled; public bool ZeroingContractObserved; public uint Type; public uint Persist;
        public int Write(string target, ReadOnlySpan<byte> blob, uint type, uint persist) { Target = target; Type = type; Persist = persist; WrittenSecret = MemoryMarshal.Cast<byte, char>(blob).ToString().TrimEnd('\0'); return 0; }
        public WindowsCredentialReadResult Read(string target) { Target = target; return ReadError != 0 ? new(null, ReadError) : new(ReadValue is null ? null : MemoryMarshal.AsBytes(ReadValue.AsSpan()).ToArray(), 0); }
        public void Free() => FreeCalled = true;
        public int Delete(string target) { Target = target; DeleteCalled = true; return 0; }
        public void ObserveZeroing() => ZeroingContractObserved = true;
        public void ZeroNativeBlob() => ZeroingContractObserved = true;
    }
    private sealed class FailingCredentialStore : ICredentialStore
    {
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.Unavailable;
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    private sealed class RecordingHistory : IQuotaHistory
    {
        public DateOnly? PrunedBefore; public ValueTask AppendAsync(IReadOnlyList<QuotaSnapshot> s, CancellationToken c) => ValueTask.CompletedTask; public ValueTask<IReadOnlyList<QuotaSnapshot>> ReadAsync(DateOnly d, CancellationToken c) => ValueTask.FromResult<IReadOnlyList<QuotaSnapshot>>([]); public ValueTask PruneAsync(DateOnly b, CancellationToken c) { PrunedBefore = b; return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyList<QuotaHistoryEntry>> ReadEventsAsync(DateOnly d, CancellationToken c) => ValueTask.FromResult<IReadOnlyList<QuotaHistoryEntry>>([]); public ValueTask DeleteEventAsync(Guid id, CancellationToken c) => ValueTask.CompletedTask; public ValueTask DeleteAsync(DateOnly? d, CancellationToken c) => ValueTask.CompletedTask; public ValueTask ExportJsonAsync(Stream o, CancellationToken c) => ValueTask.CompletedTask; public ValueTask ExportCsvAsync(Stream o, CancellationToken c) => ValueTask.CompletedTask;
    }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class RecordingGitHubFactory : IGitHubClientFactory { public string? LastClientId; public GitHubDeviceFlowClient Create(string clientId) { LastClientId = clientId; return null!; } }
}
