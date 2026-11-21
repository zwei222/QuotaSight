using System.Net;
using System.Text;
using QuotaSight.Application;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Contract tests for the non-visual part of the Usage connection settings: endpoint
/// validation, credential-store persistence, read-port replacement and the view-model
/// surface a settings page binds to. Every test uses a fake credential store and a
/// fake HTTP handler, so no real credential store and no network is involved.
/// </summary>
public sealed class UsageConnectionSettingsTests
{
    private const string Endpoint = "https://usage.example.test";
    private const string ClientId = "synthetic-usage-id";
    private const string ClientSecret = "synthetic-usage-secret";
    private const string OtherEndpoint = "https://other-usage.example.test";

    [Theory]
    [InlineData("https://usage.example.test", "https://usage.example.test")]
    [InlineData("https://usage.example.test/", "https://usage.example.test")]
    [InlineData("  https://usage.example.test/  ", "https://usage.example.test")]
    [InlineData("HTTPS://Usage.Example.Test/", "https://usage.example.test")]
    [InlineData("https://usage.example.test:8443/", "https://usage.example.test:8443")]
    public void ValidEndpointsNormalizeToTheirOrigin(string raw, string expected)
    {
        Assert.True(UsageEndpoint.TryNormalize(raw, out var endpoint, out var error));
        Assert.Equal(UsageConnectionError.None, error);
        Assert.Equal(expected, endpoint);
    }

    [Theory]
    [InlineData("", UsageConnectionError.Missing)]
    [InlineData("   ", UsageConnectionError.Missing)]
    [InlineData(null, UsageConnectionError.Missing)]
    [InlineData("usage.example.test", UsageConnectionError.Malformed)]
    // A scheme-relative reference parses as a file URI, so it is refused as "not https".
    [InlineData("//usage.example.test", UsageConnectionError.NotHttps)]
    [InlineData("http://usage.example.test", UsageConnectionError.NotHttps)]
    [InlineData("http://127.0.0.1/", UsageConnectionError.NotHttps)]
    [InlineData("http://localhost/", UsageConnectionError.NotHttps)]
    [InlineData("http://[::1]/", UsageConnectionError.NotHttps)]
    [InlineData("https://synthetic-user:synthetic-password@usage.example.test/", UsageConnectionError.UserInformation)]
    [InlineData("https://usage.example.test/?x=synthetic-query-secret", UsageConnectionError.Query)]
    [InlineData("https://usage.example.test/#synthetic-fragment", UsageConnectionError.Fragment)]
    [InlineData("https://usage.example.test/v1", UsageConnectionError.Path)]
    [InlineData("https://usage.example.test/v1/", UsageConnectionError.Path)]
    [InlineData("https:///usage", UsageConnectionError.Malformed)]
    [InlineData("https://", UsageConnectionError.Malformed)]
    [InlineData("https://:8443/", UsageConnectionError.Malformed)]
    [InlineData("https://usage.example.test/a b", UsageConnectionError.InvalidCharacters)]
    [InlineData("https://usage.example.test/a\u0001b", UsageConnectionError.InvalidCharacters)]
    public void InvalidEndpointsAreRejectedWithAClassifiedReason(string? raw, UsageConnectionError expected)
    {
        Assert.False(UsageEndpoint.TryNormalize(raw, out var endpoint, out var error));
        Assert.Equal(expected, error);
        Assert.Equal(string.Empty, endpoint);
    }

    [Fact]
    public void TooLongEndpointsAreRejectedBeforeTheyAreParsed()
    {
        var raw = "https://usage.example.test/" + new string('a', UsageEndpoint.MaxLength);

        Assert.False(UsageEndpoint.TryNormalize(raw, out _, out var error));

        Assert.Equal(UsageConnectionError.TooLong, error);
        Assert.Equal(256, UsageEndpoint.MaxLength);
    }

