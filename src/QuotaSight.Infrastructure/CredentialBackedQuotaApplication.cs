using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public sealed class CredentialBackedQuotaApplication(
    Func<string, IQuotaAdapter> adapterFactory,
    ICredentialStore credentials,
    IQuotaHistory history,
    TimeProvider? timeProvider = null) : IQuotaApplication
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async ValueTask<IReadOnlyList<QuotaSnapshot>> RefreshAsync(CancellationToken cancellationToken)
    {
        var key = await credentials.GetAsync("OpenCode Go", cancellationToken);
        if (string.IsNullOrWhiteSpace(key)) return [];
        var result = await adapterFactory(key).FetchAsync("OpenCode Go", cancellationToken);
        if (!result.IsSuccess || result.Value is null || result.Value.Count == 0) return [];
        await history.AppendAsync(result.Value, cancellationToken);
        return result.Value;
    }
}
