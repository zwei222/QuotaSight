using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using QuotaSight.UI;

namespace QuotaSight.UI;

internal enum QuotaRingMark { Unknown, Zero, Remaining, Stale, OverLimit, StaleOverLimit }

/// <summary>Draws remaining quota windows as short-outside / long-inside rings.</summary>
public sealed class QuotaRingControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<QuotaRingSegmentViewModel>> SegmentsProperty =
        AvaloniaProperty.Register<QuotaRingControl, IReadOnlyList<QuotaRingSegmentViewModel>>(
            nameof(Segments), Array.Empty<QuotaRingSegmentViewModel>());
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<QuotaRingControl, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> NormalBrushProperty =
        AvaloniaProperty.Register<QuotaRingControl, IBrush?>(nameof(NormalBrush));
    public static readonly StyledProperty<IBrush?> AttentionBrushProperty =
        AvaloniaProperty.Register<QuotaRingControl, IBrush?>(nameof(AttentionBrush));
    public static readonly StyledProperty<IBrush?> DangerBrushProperty =
        AvaloniaProperty.Register<QuotaRingControl, IBrush?>(nameof(DangerBrush));
    public static readonly StyledProperty<IBrush?> OverLimitBrushProperty =
        AvaloniaProperty.Register<QuotaRingControl, IBrush?>(nameof(OverLimitBrush));
    public static readonly StyledProperty<IBrush?> StaleBrushProperty =
        AvaloniaProperty.Register<QuotaRingControl, IBrush?>(nameof(StaleBrush));

    static QuotaRingControl()
    {
        AffectsRender<QuotaRingControl>(SegmentsProperty, TrackBrushProperty, NormalBrushProperty,
            AttentionBrushProperty, DangerBrushProperty, OverLimitBrushProperty, StaleBrushProperty);
    }

    public IReadOnlyList<QuotaRingSegmentViewModel> Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? NormalBrush { get => GetValue(NormalBrushProperty); set => SetValue(NormalBrushProperty, value); }
    public IBrush? AttentionBrush { get => GetValue(AttentionBrushProperty); set => SetValue(AttentionBrushProperty, value); }
    public IBrush? DangerBrush { get => GetValue(DangerBrushProperty); set => SetValue(DangerBrushProperty, value); }
    public IBrush? OverLimitBrush { get => GetValue(OverLimitBrushProperty); set => SetValue(OverLimitBrushProperty, value); }
    public IBrush? StaleBrush { get => GetValue(StaleBrushProperty); set => SetValue(StaleBrushProperty, value); }

    internal static QuotaRingMark GetMark(QuotaRingSegmentViewModel segment)
    {
        if (segment.Band == UsageBand.OverLimit) return segment.IsStale ? QuotaRingMark.StaleOverLimit : QuotaRingMark.OverLimit;
        if (segment.RemainingPercent is null) return QuotaRingMark.Unknown;
        if (segment.IsStale) return QuotaRingMark.Stale;
        return segment.RemainingPercent <= 0m ? QuotaRingMark.Zero : QuotaRingMark.Remaining;
    }

    internal static Pen CreateTrackPen(QuotaRingSegmentViewModel segment, IBrush? trackBrush,
        IBrush? overLimitBrush, IBrush? staleBrush, double thickness)
    {
        var mark = GetMark(segment);
        var overLimit = mark is QuotaRingMark.OverLimit or QuotaRingMark.StaleOverLimit;
        var stale = mark is QuotaRingMark.Stale or QuotaRingMark.StaleOverLimit;
        var brush = stale ? staleBrush ?? trackBrush : overLimit ? overLimitBrush ?? trackBrush : trackBrush;
        var dash = mark == QuotaRingMark.Unknown || overLimit ? new DashStyle([2, 2], 0) : null;
        return new Pen(brush ?? Brushes.Gray, thickness, dash,
            overLimit ? PenLineCap.Flat : PenLineCap.Round, PenLineJoin.Round);
    }

    internal static decimal? GetDrawableRemaining(QuotaRingSegmentViewModel segment)
    {
        var mark = GetMark(segment);
        if (mark is QuotaRingMark.Zero or QuotaRingMark.Unknown or QuotaRingMark.OverLimit or QuotaRingMark.StaleOverLimit)
            return null;
        return segment.RemainingPercent is > 0m ? segment.RemainingPercent : null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var count = Math.Min(3, Segments.Count);
        if (count == 0) return;
        var outerRadius = side / 2 - 3;
        var thickness = count == 1 ? Math.Clamp(side * 0.085, 4, 7) : Math.Clamp(side * 0.065, 3, 5);
        var gap = Math.Max(2, side * 0.035);

        for (var index = 0; index < count; index++)
        {
            var segment = Segments[index];
            var radius = outerRadius - index * (thickness + gap);
            if (radius <= thickness) break;
            var mark = GetMark(segment);
            var stale = mark is QuotaRingMark.Stale or QuotaRingMark.StaleOverLimit;
            var track = CreateTrackPen(segment, TrackBrush, OverLimitBrush, StaleBrush, thickness);
            context.DrawEllipse(null, track, center, radius, radius);

            if (GetDrawableRemaining(segment) is not { } remaining) continue;
            var color = (stale ? StaleBrush : segment.Band switch
            {
                UsageBand.OverLimit => OverLimitBrush,
                UsageBand.Danger => DangerBrush,
                UsageBand.Attention => AttentionBrush,
                _ => NormalBrush
            }) ?? NormalBrush ?? Brushes.MediumSeaGreen;
            var pen = new Pen(color, thickness, null, PenLineCap.Round, PenLineJoin.Round);
            if (remaining >= 100m)
            {
                context.DrawEllipse(null, pen, center, radius, radius);
                continue;
            }

            var startAngle = -90d;
            var endAngle = startAngle + (double)Math.Clamp(remaining, 0m, 100m) * 3.6d;
            var start = PointOnCircle(center, radius, startAngle);
            var end = PointOnCircle(center, radius, endAngle);
            var geometry = new StreamGeometry();
            using (var builder = geometry.Open())
            {
                builder.BeginFigure(start, false);
                builder.ArcTo(end, new Size(radius, radius), 0, endAngle - startAngle > 180,
                    SweepDirection.Clockwise, true);
                builder.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private static Point PointOnCircle(Point center, double radius, double angle)
    {
        var radians = angle * Math.PI / 180;
        return new Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }
}