    [Fact]
    public async Task SaveStoresTheEndpointInSettingsAndThePairOnlyInTheCredentialStore()
    {
        using var harness = Harness.Create();

        var result = await harness.ViewModel.SaveUsageConnectionAsync(Endpoint + "/", ClientId, ClientSecret, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(UsageConnectionError.None, result.Error);
        Assert.Equal(UsageConnectionState.Configured, result.State);
        Assert.Equal(UsageConnectionState.Configured, harness.ViewModel.UsageConnectionState);
        Assert.Equal(Endpoint, harness.ViewModel.UsageApiEndpointText);
        Assert.True(harness.ViewModel.HasUsageCredential);
        Assert.True(harness.ViewModel.IsUsageConfigured);
        Assert.Equal(ClientId, harness.Store.Peek(StoreUsageCredentialProvider.ClientIdAccount));
        Assert.Equal(ClientSecret, harness.Store.Peek(StoreUsageCredentialProvider.ClientSecretAccount));
        Assert.Equal(Endpoint, harness.Settings.Load().UsageApiEndpoint);
        Assert.Equal(Endpoint, harness.ViewModel.CurrentSettingsForTests.UsageApiEndpoint);
    }

    [Fact]
    public async Task SavedSettingsFileCarriesNoCredentialAndNoSecretReachesAnyFileOrText()
    {
        using var harness = Harness.Create();

        await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);
        var settingsJson = await File.ReadAllTextAsync(harness.Settings.Path);

        Assert.Contains("\"UsageApiEndpoint\":\"" + Endpoint + "\"", settingsJson, StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(harness.Root, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(ClientId, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ClientSecret, text, StringComparison.Ordinal);
        }

        var viewModelText = harness.ViewModel.ToString() ?? string.Empty;
        Assert.DoesNotContain(ClientId, viewModelText, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientSecret, viewModelText, StringComparison.Ordinal);
        foreach (var state in Enum.GetValues<UsageConnectionState>())
        {
            var copy = new UiCopy(UiLanguage.English);
            Assert.DoesNotContain(ClientId, copy.UsageConnectionStatus(state), StringComparison.Ordinal);
            Assert.DoesNotContain(ClientSecret, copy.UsageConnectionStatus(state), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StatusTextDescribesTheStateWithoutEchoingTheEndpoint()
    {
        using var harness = Harness.Create();

        await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);

        Assert.Equal(
            new UiCopy(UiLanguage.English).UsageConnectionStatus(UsageConnectionState.Configured),
            harness.ViewModel.UsageConnectionStatusText);
        Assert.DoesNotContain("usage.example.test", harness.ViewModel.UsageConnectionStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidEndpointDoesNotDisturbAWorkingConnection()
    {
        using var harness = Harness.Create();
        await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);
        harness.Store.Writes = 0;

        var result = await harness.ViewModel.SaveUsageConnectionAsync("http://usage.example.test", "synthetic-id-two", "synthetic-secret-two", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UsageConnectionError.NotHttps, result.Error);
        Assert.Equal(UsageConnectionState.Configured, harness.ViewModel.UsageConnectionState);
        Assert.Equal(UsageConnectionError.NotHttps, harness.ViewModel.UsageConnectionError);
        Assert.Equal(Endpoint, harness.ViewModel.UsageApiEndpointText);
        Assert.Equal(ClientId, harness.Store.Peek(StoreUsageCredentialProvider.ClientIdAccount));
        Assert.Equal(0, harness.Store.Writes);
        Assert.Equal(Endpoint, harness.Settings.Load().UsageApiEndpoint);
    }

    [Fact]
    public async Task InvalidCredentialValuesAreRefusedBeforeAnythingIsWritten()
    {
        using var harness = Harness.Create();

        var result = await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, "synthetic id with spaces", ClientSecret, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UsageConnectionError.InvalidCredential, result.Error);
        Assert.Equal(0, harness.Store.Writes);
        Assert.Equal(string.Empty, harness.Settings.Load().UsageApiEndpoint);
        Assert.False(harness.ViewModel.HasUsageCredential);
    }

    [Theory]
    [InlineData(CredentialStoreAvailability.Unavailable)]
    [InlineData(CredentialStoreAvailability.Locked)]
    public async Task SaveIsRefusedWhenTheStoreIsUnavailableOrLocked(CredentialStoreAvailability availability)
    {
        using var harness = Harness.Create();
        harness.Store.Availability = availability;

        var result = await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UsageConnectionError.CredentialStoreUnavailable, result.Error);
        Assert.Equal(UsageConnectionState.StoreUnavailable, result.State);
        Assert.Equal(UsageConnectionState.StoreUnavailable, harness.ViewModel.UsageConnectionState);
        Assert.Equal(0, harness.Store.Writes);
        Assert.Equal(0, harness.Store.Count);
        Assert.Equal(string.Empty, harness.Settings.Load().UsageApiEndpoint);
        Assert.False(harness.ViewModel.IsUsageConfigured);
        Assert.Equal(
            new UiCopy(UiLanguage.English).UsageConnectionStatus(UsageConnectionState.StoreUnavailable),
            harness.ViewModel.UsageConnectionStatusText);
    }

    [Fact]
    public async Task SessionOnlyStoreSavesButReportsTheSessionOnlyState()
    {
        using var harness = Harness.Create();
        harness.Store.Availability = CredentialStoreAvailability.SessionOnly;

        var result = await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(UsageConnectionState.SessionOnly, result.State);
        Assert.Equal(UsageConnectionState.SessionOnly, harness.ViewModel.UsageConnectionState);
        Assert.True(harness.ViewModel.HasUsageCredential);
        Assert.True(harness.ViewModel.IsUsageConfigured);
        Assert.Equal(
            new UiCopy(UiLanguage.English).UsageConnectionStatus(UsageConnectionState.SessionOnly),
            harness.ViewModel.UsageConnectionStatusText);
    }

    [Fact]
    public async Task InMemoryFallbackStoreIsReportedAsSessionOnly()
    {
        using var harness = Harness.Create();
        harness.Store.Availability = CredentialStoreAvailability.InMemoryFallback;

        var result = await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);

        Assert.Equal(UsageConnectionState.SessionOnly, result.State);
        Assert.Equal(UsageConnectionState.SessionOnly, harness.ViewModel.UsageConnectionState);
    }

    [Fact]
    public async Task FailedCredentialWriteLeavesNoPartialSettingBehind()
    {
        using var harness = Harness.Create();
        harness.Store.FailWrites = true;

        var result = await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UsageConnectionError.CredentialStoreUnavailable, result.Error);
        Assert.Equal(UsageConnectionState.StoreUnavailable, result.State);
        Assert.Equal(0, harness.Store.Count);
        Assert.Equal(string.Empty, harness.Settings.Load().UsageApiEndpoint);
        Assert.False(harness.ViewModel.IsUsageConfigured);
    }

