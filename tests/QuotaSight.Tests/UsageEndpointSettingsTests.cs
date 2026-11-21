using QuotaSight.Infrastructure;

namespace QuotaSight.Tests;

/// <summary>
/// The Usage API endpoint is an ordinary, non-secret setting. It must round-trip
/// through settings.json and must not break a settings file written before it existed.
/// </summary>
public sealed class UsageEndpointSettingsTests
{
    [Fact]
    public async Task SettingsWrittenWithoutTheUsageEndpointStillLoadAndKeepTheirValues()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new AppSettingsStore(root);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            store.Path,
            "{\"Theme\":\"Dark\",\"Language\":\"Japanese\",\"RefreshMinutes\":5,\"OverallThreshold\":70,\"NotificationsEnabled\":false,\"GithubOAuthClientId\":\"existing-client\",\"GithubOrganization\":\"acme\",\"ResidentMode\":true}");

        var loaded = await store.LoadAsync();

        Assert.Equal(string.Empty, loaded.UsageApiEndpoint);
        Assert.Equal("Dark", loaded.Theme);
        Assert.Equal("Japanese", loaded.Language);
        Assert.Equal(5, loaded.RefreshMinutes);
        Assert.Equal(70m, loaded.OverallThreshold);
        Assert.False(loaded.NotificationsEnabled);
        Assert.Equal("existing-client", loaded.GithubOAuthClientId);
        Assert.Equal("acme", loaded.GithubOrganization);
        Assert.True(loaded.ResidentMode);
    }

    [Fact]
    public async Task UsageEndpointRoundTripsThroughTheSettingsFile()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new AppSettingsStore(root);

        await store.SaveAsync(new AppSettingsDto(Theme: "Light", GithubOAuthClientId: "existing-client", UsageApiEndpoint: "https://usage.example.test"));
        var loaded = await store.LoadAsync();

        Assert.Equal("https://usage.example.test", loaded.UsageApiEndpoint);
        Assert.Equal("Light", loaded.Theme);
        Assert.Equal("existing-client", loaded.GithubOAuthClientId);
        var json = await File.ReadAllTextAsync(store.Path);
        Assert.Contains("\"UsageApiEndpoint\":\"https://usage.example.test\"", json, StringComparison.Ordinal);
    }
}
