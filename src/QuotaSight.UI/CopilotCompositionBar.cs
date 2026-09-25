using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace QuotaSight.UI;

public sealed class CopilotCompositionBar : Control
{
    public static readonly StyledProperty<decimal> IncludedRatioProperty = AvaloniaProperty.Register<CopilotCompositionBar, decimal>(nameof(IncludedRatio));
    public static readonly StyledProperty<decimal> AdditionalRatioProperty = AvaloniaProperty.Register<CopilotCompositionBar, decimal>(nameof(AdditionalRatio));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<CopilotCompositionBar, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> IncludedBrushProperty = AvaloniaProperty.Register<CopilotCompositionBar, IBrush?>(nameof(IncludedBrush));
    public static readonly StyledProperty<IBrush?> AdditionalBrushProperty = AvaloniaProperty.Register<CopilotCompositionBar, IBrush?>(nameof(AdditionalBrush));

    public decimal IncludedRatio { get => GetValue(IncludedRatioProperty); set => SetValue(IncludedRatioProperty, value); }
    public decimal AdditionalRatio { get => GetValue(AdditionalRatioProperty); set => SetValue(AdditionalRatioProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? IncludedBrush { get => GetValue(IncludedBrushProperty); set => SetValue(IncludedBrushProperty, value); }
    public IBrush? AdditionalBrush { get => GetValue(AdditionalBrushProperty); set => SetValue(AdditionalBrushProperty, value); }

    static CopilotCompositionBar()
    {
        AffectsRender<CopilotCompositionBar>(IncludedRatioProperty, AdditionalRatioProperty, TrackBrushProperty, IncludedBrushProperty, AdditionalBrushProperty);
        AffectsMeasure<CopilotCompositionBar>(BoundsProperty);
    }

    protected override Size MeasureOverride(Size availableSize) => new(availableSize.Width, 14);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        var radius = Math.Min(7, bounds.Height / 2);
        var track = TrackBrush ?? Brushes.Transparent;
        context.DrawRectangle(track, null, bounds, radius, radius);

        var included = decimal.Clamp(IncludedRatio, 0m, 1m);
        var additional = decimal.Clamp(AdditionalRatio, 0m, 1m - included);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var includedWidth = bounds.Width * (double)included;
        var additionalWidth = bounds.Width * (double)additional;
        if (includedWidth > 0) context.DrawRectangle(IncludedBrush ?? Brushes.Transparent, null, new Rect(0, 0, includedWidth, bounds.Height));
        if (additionalWidth > 0) context.DrawRectangle(AdditionalBrush ?? Brushes.Transparent, null, new Rect(includedWidth, 0, additionalWidth, bounds.Height));
    }
}
