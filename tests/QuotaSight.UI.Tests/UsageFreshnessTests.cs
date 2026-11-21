using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Contract tests for the non-visual part of usage freshness and typed read failures.
///
/// Two rules drive all of them:
///   * a load never erases the values already on screen — a failed refresh keeps the last
///     successful rows, total and freshness and only marks them stale;
///   * every failure is a fixed classification, so the screen can name it without ever
///     showing a status code, a URL, a header or a remote message.
/// Time is fixed through <see cref="TimeProvider"/> and the display zone is injected, so
/// the assertions do not depend on the machine's zone.
/// </summary>
public sealed class UsageFreshnessTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    private static readonly DateOnly From = new(2026, 10, 22);
    private static readonly DateOnly To = new(2026, 11, 20);

    private static DailyUsage Usage(long totalTokens, params UsageCollectorReceipt[] collectors) =>
        new(From, To, [new UsageDay(To, "collector-a", totalTokens)], totalTokens, collectors);

    private static UsageCollectorReceipt Reported(string id, string instant) =>
        new(id, 1, DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture));

    private static MainViewModel Create(
        IUsageReadPort port,
        DateTimeOffset now,
        out MutableTimeProvider clock)
    {
        clock = new MutableTimeProvider(now);
        return new MainViewModel(
            new EmptyDashboardSource(),
            timeProvider: clock,
            uiDispatcher: new ImmediateUiDispatcher(),
            usageReadPort: port,
            localTimeZone: Tokyo);
    }

    private static async Task LoadAsync(MainViewModel viewModel)
    {
        viewModel.NavigateToUsage();
        await viewModel.RefreshUsageAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FreshnessUsesTheOldestReceptionTimeInLocalTimeAndNamesAnUnreportedCollector()
    {
        var now = new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero);
        var port = new ScriptedUsageReadPort
        {
            Next = () => Usage(
                17,
                // 03:04:05.006Z is 12:04 in JST: the older of the two collectors wins.
                Reported("collector-a", "2026-11-20T03:04:05.006Z"),
                Reported("collector-b", "2026-11-20T04:30:00.000Z"),
                new UsageCollectorReceipt("collector-c", 2, null)),
        };
        using var viewModel = Create(port, now, out _);
        viewModel.Language = UiLanguage.Japanese;

        await LoadAsync(viewModel);

        Assert.Equal("最終受信: 2026-11-20 12:04（未受信の collector あり）", viewModel.UsageFreshnessText);
        Assert.True(viewModel.IsUsageFreshnessVisible);
        Assert.False(viewModel.IsUsageDelayed);
        Assert.Equal(string.Empty, viewModel.UsageDelayedText);
        Assert.Equal(UsageFailureKind.None, viewModel.UsageFailure);
        Assert.False(viewModel.IsUsageStale);

        viewModel.Language = UiLanguage.English;
        Assert.Equal(
            "Last received: 2026-11-20 12:04 (A collector has not reported yet)",
            viewModel.UsageFreshnessText);
    }

    [Fact]
    public async Task FreshnessSaysSoWhenNoCollectorHasEverReported()
    {
        var now = new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero);
        var port = new ScriptedUsageReadPort
        {
            Next = () => Usage(0, new UsageCollectorReceipt("collector-a", 1, null)),
        };
        using var viewModel = Create(port, now, out _);

        await LoadAsync(viewModel);

        Assert.Equal("A collector has not reported yet", viewModel.UsageFreshnessText);
        Assert.False(viewModel.IsUsageDelayed);
    }

    [Theory]
    [InlineData(12, false)]
    [InlineData(12.5, true)]
    [InlineData(30, true)]
    public async Task DataOlderThanTheNamedThresholdIsDelayedButStillShown(double ageHours, bool delayed)
    {
        // Collectors run every one to six hours; twelve hours is twice the slowest
        // normal interval, and it is a named value rather than a literal in the getter.
        Assert.Equal(TimeSpan.FromHours(12), MainViewModel.UsageDelayThreshold);
        var receivedAt = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        var now = receivedAt.AddHours(ageHours);
        var port = new ScriptedUsageReadPort
        {
            Next = () => Usage(17, new UsageCollectorReceipt("collector-a", 1, receivedAt)),
        };
        using var viewModel = Create(port, now, out _);

        await LoadAsync(viewModel);

        Assert.Equal(delayed, viewModel.IsUsageDelayed);
        Assert.Equal(delayed, viewModel.UsageDelayedText.Length > 0);
        // Delayed is a label, not a reason to hide the value.
        Assert.Equal("17", viewModel.UsageTotalText);
        Assert.Single(viewModel.UsageRows);
        Assert.Contains("2026-11-20 09:00", viewModel.UsageFreshnessText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRefreshKeepsThePreviousValuesAndMarksThemStale()
    {
        var receivedAt = new DateTimeOffset(2026, 11, 20, 3, 4, 5, 6, TimeSpan.Zero);
        var port = new ScriptedUsageReadPort
        {
            Next = () => Usage(17, new UsageCollectorReceipt("collector-a", 1, receivedAt)),
        };
        using var viewModel = Create(port, new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero), out var clock);
        viewModel.Language = UiLanguage.Japanese;

        await LoadAsync(viewModel);
        var freshnessBefore = viewModel.UsageFreshnessText;
        var rangeBefore = viewModel.UsageRangeText;
        Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageState("Ready"), viewModel.UsageStateText);

        // A later refresh fails: the values are kept, nothing is zeroed, nothing is hidden.
        port.Next = () => throw new UsageReadException(
            UsageReadFailure.Unauthorized,
            "The usage service rejected the credential.",
            401);
        clock.SetUtcNow(new DateTimeOffset(2026, 11, 21, 5, 0, 0, TimeSpan.Zero));
        await LoadAsync(viewModel);

        Assert.Single(viewModel.UsageRows);
        Assert.Equal("17", viewModel.UsageTotalText);
        Assert.True(viewModel.IsUsageTotalVisible);
        Assert.Equal(freshnessBefore, viewModel.UsageFreshnessText);
        Assert.Equal(rangeBefore, viewModel.UsageRangeText);
        Assert.True(viewModel.IsUsageStale);
        Assert.Equal(UsageFailureKind.Unauthorized, viewModel.UsageFailure);
        Assert.Equal("資格情報を確認してください。", viewModel.UsageFailureText);
        Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageState("Stale"), viewModel.UsageStateText);
        Assert.Equal(viewModel.UsageStateText, viewModel.UsageStatusText);
        // The retained reception time is now well past the delay threshold.
        Assert.True(viewModel.IsUsageDelayed);
    }

    [Fact]
    public async Task ARetryAfterAFailureClearsTheStaleMarker()
    {
        var port = new ScriptedUsageReadPort
        {
            Next = () => Usage(17, new UsageCollectorReceipt("collector-a", 1, new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero))),
        };
        using var viewModel = Create(port, new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero), out _);
        await LoadAsync(viewModel);

        port.Next = () => throw new UsageReadException(UsageReadFailure.Timeout, "timed out");
        await LoadAsync(viewModel);
        Assert.True(viewModel.IsUsageStale);

        port.Next = () => Usage(23, new UsageCollectorReceipt("collector-a", 1, new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero)));
        await LoadAsync(viewModel);

        Assert.False(viewModel.IsUsageStale);
        Assert.Equal(UsageFailureKind.None, viewModel.UsageFailure);
        Assert.Equal(string.Empty, viewModel.UsageFailureText);
        Assert.Equal("23", viewModel.UsageTotalText);
    }

    [Theory]
    [InlineData(UsageReadFailure.Unauthorized, UsageFailureKind.Unauthorized, "Check the credential.")]
    [InlineData(UsageReadFailure.Forbidden, UsageFailureKind.Forbidden, "Check the credential.")]
    [InlineData(UsageReadFailure.NotFound, UsageFailureKind.NotFound, "Check the server response.")]
    [InlineData(UsageReadFailure.MalformedResponse, UsageFailureKind.InvalidResponse, "Check the server response.")]
    [InlineData(UsageReadFailure.UnexpectedStatus, UsageFailureKind.InvalidResponse, "Check the server response.")]
    [InlineData(UsageReadFailure.InconsistentTotal, UsageFailureKind.InvalidResponse, "Check the server response.")]
    [InlineData(UsageReadFailure.ResponseTooLarge, UsageFailureKind.InvalidResponse, "Check the server response.")]
    [InlineData(UsageReadFailure.Timeout, UsageFailureKind.Transient, "Temporary failure. You can retry.")]
    [InlineData(UsageReadFailure.TransportFailure, UsageFailureKind.Transient, "Temporary failure. You can retry.")]
    [InlineData(UsageReadFailure.AuthUnavailable, UsageFailureKind.Transient, "Temporary failure. You can retry.")]
    [InlineData(UsageReadFailure.Transient, UsageFailureKind.Transient, "Temporary failure. You can retry.")]
    [InlineData(UsageReadFailure.RedirectRejected, UsageFailureKind.Redirected, "The usage API redirected the request. Check the endpoint.")]
    [InlineData(UsageReadFailure.CredentialUnavailable, UsageFailureKind.CredentialMissing, "No usage API credential is stored. Connect the API in settings.")]
    public async Task AnInitialFailureIsAnErrorStateThatNamesTheKindWithoutEchoingDetails(
        UsageReadFailure failure,
        UsageFailureKind expectedKind,
        string expectedText)
    {
        const string Secret = "https://usage.test/v1/usage/daily?from=2026-11-01";
        var port = new ScriptedUsageReadPort
        {
            Next = () => throw new UsageReadException(failure, Secret, 500),
        };
        using var viewModel = Create(port, new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero), out _);

        await LoadAsync(viewModel);

        Assert.Equal(expectedKind, viewModel.UsageFailure);
        Assert.Equal(expectedText, viewModel.UsageFailureText);
        Assert.DoesNotContain(Secret, viewModel.UsageFailureText, StringComparison.Ordinal);
        Assert.DoesNotContain("500", viewModel.UsageFailureText, StringComparison.Ordinal);
        Assert.Equal(new UiCopy(UiLanguage.English).UsageState("Error"), viewModel.UsageStateText);
        Assert.False(viewModel.IsUsageStale);
        Assert.Empty(viewModel.UsageRows);
        Assert.Empty(viewModel.UsageTotalText);
        Assert.Empty(viewModel.UsageFreshnessText);
        Assert.False(viewModel.IsUsageDelayed);
    }

    [Fact]
    public async Task AnUnclassifiedFailureIsTreatedAsTransient()
    {
        var port = new ScriptedUsageReadPort { Next = () => throw new IOException("offline") };
        using var viewModel = Create(port, new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero), out _);

        await LoadAsync(viewModel);

        Assert.Equal(UsageFailureKind.Transient, viewModel.UsageFailure);
        Assert.Equal(new UiCopy(UiLanguage.English).UsageState("Error"), viewModel.UsageStateText);
    }

    [Fact]
    public async Task FailureAndFreshnessCopyFollowTheLanguageSwitch()
    {
        var port = new ScriptedUsageReadPort
        {
            Next = () => Usage(17, new UsageCollectorReceipt("collector-a", 1, new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero))),
        };
        using var viewModel = Create(port, new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero), out _);
        viewModel.Language = UiLanguage.Japanese;
        await LoadAsync(viewModel);

        port.Next = () => throw new UsageReadException(UsageReadFailure.Forbidden, "forbidden");
        await LoadAsync(viewModel);

        Assert.Equal("資格情報を確認してください。", viewModel.UsageFailureText);
        Assert.Equal("最終受信: 2026-11-19 09:00", viewModel.UsageFreshnessText);
        Assert.Contains("遅延データ", viewModel.UsageDelayedText, StringComparison.Ordinal);

        viewModel.Language = UiLanguage.English;

        Assert.Equal("Check the credential.", viewModel.UsageFailureText);
        Assert.Equal("Last received: 2026-11-19 09:00", viewModel.UsageFreshnessText);
        Assert.Contains("Delayed data", viewModel.UsageDelayedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnconfiguringTheConnectionDropsTheValuesInsteadOfLeavingThemStale()
    {
        var port = new ScriptedUsageReadPort
        {
            Next = () => Usage(17, new UsageCollectorReceipt("collector-a", 1, new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero))),
        };
        var connection = new ReconfigurableUsageReadPort();
        using var configured = new MainViewModel(
            new EmptyDashboardSource(),
            timeProvider: new MutableTimeProvider(new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero)),
            uiDispatcher: new ImmediateUiDispatcher(),
            usageConnectionPort: connection,
            localTimeZone: Tokyo);
        connection.Configure(port);
        await LoadAsync(configured);
        Assert.Equal("17", configured.UsageTotalText);

        connection.Reset();
        await LoadAsync(configured);

        Assert.Empty(configured.UsageRows);
        Assert.Empty(configured.UsageTotalText);
        Assert.Empty(configured.UsageFreshnessText);
        Assert.False(configured.IsUsageStale);
        Assert.Equal(new UiCopy(configured.Language).UsageState("Unconfigured"), configured.UsageStateText);
    }

    private sealed class ScriptedUsageReadPort : IUsageReadPort
    {
        /// <summary>Next answer: a result, or an exception thrown by the port.</summary>
        public Func<DailyUsage> Next { get; set; } = () => throw new InvalidOperationException("no script");

        public Task<DailyUsage> ReadDailyAsync(
            DateOnly from,
            DateOnly to,
            string? collectorId,
            CancellationToken cancellationToken)
        {
            try
            {
                return Task.FromResult(Next());
            }
            catch (Exception failure)
            {
                return Task.FromException<DailyUsage>(failure);
            }
        }

        public Task<UsageBreakdown> ReadBreakdownAsync(
            DateOnly from,
            DateOnly to,
            string? collectorId,
            CancellationToken cancellationToken) =>
            Task.FromException<UsageBreakdown>(new InvalidOperationException("Not used by this test."));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void SetUtcNow(DateTimeOffset value) => current = value;
    }
}
