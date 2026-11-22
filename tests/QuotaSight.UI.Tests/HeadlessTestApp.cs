using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(QuotaSight.UI.Tests.SkiaHeadlessTestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly, DisableTestParallelization = true)]

namespace QuotaSight.UI.Tests;

public sealed class SkiaHeadlessTestAppBuilder
{
    // Real Skia rendering (not the no-op headless drawing path) so tests can capture rendered
    // frames and assert the actual rendered output pixel-by-pixel.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia();
}
