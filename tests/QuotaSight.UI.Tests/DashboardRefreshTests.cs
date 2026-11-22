using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class DashboardRefreshTests
{
    private sealed class ScriptedQuotaPort : IQuotaReadPort
    {
        public int Calls;
        public bool Fail;
        public TaskCompletionSource<LatestQuota>? Gate;
        public Task<LatestQuota> ReadLatestAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) throw new HttpRequestException("down");
            return Gate?.Task ?? Task.FromResult(new LatestQuota(DateTimeOffset.UnixEpoch, []));
        }
    }

    [Fact]
    public async Task DashboardRefreshReadsQuotaEachTime()
    {
        var port = new ScriptedQuotaPort();
        using var vm = new MainViewModel(quotaReadPort: port);
        await vm.RefreshDashboardAsync();
        await vm.RefreshDashboardAsync();
        Assert.Equal(2, port.Calls);
        Assert.Equal("Ready", vm.QuotaReadStatus);
    }

    [Fact]
    public async Task QuotaFailureDoesNotThrowAndKeepsPreviousValue()
    {
        var port = new ScriptedQuotaPort();
        using var vm = new MainViewModel(quotaReadPort: port);
        await vm.RefreshDashboardAsync();
        var before = vm.LatestQuota;
        port.Fail = true;
        await vm.RefreshDashboardAsync();
        Assert.Equal("Error", vm.QuotaReadStatus);
        Assert.Same(before, vm.LatestQuota);
    }

    [Fact]
    public async Task OverlappingRefreshesShareOneRead()
    {
        var port = new ScriptedQuotaPort { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var vm = new MainViewModel(quotaReadPort: port);
        var first = vm.RefreshDashboardAsync();
        var second = vm.RefreshDashboardAsync();
        port.Gate.SetResult(new LatestQuota(DateTimeOffset.UnixEpoch, []));
        await Task.WhenAll(first, second);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task NoQuotaPortIsANoOp()
    {
        using var vm = new MainViewModel();
        await vm.RefreshDashboardAsync();
        Assert.NotEqual("Loading", vm.QuotaReadStatus);
    }
}
