using System.Globalization;
using System.Net;
using System.Text;

namespace QuotaSight.Collector.Tests;

/// <summary>One in-process CLI run: exit code plus the two captured streams.</summary>
internal sealed record CliResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// In-process send harness: a fake handler, a counting credential source and a
/// private state directory. No socket is opened and no real state directory is
/// used. Every synthetic value here is invented; none is a real token or host.
/// </summary>
internal sealed class SendHarness : IDisposable
{
    internal const string SyntheticId = "synthetic-id";
    internal const string SyntheticSecret = "synthetic-secret-value";
    internal const string Endpoint = "https://send-target.invalid";
    internal const string EndpointHost = "send-target.invalid";
    internal const string CollectorId = "col-test";
    internal const string Revision = "3";
    internal const long Generation = 7;

    private readonly string? _stateDirectory;
    private readonly bool _ownsDirectory;

    private SendHarness(
        ScriptedHandler handler,
        HttpClient client,
        CountingCredentials credentials,
        List<TimeSpan> delays,
        string? stateDirectory,
        bool ownsDirectory)
    {
        Handler = handler;
        Client = client;
        Credentials = credentials;
        Delays = delays;
        _stateDirectory = stateDirectory;
        _ownsDirectory = ownsDirectory;
    }

    public ScriptedHandler Handler { get; }

    public HttpClient Client { get; }

    public CountingCredentials Credentials { get; }

    public List<TimeSpan> Delays { get; }

    /// <summary>Directory passed to the CLI as <c>STATE_DIRECTORY</c>, or null when it is unset.</summary>
    public string? StateDirectory => _stateDirectory;

    public string StateFilePath => Path.Combine(_stateDirectory!, CollectorStateStore.StateFileName);

    public string StateTempPath => Path.Combine(_stateDirectory!, CollectorStateStore.TempFileName);

    public static SendHarness Create(CollectorCredentialResult? credentials = null) =>
        CreateAt(NewStateDirectory(), ownsDirectory: true, credentials);

    /// <summary>Harness without any state directory, as if the variable were unset.</summary>
    public static SendHarness CreateWithoutStateDirectory(CollectorCredentialResult? credentials = null) =>
        CreateAt(null, ownsDirectory: false, credentials);

    /// <summary>Harness over an operator-supplied directory that the test owns.</summary>
    public static SendHarness CreateAt(string? stateDirectory, CollectorCredentialResult? credentials = null) =>
        CreateAt(stateDirectory, ownsDirectory: false, credentials);

    private static SendHarness CreateAt(string? stateDirectory, bool ownsDirectory, CollectorCredentialResult? credentials)
    {
        var handler = new ScriptedHandler(CollectorId, Generation);
        var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var counting = new CountingCredentials(credentials ?? CollectorCredentialResult.Success(
            new CollectorAccessCredential(SyntheticId, SyntheticSecret)));
        return new SendHarness(handler, client, counting, [], stateDirectory, ownsDirectory);
    }

