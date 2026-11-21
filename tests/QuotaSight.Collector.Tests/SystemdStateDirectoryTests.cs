using System.Runtime.Versioning;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// <c>STATE_DIRECTORY</c> is the only state location. These tests pin the
/// validation: absolute, normalized, a real directory, owned by the effective
/// user, and not readable or writable by anybody else.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class SystemdStateDirectoryTests
{
    private const string Marker = "qs-state-marker";

    private static SystemdStateDirectory Source(Func<string?> directory) =>
        new(static () => true, directory);

    private static string NewDirectory(UnixFileMode mode)
    {
        var path = Path.Combine(Path.GetTempPath(), Marker + "-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, mode);
        return path;
    }

    private static CollectorStateFailure Validate(string? raw) => Source(() => raw).Validate();

    [LinuxFact]
    public void UnsupportedPlatformIsRefusedBeforeAnyPathIsRead()
    {
        var source = new SystemdStateDirectory(static () => false, static () => null);

        Assert.Equal(CollectorStateFailure.UnsupportedPlatform, source.Validate());
        Assert.False(source.TryValidate(out var path));
        Assert.Null(path);
    }

    [LinuxTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void UnsetOrBlankIsRefused(string? raw)
    {
        Assert.Equal(CollectorStateFailure.DirectoryUnset, Validate(raw));
    }

    [LinuxFact]
    public void RelativePathIsRefused()
    {
        Assert.Equal(CollectorStateFailure.DirectoryNotAbsolute, Validate(Marker + "-relative"));
    }

    [LinuxTheory]
    [InlineData("/var/lib/example/../example")]
    [InlineData("/var/lib//example")]
    [InlineData("/var/lib/example/.")]
    [InlineData("/var/lib/example/sub/..")]
    public void NonNormalizedPathIsRefused(string raw)
    {
        Assert.Equal(CollectorStateFailure.DirectoryNotNormalized, Validate(raw));
    }

    [LinuxFact]
    public void MultipleDirectoriesAreRefused()
    {
        Assert.Equal(CollectorStateFailure.DirectoryMultiple, Validate("/var/lib/first:/var/lib/second"));
    }

    [LinuxFact]
    public void MissingDirectoryIsRefused()
    {
        var missing = Path.Combine(Path.GetTempPath(), Marker + "-missing-" + Guid.NewGuid().ToString("N")[..8]);

        Assert.Equal(CollectorStateFailure.DirectoryMissing, Validate(missing));
    }

    [LinuxFact]
    public void ATrailingSeparatorIsRefusedBecauseItWouldFollowASymlink()
    {
        var path = NewDirectory(SendHarness.PrivateDirectoryMode);
        try
        {
            Assert.Equal(CollectorStateFailure.DirectoryNotNormalized, Validate(path + "/"));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [LinuxFact]
    public void SymlinkedDirectoryIsRefused()
    {
        var target = NewDirectory(SendHarness.PrivateDirectoryMode);
        var link = Path.Combine(Path.GetTempPath(), Marker + "-link-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateSymbolicLink(link, target);

            Assert.Equal(CollectorStateFailure.DirectorySymlink, Validate(link));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target, recursive: true);
        }
    }

    [LinuxFact]
    public void RegularFileIsRefused()
    {
        var file = Path.Combine(Path.GetTempPath(), Marker + "-file-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(file, "{}");
            File.SetUnixFileMode(file, SendHarness.PrivateFileMode);

            Assert.Equal(CollectorStateFailure.DirectoryNotDirectory, Validate(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [LinuxTheory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite)]
    public void GroupOrOtherAccessIsRefused(UnixFileMode mode)
    {
        var path = NewDirectory(mode);
        try
        {
            Assert.Equal(CollectorStateFailure.DirectoryWrongMode, Validate(path));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [LinuxFact]
    public void APrivateDirectoryIsAccepted()
    {
        var path = NewDirectory(SendHarness.PrivateDirectoryMode);
        try
        {
            var source = Source(() => path);

            Assert.Equal(CollectorStateFailure.None, source.Validate());
            Assert.True(source.TryValidate(out var validated));
            Assert.Equal(path, validated);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [LinuxFact]
    public void FailureTextNeverCarriesThePath()
    {
        var path = NewDirectory(SendHarness.PrivateDirectoryMode);
        try
        {
            var source = Source(() => path + "/../" + Path.GetFileName(path));

            Assert.Equal(CollectorStateFailure.DirectoryNotNormalized, source.Validate());
            Assert.DoesNotContain(path, CollectorStateFailureText.Describe(source.Validate()), StringComparison.Ordinal);
            Assert.DoesNotContain(path, source.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
