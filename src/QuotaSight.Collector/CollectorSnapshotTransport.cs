using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace QuotaSight.Collector;

/// <summary>
/// The result of one explicit send, as the state file needs to record it.
/// The message is a fixed reason class and never quotes a body, a header, a URL
/// or an identifier from the response.
/// </summary>
/// <param name="ExitCode">Process exit code for this outcome.</param>
/// <param name="Message">Line for stderr. Fixed tokens only.</param>
/// <param name="Outcome">What may be recorded in the state file.</param>
/// <param name="ConflictReason">Allowlisted 409 reason, or <c>conflict_unknown</c>.</param>
/// <param name="IgnoredReductions">
/// Days the API accepted but did not write because their accepted total was
/// higher. Always <c>0</c> unless a receipt was verified.
/// </param>
public sealed record SendOutcome(
    int ExitCode,
    string Message,
    CollectorSendOutcome Outcome,
    string? ConflictReason,
    int IgnoredReductions = 0);

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

    /// <summary>Response bodies larger than this are refused and never quoted.</summary>
    public const int MaxResponseBytes = 8 * 1024;

    private static readonly string[] ForbiddenDefaultHeaders =
    [
        "Authorization",
        "CF-Access-Client-Id",
        "CF-Access-Client-Secret",
        "Cookie",
    ];

    private static readonly string[] ReceiptKeys =
    [
        "status",
        "collectorId",
        "generation",
        "revision",
        "receivedAt",
        "changedDays",
        "ignoredReductions",
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
    /// PUT the exact wire bytes. The same revision and body are used on every
    /// attempt.
    ///
    /// A 2xx is only a success when the body is the receipt the API documents for
    /// this collector, generation and revision; anything else 2xx is
    /// <see cref="CollectorSendOutcome.UnverifiedReceipt"/>. A 409 records the
    /// reason word from the allowlist (an unknown or unreadable body becomes
    /// <c>conflict_unknown</c>) and is never retried. Other 4xx are rejected
    /// without a retry. 5xx, timeouts and transport failures retry up to
    /// <see cref="MaxAttempts"/> and leave the outcome unknown.
    /// </summary>
    public static SendOutcome Transmit(
        HttpClient client,
        Uri endpoint,
        string collectorId,
        long generation,
        long revision,
        byte[] body,
        CollectorAccessCredential credential,
        Action<TimeSpan> delay,
        Func<double> nextJitterUnit)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(collectorId);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(nextJitterUnit);

        var uri = BuildSnapshotUri(endpoint, collectorId, revision);
        var last = Unavailable();
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            last = Once(client, uri, body, credential, collectorId, generation, revision);
            if (!IsRetryable(last) || attempt == MaxAttempts)
            {
                break;
            }

            delay(Backoff(attempt, nextJitterUnit()));
        }

        return last;
    }

    /// <summary>
    /// True when <paramref name="body"/> is the receipt the API returns for this
    /// write: exactly the documented keys, this collector, generation and
    /// revision, a canonical instant, ascending unique days and a count of
    /// ignored reductions that fits the API's per-snapshot day bound.
    /// <paramref name="ignoredReductions"/> is only meaningful when this is true.
    /// </summary>
    public static bool IsExpectedReceipt(
        ReadOnlySpan<byte> body,
        string collectorId,
        long generation,
        long revision,
        out int ignoredReductions)
    {
        ArgumentNullException.ThrowIfNull(collectorId);
        ignoredReductions = 0;
        if (body.IsEmpty || body.Length > MaxResponseBytes)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 3,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var status = false;
            var collector = false;
            var generationMatches = false;
            var revisionMatches = false;
            var instant = false;
            var days = false;
            var ignored = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return !reader.Read() && status && collector && generationMatches
                        && revisionMatches && instant && days && ignored && seen.Count == ReceiptKeys.Length;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                var name = reader.GetString()!;
                if (!seen.Add(name) || !reader.Read())
                {
                    return false;
                }

                switch (name)
                {
                    case "status":
                        status = reader.TokenType == JsonTokenType.String
                            && (reader.ValueTextEquals("applied"u8) || reader.ValueTextEquals("replayed"u8));
                        break;
                    case "collectorId":
                        collector = reader.TokenType == JsonTokenType.String
                            && string.Equals(reader.GetString(), collectorId, StringComparison.Ordinal);
                        break;
                    case "generation":
                        generationMatches = reader.TokenType == JsonTokenType.Number
                            && reader.TryGetInt64(out var receivedGeneration)
                            && receivedGeneration == generation;
                        break;
                    case "revision":
                        revisionMatches = reader.TokenType == JsonTokenType.Number
                            && reader.TryGetInt64(out var receivedRevision)
                            && receivedRevision == revision;
                        break;
                    case "receivedAt":
                        instant = reader.TokenType == JsonTokenType.String && IsCanonicalInstant(reader.GetString());
                        break;
                    case "changedDays":
                        days = IsDayList(ref reader);
                        break;
                    case "ignoredReductions":
                        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var count))
                        {
                            ignored = count is >= 0 and <= TokscaleGraphContract.MaxDays;
                            if (ignored)
                            {
                                ignoredReductions = count;
                            }
                        }

                        break;
                    default:
                        return false;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Maps a 409 body to an allowlisted reason word.</summary>
    public static string ConflictReason(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty || body.Length > MaxResponseBytes)
        {
            return CollectorSendState.UnknownConflictReason;
        }

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 3,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject || !reader.Read())
            {
                return CollectorSendState.UnknownConflictReason;
            }

            if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("error"u8))
            {
                return CollectorSendState.UnknownConflictReason;
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return CollectorSendState.UnknownConflictReason;
            }

            string? code = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return CollectorSendState.UnknownConflictReason;
                }

                var name = reader.GetString();
                if (!reader.Read())
                {
                    return CollectorSendState.UnknownConflictReason;
                }

                if (string.Equals(name, "code", StringComparison.Ordinal))
                {
                    if (reader.TokenType != JsonTokenType.String || code is not null)
                    {
                        return CollectorSendState.UnknownConflictReason;
                    }

                    code = reader.GetString();
                }
                else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
            }

            return code is null ? CollectorSendState.UnknownConflictReason : CollectorSendState.NormalizeConflictReason(code);
        }
        catch (JsonException)
        {
            return CollectorSendState.UnknownConflictReason;
        }
        catch (InvalidOperationException)
        {
            return CollectorSendState.UnknownConflictReason;
        }
    }

    private static bool IsRetryable(SendOutcome outcome) =>
        outcome.ExitCode is CollectorExitCodes.SendUnavailable
            or CollectorExitCodes.SendTimeout
            or CollectorExitCodes.SendTransport;

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

    private static SendOutcome Once(
        HttpClient client,
        Uri uri,
        byte[] body,
        CollectorAccessCredential credential,
        string collectorId,
        long generation,
        long revision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;
        if (!request.Headers.TryAddWithoutValidation("CF-Access-Client-Id", credential.ClientId)
            || !request.Headers.TryAddWithoutValidation("CF-Access-Client-Secret", credential.ClientSecret))
        {
            return Rejected(CollectorExitCodes.SendRejected, "send_rejected: request rejected");
        }

        using var timeout = new CancellationTokenSource(RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return Unknown(CollectorExitCodes.SendTimeout, "send_timeout: deadline exceeded");
        }
        catch (HttpRequestException)
        {
            return Unknown(CollectorExitCodes.SendTransport, "send_transport: transport failed");
        }

        using (response)
        {
            if (AnsweredDifferentUri(response, uri))
            {
                return Rejected(CollectorExitCodes.SendRedirect, "send_redirect: redirect not followed");
            }

            var status = (int)response.StatusCode;
            if (status is >= 300 and <= 399)
            {
                return Rejected(CollectorExitCodes.SendRedirect, "send_redirect: redirect not followed");
            }

            if (status == (int)HttpStatusCode.Unauthorized)
            {
                return Rejected(CollectorExitCodes.SendUnauthorized, "send_unauthorized: authentication failed");
            }

            if (status == (int)HttpStatusCode.Forbidden)
            {
                return Rejected(CollectorExitCodes.SendForbidden, "send_forbidden: authorization failed");
            }

            if (status == (int)HttpStatusCode.Conflict)
            {
                var reason = ReadBounded(response, timeout.Token, out var conflictBody) == BodyRead.Ok
                    ? ConflictReason(conflictBody)
                    : CollectorSendState.UnknownConflictReason;
                return new SendOutcome(
                    CollectorExitCodes.SendConflict,
                    "send_conflict: " + reason,
                    CollectorSendOutcome.Conflict,
                    reason);
            }

            if (status == (int)HttpStatusCode.PreconditionFailed)
            {
                return Rejected(CollectorExitCodes.SendPrecondition, "send_precondition: precondition failed");
            }

            if (status == 422)
            {
                return Rejected(CollectorExitCodes.SendUnprocessable, "send_unprocessable: snapshot not processable");
            }

            if (status is >= 400 and <= 499)
            {
                return Rejected(CollectorExitCodes.SendRejected, "send_rejected: request rejected");
            }

            if (status is >= 500 and <= 599)
            {
                return Unknown(CollectorExitCodes.SendUnavailable, "send_unavailable: service unavailable");
            }

            if (status is >= 200 and <= 299)
            {
                var read = ReadBounded(response, timeout.Token, out var receipt);
                if (read == BodyRead.TooLarge)
                {
                    return Unverified(CollectorExitCodes.SendResponseTooLarge, "send_response_too_large: response too large");
                }

                if (read != BodyRead.Ok
                    || !IsExpectedReceipt(receipt, collectorId, generation, revision, out var ignoredReductions))
                {
                    return Unverified(CollectorExitCodes.SendReceiptUnverified, "send_receipt_unverified: receipt not verified");
                }

                return new SendOutcome(
                    CollectorExitCodes.Candidate,
                    "sent: accepted",
                    CollectorSendOutcome.Accepted,
                    null,
                    ignoredReductions);
            }

            return Rejected(CollectorExitCodes.SendRejected, "send_rejected: request rejected");
        }
    }

    private static SendOutcome Unknown(int exitCode, string message) =>
        new(exitCode, message, CollectorSendOutcome.Unknown, null);

    private static SendOutcome Rejected(int exitCode, string message) =>
        new(exitCode, message, CollectorSendOutcome.Rejected, null);

    private static SendOutcome Unverified(int exitCode, string message) =>
        new(exitCode, message, CollectorSendOutcome.UnverifiedReceipt, null);

    private static SendOutcome Unavailable() =>
        new(CollectorExitCodes.SendUnavailable, "send_unavailable: service unavailable", CollectorSendOutcome.Unknown, null);

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

    private static bool IsCanonicalInstant(string? value) =>
        value is not null
        && DateTimeOffset.TryParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);

    private static bool IsDayList(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return false;
        }

        var previous = string.Empty;
        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return true;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                return false;
            }

            var date = reader.GetString();
            if (!IsCanonicalDate(date) || count == TokscaleGraphContract.MaxDays)
            {
                return false;
            }

            if (count > 0 && string.CompareOrdinal(date, previous) <= 0)
            {
                return false;
            }

            previous = date!;
            count++;
        }

        return false;
    }

    private static bool IsCanonicalDate(string? value) =>
        value is not null
        && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        && date.Year is >= TokscaleGraphContract.MinYear and <= TokscaleGraphContract.MaxYear;

    private static BodyRead ReadBounded(HttpResponseMessage response, CancellationToken cancellationToken, out byte[] body)
    {
        body = [];
        var content = response.Content;
        if (content is null)
        {
            return BodyRead.Ok;
        }

        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            return BodyRead.TooLarge;
        }

        try
        {
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

            if (total > MaxResponseBytes)
            {
                return BodyRead.TooLarge;
            }

            body = buffer.AsSpan(0, total).ToArray();
            return BodyRead.Ok;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException)
        {
            return BodyRead.Unreadable;
        }
    }

    private enum BodyRead
    {
        Ok,
        TooLarge,
        Unreadable,
    }
}
