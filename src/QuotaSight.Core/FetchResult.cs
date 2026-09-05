namespace QuotaSight.Core;

public sealed record FetchResult<T>(FetchStatus Status, T? Value = default, TimeSpan? RetryAfter = null, string? Error = null)
{
    public bool IsSuccess => Status == FetchStatus.Success;
    public static FetchResult<T> Success(T value) => new(FetchStatus.Success, value);
}
public enum FetchStatus { Success, Unsupported, Unauthorized, Forbidden, RateLimited, TransientFailure }
