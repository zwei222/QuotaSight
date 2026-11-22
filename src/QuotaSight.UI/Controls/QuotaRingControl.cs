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
    public static readonly StyledProperty<IBrush?> AttentionBrushProperty = AvaloniaProperty.Register<QuotaRingControl, IBrush?>(nameof(AttentionBrush));
    public static readonly StyledProperty<IBrush?> DangerBrushProperty = AvaloniaProperty.Register<QuotaRingControl, IBrush?>(nameof(DangerBrush));
    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<QuotaRingControl, double>(nameof(StrokeThickness));
    public static readonly StyledProperty<double> RingGapProperty = AvaloniaProperty.Register<QuotaRingControl, double>(nameof(RingGap));
    /// <summary>Ring stroke width; 0 keeps the size-proportional default.</summary>
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    /// <summary>Gap between concentric rings; 0 keeps the size-proportional default.</summary>
    public double RingGap { get => GetValue(RingGapProperty); set => SetValue(RingGapProperty, value); }
    public static readonly StyledProperty<double> OuterInsetProperty = AvaloniaProperty.Register<QuotaRingControl, double>(nameof(OuterInset), 5);
    /// <summary>Distance from the control edge to the outer ring's stroke centre; the approved mock uses 3 (ring centre-line radius 41 in an 88px box).</summary>
    public double OuterInset { get => GetValue(OuterInsetProperty); set => SetValue(OuterInsetProperty, value); }
    public IBrush? UnknownBrush { get => GetValue(UnknownBrushProperty); set => SetValue(UnknownBrushProperty, value); }
    public IBrush? AttentionBrush { get => GetValue(AttentionBrushProperty); set => SetValue(AttentionBrushProperty, value); }
    public IBrush? DangerBrush { get => GetValue(DangerBrushProperty); set => SetValue(DangerBrushProperty, value); }
    public IReadOnlyList<int?>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? ValueBrush { get => GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    public IBrush? InnerBrush { get => GetValue(InnerBrushProperty); set => SetValue(InnerBrushProperty, value); }
    static QuotaRingControl() => AffectsRender<QuotaRingControl>(StrokeThicknessProperty, RingGapProperty, OuterInsetProperty, UnknownBrushProperty, AttentionBrushProperty, DangerBrushProperty, SegmentsProperty, TrackBrushProperty, ValueBrushProperty, InnerBrushProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var side = Math.Min(Bounds.Width, Bounds.Height);
        var values = Segments;
        if (side <= 0 || values is null) return;
        var count = Math.Min(3, values.Count);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var outerRadius = side / 2 - OuterInset;
        var width = StrokeThickness > 0 ? StrokeThickness : Math.Clamp(side * .075, 3, 6);
        var gap = RingGap > 0 ? RingGap : Math.Max(2, side * .035);
        for (var i = 0; i < count; i++)
        {
            var radius = outerRadius - i * (width + gap);
            var raw = values[i];
            var unknown = raw is null;
            var trackBrush = unknown ? UnknownBrush ?? Brushes.Gray : TrackBrush ?? Brushes.Gray;
            // Approved mock: unknown/expired windows are an even, short, flat dash (3px on / 4px off at a 6px stroke) at 60% opacity.
            var track = new Pen(trackBrush, width, unknown ? new DashStyle([3 / width, 4 / width], 0) : null, unknown ? PenLineCap.Flat : PenLineCap.Round);
            if (unknown) { using (context.PushOpacity(.6)) context.DrawEllipse(null, track, center, radius, radius); }
            else context.DrawEllipse(null, track, center, radius, radius);
            if (raw is not { } basisPoints) continue;
            if (basisPoints <= 0)
            {
                var dotRadius = Math.Max(2, width * .75);
                context.DrawEllipse(DangerBrush ?? Brushes.Red, null, new Point(center.X, center.Y - radius), dotRadius, dotRadius);
                continue;
            }
            var brush = basisPoints <= 1000 ? DangerBrush ?? ValueBrush ?? Brushes.IndianRed
                : basisPoints <= 3000 ? AttentionBrush ?? ValueBrush ?? Brushes.DarkGoldenrod
                : i == 0 ? ValueBrush ?? Brushes.MediumSeaGreen : InnerBrush ?? ValueBrush ?? Brushes.DeepSkyBlue;
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
