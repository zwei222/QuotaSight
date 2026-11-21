using QuotaSight.Application;
using QuotaSight.Infrastructure;
using Xunit;

namespace QuotaSight.Tests;

/// <summary>
/// A stand-in for the real <c>secret-tool</c> executable. It mirrors the observable
/// behaviour that matters here: looking up an unregistered attribute prints nothing
/// on stderr and exits 1, a stored item is returned by a later lookup, a missing
/// executable is a start failure and any other diagnostic exits non-zero with text
/// on stderr.
/// </summary>
internal sealed class SecretToolLikeRunner : ISecretToolProcessRunner
{
    private readonly Dictionary<string, string> items = new(StringComparer.Ordinal);

    public bool ToolMissing { get; set; }

    /// <summary>Forces a diagnostic exit for every call; <c>null</c> restores the normal behaviour.</summary>
    public int? ForcedExitCode { get; set; }

    public string ForcedStderr { get; set; } = string.Empty;

    public string? LastStdin { get; private set; }

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public void Seed(string account, string secret) => items[account] = secret;

    public ValueTask<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? stdin, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(arguments);
        LastStdin = stdin;

        if (ToolMissing) return ValueTask.FromResult(new SecretToolResult(-1, string.Empty, "secret-tool unavailable"));
        if (ForcedExitCode is { } forced) return ValueTask.FromResult(new SecretToolResult(forced, string.Empty, ForcedStderr));

        var account = arguments[^1];
        return arguments[0] switch
        {
            "lookup" => items.TryGetValue(account, out var value)
                ? ValueTask.FromResult(new SecretToolResult(0, value + "\n", string.Empty))
                : ValueTask.FromResult(new SecretToolResult(1, string.Empty, string.Empty)),
            "store" => Store(account, stdin),
            "clear" => Clear(account),
            _ => ValueTask.FromResult(new SecretToolResult(2, string.Empty, "unknown subcommand")),
        };

        ValueTask<SecretToolResult> Store(string key, string? secret)
        {
            items[key] = secret ?? string.Empty;
            return ValueTask.FromResult(new SecretToolResult(0, string.Empty, string.Empty));
        }

        ValueTask<SecretToolResult> Clear(string key)
        {
            items.Remove(key);
            return ValueTask.FromResult(new SecretToolResult(0, string.Empty, string.Empty));
        }
    }
}

public sealed class LinuxSecretToolCredentialStoreTests
{
    [Fact]
    public async Task Unregistered_lookup_leaves_the_store_secure()
    {
        var store = new LinuxSecretToolCredentialStore(new SecretToolLikeRunner());

        Assert.Null(await store.GetAsync("quota-account", CancellationToken.None));

        Assert.Equal(CredentialStoreAvailability.SecureStore, store.Availability);
    }

    [Fact]
    public async Task Stored_value_is_read_back_and_the_store_stays_secure()
    {
        var runner = new SecretToolLikeRunner();
        var store = new LinuxSecretToolCredentialStore(runner);

        await store.SetAsync("quota-account", "synthetic-secret", CancellationToken.None);

        Assert.Equal(CredentialStoreAvailability.SecureStore, store.Availability);
        Assert.Equal("synthetic-secret", await store.GetAsync("quota-account", CancellationToken.None));
        Assert.Equal(CredentialStoreAvailability.SecureStore, store.Availability);
    }

    [Fact]
    public async Task Secret_is_written_through_stdin_and_never_as_an_argument()
    {
        var runner = new SecretToolLikeRunner();
        var store = new LinuxSecretToolCredentialStore(runner);

        await store.SetAsync("quota-account", "synthetic-secret", CancellationToken.None);

        Assert.Equal("synthetic-secret", runner.LastStdin);
        Assert.DoesNotContain(runner.Calls.SelectMany(call => call), argument => argument.Contains("synthetic-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_tool_marks_the_store_unavailable()
    {
        var store = new LinuxSecretToolCredentialStore(new SecretToolLikeRunner { ToolMissing = true });

        Assert.Null(await store.GetAsync("quota-account", CancellationToken.None));

        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
    }

    [Fact]
    public async Task Abnormal_exit_marks_the_store_unavailable()
    {
        var store = new LinuxSecretToolCredentialStore(new SecretToolLikeRunner { ForcedExitCode = 2, ForcedStderr = "a diagnostic" });

        Assert.Null(await store.GetAsync("quota-account", CancellationToken.None));

        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
    }

    [Fact]
    public async Task Non_empty_locked_diagnostic_marks_the_store_locked()
    {
        var store = new LinuxSecretToolCredentialStore(new SecretToolLikeRunner { ForcedExitCode = 1, ForcedStderr = "The secret service is locked." });

        Assert.Null(await store.GetAsync("quota-account", CancellationToken.None));

        Assert.Equal(CredentialStoreAvailability.Locked, store.Availability);
    }

    [Fact]
    public async Task Successful_write_recovers_a_store_that_was_unavailable()
    {
        var runner = new SecretToolLikeRunner { ForcedExitCode = 2, ForcedStderr = "a diagnostic" };
        var store = new LinuxSecretToolCredentialStore(runner);
        Assert.Null(await store.GetAsync("quota-account", CancellationToken.None));
        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);

        runner.ForcedExitCode = null;
        await store.SetAsync("quota-account", "synthetic-secret", CancellationToken.None);

        Assert.Equal(CredentialStoreAvailability.SecureStore, store.Availability);
    }

    [Fact]
    public async Task Successful_lookup_recovers_a_store_that_was_unavailable()
    {
        var runner = new SecretToolLikeRunner { ForcedExitCode = 2, ForcedStderr = "a diagnostic" };
        runner.Seed("quota-account", "synthetic-secret");
        var store = new LinuxSecretToolCredentialStore(runner);
        Assert.Null(await store.GetAsync("quota-account", CancellationToken.None));
        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);

        runner.ForcedExitCode = null;

        Assert.Equal("synthetic-secret", await store.GetAsync("quota-account", CancellationToken.None));
        Assert.Equal(CredentialStoreAvailability.SecureStore, store.Availability);
    }
}
