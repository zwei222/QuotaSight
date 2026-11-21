using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace QuotaSight.UI;

/// <summary>
/// Stable classification of a failed daily read. The classification, never the
/// remote payload, is what a caller may surface.
/// </summary>
public enum UsageReadFailure
{
    /// <summary>The scoped credential provider had nothing to offer.</summary>
    CredentialUnavailable,

    /// <summary>The available credential belongs to a different identity than this port is scoped to.</summary>
    IdentityMismatch,

    /// <summary>The caller supplied a range or filter this port refuses to send.</summary>
    InvalidRequest,

    /// <summary>The transport failed before a response could be read.</summary>
    TransportFailure,

    /// <summary>No response arrived within the client timeout.</summary>
    Timeout,

    /// <summary>The service rejected the credential (401).</summary>
    Unauthorized,

    /// <summary>The credential is not allowed to read usage (403).</summary>
    Forbidden,

    /// <summary>The service reported that the collector is unknown (404).</summary>
    NotFound,

    /// <summary>
    /// The service answered with a redirect, or with a response that belongs to a
    /// different request URI than the one sent. Neither is ever accepted.
    /// </summary>
    RedirectRejected,

    /// <summary>An unexpected HTTP status was returned.</summary>
    UnexpectedStatus,

    /// <summary>The response media type or content encoding is not the accepted one.</summary>
    UnsupportedContent,

    /// <summary>The response exceeded the bounded read size.</summary>
    ResponseTooLarge,

    /// <summary>The response did not satisfy the strict daily contract.</summary>
    MalformedResponse,

    /// <summary>The response echoed a different range than the one requested.</summary>
    RangeMismatch,

    /// <summary>A row did not belong to the requested collector filter.</summary>
    FilterMismatch,

    /// <summary>The echoed total did not equal the safely recomputed total.</summary>
    InconsistentTotal,

    /// <summary>
    /// The service could not authenticate the request right now (HTTP 503:
    /// `auth_unavailable` for a key-set outage, `auth_unconfigured` for a deployment
    /// without Access configuration). This is a retryable transient failure, not a
    /// credential rejection, and it is never inferred from the response body.
    /// </summary>
    AuthUnavailable,

    /// <summary>
    /// A retryable server-side failure that is not an authentication outage:
    /// HTTP 429 or any 5xx other than 503. Classified from the status alone, never
    /// from the body.
    /// </summary>
    Transient,
}

/// <summary>
/// The only failure type this port produces. <see cref="Exception.Message"/> is
/// always a fixed sentence chosen here: remote body text, response headers and
/// the signed-in identity are never part of it.
/// </summary>
public sealed class UsageReadException : Exception
{
    /// <summary>Creates a classified failure with a fixed, non-echoing message.</summary>
    public UsageReadException(UsageReadFailure failure, string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
    }

    /// <summary>Stable classification of the failure.</summary>
    public UsageReadFailure Failure { get; }

    /// <summary>HTTP status that caused the failure, when the failure is status-derived.</summary>
    public int? StatusCode { get; }

    /// <summary>
    /// True only for a transient outage (authentication or server side). The caller
    /// may retry without user action. A credential rejection is never retryable.
    /// </summary>
    public bool IsRetryable => Failure is UsageReadFailure.AuthUnavailable or UsageReadFailure.Transient;

    /// <summary>
    /// True when the credential was rejected. The caller must not retry until the
    /// user replaces it.
    /// </summary>
    public bool RequiresUserAction => Failure == UsageReadFailure.Unauthorized;
}

/// <summary>
/// A scoped read credential: the identity it belongs to plus the Cloudflare Access
/// service-token pair sent for exactly one request. The client id and client secret
/// are never included in text or debugger output.
/// </summary>
[DebuggerDisplay("UsageCredential(identity: {IdentityId})")]
public sealed class UsageCredential
{
    private const int MaxClientIdLength = 256;
    private const int MaxClientSecretLength = 512;

