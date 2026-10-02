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
    public void Brand_icon_has_required_raster_sizes_and_windows_multi_size_ico()
    {
        var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/QuotaSight.UI/Assets"));
        foreach (var size in new[] { 32, 256, 512 })
        {
            using var stream = File.OpenRead(Path.Combine(assets, $"quotasight-{size}.png"));
            var header = new byte[24];
            stream.ReadExactly(header);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, header[..8].ToArray());
            Assert.Equal(size, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]));
            Assert.Equal(size, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
        }
        using var ico = File.OpenRead(Path.Combine(assets, "quotasight.ico"));
        Span<byte> icoHeader = stackalloc byte[6];
        ico.ReadExactly(icoHeader);
        Assert.Equal((ushort)0, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(icoHeader[..2]));
        Assert.Equal((ushort)1, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(icoHeader[2..4]));
        Assert.Equal(7, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(icoHeader[4..6]));
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
    public async Task Monitor_ignores_single_owner_loss_and_cancels_cleanly()
    {
        var values = new Queue<bool>([true, false]);
        var changes = new List<bool>();
        var firstChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var monitor = new LinuxStatusNotifierMonitor(() => Task.FromResult(values.Dequeue()), value => { changes.Add(value); firstChange.TrySetResult(); }, TimeSpan.FromMilliseconds(1));

        var running = monitor.RunAsync(cancel.Token);
        await firstChange.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await running;

        Assert.Equal([true], changes);
    }

    [Fact]
    public async Task Monitor_reports_unavailable_only_on_third_failure_and_recovers_after_success()
    {
        var changes = new List<bool>();
        var probes = new[] { true, false, false, false, true };
        var probeCount = 0;
        var thirdFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var monitor = new LinuxStatusNotifierMonitor(() =>
        {
            var value = probes[Interlocked.Increment(ref probeCount) - 1];
            if (value && probeCount == 5)
            {
                recoveryGate.Task.GetAwaiter().GetResult();
                recovered.TrySetResult();
            }
            return Task.FromResult(value);
        }, value => { changes.Add(value); if (!value) thirdFailure.TrySetResult(); }, TimeSpan.FromMilliseconds(1));

        var running = monitor.RunAsync(cancel.Token);
        await thirdFailure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([true, false], changes);
        recoveryGate.TrySetResult();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await running;

        Assert.Equal([true, false, true], changes);
    }

    [Fact]
    public async Task Monitor_does_not_publish_unavailable_after_one_or_two_failures()
    {
        var changes = new List<bool>();
        var secondFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var cancel = new CancellationTokenSource();
        var monitor = new LinuxStatusNotifierMonitor(() =>
        {
            var probe = Interlocked.Increment(ref count);
            if (probe == 3) secondFailure.TrySetResult();
            // A fourth probe would be a third consecutive failure; park it on a task that only
            // cancellation resolves so the test deterministically observes exactly two failures.
            if (probe >= 4) return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            return Task.FromResult(probe == 1);
        }, changes.Add, TimeSpan.FromMilliseconds(1));

        var running = monitor.RunAsync(cancel.Token);
        await secondFailure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([true], changes);
    }
}
