using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using QuotaSight.UI;
using QuotaSight.UI.Controls;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class QuotaRingDrawingTests
{
    private const int Size = 86;

    private static Bitmap Render(params int?[] segments)
    {
        var ring = new QuotaRingControl
        {
            Width = Size,
            Height = Size,
            Segments = segments,
            TrackBrush = Brushes.LightGray,
            UnknownBrush = Brushes.Gray,
            DangerBrush = Brushes.Red,
            AttentionBrush = Brushes.Goldenrod,
            ValueBrush = Brushes.DarkGreen,
            InnerBrush = Brushes.DarkBlue,
        };
        ring.Measure(new Size(Size, Size));
        ring.Arrange(new Rect(0, 0, Size, Size));
        var target = new RenderTargetBitmap(new PixelSize(Size, Size));
        target.Render(ring);
        using var stream = new MemoryStream();
        target.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new Bitmap(stream);
    }

    /// <summary>Walks one ring and returns how many separate painted runs it has (1 = unbroken, more = dashed) plus whether it is painted at all.</summary>
    private static (int Runs, int Painted, Color First, int Greenness) Probe(Bitmap bitmap, int ringIndex)
    {
        var side = (double)Size;
        var width = Math.Clamp(side * .075, 3, 6);
        var gap = Math.Max(2, side * .035);
        var radius = side / 2 - 5 - ringIndex * (width + gap);
        var rowBytes = Size * 4;
        var bytes = new byte[rowBytes * Size];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, Size, Size), handle.AddrOfPinnedObject(), bytes.Length, rowBytes); }
        finally { handle.Free(); }
        bool PaintedAt(int degrees)
        {
            var radians = degrees * Math.PI / 180;
            var x = (int)Math.Round(side / 2 + Math.Cos(radians) * radius);
            var y = (int)Math.Round(side / 2 + Math.Sin(radians) * radius);
            return bytes[y * rowBytes + x * 4 + 3] > 128;
        }
        var samples = Enumerable.Range(0, 360).Select(PaintedAt).ToArray();
        var runs = 0;
        for (var i = 0; i < samples.Length; i++)
            if (samples[i] && !samples[(i + samples.Length - 1) % samples.Length]) runs++;
        if (runs == 0 && samples.All(s => s)) runs = 1;
        var cx = (int)Math.Round(side / 2); var cy = (int)Math.Round(side / 2 - radius);
        var o = cy * rowBytes + cx * 4;
        var greenness = 0;
        for (var d = 0; d < 360; d++)
        {
            var radians = d * Math.PI / 180;
            var px = (int)Math.Round(side / 2 + Math.Cos(radians) * radius); var py = (int)Math.Round(side / 2 + Math.Sin(radians) * radius);
            var i = py * rowBytes + px * 4;
            if (bytes[i + 3] > 128) greenness = Math.Max(greenness, bytes[i + 1] - Math.Max(bytes[i + 2], bytes[i]));
        }
        return (runs, samples.Count(s => s), Color.FromArgb(bytes[o + 3], bytes[o + 2], bytes[o + 1], bytes[o]), greenness);
    }

    [AvaloniaFact]
    public void ZeroRemainderGetsAVisibleDangerDotOnTheTrack()
    {
        var bitmap = Render(0);
        var probe = Probe(bitmap, 0);
        Assert.Equal(1, probe.Runs);
        Assert.True(probe.First.R > probe.First.G * 1.8, $"zero window marker should use the danger color, got {probe.First}");
    }

    [AvaloniaFact]
    public void RealZeroWindowDrawsAnUnbrokenEmptyTrackWithNoDashes()
    {
        var bitmap = Render(0, 5000);
        var (runs, painted, _, _) = Probe(bitmap, 0);
        Assert.Equal(1, runs);
        Assert.True(painted >= 350, $"zero ring must be a full track, painted {painted}/360");
    }

    [AvaloniaFact]
    public void UnknownWindowIsDashedWhileKnownInnerRingStaysSolid()
    {
        var bitmap = Render(5000, null);
        Assert.Equal(1, Probe(bitmap, 0).Runs);
        Assert.InRange(Probe(bitmap, 1).Runs, 4, 100);
    }

    [AvaloniaFact]
    public void ZeroOuterAndUnknownInnerAreVisiblyDifferent()
    {
        var bitmap = Render(0, null);
        var outer = Probe(bitmap, 0);
        var inner = Probe(bitmap, 1);
        Assert.Equal(1, outer.Runs);
        Assert.True(inner.Runs >= 4, $"unknown inner ring must be dashed, runs={inner.Runs}");
        Assert.NotEqual(inner.First, outer.First);
    }

    [AvaloniaFact]
    public void UnknownOuterAndZeroInnerAreVisiblyDifferent()
    {
        var bitmap = Render(null, 0);
        Assert.True(Probe(bitmap, 0).Runs >= 4);
        Assert.Equal(1, Probe(bitmap, 1).Runs);
    }

    [Fact]
    public void RollingWindowFeedsTheCenterWithPercentAndWindowName()
    {
        var now = DateTimeOffset.Parse("2026-11-22T10:00:00Z");
        var quota = new LatestQuota(now, [new QuotaProviderValue(QuotaProvider.OpenCodeGo, QuotaProviderStatus.Available, QuotaFreshness.Fresh, now, [new(QuotaWindow.Rolling, 6100, now.AddHours(3)), new(QuotaWindow.Monthly, 8800, now.AddDays(9))])]);
        var card = Assert.Single(QuotaCardProjection.Project(quota, false, now));
        Assert.Equal("61%", card.CenterText);
        Assert.False(string.IsNullOrWhiteSpace(card.CenterPeriodText));
        Assert.True(card.HasCenter);
    }

    [AvaloniaFact]
    public void UnknownDashesUseTheMutedUnknownColourNeverTheValueColour()
    {
        var bitmap = Render(0, null);
        var unknownColour = Probe(bitmap, 1);
        // sample a painted dash pixel colour: the dash must be neutral grey (R==G==B), not the green value brush
        Assert.True(unknownColour.Greenness < 20, $"unknown dashes look like a value ring (greenness {unknownColour.Greenness})");
        Assert.True(unknownColour.Runs >= 4);
        Assert.Equal(1, Probe(bitmap, 0).Runs);
    }
}

