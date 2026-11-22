using System.IO;
using QuotaSight.Infrastructure;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Settings compatibility across the quota-UI removal: a settings file written by an
/// older build (carrying quota-era fields such as thresholds, the GitHub integration
/// and Copilot reference values, plus unknown future keys) must load without breaking,
/// must keep every TokScale-related value the new UI still uses, and a save from the
/// new build must keep the legacy fields intact.
/// </summary>
public sealed class SettingsCompatibilityTests
{
    private static readonly string LegacySettingsJson = """
        {
          "Theme": "Dark",
          "Language": "Japanese",
          "RefreshMinutes": 12,
          "OverallThreshold": 65,
          "NotificationsEnabled": false,
          "GithubOAuthClientId": "legacy-client-id",
          "ProviderThresholds": { "ChatGpt": 90, "Claude": 75 },
          "ResidentMode": true,
          "GithubOrganization": "acme-engineering",
          "CopilotReferenceCredits": 1900,
          "UsageApiEndpoint": "https://usage.example.test",
          "TokScaleClients": ["antigravity-cli", "grok"],
          "TokScaleProviders": ["openai", "deepseek"],
          "SomeFutureField": "ignored-by-newer-builds"
        }
        """;

    [Fact]
    public void ALegacySettingsFileLoadsAndKeepsTheTokScaleSettings()
    {
        using var store = TemporarySettingsStore(LegacySettingsJson);

        var loaded = store.Load();

        // The TokScale values the new UI still uses survive the load; the unknown
        // future key and the quota-era values are ignored, not fatal.
        Assert.Equal(["antigravity-cli", "grok"], loaded.TokScaleClients ?? []);
        Assert.Equal(["openai", "deepseek"], loaded.TokScaleProviders ?? []);
        Assert.Equal("https://usage.example.test", loaded.UsageApiEndpoint);
        Assert.Equal("Dark", loaded.Theme);
        Assert.Equal("Japanese", loaded.Language);
        Assert.Equal(12, loaded.RefreshMinutes);
        Assert.True(loaded.ResidentMode);
    }

    [Fact]
    public async Task TheViewModelKeepsTheTokScaleSettingsAndPersistsThemBack()
    {
        using var store = TemporarySettingsStore(LegacySettingsJson);

        using var viewModel = new MainViewModel(
            timeProvider: TimeProvider.System,
            settingsStore: store.Store,
            usageReadPort: null,
            uiDispatcher: new ImmediateUiDispatcher());
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        // The imported tracked provider list drives the observed-usage section.
        Assert.Equal(["openai", "deepseek"], viewModel.Settings.TokScaleProviders);
        Assert.Equal(["antigravity-cli", "grok"], viewModel.Settings.TokScaleClients);
        Assert.Equal("https://usage.example.test", viewModel.Settings.UsageApiEndpoint);
        Assert.True(viewModel.IsTokScaleSectionVisible);

        // Adding another provider persists next to the imported lists, and the save
        // keeps the quota-era fields intact so an older build can still read the file.
        await viewModel.AddTokScaleProviderAsync("nous", TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["openai", "deepseek", "nous"], saved.TokScaleProviders ?? []);
        Assert.Equal(["antigravity-cli", "grok"], saved.TokScaleClients ?? []);
        Assert.Equal("https://usage.example.test", saved.UsageApiEndpoint);
        Assert.Equal(65m, saved.OverallThreshold);
        Assert.Equal("legacy-client-id", saved.GithubOAuthClientId);
        Assert.Equal("acme-engineering", saved.GithubOrganization);
        Assert.Equal(1900m, saved.CopilotReferenceCredits);
        Assert.Equal(90m, saved.ProviderThresholds!["ChatGpt"]);
        Assert.Equal(75m, saved.ProviderThresholds!["Claude"]);
        Assert.False(saved.NotificationsEnabled);
        Assert.True(saved.ResidentMode);
        Assert.Equal("Dark", saved.Theme);
        Assert.Equal("Japanese", saved.Language);
        Assert.Equal(12, saved.RefreshMinutes);
    }

    /// <summary>A throwaway settings store pre-seeded with the given file text.</summary>
    private static TempSettingsStore TemporarySettingsStore(string json)
    {
        var store = new TempSettingsStore();
        Directory.CreateDirectory(store.Root);
        File.WriteAllText(store.Path, json);
        return store;
    }

    private sealed class TempSettingsStore : IDisposable
    {
        public TempSettingsStore()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quotasight-tests", Guid.NewGuid().ToString("N"));
            Store = new AppSettingsStore(Root);
        }

        public string Root { get; }
        public AppSettingsStore Store { get; }
        public string Path => Store.Path;

        public AppSettingsDto Load() => Store.Load();
        public ValueTask<AppSettingsDto> LoadAsync(CancellationToken token = default) => Store.LoadAsync(token);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (DirectoryNotFoundException) { }
        }
    }
}