    /// <summary>Creates a private (0700) temporary directory for a state file.</summary>
    public static string NewStateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "qs-send-state-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        SetMode(path, PrivateDirectoryMode);
        return path;
    }

    internal static UnixFileMode PrivateDirectoryMode =>
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static UnixFileMode PrivateFileMode => UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Writes a canonical state file directly, independently of the store under test.</summary>
    public void WriteState(long lastRevision, CollectorSendOutcome lastResult = CollectorSendOutcome.None, string? blockedReason = null) =>
        WriteStateBytes(CollectorSendState.Create(CollectorId, Generation, lastRevision, lastResult, blockedReason).ToCanonicalJson());

    public void WriteStateBytes(byte[] bytes)
    {
        File.WriteAllBytes(StateFilePath, bytes);
        SetMode(StateFilePath, PrivateFileMode);
    }

    /// <summary>Applies a mode on Linux only, so the analyzer sees the guard.</summary>
    internal static void SetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    public bool StateFileExists => File.Exists(StateFilePath);

    public CollectorSendState? ReadState()
    {
        var bytes = File.ReadAllBytes(StateFilePath);
        Assert.True(CollectorSendState.TryParse(bytes, out var state, out var failure), $"state file did not parse: {failure}");
        return state;
    }

    public byte[] ReadStateBytes() => File.ReadAllBytes(StateFilePath);

    public CliResult Run(string[] args, byte[]? stdin = null)
    {
        stdin ??= FixtureFiles.Bytes(FixtureFiles.Positive);
        using var input = new MemoryStream(stdin, writable: false);
        using var output = new MemoryStream();
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = new CollectorSendContext
        {
            HttpClient = Client,
            Credentials = Credentials,
            Delay = Delays.Add,
            NextJitterUnit = static () => 0d,
            StateDirectory = _stateDirectory is null ? static () => null : () => _stateDirectory,
        };
        var exitCode = CollectorCli.Run(args, input, output, stderr, context);
        return new CliResult(exitCode, Encoding.UTF8.GetString(output.ToArray()), stderr.ToString());
    }

    public CliResult RunSend(byte[]? stdin = null, params string[] extra) =>
        Run([.. SendArgs(), .. extra], stdin);

    public static string[] PreviewArgs() =>
    [
        "--preview",
        "--generation",
        Generation.ToString(CultureInfo.InvariantCulture),
        "--bucket-time-zone",
        "Asia/Tokyo",
    ];

    public static string[] SendArgs(string? endpoint = null) =>
    [
        "--send",
        "--endpoint",
        endpoint ?? Endpoint,
        "--collector-id",
        CollectorId,
        "--generation",
        Generation.ToString(CultureInfo.InvariantCulture),
        "--bucket-time-zone",
        "Asia/Tokyo",
    ];

    /// <summary>The receipt body <c>src/QuotaSight.Usage.Api</c> returns for a write.</summary>
    internal static string Receipt(
        long revision,
        string status = "applied",
        string collectorId = CollectorId,
        long generation = Generation,
        string receivedAt = "2026-11-21T00:00:00.000Z",
        string changedDays = "[\"2026-11-17\"]",
        string ignoredReductions = "0") =>
        "{\"status\":\"" + status
        + "\",\"collectorId\":\"" + collectorId
        + "\",\"generation\":" + generation.ToString(CultureInfo.InvariantCulture)
        + ",\"revision\":" + revision.ToString(CultureInfo.InvariantCulture)
        + ",\"receivedAt\":\"" + receivedAt
        + "\",\"changedDays\":" + changedDays
        + ",\"ignoredReductions\":" + ignoredReductions + "}";

    /// <summary>The wire-v2 receipt <c>src/QuotaSight.Usage.Api</c> returns for a v2 write.</summary>
    internal static string ReceiptV2(
        long revision,
        string status = "applied",
        string collectorId = CollectorId,
        long generation = Generation,
        string receivedAt = "2026-11-21T00:00:00.000Z",
        string changedDays = "[\"2026-11-17\"]",
        string ignoredReductions = "0",
        string changedParts = "1",
        string ignoredPartReductions = "0") =>
        Receipt(revision, status, collectorId, generation, receivedAt, changedDays, ignoredReductions)
            .TrimEnd('}')
        + ",\"changedParts\":" + changedParts
        + ",\"ignoredPartReductions\":" + ignoredPartReductions + "}";

    public static byte[] ExpectedWire()
    {
        var candidate = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.Positive)).Candidate;
        Assert.NotNull(candidate);
        return SnapshotWire.Serialize(candidate!, Generation);
    }

    /// <summary>The v2 bytes for the parts fixture, as the shared golden fixture holds them.</summary>
    public static byte[] ExpectedWireV2(long generation = 1)
    {
        var candidate = TokscaleGraphNormalizer.Normalize(FixtureFiles.Bytes(FixtureFiles.Parts)).Candidate;
        Assert.NotNull(candidate);
        return SnapshotWire.Serialize(candidate!, generation, WireSchema.V2).Bytes;
    }

    public static string Header(CapturedRequest captured, string name)
    {
        Assert.True(captured.Headers.TryGetValue(name, out var values));
        return Assert.Single(values!);
    }

    public static string[] RemoveOption(string[] args, string option)
    {
        var kept = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            kept.Add(args[index]);
        }

        return kept.ToArray();
    }

    /// <summary>
    /// Scans both streams: no credential, no endpoint, no state path and no other
    /// caller-supplied value may appear in output.
    /// </summary>
    public static void AssertNoLeak(params CliResult[] runs)
    {
        foreach (var run in runs)
        {
            AssertNoLeak(run, []);
        }
    }

    public static void AssertNoLeak(CliResult run, params string[] extra)
    {
        var text = run.Stdout + "\n" + run.Stderr;
        Assert.DoesNotContain(SyntheticId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Endpoint, text, StringComparison.Ordinal);
        Assert.DoesNotContain(EndpointHost, text, StringComparison.Ordinal);
        Assert.DoesNotContain("access-client", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), text, StringComparison.Ordinal);
        foreach (var item in extra)
        {
            if (item.Length == 0)
            {
                continue;
            }

            Assert.DoesNotContain(item, text, StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        Handler.Dispose();
        if (_ownsDirectory && _stateDirectory is not null && Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }
}

internal sealed class CountingCredentials : ICollectorCredentialSource
{
    private readonly CollectorCredentialResult _result;

    public CountingCredentials(CollectorCredentialResult result) => _result = result;

    public int Reads { get; private set; }

    public CollectorCredentialResult Read()
    {
        Reads++;
        return _result;
    }

    public override string ToString() => nameof(CountingCredentials);
}

/// <summary>
/// Scripted responses. The fallback answer is the API's own receipt for the
/// revision in the request path, so an unscripted send is a well-formed success.
/// </summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly List<Func<CancellationToken, HttpResponseMessage>> _steps = [];
    private readonly string _collectorId;
    private readonly long _generation;

    public ScriptedHandler(string collectorId, long generation)
    {
        _collectorId = collectorId;
        _generation = generation;
    }

    public List<CapturedRequest> Captured { get; } = [];

    /// <summary>Runs while the request is in flight, before the response is produced.</summary>
    public Action<CapturedRequest>? Observed { get; set; }

    public void Enqueue(HttpStatusCode status, string body, params (string Name, string Value)[] headers) =>
        Enqueue(status, Encoding.UTF8.GetBytes(body), headers: headers);

    public void Enqueue(
        HttpStatusCode status,
        byte[] body,
        long? contentLength = null,
        params (string Name, string Value)[] headers)
    {
        _steps.Add(_ =>
        {
            var content = new ByteArrayContent(body);
            if (contentLength is not null)
            {
                content.Headers.ContentLength = contentLength;
            }

            var response = new HttpResponseMessage(status) { Content = content };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });
    }

    public void EnqueueException(Func<Exception> factory)
    {
        for (var index = 0; index < CollectorSnapshotTransport.MaxAttempts; index++)
        {
            _steps.Add(_ => throw factory());
        }
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Dispatch(request);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Dispatch(request));

    private HttpResponseMessage Dispatch(HttpRequestMessage request)
    {
        var captured = CapturedRequest.From(request);
        Captured.Add(captured);
        Observed?.Invoke(captured);
        if (Captured.Count <= _steps.Count)
        {
            return _steps[Captured.Count - 1](CancellationToken.None);
        }

        var path = captured.Path;
        var revision = long.Parse(path[(path.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(
                SendHarness.Receipt(revision, collectorId: _collectorId, generation: _generation))),
        };
    }
}

internal sealed record CapturedRequest(
    string Method,
    string Scheme,
    string Host,
    string Path,
    string Query,
    string UserInfo,
    string? ContentType,
    string? ContentCharset,
    byte[] Body,
    IReadOnlyDictionary<string, string[]> Headers)
{
    public static CapturedRequest From(HttpRequestMessage request)
    {
        var body = Array.Empty<byte>();
        if (request.Content is not null)
        {
            using var buffer = new MemoryStream();
            request.Content.CopyTo(buffer, context: null, cancellationToken: CancellationToken.None);
            body = buffer.ToArray();
        }

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = header.Value.ToArray();
        }

        var uri = request.RequestUri;
        return new CapturedRequest(
            request.Method.Method,
            uri?.Scheme ?? string.Empty,
            uri?.IdnHost ?? string.Empty,
            uri?.AbsolutePath ?? string.Empty,
            uri?.Query ?? string.Empty,
            uri?.UserInfo ?? string.Empty,
            request.Content?.Headers.ContentType?.MediaType,
            request.Content?.Headers.ContentType?.CharSet,
            body,
            headers);
    }
}