    /// <summary>Creates a credential after rejecting values that cannot be header tokens.</summary>
    public UsageCredential(string identityId, string clientId, string clientSecret)
    {
        if (string.IsNullOrWhiteSpace(identityId))
        {
            throw new ArgumentException("A credential identity is required.", nameof(identityId));
        }

        if (!IsHeaderToken(clientId, MaxClientIdLength))
        {
            throw new ArgumentException(
                "A client id must be a non-empty printable ASCII token without whitespace.",
                nameof(clientId));
        }

        if (!IsHeaderToken(clientSecret, MaxClientSecretLength))
        {
            throw new ArgumentException(
                "A client secret must be a non-empty printable ASCII token without whitespace.",
                nameof(clientSecret));
        }

        IdentityId = identityId;
        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    /// <summary>Stable identity this credential was issued for.</summary>
    public string IdentityId { get; }

    /// <summary>Access service-token client id for this request only. Never log or persist this value.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string ClientId { get; }

    /// <summary>Access service-token client secret for this request only. Never log or persist this value.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string ClientSecret { get; }

    /// <summary>Deliberately omits the client id and the client secret.</summary>
    public override string ToString() => $"UsageCredential(identity: {IdentityId})";

    private static bool IsHeaderToken(string? value, int maxLength)
    {
        if (value is null || value.Length is 0 || value.Length > maxLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            // Printable ASCII only: no controls (CR/LF), no space and no DEL.
            if (character is <= ' ' or >= '\u007f')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Supplies the credential for one read. It is asked once per request and its
/// answer is never cached by this application.
/// </summary>
public interface IUsageCredentialProvider
{
    /// <summary>Returns the credential to use for the next request, or <c>null</c> if none is available.</summary>
    ValueTask<UsageCredential?> GetCredentialAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Explicit, transport-only daily read adapter for
/// <c>GET /v1/usage/daily</c>.
///
/// The origin is fixed at construction, the returned range is verified against
/// the requested one, every number is range-checked, and any deviation is
/// reported as a classified failure instead of a guessed value. One read is
/// bounded by a finite deadline covering the response headers and the body, and
/// an answer that reports a different request URI than the one sent is refused.
/// Nothing here registers itself: the application builds this port from a
/// configured origin, the shared client and the credential provider, and keeps it
/// behind <see cref="ReconfigurableUsageReadPort"/>.
/// </summary>
public sealed class HttpUsageReadPort : IUsageReadPort
{
    /// <summary>Maximum daily rows accepted from one response (the API's own read bound).</summary>
    public const int MaxRows = 5_000;

    /// <summary>Maximum collector freshness entries accepted from one response (the API's own bound).</summary>
    public const int MaxCollectors = 64;

    /// <summary>Maximum query span in days (the API's own query bound).</summary>
    public const int MaxQuerySpanDays = 366;

    /// <summary>Maximum response body accepted, in bytes.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>Largest integer the API can carry without losing precision.</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991L;

    private const string DailyPath = "/v1/usage/daily";
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>Exact form of a collector reception time: UTC, milliseconds, `Z`.</summary>
    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _identityId;
    private readonly IUsageCredentialProvider _credentials;
    private readonly TimeSpan _requestDeadline;

    /// <summary>
    /// Creates the port for one fixed HTTPS origin and one expected identity,
    /// using <see cref="DefaultRequestDeadline"/>.
    /// </summary>
    /// <param name="client">Injected client; it must not carry a default Authorization, Access service-token, or Cookie header.</param>
    /// <param name="origin">Absolute <c>https</c> origin with no path, user info, query or fragment.</param>
    /// <param name="identityId">Identity this port is scoped to; a provider answer for another identity is refused.</param>
    /// <param name="credentials">Per-request credential provider.</param>
    public HttpUsageReadPort(
        HttpClient client,
        Uri origin,
        string identityId,
        IUsageCredentialProvider credentials)
        : this(client, origin, identityId, credentials, DefaultRequestDeadline)
    {
    }

    /// <summary>
    /// Creates the port for one fixed HTTPS origin and one expected identity with
    /// an explicit finite deadline for the whole exchange.
    /// </summary>
    /// <param name="client">Injected client; it must not carry a default Authorization, Access service-token, or Cookie header.</param>
    /// <param name="origin">Absolute <c>https</c> origin with no path, user info, query or fragment.</param>
    /// <param name="identityId">Identity this port is scoped to; a provider answer for another identity is refused.</param>
    /// <param name="credentials">Per-request credential provider.</param>
    /// <param name="requestDeadline">
    /// Positive finite deadline for the response headers <em>and</em> the response
    /// body. It is applied through a token linked to the caller's, so a caller
    /// cancellation is never reported as a timeout and vice versa.
    /// </param>
    public HttpUsageReadPort(
        HttpClient client,
        Uri origin,
        string identityId,
        IUsageCredentialProvider credentials,
        TimeSpan requestDeadline)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(identityId))
        {
            throw new ArgumentException("A port identity is required.", nameof(identityId));
        }

        if (requestDeadline <= TimeSpan.Zero || requestDeadline == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestDeadline),
                "The request deadline must be a positive, finite duration.");
        }

        _endpoint = BuildEndpoint(origin);
        RejectForbiddenDefaultHeaders(client);

        _client = client;
        _identityId = identityId;
        _credentials = credentials;
        _requestDeadline = requestDeadline;
    }

    /// <summary>
    /// Default deadline covering the response headers and the response body of one
    /// read. A client timeout alone does not bound the body once the headers are read.
    /// </summary>
    public static TimeSpan DefaultRequestDeadline => TimeSpan.FromSeconds(30);

    /// <summary>Fixed request endpoint, without query.</summary>
    public Uri Endpoint => _endpoint;

    /// <summary>Deadline covering the headers and the body of one read.</summary>
    public TimeSpan RequestDeadline => _requestDeadline;

    /// <inheritdoc />
    public async Task<DailyUsage> ReadDailyAsync(
        DateOnly from,
        DateOnly to,
        string? collectorId,
        CancellationToken cancellationToken)
    {
        var span = to.DayNumber - from.DayNumber;
        if (span < 0 || span > MaxQuerySpanDays - 1)
        {
            throw new UsageReadException(
                UsageReadFailure.InvalidRequest,
                "The requested daily range is inverted or longer than the supported window.");
        }

        if (collectorId is not null && !IsCollectorId(collectorId))
        {
            throw new UsageReadException(
                UsageReadFailure.InvalidRequest,
                "The collector filter is not a valid collector id.");
        }

        // One request, one credential, no memoisation: a response is never reused
        // for a later call or for a different identity.
        var credential = await _credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            throw new UsageReadException(
                UsageReadFailure.CredentialUnavailable,
                "No usage read credential is available.");
        }

        if (!string.Equals(credential.IdentityId, _identityId, StringComparison.Ordinal))
        {
            throw new UsageReadException(
                UsageReadFailure.IdentityMismatch,
                "The available credential belongs to a different identity than this reader.");
        }

        var requestUri = BuildRequestUri(from, to, collectorId);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        // Applied to this request only; never as a client default, so a second
        // identity can never inherit the first one's headers. Access is the gate:
        // these two service-token headers replace Authorization.
        request.Headers.TryAddWithoutValidation("CF-Access-Client-Id", credential.ClientId);
        request.Headers.TryAddWithoutValidation("CF-Access-Client-Secret", credential.ClientSecret);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        // A client timeout stops bounding the exchange once the response headers are
        // read, so this port applies its own finite deadline to the headers *and* the
        // body. The token is linked to the caller's, so a timeout is never reported as
        // a caller cancellation, and a caller cancellation is never reported as a
        // timeout, in either phase.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestDeadline);
        var exchangeToken = deadline.Token;

        HttpResponseMessage response;
        try
        {
            response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, exchangeToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation is not a failure of the remote service.
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw TimeoutFailure("The usage service did not answer in time.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new UsageReadException(
                UsageReadFailure.TransportFailure,
                "The usage service could not be reached.",
                innerException: exception);
        }

        using (response)
        {
            // Checked before the status: an answer that came from another location is
            // never classified, parsed or surfaced as this endpoint's data.
            EnsureRequestUri(response, requestUri);
            EnsureStatus(response);
            EnsureContent(response);

            byte[]? bytes;
            try
            {
                bytes = await ReadBoundedAsync(response.Content, exchangeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancelling while the body is still arriving is still the caller's act.
                throw;
            }
            catch (OperationCanceledException exception)
            {
                throw TimeoutFailure("The usage response body did not arrive in time.", exception);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                // A body that dies mid-read is a transport failure, not a malformed
                // document: nothing is parsed and nothing is guessed from a prefix.
                throw new UsageReadException(
                    UsageReadFailure.TransportFailure,
                    "The usage response body could not be read.",
                    innerException: exception);
            }

            if (bytes is null)
            {
                throw new UsageReadException(
                    UsageReadFailure.ResponseTooLarge,
                    "The usage response exceeded the bounded read size.");
            }

            return Parse(bytes, from, to, collectorId);
        }
    }

    private static UsageReadException TimeoutFailure(string message, Exception innerException) =>
        new(UsageReadFailure.Timeout, message, innerException: innerException);

    private static readonly string[] ForbiddenDefaultHeaders =
    [
        "Authorization",
        "CF-Access-Client-Id",
        "CF-Access-Client-Secret",
        "Cookie",
    ];

    /// <summary>
    /// Rejects a client that already carries a credential or cookie header. The
    /// message is fixed and never includes the header value.
    /// </summary>
    private static void RejectForbiddenDefaultHeaders(HttpClient client)
    {
        if (client.DefaultRequestHeaders.Authorization is not null || HasForbiddenDefaultHeader(client))
        {
            throw new ArgumentException(
                "The injected client must not carry a default Authorization, Access service-token, or Cookie header; credentials are applied per request.",
                nameof(client));
        }
    }

    private static bool HasForbiddenDefaultHeader(HttpClient client)
    {
        foreach (var header in client.DefaultRequestHeaders)
        {
            foreach (var forbidden in ForbiddenDefaultHeaders)
            {
                if (string.Equals(header.Key, forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Rejects an answer that reports a different request URI than the one this port
    /// sent. The runtime transport factory disables redirects; this check is what
    /// catches a follow-up request made by anything else (for example an injected
    /// handler), because a response that names its request must name exactly this
    /// one. A response that names no request cannot be checked this way.
    /// </summary>
    private static void EnsureRequestUri(HttpResponseMessage response, Uri requested)
    {
        var answered = response.RequestMessage?.RequestUri;
        if (answered is null || IsSameUri(answered, requested))
        {
            return;
        }

        throw new UsageReadException(
            UsageReadFailure.RedirectRejected,
            "The usage service answered a different URI than the one that was requested.");
    }

    private static bool IsSameUri(Uri actual, Uri expected) =>
        actual.IsAbsoluteUri
        && string.Equals(actual.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(actual.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase)
        && actual.Port == expected.Port
        && string.Equals(actual.AbsolutePath, expected.AbsolutePath, StringComparison.Ordinal)
        && string.Equals(actual.Query, expected.Query, StringComparison.Ordinal);

    private static Uri BuildEndpoint(Uri origin)
    {
        if (!origin.IsAbsoluteUri)
        {
            throw new ArgumentException("The origin must be an absolute URI.", nameof(origin));
        }

        if (!string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The origin must use https.", nameof(origin));
        }

        if (!string.IsNullOrEmpty(origin.UserInfo))
        {
            throw new ArgumentException("The origin must not carry user information.", nameof(origin));
        }

        if (!string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
        {
            throw new ArgumentException("The origin must not carry a query or fragment.", nameof(origin));
        }

        if (!string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal))
        {
            throw new ArgumentException("The origin must not carry a path prefix.", nameof(origin));
        }

        return new UriBuilder(origin) { Path = DailyPath }.Uri;
    }

    private Uri BuildRequestUri(DateOnly from, DateOnly to, string? collectorId)
    {
        var query = "from=" + FormatDate(from) + "&to=" + FormatDate(to);
        if (collectorId is not null)
        {
            // collectorId was validated as a slug above, so it needs no escaping
            // and cannot introduce another parameter.
            query += "&collector=" + collectorId;
        }

        return new UriBuilder(_endpoint) { Query = query }.Uri;
    }

    private static void EnsureStatus(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return;
            case HttpStatusCode.Unauthorized:
                throw new UsageReadException(
                    UsageReadFailure.Unauthorized,
                    "The usage service rejected the credential.",
                    status);
            case HttpStatusCode.Forbidden:
                throw new UsageReadException(
                    UsageReadFailure.Forbidden,
                    "The credential is not allowed to read usage.",
                    status);
            case HttpStatusCode.NotFound:
                throw new UsageReadException(
                    UsageReadFailure.NotFound,
                    "The usage service reported that the requested resource does not exist.",
                    status);
            case HttpStatusCode.ServiceUnavailable:
                // 503 from this service is auth_unavailable: retryable, and classified
                // from the status alone so the body is never read or echoed.
                throw new UsageReadException(
                    UsageReadFailure.AuthUnavailable,
                    "The usage service could not authenticate the request right now.",
                    status);
            case HttpStatusCode.TooManyRequests:
                // Rate limiting is a temporary condition, never a credential problem.
                throw new UsageReadException(
                    UsageReadFailure.Transient,
                    "The usage service asked this client to retry later.",
                    status);
        }

        if (status is >= 500 and <= 599)
        {
            // Any other server-side failure is retryable and is never confused with a
            // rejected credential. The status is the only input; the body is not read.
            throw new UsageReadException(
                UsageReadFailure.Transient,
                "The usage service failed temporarily.",
                status);
        }

        if (status is >= 300 and <= 399)
        {
            // The client's handler disables redirects; a redirect that still
            // arrives is reported and never followed.
            throw new UsageReadException(
                UsageReadFailure.RedirectRejected,
                "The usage service answered with a redirect, which is never followed.",
                status);
        }

        throw new UsageReadException(
            UsageReadFailure.UnexpectedStatus,
            "The usage service answered with an unexpected status.",
            status);
    }

    private static void EnsureContent(HttpResponseMessage response)
    {
        foreach (var encoding in response.Content.Headers.ContentEncoding)
        {
            if (!string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase))
            {
                throw new UsageReadException(
                    UsageReadFailure.UnsupportedContent,
                    "A compressed usage response is not accepted; the transport negotiates no content encoding.");
            }
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageReadException(
                UsageReadFailure.UnsupportedContent,
                "The usage response must be application/json.");
        }
    }

    /// <summary>Reads at most <see cref="MaxResponseBytes"/>; <c>null</c> means the bound was hit.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declared && declared > MaxResponseBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var read = await stream
                    .ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > MaxResponseBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static DailyUsage Parse(byte[] bytes, DateOnly from, DateOnly to, string? collectorId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException exception)
        {
            throw new UsageReadException(
                UsageReadFailure.MalformedResponse,
                "The usage response was not valid JSON.",
                innerException: exception);
        }

        using (document)
        {
            var root = document.RootElement;
            RequireObjectWithExactFields(root, ["from", "to", "days", "totalTokens", "collectors"]);

            var echoedFrom = ReadStrictDate(root, "from");
            var echoedTo = ReadStrictDate(root, "to");
            if (echoedFrom != from || echoedTo != to)
            {
                throw new UsageReadException(
                    UsageReadFailure.RangeMismatch,
                    "The usage response echoed a different range than the one requested.");
            }

            // Freshness first, so the day rows can be checked against the collectors
            // the response says it covers.
            var collectors = ReadCollectors(root, collectorId);
            var coveredCollectors = new HashSet<string>(
                collectors.Select(entry => entry.CollectorId), StringComparer.Ordinal);

            var rows = ReadRequiredField(root, "days");
            if (rows.ValueKind != JsonValueKind.Array)
            {
                throw Malformed("The usage response days field is not an array.");
            }

            if (rows.GetArrayLength() > MaxRows)
            {
                throw new UsageReadException(
                    UsageReadFailure.ResponseTooLarge,
                    "The usage response contained more rows than the daily read bound.");
            }

            var days = new List<UsageDay>(rows.GetArrayLength());
            long recomputedTotal = 0;
            DateOnly? previousDate = null;
            string? previousCollector = null;

            foreach (var row in rows.EnumerateArray())
            {
                RequireObjectWithExactFields(row, ["date", "collectorId", "totalTokens"]);

                var date = ReadStrictDate(row, "date");
                if (date < from || date > to)
                {
                    throw new UsageReadException(
                        UsageReadFailure.RangeMismatch,
                        "The usage response contained a day outside the requested range.");
                }

                var rowCollector = ReadRequiredField(row, "collectorId");
                if (rowCollector.ValueKind != JsonValueKind.String)
                {
                    throw Malformed("A usage row has no collector id.");
                }

                var rowCollectorId = rowCollector.GetString()!;
                if (!IsCollectorId(rowCollectorId))
                {
                    throw Malformed("A usage row has an invalid collector id.");
                }

                if (!coveredCollectors.Contains(rowCollectorId))
                {
                    // A row for a collector the response does not describe would leave
                    // its freshness unknown, so the answer is refused instead of shown.
                    throw Malformed("A usage row names a collector the response does not describe.");
                }

                if (collectorId is not null && !string.Equals(rowCollectorId, collectorId, StringComparison.Ordinal))
                {
                    throw new UsageReadException(
                        UsageReadFailure.FilterMismatch,
                        "The usage response contained a row for a different collector than the one requested.");
                }

                if (previousDate is { } lastDate)
                {
                    var ordered = date.CompareTo(lastDate);
                    var sameDayOrdered = ordered == 0
                        && string.CompareOrdinal(rowCollectorId, previousCollector) > 0;
                    if (ordered < 0 || (ordered == 0 && !sameDayOrdered))
                    {
                        throw Malformed("The usage rows are not in the documented ascending order.");
                    }
                }

                previousDate = date;
                previousCollector = rowCollectorId;

                var tokens = ReadSafeInteger(row, "totalTokens");
                if (recomputedTotal > MaxSafeInteger - tokens)
                {
                    throw new UsageReadException(
                        UsageReadFailure.InconsistentTotal,
                        "The daily totals cannot be aggregated within the safe integer range.");
                }

                recomputedTotal += tokens;
                days.Add(new UsageDay(date, rowCollectorId, tokens));
            }

            var echoedTotal = ReadSafeInteger(root, "totalTokens");
            if (echoedTotal != recomputedTotal)
            {
                throw new UsageReadException(
                    UsageReadFailure.InconsistentTotal,
                    "The echoed total does not equal the sum of the returned days.");
            }

            return new DailyUsage(from, to, days, recomputedTotal, collectors);
        }
    }

    /// <summary>
    /// Parses the `collectors` freshness list: exact fields, ascending unique
    /// collector ids, a positive safe-integer generation, and a strict UTC instant
    /// with milliseconds or `null`. Each collector is one entry and the list is
    /// bounded; nothing is defaulted or merged.
    /// </summary>
    private static List<UsageCollectorReceipt> ReadCollectors(JsonElement root, string? collectorFilter)
    {
        var entries = ReadRequiredField(root, "collectors");
        if (entries.ValueKind != JsonValueKind.Array)
        {
            throw Malformed("The usage response collectors field is not an array.");
        }

        if (entries.GetArrayLength() > MaxCollectors)
        {
            throw new UsageReadException(
                UsageReadFailure.ResponseTooLarge,
                "The usage response described more collectors than the read bound.");
        }

        var collectors = new List<UsageCollectorReceipt>(entries.GetArrayLength());
        string? previousCollectorId = null;
        foreach (var entry in entries.EnumerateArray())
        {
            RequireObjectWithExactFields(entry, ["collectorId", "generation", "lastReceivedAt"]);

            var collectorValue = ReadRequiredField(entry, "collectorId");
            if (collectorValue.ValueKind != JsonValueKind.String)
            {
                throw Malformed("A collector entry has no collector id.");
            }

            var id = collectorValue.GetString()!;
            if (!IsCollectorId(id))
            {
                throw Malformed("A collector entry has an invalid collector id.");
            }

            if (collectorFilter is not null && !string.Equals(id, collectorFilter, StringComparison.Ordinal))
            {
                throw new UsageReadException(
                    UsageReadFailure.FilterMismatch,
                    "The usage response described a collector other than the one requested.");
            }

            if (previousCollectorId is not null)
            {
                var ordered = string.CompareOrdinal(id, previousCollectorId);
                if (ordered < 0)
                {
                    throw Malformed("The collector entries are not in the documented ascending order.");
                }

                if (ordered == 0)
                {
                    throw Malformed("The collector entries repeat a collector id.");
                }
            }

            previousCollectorId = id;

            var generation = ReadSafeInteger(entry, "generation");
            if (generation < 1)
            {
                throw Malformed("A collector generation is not a positive integer.");
            }

            collectors.Add(new UsageCollectorReceipt(id, generation, ReadOptionalUtcInstant(entry)));
        }

        return collectors;
    }

    /// <summary>
    /// Reads `lastReceivedAt`: `null`, or a strict `YYYY-MM-DDTHH:MM:SS.mmmZ` UTC
    /// instant. A local-time or offset form is refused rather than converted,
    /// because a silently reinterpreted reception time would misstate freshness.
    /// </summary>
    private static DateTimeOffset? ReadOptionalUtcInstant(JsonElement parent)
    {
        var value = ReadRequiredField(parent, "lastReceivedAt");
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Malformed("A collector reception time is neither an instant nor null.");
        }

        var raw = value.GetString();
        if (raw is null
            || raw.Length != 24
            || !DateTimeOffset.TryParseExact(
                raw,
                InstantFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            || !string.Equals(
                parsed.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture),
                raw,
                StringComparison.Ordinal))
        {
            throw Malformed("A collector reception time is not a strict UTC instant with milliseconds.");
        }

        return parsed;
    }

    /// <summary>
    /// Requires exactly the contract's fields, each present exactly once. An unknown
    /// field, a repeated field and a missing field are all the same typed failure:
    /// nothing here is defaulted, merged or overwritten by a later duplicate.
    /// </summary>
    private static void RequireObjectWithExactFields(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Malformed("The usage response is not a JSON object.");
        }

        var present = new bool[expected.Length];
        var seen = 0;
        foreach (var property in element.EnumerateObject())
        {
            var index = Array.IndexOf(expected, property.Name);
            if (index < 0)
            {
                throw Malformed("The usage response contains a field that is not part of the daily contract.");
            }

            if (present[index])
            {
                throw Malformed("The usage response repeats a field of the daily contract.");
            }

            present[index] = true;
            seen++;
        }

        if (seen != expected.Length)
        {
            throw Malformed("The usage response is missing a field of the daily contract.");
        }
    }

    /// <summary>
    /// Reads a field already proven present by <see cref="RequireObjectWithExactFields"/>.
    /// The lookup stays checked so a missing field can never surface as an untyped exception.
    /// </summary>
    private static JsonElement ReadRequiredField(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw Malformed("The usage response is missing a field of the daily contract.");
        }

        return value;
    }

    private static DateOnly ReadStrictDate(JsonElement parent, string name)
    {
        var value = ReadRequiredField(parent, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Malformed("A usage date is not a string.");
        }

        var raw = value.GetString();
        if (raw is null
            || raw.Length != 10
            || !DateOnly.TryParseExact(raw, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || !string.Equals(parsed.ToString(DateFormat, CultureInfo.InvariantCulture), raw, StringComparison.Ordinal))
        {
            throw Malformed("A usage date is not a strict YYYY-MM-DD date.");
        }

        return parsed;
    }

    /// <summary>
    /// Non-negative safe integer only. A missing or unreadable value is an
    /// error: nothing is ever defaulted to zero.
    /// </summary>
    private static long ReadSafeInteger(JsonElement parent, string name)
    {
        var value = ReadRequiredField(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed))
        {
            throw Malformed("A usage total is not an integer.");
        }

        if (parsed < 0 || parsed > MaxSafeInteger)
        {
            throw Malformed("A usage total is outside the non-negative safe integer range.");
        }

        return parsed;
    }

    private static bool IsCollectorId(string value)
    {
        if (value.Length is < 3 or > 64)
        {
            return false;
        }

        if (!IsLowerAlphaNumeric(value[0]))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsLowerAlphaNumeric(character) && character is not ('_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static string FormatDate(DateOnly value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

    private static UsageReadException Malformed(string message) =>
        new(UsageReadFailure.MalformedResponse, message);
}

/// <summary>
/// Encapsulated transport factory for the real deployment. It is the one place a
/// production handler is built, so redirects and cookies are disabled and no
/// content encoding is negotiated; <see cref="ReconfigurableUsageReadPort"/> owns
/// the single long-lived client built from it.
/// </summary>
public static class UsageReadTransport
{
    /// <summary>Creates the handler used for a real transport.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 4,
    };

    /// <summary>Creates a client over <see cref="CreateHandler"/> with an explicit timeout.</summary>
    public static HttpClient CreateClient(TimeSpan timeout) =>
        new(CreateHandler(), disposeHandler: true) { Timeout = timeout };
}
