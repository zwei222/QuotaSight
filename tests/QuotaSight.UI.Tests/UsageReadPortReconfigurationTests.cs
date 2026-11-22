using System.Net;
using System.Text;
using QuotaSight.Application;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// The usage read port is reconfigured at runtime (save, save another endpoint,
/// remove). One long-lived client serves every configuration and a reconfigure
/// only swaps the port (origin + credential provider), so the transport is never
/// disposed while a read may still be using it.
///
/// These tests pin the absence of the old dispose/use race: the client is never
/// disposed by the adapter, a read that started under the old settings finishes
/// under them, a later read uses the new ones, and repeated configure/clear
/// cycles during a read neither throw nor disturb it. Every credential value
/// here is synthetic and no request leaves the process.
/// </summary>
public sealed class UsageReadPortReconfigurationTests
{
    private const string OriginA = "https://usage-a.example.test";
    private const string OriginB = "https://usage-b.example.test";
    private const string ClientId = "synthetic-client-id";
    private const string ClientSecret = "synthetic-client-secret";
    private const string CollectorId = "col-a1";

    private static readonly DateOnly From = new(2026, 11, 1);
    private static readonly DateOnly To = new(2026, 11, 2);

    private static HttpUsageReadPort Port(HttpClient client, string origin) =>
        new(client, new Uri(origin), StoreUsageCredentialProvider.IdentityId, new StaticCredentials());

    [Fact]
    public async Task AReadFinishesUnderTheConfigurationItStartedWithWhileThePortIsReconfigured()
    {
        var handler = new GatedHandler();
        var client = new TrackedClient(handler);
        using var port = new ReconfigurableUsageReadPort(client);
        port.Configure(Port(client, OriginA));

        var inFlight = port.ReadDailyAsync(From, To, null, CancellationToken.None);

        // The churn a save / remove / save sequence produces while the page is
        // loading: repeated replacement, a reset, then a final configuration.
        for (var round = 0; round < 8; round++)
        {
            port.Configure(Port(client, OriginA));
            port.Reset();
        }

        port.Configure(Port(client, OriginB));
        Assert.Equal("usage-b.example.test", port.Endpoint!.Host);

        handler.Release();

        var started = await inFlight.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1L, started.TotalTokens);
        Assert.Equal("usage-a.example.test", handler.Requests[0].Host);

        var afterwards = await port.ReadDailyAsync(From, To, null, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2L, afterwards.TotalTokens);
        Assert.Equal("usage-b.example.test", handler.Requests[^1].Host);
        Assert.False(client.Disposed, "The shared client must never be disposed by a reconfiguration.");
    }

    [Fact]
    public async Task ConfiguringAndResettingRepeatedlyNeverThrows()
    {
        var client = new TrackedClient(new OriginHandler());
        using var port = new ReconfigurableUsageReadPort(client);

        for (var round = 0; round < 32; round++)
        {
            port.Configure(Port(client, round % 2 == 0 ? OriginA : OriginB));
            await port.ReadDailyAsync(From, To, null, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            port.Reset();
        }

        Assert.False(port.IsConfigured);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task DisposingThePortDoesNotDisposeTheClientTheCallerSupplied()
    {
        var client = new TrackedClient(new OriginHandler());
        var port = new ReconfigurableUsageReadPort(client);
        port.Configure(Port(client, OriginA));
        port.Reset();
        port.Dispose();

        Assert.False(client.Disposed, "Client lifetime belongs to the caller, not to a configuration.");
        Assert.False(port.IsConfigured);
        // Dispose only drops the installed reference, as before: a later read is the
        // unconfigured failure, never an access to a disposed transport.
        await Assert.ThrowsAsync<InvalidOperationException>(() => port.ReadDailyAsync(From, To, null, CancellationToken.None));
    }

    [Fact]
    public async Task SavingTheConnectionTwiceOverOneSharedClientDoesNotDisposeIt()
    {
        var client = new TrackedClient(new OriginHandler());
        using var viewModel = new MainViewModel(
            new EmptyDashboardSource(),
            usageCredentialStore: new FakeCredentialStore(),
            usageHttpClientFactory: () => client);

        var first = await viewModel.SaveUsageConnectionAsync(OriginA, ClientId, ClientSecret, CancellationToken.None);
        var second = await viewModel.SaveUsageConnectionAsync(
            OriginB, "synthetic-client-id-two", "synthetic-client-secret-two", CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(OriginB, viewModel.UsageApiEndpointText);
        Assert.False(client.Disposed,
            "A second save replaces the port over the same long-lived client; it must not dispose the transport.");
    }

    private static string Body(Uri uri)
    {
        var from = string.Empty;
        var to = string.Empty;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (parts[0] == "from") from = parts[1];
            if (parts[0] == "to") to = parts[1];
        }

        var tokens = uri.Host.Contains("usage-a", StringComparison.Ordinal) ? 1
            : uri.Host.Contains("usage-b", StringComparison.Ordinal) ? 2
            : 0;

        return "{\"from\":\"" + from + "\",\"to\":\"" + to + "\",\"days\":[" +
               "{\"date\":\"2026-11-01\",\"collectorId\":\"" + CollectorId + "\",\"totalTokens\":" + tokens + "}]," +
               "\"totalTokens\":" + tokens + ",\"collectors\":[" +
               "{\"collectorId\":\"" + CollectorId + "\",\"generation\":1,\"lastReceivedAt\":null}]}";
    }

    private static HttpResponseMessage Response(Uri uri) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(Body(uri), Encoding.UTF8, "application/json"),
    };

    /// <summary>Answers immediately and keeps every request URI, in order.</summary>
    private sealed class OriginHandler : HttpMessageHandler
    {
        private readonly List<Uri> requests = [];

        public IReadOnlyList<Uri> Requests
        {
            get { lock (requests) return requests.ToList(); }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (requests) requests.Add(uri);
            return Task.FromResult(Response(uri));
        }
    }

    /// <summary>Holds every request until <see cref="Release"/>, then answers in order.</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Uri> requests = [];

        public IReadOnlyList<Uri> Requests
        {
            get { lock (requests) return requests.ToList(); }
        }

        public void Release() => gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (requests) requests.Add(uri);
            await gate.Task.ConfigureAwait(false);
            return Response(uri);
        }
    }

    private sealed class TrackedClient(HttpMessageHandler handler) : HttpClient(handler, disposeHandler: false)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StaticCredentials : IUsageCredentialProvider
    {
        public ValueTask<UsageCredential?> GetCredentialAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<UsageCredential?>(
                new UsageCredential(StoreUsageCredentialProvider.IdentityId, ClientId, ClientSecret));
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public CredentialStoreAvailability Availability => CredentialStoreAvailability.SecureStore;

        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(values.GetValueOrDefault(account));
        }

        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values[account] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.Remove(account);
            return ValueTask.CompletedTask;
        }
    }
}