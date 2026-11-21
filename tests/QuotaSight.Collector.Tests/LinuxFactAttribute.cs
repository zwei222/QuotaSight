using Xunit;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// A fact that only runs where the send state is supported: Linux x86-64, the
/// same condition the implementation uses. On any other host these tests are
/// reported as skipped rather than failed.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    /// <summary>Skips the test when the host is not a supported state host.</summary>
    public LinuxFactAttribute()
    {
        if (!StateHost.Supported)
        {
            Skip = "The collector state directory needs Linux x86-64.";
        }
    }
}

/// <summary>A theory that only runs where the send state is supported.</summary>
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    /// <summary>Skips the theory when the host is not a supported state host.</summary>
    public LinuxTheoryAttribute()
    {
        if (!StateHost.Supported)
        {
            Skip = "The collector state directory needs Linux x86-64.";
        }
    }
}

/// <summary>The single skip condition for the state tests.</summary>
internal static class StateHost
{
    /// <summary>True when this host is the one the state directory supports.</summary>
    internal static bool Supported => SystemdStateDirectory.IsSupportedPlatform;
}
