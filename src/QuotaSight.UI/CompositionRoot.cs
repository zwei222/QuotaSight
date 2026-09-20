using System.Net.Http.Headers;
using System.Text.Json;
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

public sealed class DynamicCopilotAdapter(HttpClient client, ICredentialStore credentials, Func<string> organization) : IActiveAccountQuotaAdapter
{
    public ProviderKind Provider => ProviderKind.Copilot;

    public async ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchAsync(string account, CancellationToken cancellationToken) =>
        (await FetchActiveAsync(account, cancellationToken)).Result;

    public ValueTask<ActiveAccountFetchResult> FetchWithAccountAsync(string account, CancellationToken cancellationToken) =>
        FetchActiveAsync(account, cancellationToken);

    public ValueTask<ActiveAccountFetchResult> FetchWithActiveAccountAsync(string account, CancellationToken cancellationToken) =>
        FetchActiveAsync(account, cancellationToken);

    private async ValueTask<ActiveAccountFetchResult> FetchActiveAsync(string account, CancellationToken cancellationToken)
    {
        var configuredOrganization = (organization() ?? string.Empty).Trim();
        if (!GitHubOrganizationSlug.IsValid(configuredOrganization))
            return new(new(FetchStatus.ConfigurationError, Error: "GitHub organization is not configured."));

        var token = await credentials.GetAsync("github", cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) return new(new(FetchStatus.Unauthorized));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("QuotaSight");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(MapUserStatus(response));

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var login = document.RootElement.TryGetProperty("login", out var value) ? value.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(login)) return new(new(FetchStatus.Unsupported, Error: "GitHub user response did not include a login."));

            var activeAccount = $"{configuredOrganization}/{login}";
            var billing = await new CopilotBillingUsageAdapter(client, credentials, configuredOrganization, login).FetchWithTokenAsync(account, token, cancellationToken);
            return new(billing, activeAccount);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(new(FetchStatus.TransientFailure, Error: "GitHub user request timed out."));
        }
        catch (JsonException) { return new(new(FetchStatus.TransientFailure, Error: "GitHub user response was invalid.")); }
        catch (HttpRequestException) { return new(new(FetchStatus.TransientFailure, Error: "GitHub user request failed.")); }
    }

    private static FetchResult<IReadOnlyList<QuotaSnapshot>> MapUserStatus(HttpResponseMessage response) =>
        response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => new(FetchStatus.Unauthorized),
            System.Net.HttpStatusCode.Forbidden when response.Headers.Contains("Retry-After") || response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.FirstOrDefault() == "0" => new(FetchStatus.RateLimited),
            System.Net.HttpStatusCode.Forbidden => new(FetchStatus.Forbidden),
            _ when (int)response.StatusCode == 429 => new(FetchStatus.RateLimited),
            _ when (int)response.StatusCode >= 500 => new(FetchStatus.TransientFailure),
            _ => new(FetchStatus.Unsupported)
        };

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
        var settings = new AppSettingsStore(dataDirectory);
        var credentials = CreateCredentialStore();
        var source = new PersistentDashboardSource(history);
        var codex = new CodexSessionManager(new CodexOAuthClient(client, credentials), client);
        var copilot = new DynamicCopilotAdapter(client, credentials, () => settings.Load().GithubOrganization);
        var application = new CredentialBackedQuotaApplication(key => new OpenCodeGoAdapter(client, key), credentials, codex, copilotAdapter: copilot);
        return new MainViewModel(source, new ManualQuotaService(history), history, settingsStore: settings, quotaApplication: application);
    }

    public static (MainViewModel ViewModel, IProviderUiService Provider) CreateMainWindowParts(string? dataDirectory = null, HttpClient? httpClient = null, ICredentialStore? credentialStore = null)
    {
        var client = httpClient ?? new HttpClient();
        var history = new JsonlQuotaHistory(dataDirectory ?? PlatformPaths.DataDirectory());
        var settings = new AppSettingsStore(dataDirectory);
        var credentials = credentialStore ?? CreateCredentialStore();
        var codex = new CodexSessionManager(new CodexOAuthClient(client, credentials), client);
        var copilot = new DynamicCopilotAdapter(client, credentials, () => settings.Load().GithubOrganization);
        var application = new CredentialBackedQuotaApplication(key => new OpenCodeGoAdapter(client, key), credentials, codex, copilotAdapter: copilot);
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