[Collection("culture")]
public sealed class QuotaResetTextCultureTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T10:00:00Z");

    private static string[] ResetTexts(bool japanese, string culture)
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
            var quota = new LatestQuota(Now, [new QuotaProviderValue(QuotaProvider.Claude, QuotaProviderStatus.Available, QuotaFreshness.Fresh, Now, [new(QuotaWindow.Session, 4000, Now.AddHours(5)), new(QuotaWindow.Weekly, 7000, Now.AddHours(41))])]);
            return QuotaCardProjection.Project(quota, japanese, Now).Single().Windows.Select(window => window.ResetText).ToArray();
        }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous; }
    }

    [Theory]
    [InlineData("ja-JP")]
    [InlineData("en-US")]
    public void EnglishResetTextNeverContainsJapaneseMeridiem(string culture)
    {
        foreach (var text in ResetTexts(false, culture))
        {
            Assert.DoesNotContain("午前", text, StringComparison.Ordinal);
            Assert.DoesNotContain("午後", text, StringComparison.Ordinal);
            Assert.Matches(@"^↻ \d{1,2}/\d{1,2} \d{2}:\d{2}$", text);
        }
    }

    [Theory]
    [InlineData("ja-JP")]
    [InlineData("en-US")]
    public void JapaneseResetTextNeverContainsAmPm(string culture)
    {
        foreach (var text in ResetTexts(true, culture))
        {
            Assert.DoesNotContain("AM", text, StringComparison.Ordinal);
            Assert.DoesNotContain("PM", text, StringComparison.Ordinal);
            Assert.Matches(@"^↻ \d{1,2}/\d{1,2} \d{2}:\d{2}$", text);
        }
    }

    [Fact]
    public void ResetTextIsIdenticalWhateverTheMachineCulture()
    {
        Assert.Equal(ResetTexts(false, "ja-JP"), ResetTexts(false, "en-US"));
        Assert.Equal(ResetTexts(true, "ja-JP"), ResetTexts(true, "en-US"));
    }
}
