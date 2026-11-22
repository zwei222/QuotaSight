using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace QuotaSight.UI.Controls;

/// <summary>Draws up to three independent quota windows as concentric remaining-percentage rings.</summary>
public sealed class QuotaRingControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<int?>?> SegmentsProperty = AvaloniaProperty.Register<QuotaRingControl, IReadOnlyList<int?>?>(nameof(Segments));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<QuotaRingControl, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> ValueBrushProperty = AvaloniaProperty.Register<QuotaRingControl, IBrush?>(nameof(ValueBrush));
    public static readonly StyledProperty<IBrush?> InnerBrushProperty = AvaloniaProperty.Register<QuotaRingControl, IBrush?>(nameof(InnerBrush));
    public static readonly StyledProperty<IBrush?> UnknownBrushProperty = AvaloniaProperty.Register<QuotaRingControl, IBrush?>(nameof(UnknownBrush));
    public IBrush? UnknownBrush { get => GetValue(UnknownBrushProperty); set => SetValue(UnknownBrushProperty, value); }
    public IReadOnlyList<int?>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? ValueBrush { get => GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    public IBrush? InnerBrush { get => GetValue(InnerBrushProperty); set => SetValue(InnerBrushProperty, value); }
    static QuotaRingControl() => AffectsRender<QuotaRingControl>(UnknownBrushProperty, SegmentsProperty, TrackBrushProperty, ValueBrushProperty, InnerBrushProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var side = Math.Min(Bounds.Width, Bounds.Height);
        var values = Segments;
        if (side <= 0 || values is null) return;
        var count = Math.Min(3, values.Count);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var outerRadius = side / 2 - 5;
        var width = Math.Clamp(side * .075, 3, 6);
        var gap = Math.Max(2, side * .035);
        for (var i = 0; i < count; i++)
        {
            var radius = outerRadius - i * (width + gap);
            var raw = values[i];
            var unknown = raw is null;
            var trackBrush = unknown ? UnknownBrush ?? Brushes.Gray : TrackBrush ?? Brushes.Gray;
            var track = new Pen(trackBrush, width, unknown ? new DashStyle([1.2, 2.2], 0) : null, unknown ? PenLineCap.Flat : PenLineCap.Round);
            context.DrawEllipse(null, track, center, radius, radius);
            if (raw is not { } basisPoints || basisPoints <= 0) continue;
            var brush = i == 0 ? ValueBrush ?? Brushes.MediumSeaGreen : InnerBrush ?? ValueBrush ?? Brushes.DeepSkyBlue;
            var pen = new Pen(brush, width, null, PenLineCap.Round);
            if (basisPoints >= 10000) { context.DrawEllipse(null, pen, center, radius, radius); continue; }
            var endAngle = -90d + basisPoints / 10000d * 360d;
            Point At(double angle) { var radians = angle * Math.PI / 180; return new(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius); }
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(At(-90), false);
                path.ArcTo(At(endAngle), new Size(radius, radius), 0, basisPoints > 5000, SweepDirection.Clockwise, true);
                path.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
