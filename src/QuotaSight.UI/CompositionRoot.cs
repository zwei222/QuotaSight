using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.Infrastructure;

namespace QuotaSight.UI;

public static class OfficialUsageUrls
{
    public const string ChatGpt = "https://chatgpt.com/#settings/Subscription";
    public const string Claude = "https://claude.ai/settings/usage";
    public const string Copilot = "https://github.com/settings/copilot";
}

public static class CompositionRoot
{
    public static IProviderUiService CreateProviderFacade(HttpClient? httpClient = null, string? clientId = null)
    {
        var client = httpClient ?? new HttpClient();
        var github = new GitHubDeviceFlowClient(client, clientId ?? new JsonSettingsStore().Load().GithubOAuthClientId);
        var credentials = CreateCredentialStore();
        var codex = new CodexSessionManager(new CodexOAuthClient(client, credentials), client);
        return new UiProviderFacade(key => new OpenCodeGoAdapter(client, key), github, credentials, codexSessionManager: codex);
    }

    private static ICredentialStore CreateCredentialStore()
    {
        return new FallbackCredentialStore(CreatePrimaryCredentialStore(), new InMemoryCredentialStore());
    }
    private static ICredentialStore CreatePrimaryCredentialStore()
    {
        ICredentialStore primary = OperatingSystem.IsWindows()
            ? WindowsCredentialStore.Create()
            : OperatingSystem.IsLinux()
                ? new LinuxSecretToolCredentialStore(new SecretToolProcessRunner())
                : new InMemoryCredentialStore();
        return primary;
    }

    public static MainViewModel CreateMainViewModel(string? dataDirectory = null, HttpClient? httpClient = null)
    {
        var client = httpClient ?? new HttpClient();
        var history = new JsonlQuotaHistory(dataDirectory ?? PlatformPaths.DataDirectory());
        var settings = new AppSettingsStore();
        var credentials = CreateCredentialStore();
        var source = new PersistentDashboardSource(history);
        var codex = new CodexSessionManager(new CodexOAuthClient(client, credentials), client);
        var application = new CredentialBackedQuotaApplication(key => new OpenCodeGoAdapter(client, key), credentials, codex);
        return new MainViewModel(source, new ManualQuotaService(history), history, settingsStore: settings, quotaApplication: application);
    }

    public static (MainViewModel ViewModel, IProviderUiService Provider) CreateMainWindowParts(string? dataDirectory = null, HttpClient? httpClient = null, ICredentialStore? credentialStore = null)
    {
        var client = httpClient ?? new HttpClient();
        var history = new JsonlQuotaHistory(dataDirectory ?? PlatformPaths.DataDirectory());
        var credentials = credentialStore ?? CreateCredentialStore();
        var codex = new CodexSessionManager(new CodexOAuthClient(client, credentials), client);
        var application = new CredentialBackedQuotaApplication(key => new OpenCodeGoAdapter(client, key), credentials, codex);
        var settings = new AppSettingsStore();
        var factory = new GitHubClientFactory(client);
        var github = factory.Create(settings.Load().GithubOAuthClientId);
        var viewModel = new MainViewModel(new PersistentDashboardSource(history), new ManualQuotaService(history), history, settingsStore: settings, githubFactory: factory, quotaApplication: application);
        var provider = new UiProviderFacade(key => new OpenCodeGoAdapter(client, key), github, credentials, githubFactory: factory, codexSessionManager: codex);
        return (viewModel, provider);
    }

    public static IQuotaApplication CreateApplication(string dataDirectory, string openCodeApiKey = "", HttpClient? httpClient = null)
    {
        var client = httpClient ?? new HttpClient();
        var history = new JsonlQuotaHistory(dataDirectory);
        IQuotaAdapter[] adapters = [
            new OpenCodeGoAdapter(client, openCodeApiKey),
            new ManualMetadataAdapter(ProviderKind.ChatGpt, "metadata", OfficialUsageUrls.ChatGpt),
            new ManualMetadataAdapter(ProviderKind.Claude, "metadata", OfficialUsageUrls.Claude),
            new CopilotAdapter()];
        return new QuotaApplication(adapters, history, TimeProvider.System);
    }

}
