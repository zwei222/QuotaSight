using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace QuotaSight.Collector;

/// <summary>
/// HTTPS transport for one explicit snapshot send. Redirects and cookies are
/// disabled. <see cref="SocketsHttpHandler.UseProxy"/> stays at its default.
/// </summary>
public static class CollectorSnapshotTransport
{
    /// <summary>Total attempts, including the first send. Not a retry count on top of that.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Per-attempt deadline.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Success bodies larger than this are refused and never quoted.</summary>
    public const int MaxResponseBytes = 8 * 1024;

    private static readonly string[] ForbiddenDefaultHeaders =
    [
        "Authorization",
        "CF-Access-Client-Id",
        "CF-Access-Client-Secret",
        "Cookie",
    ];

    /// <summary>Creates the production handler. Does not open a socket.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    };

    /// <summary>Creates a client over <see cref="CreateHandler"/> with the send timeout.</summary>
    public static HttpClient CreateClient() =>
        new(CreateHandler(), disposeHandler: true) { Timeout = RequestTimeout };

    /// <summary>
    /// Delay before the next attempt after <paramref name="failuresSoFar"/> failures.
    /// Base is 1s, doubled each failure, plus up to 25% jitter. The unit is clamped to [0, 1].
    /// </summary>
    public static TimeSpan Backoff(int failuresSoFar, double jitterUnit)
    {
        if (failuresSoFar < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failuresSoFar));
        }

        var unit = jitterUnit < 0 ? 0 : jitterUnit > 1 ? 1 : jitterUnit;
        var baseMs = 1000d * Math.Pow(2, failuresSoFar - 1);
        return TimeSpan.FromMilliseconds(baseMs + (baseMs * 0.25 * unit));
    }

    /// <summary>
    /// True when the injected client already carries a credential or cookie header.
    /// The check is case-insensitive and never reads header values into a message.
    /// </summary>
    public static bool HasForbiddenDefaultHeader(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.DefaultRequestHeaders.Authorization is not null)
        {
            return true;
        }

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
    /// PUT the exact wire bytes. Same revision and body on every attempt.
    /// 2xx is success, including the API's identical-payload replay (also 2xx).
    /// 409/412/422 and other 4xx are not retried. 5xx, timeouts and transport
    /// failures retry up to <see cref="MaxAttempts"/>. The response body is not
    /// quoted; a 2xx body over <see cref="MaxResponseBytes"/> is refused.
    /// </summary>
    public static int Transmit(
        HttpClient client,
        Uri endpoint,
        string collectorId,
        long revision,
        byte[] body,
        CollectorAccessCredential credential,
        Action<TimeSpan> delay,
        Func<double> nextJitterUnit,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(collectorId);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(nextJitterUnit);
        ArgumentNullException.ThrowIfNull(stderr);

        var uri = BuildSnapshotUri(endpoint, collectorId, revision);
        Attempt last = default;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            last = Once(client, uri, body, credential);
            if (!last.Retryable || attempt == MaxAttempts)
            {
                break;
            }

            delay(Backoff(attempt, nextJitterUnit()));
        }

        stderr.WriteLine(last.Message);
        return last.ExitCode;
    }

    private static Uri BuildSnapshotUri(Uri endpoint, string collectorId, long revision)
    {
        var builder = new UriBuilder(endpoint)
        {
            Path = "/v1/collectors/" + collectorId + "/snapshots/" + revision.ToString(CultureInfo.InvariantCulture),
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
        };
        return builder.Uri;
    }

    private static Attempt Once(HttpClient client, Uri uri, byte[] body, CollectorAccessCredential credential)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;
        if (!request.Headers.TryAddWithoutValidation("CF-Access-Client-Id", credential.ClientId)
            || !request.Headers.TryAddWithoutValidation("CF-Access-Client-Secret", credential.ClientSecret))
        {
            return Attempt.Final(CollectorExitCodes.SendRejected, "send_rejected: request rejected");
        }

        using var timeout = new CancellationTokenSource(RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return Attempt.Retry(CollectorExitCodes.SendTimeout, "send_timeout: deadline exceeded");
        }
        catch (HttpRequestException)
        {
            return Attempt.Retry(CollectorExitCodes.SendTransport, "send_transport: transport failed");
        }

        using (response)
        {
            if (AnsweredDifferentUri(response, uri))
            {
                return Attempt.Final(CollectorExitCodes.SendRedirect, "send_redirect: redirect not followed");
            }

            var status = (int)response.StatusCode;
            if (status is >= 300 and <= 399)
            {
                return Attempt.Final(CollectorExitCodes.SendRedirect, "send_redirect: redirect not followed");
            }

            if (status == (int)HttpStatusCode.Unauthorized)
            {
                return Attempt.Final(CollectorExitCodes.SendUnauthorized, "send_unauthorized: authentication failed");
            }

            if (status == (int)HttpStatusCode.Forbidden)
            {
                return Attempt.Final(CollectorExitCodes.SendForbidden, "send_forbidden: authorization failed");
            }

            if (status == (int)HttpStatusCode.Conflict)
            {
                return Attempt.Final(CollectorExitCodes.SendConflict, "send_conflict: revision rejected");
            }

            if (status == (int)HttpStatusCode.PreconditionFailed)
            {
                return Attempt.Final(CollectorExitCodes.SendPrecondition, "send_precondition: precondition failed");
            }

            if (status == 422)
            {
                return Attempt.Final(CollectorExitCodes.SendUnprocessable, "send_unprocessable: snapshot not processable");
            }

            if (status is >= 400 and <= 499)
            {
                return Attempt.Final(CollectorExitCodes.SendRejected, "send_rejected: request rejected");
            }

            if (status is >= 500 and <= 599)
            {
                return Attempt.Retry(CollectorExitCodes.SendUnavailable, "send_unavailable: service unavailable");
            }

            if (status is >= 200 and <= 299)
            {
                if (SuccessBodyTooLarge(response, timeout.Token))
                {
                    return Attempt.Final(CollectorExitCodes.SendResponseTooLarge, "send_response_too_large: response too large");
                }

                // 2xx covers both applied and replayed. The API returns 200 for an
                // identical revision+payload replay; the body is not read to tell them apart.
                return Attempt.Final(CollectorExitCodes.Candidate, "sent: accepted");
            }

            return Attempt.Final(CollectorExitCodes.SendRejected, "send_rejected: request rejected");
        }
    }

    private static bool AnsweredDifferentUri(HttpResponseMessage response, Uri requested)
    {
        var answered = response.RequestMessage?.RequestUri;
        if (answered is null || !answered.IsAbsoluteUri)
        {
            return false;
        }

        return !string.Equals(answered.Scheme, requested.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(answered.IdnHost, requested.IdnHost, StringComparison.OrdinalIgnoreCase)
            || answered.Port != requested.Port
            || !string.Equals(answered.AbsolutePath, requested.AbsolutePath, StringComparison.Ordinal)
            || !string.Equals(answered.Query, requested.Query, StringComparison.Ordinal);
    }

    private static bool SuccessBodyTooLarge(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = response.Content;
        if (content is null)
        {
            return false;
        }

        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            return true;
        }

        if (content.Headers.ContentLength is not null)
        {
            return false;
        }

        using var stream = content.ReadAsStream(cancellationToken);
        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total > MaxResponseBytes;
    }

    private readonly struct Attempt
    {
        private Attempt(int exitCode, string message, bool retryable)
        {
            ExitCode = exitCode;
            Message = message;
            Retryable = retryable;
        }

        public int ExitCode { get; }

        public string Message { get; }

        public bool Retryable { get; }

        public static Attempt Final(int exitCode, string message) => new(exitCode, message, retryable: false);

        public static Attempt Retry(int exitCode, string message) => new(exitCode, message, retryable: true);
    }
}
