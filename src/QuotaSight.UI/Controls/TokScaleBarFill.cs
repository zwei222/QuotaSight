using Avalonia;
using Avalonia.Controls;

namespace QuotaSight.UI.Controls;

/// <summary>
/// One bar of the observed-usage comparison: an absolute observed amount drawn as its
/// share of the comparison bars' common top, laid out from the left on the shared token
/// scale. It is a comparison length only — no track background, no gauge, no threshold:
/// a zero observation simply draws nothing, and the period is named by its own row label.
/// </summary>
public sealed class TokScaleBarFill : Panel
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<TokScaleBarFill, double>(nameof(Ratio), 0.0, coerce: CoerceRatio);

    static TokScaleBarFill() => AffectsArrange<TokScaleBarFill>(RatioProperty);

    /// <summary>The observed amount's share of the common top, between 0 and 1.</summary>
    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    private static double CoerceRatio(AvaloniaObject sender, double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 0d;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 0d;
        var height = 0d;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var fillWidth = Ratio * finalSize.Width;
        foreach (var child in Children)
        {
            child.Arrange(new Rect(0, 0, fillWidth, finalSize.Height));
        }

        return finalSize;
    }
}
