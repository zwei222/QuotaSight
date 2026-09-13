using QuotaSight.Application;
using QuotaSight.Core;
namespace QuotaSight.Infrastructure;

public sealed class ManualMetadataAdapter(ProviderKind provider, string metric, string url) : IQuotaAdapter
{
    public ProviderKind Provider => provider;
    public ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<FetchResult<IReadOnlyList<QuotaSnapshot>>>(new(FetchStatus.Unsupported, Error: $"Manual entry required for {metric}; official URL: {url}"));
}
public sealed class CopilotAdapter : IQuotaAdapter
{
    public ProviderKind Provider => ProviderKind.Copilot;
    public ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<FetchResult<IReadOnlyList<QuotaSnapshot>>>(new(FetchStatus.Unsupported, Error: "Organization quota requires supported contract/permission; use manual fallback."));
}
