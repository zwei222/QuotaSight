using System.Runtime.Versioning;

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace QuotaSight.Collector.Tests;

/// <summary>
/// The state lock must exclude a second collector process, not merely a second
/// handle in this one: <c>FileStream.Lock</c> on Linux is a per-process lock, so
/// an in-process test would pass without proving anything. These tests run the
/// built CLI as a child process over a private temporary directory.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class CollectorStateLockTests
{
    [LinuxFact]
    public void AnotherProcessCannotHoldTheLockAtTheSameTime()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            using var store = Open(directory);
            using (store.Lock())
            {
                var run = RunCollector(directory, StateInitArgs());

                Assert.Equal(CollectorExitCodes.StateUnavailable, run.ExitCode);
                Assert.Contains("state_unavailable: StateLocked", run.Stderr, StringComparison.Ordinal);
                Assert.False(File.Exists(store.StatePath));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LinuxFact]
    public void AnotherProcessInitialisesTheStateOnceTheLockIsFree()
    {
        var directory = SendHarness.NewStateDirectory();
        try
        {
            var run = RunCollector(directory, StateInitArgs());

            Assert.Equal(CollectorExitCodes.Candidate, run.ExitCode);
            using var store = Open(directory);
            var state = store.Read(out var failure);
            Assert.Equal(CollectorStateFailure.None, failure);
            Assert.Equal(0, state!.LastRevision);
            Assert.Equal(SendHarness.CollectorId, state.CollectorId);
            Assert.Equal(SendHarness.Generation, state.Generation);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CollectorStateStore Open(string directory)
    {
        Assert.True(CollectorStateStore.TryOpen(new SystemdStateDirectory(static () => true, () => directory), out var store, out var failure),
            $"store did not open: {failure}");
        return store!;
    }

    private static string[] StateInitArgs() =>
    [
        "--state-init",
        "--first-revision",
        "1",
        "--collector-id",
        SendHarness.CollectorId,
        "--generation",
        SendHarness.Generation.ToString(CultureInfo.InvariantCulture),
    ];

    private static CliResult RunCollector(string stateDirectory, string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(CollectorAssembly());
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment[SystemdStateDirectory.DirectoryVariableName] = stateDirectory;
        startInfo.Environment.Remove(SystemdLoadCredentialSource.DirectoryVariableName);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the collector child process did not exit");
        }

        return new CliResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    private static string CollectorAssembly()
    {
        var root = RepoRoot();
        var configuration = AppContext.BaseDirectory.Contains("/Release/", StringComparison.Ordinal) ? "Release" : "Debug";
        var path = Path.Combine(root, "src", "QuotaSight.Collector", "bin", configuration, "net10.0", "QuotaSight.Collector.dll");
        Assert.True(File.Exists(path), "the collector assembly must be built before this test runs");
        return path;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuotaSight.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repository root was not found");
    }
}