    [Fact]
    public async Task SettingsSaveFailureRemovesTheCredentialPairItJustWrote()
    {
        using var harness = Harness.Create();
        harness.ViewModel.SettingsSaveAsyncForTests = (_, _) => ValueTask.FromException(new IOException("synthetic settings failure"));

        await Assert.ThrowsAsync<IOException>(() =>
            harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None));

        Assert.Equal(0, harness.Store.Count);
        Assert.False(harness.ViewModel.HasUsageCredential);
        Assert.False(harness.ViewModel.IsUsageConfigured);
        Assert.Equal(string.Empty, harness.ViewModel.UsageApiEndpointText);
        Assert.Equal(UsageConnectionState.Unconfigured, harness.ViewModel.UsageConnectionState);
        Assert.Equal(string.Empty, harness.Settings.Load().UsageApiEndpoint);
    }

    [Fact]
    public async Task SettingsSaveFailureRestoresTheCredentialPairThatWasAlreadyStored()
    {
        using var harness = Harness.Create();
        await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);
        harness.ViewModel.SettingsSaveAsyncForTests = (_, _) => ValueTask.FromException(new IOException("synthetic settings failure"));

        await Assert.ThrowsAsync<IOException>(() =>
            harness.ViewModel.SaveUsageConnectionAsync(OtherEndpoint, "synthetic-id-two", "synthetic-secret-two", CancellationToken.None));

        Assert.Equal(ClientId, harness.Store.Peek(StoreUsageCredentialProvider.ClientIdAccount));
        Assert.Equal(ClientSecret, harness.Store.Peek(StoreUsageCredentialProvider.ClientSecretAccount));
        Assert.Equal(Endpoint, harness.ViewModel.UsageApiEndpointText);
        Assert.Equal(Endpoint, harness.Settings.Load().UsageApiEndpoint);
        Assert.True(harness.ViewModel.IsUsageConfigured);
    }

    [Fact]
    public async Task ClearRemovesBothAccountsAndReturnsThePortToUnconfigured()
    {
        using var harness = Harness.Create();
        await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);

        await harness.ViewModel.ClearUsageConnectionAsync(CancellationToken.None);

        Assert.Equal(0, harness.Store.Count);
        Assert.Equal(string.Empty, harness.ViewModel.UsageApiEndpointText);
        Assert.False(harness.ViewModel.HasUsageCredential);
        Assert.False(harness.ViewModel.IsUsageConfigured);
        Assert.Equal(UsageConnectionState.Unconfigured, harness.ViewModel.UsageConnectionState);
        Assert.Equal(UsageConnectionError.None, harness.ViewModel.UsageConnectionError);
        Assert.Equal(string.Empty, harness.Settings.Load().UsageApiEndpoint);
    }

    [Fact]
    public async Task StartupConfiguresTheReadPortFromPersistedSettingsAndCredentials()
    {
        using var harness = Harness.Create();
        harness.Store.Seed(StoreUsageCredentialProvider.ClientIdAccount, ClientId);
        harness.Store.Seed(StoreUsageCredentialProvider.ClientSecretAccount, ClientSecret);
        harness.Settings.Save(new AppSettingsDto(UsageApiEndpoint: Endpoint));

        await harness.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(UsageConnectionState.Configured, harness.ViewModel.UsageConnectionState);
        Assert.True(harness.ViewModel.HasUsageCredential);
        Assert.True(harness.ViewModel.IsUsageConfigured);
        Assert.Equal(Endpoint, harness.ViewModel.UsageApiEndpointText);
    }

    [Fact]
    public async Task StartupWithoutACredentialPairStaysUnconfiguredAndReadsNothing()
    {
        using var harness = Harness.Create();
        harness.Settings.Save(new AppSettingsDto(UsageApiEndpoint: Endpoint));

        await harness.ViewModel.InitializeAsync(CancellationToken.None);
        harness.ViewModel.NavigateToUsage();
        await harness.ViewModel.UsageLoadTask;

        Assert.Equal(UsageConnectionState.Unconfigured, harness.ViewModel.UsageConnectionState);
        Assert.False(harness.ViewModel.HasUsageCredential);
        Assert.False(harness.ViewModel.IsUsageConfigured);
        Assert.Equal(new UiCopy(UiLanguage.English).UsageState("Unconfigured"), harness.ViewModel.UsageStateText);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task HalfAStoredPairIsNeverUsed()
    {
        using var harness = Harness.Create();
        harness.Store.Seed(StoreUsageCredentialProvider.ClientIdAccount, ClientId);
        harness.Settings.Save(new AppSettingsDto(UsageApiEndpoint: Endpoint));

        await harness.ViewModel.InitializeAsync(CancellationToken.None);
        harness.ViewModel.NavigateToUsage();
        await harness.ViewModel.UsageLoadTask;

        Assert.False(harness.ViewModel.HasUsageCredential);
        Assert.False(harness.ViewModel.IsUsageConfigured);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task StoredInvalidEndpointIsReportedAsInvalidAndIsNotUsed()
    {
        using var harness = Harness.Create();
        harness.Store.Seed(StoreUsageCredentialProvider.ClientIdAccount, ClientId);
        harness.Store.Seed(StoreUsageCredentialProvider.ClientSecretAccount, ClientSecret);
        harness.Settings.Save(new AppSettingsDto(UsageApiEndpoint: "http://usage.example.test/v1"));

        await harness.ViewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(UsageConnectionState.Invalid, harness.ViewModel.UsageConnectionState);
        Assert.Equal(UsageConnectionError.NotHttps, harness.ViewModel.UsageConnectionError);
        Assert.False(harness.ViewModel.IsUsageConfigured);
        Assert.Equal(
            new UiCopy(UiLanguage.English).UsageConnectionStatus(UsageConnectionState.Invalid),
            harness.ViewModel.UsageConnectionStatusText);
    }

    [Fact]
    public async Task ConfiguredPortSendsTheAccessServiceTokenPerRequestWithoutAuthorizationOrCookie()
    {
        using var harness = Harness.Create();
        await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);

        harness.ViewModel.NavigateToUsage();
        await harness.ViewModel.UsageLoadTask;

        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.StartsWith(Endpoint + "/v1/usage/daily?from=", request.Uri, StringComparison.Ordinal);
        Assert.Equal([ClientId], request.AccessClientIds);
        Assert.Equal([ClientSecret], request.AccessClientSecrets);
        Assert.False(request.HasAuthorization);
        Assert.False(request.HasCookie);
    }

    [Fact]
    public async Task SavingAConnectionWhileTheUsagePageIsOpenReloadsIt()
    {
        using var harness = Harness.Create();
        harness.ViewModel.NavigateToUsage();
        await harness.ViewModel.UsageLoadTask;
        Assert.Empty(harness.Handler.Requests);

        await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);
        await harness.ViewModel.UsageLoadTask;

        var request = Assert.Single(harness.Handler.Requests);
        Assert.StartsWith(Endpoint + "/v1/usage/daily?from=", request.Uri, StringComparison.Ordinal);
        Assert.Equal(new UiCopy(UiLanguage.English).UsageState("Empty"), harness.ViewModel.UsageStateText);
    }

    [Fact]
    public async Task SavingAgainReplacesThePortOrigin()
    {
        using var harness = Harness.Create();
        await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);
        harness.ViewModel.NavigateToUsage();
        await harness.ViewModel.UsageLoadTask;

        await harness.ViewModel.SaveUsageConnectionAsync(OtherEndpoint, ClientId, ClientSecret, CancellationToken.None);
        await harness.ViewModel.UsageLoadTask;

        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.StartsWith(OtherEndpoint + "/v1/usage/daily", harness.Handler.Requests[1].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingAnUnrelatedSettingKeepsTheUsageEndpoint()
    {
        using var harness = Harness.Create();
        await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);

        await harness.ViewModel.SetThresholdAsync(70m, CancellationToken.None);
        await harness.ViewModel.SetThemeAsync(ThemeMode.Dark, CancellationToken.None);

        var reloaded = harness.Settings.Load();
        Assert.Equal(Endpoint, reloaded.UsageApiEndpoint);
        Assert.Equal(70m, reloaded.OverallThreshold);
        Assert.Equal(Endpoint, harness.ViewModel.CurrentSettingsForTests.UsageApiEndpoint);
    }

    [Fact]
    public async Task LanguageSwitchRelocalizesTheConnectionStatus()
    {
        using var harness = Harness.Create();
        harness.Store.Availability = CredentialStoreAvailability.SessionOnly;
        await harness.ViewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);
        var raised = new List<string?>();
        harness.ViewModel.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        harness.ViewModel.Language = UiLanguage.Japanese;

        Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageConnectionStatus(UsageConnectionState.SessionOnly), harness.ViewModel.UsageConnectionStatusText);
        Assert.Contains(nameof(MainViewModel.UsageConnectionStatusText), raised);
        Assert.Contains(nameof(MainViewModel.UsageConnectionErrorText), raised);
    }

    [Fact]
    public async Task ConnectionErrorTextIsFixedCopyAndNeverEchoesTheEndpoint()
    {
        using var harness = Harness.Create();

        var result = await harness.ViewModel.SaveUsageConnectionAsync("https://synthetic-user:synthetic-password@usage.example.test", ClientId, ClientSecret, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UsageConnectionError.UserInformation, harness.ViewModel.UsageConnectionError);
        var text = harness.ViewModel.UsageConnectionErrorText;
        Assert.Equal(new UiCopy(UiLanguage.English).UsageConnectionErrorText(UsageConnectionError.UserInformation), text);
        Assert.DoesNotContain("synthetic-user", text, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-password", text, StringComparison.Ordinal);
        Assert.DoesNotContain("usage.example.test", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnconfiguredReconfigurablePortFailsTheRead()
    {
        using var port = new ReconfigurableUsageReadPort();

        Assert.False(port.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            port.ReadDailyAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), null, CancellationToken.None));
    }

    [Fact]
    public async Task FirstSaveSucceedsAgainstASecretToolLikeStoreWithNoStoredItem()
    {
        var root = Path.Combine(Path.GetTempPath(), "quotasight-usage-secret-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new LinuxSecretToolCredentialStore(new SecretToolLikeRunner());
        var viewModel = new MainViewModel(
            new EmptyDashboardSource(),
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero)),
            settingsStore: new AppSettingsStore(Path.Combine(root, "config")),
            usageCredentialStore: store,
            usageHttpClientFactory: () => new TrackedClient(new RecordingHandler()));
        try
        {
            Assert.Equal(CredentialStoreAvailability.SecureStore, store.Availability);

            var result = await viewModel.SaveUsageConnectionAsync(Endpoint, ClientId, ClientSecret, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(UsageConnectionState.Configured, viewModel.UsageConnectionState);
            Assert.Equal(UsageConnectionError.None, viewModel.UsageConnectionError);
            Assert.True(viewModel.HasUsageCredential);
            Assert.Equal(CredentialStoreAvailability.SecureStore, store.Availability);

            // A second save reads the stored pair first; an unregistered account must not turn
            // that read into a store failure either.
            var second = await viewModel.SaveUsageConnectionAsync(OtherEndpoint, "synthetic-id-two", "synthetic-secret-two", CancellationToken.None);
            Assert.True(second.Succeeded);
            Assert.Equal(OtherEndpoint, viewModel.UsageApiEndpointText);
        }
        finally
        {
            viewModel.Dispose();
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }

    private sealed class Harness : IDisposable
    {
        private Harness(string root, FakeCredentialStore store, RecordingHandler handler, AppSettingsStore settings, MainViewModel viewModel)
        {
            Root = root;
            Store = store;
            Handler = handler;
            Settings = settings;
            ViewModel = viewModel;
        }

        public string Root { get; }
        public FakeCredentialStore Store { get; }
        public RecordingHandler Handler { get; }
        public AppSettingsStore Settings { get; }
        public MainViewModel ViewModel { get; }

        public static Harness Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "quotasight-usage-connection-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = new FakeCredentialStore();
            var handler = new RecordingHandler();
            var settings = new AppSettingsStore(Path.Combine(root, "config"));
            var viewModel = new MainViewModel(
                new EmptyDashboardSource(),
                timeProvider: new FixedTimeProvider(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero)),
                settingsStore: settings,
                usageCredentialStore: store,
                usageHttpClientFactory: () => new TrackedClient(handler));
            return new Harness(root, store, handler, settings, viewModel);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            try { Directory.Delete(Root, true); }
            catch (IOException) { }
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public CredentialStoreAvailability Availability { get; set; } = CredentialStoreAvailability.SecureStore;
        public bool FailWrites { get; set; }
        public int Writes { get; set; }
        public int Count => values.Count;

        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(values.GetValueOrDefault(account));
        }

        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            if (FailWrites) throw new InvalidOperationException("synthetic credential store write failure");
            values[account] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.Remove(account);
            return ValueTask.CompletedTask;
        }

        public void Seed(string account, string secret) => values[account] = secret;

        public string? Peek(string account) => values.GetValueOrDefault(account);
    }

    /// <summary>
    /// Mirrors the observable `secret-tool` behaviour closely enough for the settings flow:
    /// an unregistered lookup exits 1 with empty stderr, a stored item is returned by a
    /// later lookup, and a missing executable is a start failure.
    /// </summary>
    private sealed class SecretToolLikeRunner : ISecretToolProcessRunner
    {
        private readonly Dictionary<string, string> items = new(StringComparer.Ordinal);

        public ValueTask<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? stdin, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = arguments[^1];
            return arguments[0] switch
            {
                "lookup" => items.TryGetValue(account, out var value)
                    ? ValueTask.FromResult(new SecretToolResult(0, value + "\n", string.Empty))
                    : ValueTask.FromResult(new SecretToolResult(1, string.Empty, string.Empty)),
                "store" => Store(account, stdin),
                "clear" => Clear(account),
                _ => ValueTask.FromResult(new SecretToolResult(2, string.Empty, "unknown subcommand")),
            };
        }

        private ValueTask<SecretToolResult> Store(string account, string? secret)
        {
            items[account] = secret ?? string.Empty;
            return ValueTask.FromResult(new SecretToolResult(0, string.Empty, string.Empty));
        }

        private ValueTask<SecretToolResult> Clear(string account)
        {
            items.Remove(account);
            return ValueTask.FromResult(new SecretToolResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<CapturedRequest> requests = [];

        public IReadOnlyList<CapturedRequest> Requests
        {
            get { lock (requests) return requests.ToList(); }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (requests)
            {
                requests.Add(new CapturedRequest(
                    request.Method.Method,
                    uri.AbsoluteUri,
                    HeaderValues(request, "CF-Access-Client-Id"),
                    HeaderValues(request, "CF-Access-Client-Secret"),
                    request.Headers.Contains("Authorization"),
                    request.Headers.Contains("Cookie")));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body(uri), Encoding.UTF8, "application/json"),
            });
        }

        /// <summary>An empty but well-formed daily page, echoing the range that was asked for.</summary>
        private static string Body(Uri uri)
        {
            var from = string.Empty;
            var to = string.Empty;
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2) continue;
                if (parts[0] == "from") from = parts[1];
                if (parts[0] == "to") to = parts[1];
            }

            return "{\"from\":\"" + from + "\",\"to\":\"" + to + "\",\"days\":[],\"totalTokens\":0,\"collectors\":[]}";
        }

        private static IReadOnlyList<string> HeaderValues(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.ToList() : [];
    }

    private sealed record CapturedRequest(
        string Method,
        string Uri,
        IReadOnlyList<string> AccessClientIds,
        IReadOnlyList<string> AccessClientSecrets,
        bool HasAuthorization,
        bool HasCookie);

    private sealed class TrackedClient(HttpMessageHandler handler) : HttpClient(handler, disposeHandler: false)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
