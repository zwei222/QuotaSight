using QuotaSight.Core;

namespace QuotaSight.Application;

public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    public CredentialStoreAvailability Availability => CredentialStoreAvailability.SessionOnly;
    public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult(values.GetValueOrDefault(account));
    public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) { values[account] = secret; return ValueTask.CompletedTask; }
    public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) { values.Remove(account); return ValueTask.CompletedTask; }
}

public static class SecureCredentialStoreFactory
{
    public static ICredentialStore Create() => OperatingSystem.IsWindows()
        ? new WindowsCredentialStore(new WindowsCredentialApi())
        : new UnavailableCredentialStore();

    private sealed class UnavailableCredentialStore : ICredentialStore
    {
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.Unavailable;
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => ValueTask.FromException(new PlatformNotSupportedException("No safe AOT credential backend is available."));
        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}

public sealed class NotificationDeduplicator(INotificationSink sink)
{
    private static readonly TimeSpan FailedDeliveryRetryInterval = TimeSpan.FromMinutes(5);
    private readonly Dictionary<NotificationKey, NotificationState> states = [];

    public async ValueTask ConsiderAsync(QuotaSnapshot snapshot, decimal threshold, DateTimeOffset now, CancellationToken cancellationToken, bool notificationsEnabled = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.IsStale(now) || snapshot.EffectivePercent is not { } percent)
        {
            return;
        }

        var key = new NotificationKey(snapshot.Provider, snapshot.Account, snapshot.Metric, snapshot.Window.Kind, threshold);
        var cycleBoundary = snapshot.Window.ResetAt ?? (snapshot.Window.End != default ? snapshot.Window.End : snapshot.Window.Start);
        var newCycle = false;
        if (!states.TryGetValue(key, out var state))
        {
            state = new NotificationState(cycleBoundary);
            states.Add(key, state);
        }
        else if (cycleBoundary != state.CycleBoundary)
        {
            if (state.CycleBoundary <= now && cycleBoundary > state.CycleBoundary)
            {
                state.Delivered = false;
                state.LastFailedAttempt = null;
                newCycle = true;
            }

            state.CycleBoundary = cycleBoundary;
        }

        if (state.PreviousPercent is { } oldPercent && oldPercent >= threshold && percent < threshold)
        {
            state.Delivered = false;
            state.LastFailedAttempt = null;
        }

        var crossedThreshold = state.PreviousPercent is null || state.PreviousPercent < threshold;
        var retryReady = state.LastFailedAttempt is not { } failedAt || now - failedAt >= FailedDeliveryRetryInterval;
        if (notificationsEnabled && percent >= threshold && !state.Delivered && retryReady && (crossedThreshold || state.LastFailedAttempt is not null || newCycle))
        {
            var accepted = await sink.NotifyAsync(snapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (accepted)
            {
                state.Delivered = true;
                state.LastFailedAttempt = null;
            }
            else
            {
                state.LastFailedAttempt = now;
            }
        }

        state.PreviousPercent = percent;
    }

    private sealed class NotificationState(DateTimeOffset cycleBoundary)
    {
        public DateTimeOffset CycleBoundary { get; set; } = cycleBoundary;
        public decimal? PreviousPercent { get; set; }
        public bool Delivered { get; set; }
        public DateTimeOffset? LastFailedAttempt { get; set; }
    }
}
