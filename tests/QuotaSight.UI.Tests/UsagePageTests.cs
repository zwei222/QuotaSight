using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class UsagePageTests
{
    [AvaloniaFact]
    public async Task UnconfiguredUsageHidesTheUnavailableDateRange()
    {
        using var viewModel = new MainViewModel();
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            window.UpdateLayout();
            var range = window.FindControl<TextBlock>("UsageTimeZone")!;
            Assert.False(range.IsVisible);
            Assert.Equal(string.Empty, range.Text);
            Assert.False(window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task EmptyUsageShowsStatusWithoutMisleadingZeroTotal()
    {
        using var viewModel = new MainViewModel(usageReadPort: new EmptyUsageReadPort());
        viewModel.Language = UiLanguage.Japanese;
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            window.UpdateLayout();
            Assert.Equal(new UiCopy(UiLanguage.Japanese).UsageState("Empty"), window.FindControl<TextBlock>("UsageStatus")!.Text);
            Assert.True(window.FindControl<TextBlock>("UsageTimeZone")!.IsVisible);
            Assert.False(window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
            Assert.Equal("合計トークン", window.FindControl<TextBlock>("UsageTotalLabel")!.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task UsageWindowRendersMeasuredZeroAndRefreshesOnlyUsage()
    {
        var now = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        var port = new FakeUsageReadPort();
        using var viewModel = new MainViewModel(timeProvider: new FixedTimeProvider(now), usageReadPort: port);
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            window.FindControl<Button>("UsageNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            window.UpdateLayout();
            Assert.Equal(new UiCopy(UiLanguage.English).UsageState("Ready"), viewModel.UsageStateText);
            Assert.Contains("Asia/Tokyo", window.FindControl<TextBlock>("UsageTimeZone")!.Text);
            Assert.Contains("2026-10-22", window.FindControl<TextBlock>("UsageTimeZone")!.Text);
            Assert.Equal("Usage data loaded.", window.FindControl<TextBlock>("UsageStatus")!.Text);
            Assert.Equal("0", window.FindControl<TextBlock>("UsageTotal")!.Text);
            Assert.True(window.FindControl<TextBlock>("UsageTotal")!.IsVisible);
            Assert.Equal("Total tokens", window.FindControl<TextBlock>("UsageTotalLabel")!.Text);
            Assert.Single(viewModel.UsageRows);
            Assert.Equal("collector-a", window.FindControl<ItemsControl>("UsageRows")!.ItemsSource is System.Collections.IEnumerable ? viewModel.UsageRows.Single().CollectorId : null);
            Assert.Equal(new DateOnly(2026, 10, 22), port.From);
            Assert.Equal(new DateOnly(2026, 11, 20), port.To);
            window.FindControl<Button>("UsageRefreshButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await viewModel.UsageLoadTask;
            Assert.Equal(2, port.Calls);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LeavingUsageIgnoresLateReadAndReturningStartsLatestRead()
    {
        var port = new DeferredUsageReadPort();
        using var viewModel = new MainViewModel(usageReadPort: port);
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            window.FindControl<Button>("UsageNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var pending = viewModel.UsageLoadTask;
            viewModel.Navigate(AppPage.Dashboard);
            port.Complete(new DailyUsage(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), [new(new DateOnly(2026, 10, 2), "late", 9)], 9));
            await pending;
            Assert.Empty(viewModel.UsageRows);
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            Assert.Equal("9", viewModel.UsageTotalText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NewUsagePeriodKeepsThePreviousValuesWhenTheRefreshFails()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 20, 14, 59, 0, TimeSpan.Zero));
        var port = new PeriodBoundaryUsageReadPort();
        using var viewModel = new MainViewModel(timeProvider: clock, usageReadPort: port);
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            Assert.Single(viewModel.UsageRows);
            Assert.Equal("17", viewModel.UsageTotalText);

            clock.SetUtcNow(new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.Zero));
            var refresh = viewModel.RefreshUsageAsync();
            // A new period starts, but the load keeps the rows it already has and keeps
            // the range that belongs to them: the label never describes other data.
            Assert.Equal("2026-10-22 – 2026-11-20 · Asia/Tokyo", viewModel.UsageRangeText);
            Assert.Single(viewModel.UsageRows);
            Assert.Equal("17", viewModel.UsageTotalText);
            Assert.Contains("Loading", viewModel.UsageStateText);

            port.FailSecondRead();
            await refresh;

            // The refresh failed: the previous values stay, marked stale and typed.
            Assert.Single(viewModel.UsageRows);
            Assert.Equal("17", viewModel.UsageTotalText);
            Assert.True(viewModel.IsUsageStale);
            Assert.Equal(UsageFailureKind.Transient, viewModel.UsageFailure);
            Assert.Contains("last loaded usage", viewModel.UsageStateText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Temporary failure", viewModel.UsageFailureText, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingTimeZoneDatabaseStopsInAFixedErrorState(bool missingZone)
    {
        var port = new FakeUsageReadPort();
        using var viewModel = new MainViewModel(
            usageReadPort: port,
            timeZoneResolver: _ => missingZone ? throw new TimeZoneNotFoundException() : throw new InvalidTimeZoneException());
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            window.UpdateLayout();

            Assert.Equal(new UiCopy(UiLanguage.English).UsageState("Error"), viewModel.UsageStateText);
            Assert.Equal(new UiCopy(UiLanguage.English).UsageState("Error"), window.FindControl<TextBlock>("UsageStatus")!.Text);
            Assert.Equal(string.Empty, viewModel.UsageRangeText);
            Assert.False(viewModel.IsUsageRangeVisible);
            Assert.False(viewModel.IsUsageLoading);
            Assert.Equal(0, port.Calls);
            Assert.DoesNotContain("Asia/Tokyo", viewModel.UsageStateText, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    private sealed class EmptyUsageReadPort : IUsageReadPort
    {
        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
            Task.FromResult(new DailyUsage(from, to, [], 0));

        public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
            Task.FromException<UsageBreakdown>(new InvalidOperationException("Not used by this test."));
    }

    private sealed class PeriodBoundaryUsageReadPort : IUsageReadPort
    {
        private int calls;
        private readonly TaskCompletionSource<DailyUsage> second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
                return Task.FromResult(new DailyUsage(from, to, [new(to, "old-period", 17)], 17));
            return second.Task;
        }

        public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
            Task.FromException<UsageBreakdown>(new InvalidOperationException("Not used by this test."));
        public void FailSecondRead() => second.TrySetException(new IOException("offline"));
    }

    private sealed class FakeUsageReadPort : IUsageReadPort
    {
        public int Calls { get; private set; }
        public DateOnly From { get; private set; }
        public DateOnly To { get; private set; }
        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
        {
            Calls++; From = from; To = to;
            return Task.FromResult(new DailyUsage(from, to, [new(new DateOnly(2026, 11, 19), "collector-a", 0)], 0));
        }

        public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
            Task.FromException<UsageBreakdown>(new InvalidOperationException("Not used by this test."));
    }

    private sealed class DeferredUsageReadPort : IUsageReadPort
    {
        private readonly TaskCompletionSource<DailyUsage> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1) return completion.Task;
            return Task.FromResult(new DailyUsage(from, to, [new(to, "return", 9)], 9));
        }

        public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken) =>
            Task.FromException<UsageBreakdown>(new InvalidOperationException("Not used by this test."));
        public void Complete(DailyUsage result) => completion.TrySetResult(result);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void SetUtcNow(DateTimeOffset value) => current = value;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
