namespace QuotaSight.Collector;

/// <summary>
/// Entry point. stdin/stdout/stderr are the explicit streams the operator
/// redirects; the tool has no other input or output channel.
/// </summary>
internal static class Program
{
    private static int Main(string[] args) =>
        CollectorCli.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error);
}
