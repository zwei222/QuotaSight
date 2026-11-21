using System.Text;

namespace QuotaSight.Collector.Tests;

public sealed class SystemdLoadCredentialSourceTests
{
    private const string SyntheticId = "synthetic-id";
    private const string SyntheticSecret = "synthetic-secret-value";
    private const string PathMarker = "qs-cred-marker";

    [Fact]
    public void UnsetDirectoryIsATypedFailure()
    {
        var result = Source(() => null).Read();

        Assert.Equal(CollectorCredentialFailure.DirectoryUnset, result.Failure);
        Assert.Null(result.Credential);
        AssertNoLeak(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankDirectoryIsUnset(string directory)
    {
        var result = Source(() => directory).Read();

        Assert.Equal(CollectorCredentialFailure.DirectoryUnset, result.Failure);
        AssertNoLeak(result);
    }

    [Fact]
    public void RelativeDirectoryIsRejectedWithoutReadingIt()
    {
        var relative = PathMarker + "-relative";
        Directory.CreateDirectory(relative);
        try
        {
            WritePair(relative, SyntheticId, SyntheticSecret);
            var result = Source(() => relative).Read();

            Assert.Equal(CollectorCredentialFailure.DirectoryNotAbsolute, result.Failure);
            Assert.Null(result.Credential);
            AssertNoLeak(result, relative);
        }
        finally
        {
            Directory.Delete(relative, recursive: true);
        }
    }

    [Fact]
    public void MissingDirectoryIsATypedFailure()
    {
        var missing = Path.Combine(Path.GetTempPath(), PathMarker + "-missing-" + Guid.NewGuid().ToString("N")[..8]);

        var result = Source(() => missing).Read();

        Assert.Equal(CollectorCredentialFailure.DirectoryMissing, result.Failure);
        AssertNoLeak(result, missing);
    }

    [Fact]
    public void NonLinuxIsUnsupportedAndDoesNotReadFiles()
    {
        using var dir = new CredentialDir();
        WritePair(dir.Path, SyntheticId, SyntheticSecret);

        var result = Source(() => dir.Path, isLinux: false).Read();

        Assert.Equal(CollectorCredentialFailure.UnsupportedPlatform, result.Failure);
        Assert.Null(result.Credential);
        AssertNoLeak(result, dir.Path);
    }

    [Fact]
    public void ReadsTokensAndStripsOneTrailingLineFeed()
    {
        using var dir = new CredentialDir();
        WritePair(dir.Path, SyntheticId + "\n", SyntheticSecret + "\n");

        var result = Source(() => dir.Path).Read();

        Assert.True(result.Succeeded);
        Assert.Equal(SyntheticId, result.Credential!.ClientId);
        Assert.Equal(SyntheticSecret, result.Credential.ClientSecret);
        AssertNoLeak(result, dir.Path);
    }

    [Fact]
    public void AcceptsATokenWithoutATrailingLineFeed()
    {
        using var dir = new CredentialDir();
        WritePair(dir.Path, SyntheticId, SyntheticSecret);

        var result = Source(() => dir.Path).Read();

        Assert.True(result.Succeeded);
        Assert.Equal(SyntheticId, result.Credential!.ClientId);
    }

    [Fact]
    public void SecondTrailingLineFeedIsRejected()
    {
        using var dir = new CredentialDir();
        WritePair(dir.Path, SyntheticId + "\n\n", SyntheticSecret);

        var result = Source(() => dir.Path).Read();

        Assert.Equal(CollectorCredentialFailure.ClientIdRejected, result.Failure);
        AssertNoLeak(result, dir.Path);
    }

    [Fact]
    public void CarriageReturnIsRejected()
    {
        using var dir = new CredentialDir();
        WritePair(dir.Path, SyntheticId + "\r\n", SyntheticSecret);

        var result = Source(() => dir.Path).Read();

        Assert.Equal(CollectorCredentialFailure.ClientIdRejected, result.Failure);
        AssertNoLeak(result, dir.Path);
    }

    [Fact]
    public void MissingClientIdFileIsTyped()
    {
        using var dir = new CredentialDir();
        WriteBytes(Path.Combine(dir.Path, SystemdLoadCredentialSource.ClientSecretFileName), SyntheticSecret);

        var result = Source(() => dir.Path).Read();

        Assert.Equal(CollectorCredentialFailure.ClientIdMissing, result.Failure);
        AssertNoLeak(result, dir.Path);
    }

    [Fact]
    public void MissingClientSecretFileIsTyped()
    {
        using var dir = new CredentialDir();
        WriteBytes(Path.Combine(dir.Path, SystemdLoadCredentialSource.ClientIdFileName), SyntheticId);

        var result = Source(() => dir.Path).Read();

        Assert.Equal(CollectorCredentialFailure.ClientSecretMissing, result.Failure);
        AssertNoLeak(result, dir.Path);
    }

    [Fact]
    public void EmptyClientIdIsTyped()
    {
        using var dir = new CredentialDir();
        WritePair(dir.Path, "\n", SyntheticSecret);

        var result = Source(() => dir.Path).Read();

        Assert.Equal(CollectorCredentialFailure.ClientIdEmpty, result.Failure);
        AssertNoLeak(result, dir.Path);
    }

    [Theory]
    [InlineData("synthetic id")]
    [InlineData("synthetic-id\t")]
    [InlineData("synthetic-id\u0001")]
    [InlineData("synthetic-id\u00e9")]
    public void ControlWhitespaceAndNonAsciiAreRejected(string id)
    {
        using var dir = new CredentialDir();
        WritePair(dir.Path, id, SyntheticSecret);

        var result = Source(() => dir.Path).Read();

        Assert.Equal(CollectorCredentialFailure.ClientIdRejected, result.Failure);
        AssertNoLeak(result, dir.Path, id);
    }

    [Fact]
    public void ClientIdAtTheSizeBoundIsAccepted()
    {
        using var dir = new CredentialDir();
        var id = new string('a', SystemdLoadCredentialSource.MaxClientIdBytes);
        WritePair(dir.Path, id + "\n", SyntheticSecret);

        var result = Source(() => dir.Path).Read();

        Assert.True(result.Succeeded);
        Assert.Equal(id, result.Credential!.ClientId);
        AssertNoLeak(result, dir.Path);
    }

    [Fact]
    public void ClientIdOverTheSizeBoundIsRejected()
    {
        using var dir = new CredentialDir();
        var id = new string('b', SystemdLoadCredentialSource.MaxClientIdBytes + 1);
        WritePair(dir.Path, id, SyntheticSecret);

        var result = Source(() => dir.Path).Read();

        Assert.Equal(CollectorCredentialFailure.ClientIdTooLarge, result.Failure);
        AssertNoLeak(result, dir.Path, id);
    }

    [Fact]
    public void ClientSecretOverTheSizeBoundIsRejected()
    {
        using var dir = new CredentialDir();
        var secret = new string('c', SystemdLoadCredentialSource.MaxClientSecretBytes + 1);
        WritePair(dir.Path, SyntheticId, secret);

        var result = Source(() => dir.Path).Read();

        Assert.Equal(CollectorCredentialFailure.ClientSecretTooLarge, result.Failure);
        AssertNoLeak(result, dir.Path, secret);
    }

    [Fact]
    public void SymlinkCredentialFileIsRejectedAndNotFollowed()
    {
        using var dir = new CredentialDir();
        var outside = Path.Combine(Path.GetTempPath(), PathMarker + "-target-" + Guid.NewGuid().ToString("N")[..8]);
        WriteBytes(outside, SyntheticSecret);
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir.Path, SystemdLoadCredentialSource.ClientIdFileName), outside);
            WriteBytes(Path.Combine(dir.Path, SystemdLoadCredentialSource.ClientSecretFileName), SyntheticSecret);

            var result = Source(() => dir.Path).Read();

            Assert.Equal(CollectorCredentialFailure.ClientIdSymlink, result.Failure);
            Assert.Null(result.Credential);
            AssertNoLeak(result, dir.Path, outside);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void SymlinkDirectoryIsRejected()
    {
        using var real = new CredentialDir();
        WritePair(real.Path, SyntheticId, SyntheticSecret);
        var link = Path.Combine(Path.GetTempPath(), PathMarker + "-dirlink-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateSymbolicLink(link, real.Path);
        try
        {
            var result = Source(() => link).Read();

            Assert.Equal(CollectorCredentialFailure.DirectorySymlink, result.Failure);
            AssertNoLeak(result, real.Path, link);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void DoesNotReadASiblingOutsideTheCredentialDirectory()
    {
        using var dir = new CredentialDir();
        var parent = Directory.GetParent(dir.Path)!.FullName;
        var outside = Path.Combine(parent, SystemdLoadCredentialSource.ClientIdFileName);
        WriteBytes(outside, SyntheticId);
        try
        {
            WriteBytes(Path.Combine(dir.Path, SystemdLoadCredentialSource.ClientSecretFileName), SyntheticSecret);

            var result = Source(() => dir.Path).Read();

            Assert.Equal(CollectorCredentialFailure.ClientIdMissing, result.Failure);
            AssertNoLeak(result, dir.Path, outside);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void SourceTextOmitsTheDirectory()
    {
        using var dir = new CredentialDir();
        var source = Source(() => dir.Path);

        Assert.DoesNotContain(dir.Path, source.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PathMarker, source.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecret, source.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParameterlessSourceUsesCredentialsDirectoryAndNoSecretEnvironmentVariable()
    {
        var previousDirectory = Environment.GetEnvironmentVariable(SystemdLoadCredentialSource.DirectoryVariableName);
        var previousSecret = Environment.GetEnvironmentVariable("ACCESS_CLIENT_SECRET");
        try
        {
            Environment.SetEnvironmentVariable(SystemdLoadCredentialSource.DirectoryVariableName, null);
            Environment.SetEnvironmentVariable("ACCESS_CLIENT_SECRET", SyntheticSecret);

            var result = new SystemdLoadCredentialSource().Read();

            Assert.Equal(CollectorCredentialFailure.DirectoryUnset, result.Failure);
            AssertNoLeak(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SystemdLoadCredentialSource.DirectoryVariableName, previousDirectory);
            Environment.SetEnvironmentVariable("ACCESS_CLIENT_SECRET", previousSecret);
        }
    }

    private static SystemdLoadCredentialSource Source(Func<string?> directory, bool isLinux = true) =>
        new(() => isLinux, directory);

    private static void WritePair(string directory, string id, string secret)
    {
        WriteBytes(Path.Combine(directory, SystemdLoadCredentialSource.ClientIdFileName), id);
        WriteBytes(Path.Combine(directory, SystemdLoadCredentialSource.ClientSecretFileName), secret);
    }

    private static void WriteBytes(string path, string text) =>
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));

    private static void AssertNoLeak(CollectorCredentialResult result, params string[] extra)
    {
        var text = result.ToString();
        Assert.DoesNotContain(SyntheticId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PathMarker, text, StringComparison.Ordinal);
        foreach (var item in extra)
        {
            Assert.DoesNotContain(item, text, StringComparison.Ordinal);
        }
    }

    private sealed class CredentialDir : IDisposable
    {
        public CredentialDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                PathMarker + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
