using System.Diagnostics;
using System.ComponentModel;
using QuotaSight.Application;
using QuotaSight.Core;

namespace QuotaSight.Infrastructure;

public enum GhProbeStatus { AvailableAuthenticated, Unauthorized, Missing, Timeout, Error }
public sealed record GhProbeResult(GhProbeStatus Status, bool QuotaAvailable = false);
public sealed partial class GhCliProbe : IQuotaAdapter
{
    public ProviderKind Provider => ProviderKind.Copilot;
    public async ValueTask<FetchResult<IReadOnlyList<QuotaSnapshot>>> FetchAsync(string account, CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(cancellationToken);
        return probe.Status == GhProbeStatus.AvailableAuthenticated ? new(FetchStatus.Unsupported, Error: "Copilot quota is not exposed by gh auth status.") : new(probe.Status == GhProbeStatus.Unauthorized ? FetchStatus.Unauthorized : probe.Status == GhProbeStatus.Timeout ? FetchStatus.TransientFailure : FetchStatus.Unsupported);
    }

    public GhCliProbe(string executable = "gh", IReadOnlyList<string>? arguments = null, TimeSpan? timeout = null)
    {
        this.executable = executable; this.arguments = arguments ?? ["auth", "status"]; this.timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    private readonly string executable = "gh";
    private readonly IReadOnlyList<string> arguments = ["auth", "status"];
    private readonly TimeSpan timeout = TimeSpan.FromSeconds(15);

    public async ValueTask<GhProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info) ?? throw new Win32Exception();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeoutSource.CancelAfter(timeout);
            _ = process.StandardOutput.ReadToEndAsync(timeoutSource.Token); _ = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            return process.ExitCode == 0 ? new(GhProbeStatus.AvailableAuthenticated) : new(GhProbeStatus.Unauthorized);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(GhProbeStatus.Timeout); }
        catch (Win32Exception) { return new(GhProbeStatus.Missing); }
    }
}
