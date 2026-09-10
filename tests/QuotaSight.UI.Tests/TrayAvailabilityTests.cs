using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class TrayAvailabilityTests
{
    [Fact]
    public void Status_notifier_requires_both_watcher_owner_and_registered_host()
    {
        Assert.True(LinuxStatusNotifierAvailability.ResolveDetection(true, true));
        Assert.False(LinuxStatusNotifierAvailability.ResolveDetection(true, false));
        Assert.False(LinuxStatusNotifierAvailability.ResolveDetection(false, true));
    }

    [Fact]
    public void Tray_icon_asset_is_a_32_pixel_png()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/QuotaSight.UI/Assets/quotasight-tray.png"));
        using var stream = File.OpenRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        Assert.Equal(32, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(32, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void Test_override_is_used_only_when_present(bool? overrideValue, bool detected, bool expected)
    {
        Assert.Equal(expected, LinuxStatusNotifierAvailability.ResolveOverride(overrideValue, detected));
    }

    [Fact]
    public void Null_override_preserves_detected_result()
    {
        Assert.False(LinuxStatusNotifierAvailability.ResolveOverride(null, false));
        Assert.True(LinuxStatusNotifierAvailability.ResolveOverride(null, true));
    }

    [Fact]
    public async Task Monitor_reports_owner_loss_and_cancels_cleanly()
    {
        var values = new Queue<bool>([true, false]);
        var changes = new List<bool>();
        using var cancel = new CancellationTokenSource();
        var monitor = new LinuxStatusNotifierMonitor(() => Task.FromResult(values.Dequeue()), changes.Add, TimeSpan.FromMilliseconds(1));

        var running = monitor.RunAsync(cancel.Token);
        while (changes.Count < 2) await Task.Delay(1);
        cancel.Cancel();
        await running;

        Assert.Equal([true, false], changes);
    }

    [Fact]
    public async Task Monitor_treats_host_probe_failure_as_unavailable()
    {
        var changes = new List<bool>();
        using var cancel = new CancellationTokenSource();
        var monitor = new LinuxStatusNotifierMonitor(() => throw new InvalidOperationException(), changes.Add, TimeSpan.FromMilliseconds(1));

        var running = monitor.RunAsync(cancel.Token);
        while (changes.Count == 0) await Task.Delay(1);
        cancel.Cancel();
        await running;

        Assert.Equal([false], changes);
    }
}
