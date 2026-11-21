namespace QuotaSight.Collector.Tests;

/// <summary>
/// The native layer must turn an unusable host into a fixed reason class, never
/// into an unhandled exception: a host without the symbol or the library is
/// reported as unsupported, and every other failure keeps its own meaning.
/// A real missing symbol cannot be produced on this host, so the mapping is
/// exercised directly.
/// </summary>
public sealed class CollectorUnixNativeTests
{
    [Fact]
    public void AMissingLibraryIsReportedAsUnsupported()
    {
        Assert.Equal(
            NativeCall.Unsupported,
            CollectorUnixNative.Guard(static () => throw new DllNotFoundException("synthetic")));
    }

    [Fact]
    public void AMissingSymbolIsReportedAsUnsupported()
    {
        Assert.Equal(
            NativeCall.Unsupported,
            CollectorUnixNative.Guard(static () => throw new EntryPointNotFoundException("synthetic")));
    }

    [Fact]
    public void AnOrdinaryResultIsKept()
    {
        Assert.Equal(NativeCall.Ok, CollectorUnixNative.Guard(static () => true));
        Assert.Equal(NativeCall.Failed, CollectorUnixNative.Guard(static () => false));
    }

    [Fact]
    public void AnyOtherExceptionIsNotSwallowed()
    {
        Assert.Throws<InvalidOperationException>(
            () => CollectorUnixNative.Guard(static () => throw new InvalidOperationException("synthetic")));
    }

    [LinuxFact]
    public void ThisHostExposesTheSymbolsTheStateDirectoryNeeds()
    {
        Assert.Equal(
            NativeCall.Ok,
            CollectorUnixNative.Guard(static () => CollectorUnixNative.GetEffectiveUid(out _) == NativeCall.Ok));
    }
}
