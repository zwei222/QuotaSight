using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;

namespace QuotaSight.UI;

/// <summary>
/// Lays children out in equal cells, choosing the column count from the available width so that
/// every tile keeps at least <see cref="MinTileWidth"/>. Used as the dashboard card grid panel by
/// both the production window and the fixture so they share one layout path.
/// </summary>
public sealed class AutoColumnsPanel : Panel
{
    public static readonly StyledProperty<double> MinTileWidthProperty =
        AvaloniaProperty.Register<AutoColumnsPanel, double>(nameof(MinTileWidth), 300);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<AutoColumnsPanel, double>(nameof(Gap), 12);

    public double MinTileWidth
    {
        get => GetValue(MinTileWidthProperty);
        set => SetValue(MinTileWidthProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    private double _cellHeight;

    public int ComputeColumns(double availableWidth)
    {
        var width = double.IsFinite(availableWidth) && availableWidth > 0 ? availableWidth : MinTileWidth;
        return Math.Max(1, (int)Math.Floor((width + Gap) / (Math.Max(1, MinTileWidth) + Gap)));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : MinTileWidth;
        var columns = ComputeColumns(width);
        var cellWidth = Math.Max(MinTileWidth, (width - (columns - 1) * Gap) / columns);
        var rows = (Children.Count + columns - 1) / columns;
        var cellHeight = 0d;
        foreach (var child in Children)
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            cellHeight = Math.Max(cellHeight, child.DesiredSize.Height);
        }

        _cellHeight = cellHeight;
        return new Size(width, rows <= 0 ? 0 : rows * cellHeight + (rows - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ComputeColumns(finalSize.Width);
        var cellWidth = Math.Max(MinTileWidth, (finalSize.Width - (columns - 1) * Gap) / columns);
        var rows = (Children.Count + columns - 1) / columns;
        var cellHeight = rows <= 0 ? 0 : Math.Max(_cellHeight, (finalSize.Height - (rows - 1) * Gap) / rows);
        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            var row = index / columns;
            var column = index % columns;
            child.Arrange(new Rect(column * (cellWidth + Gap), row * (cellHeight + Gap), cellWidth, cellHeight));
        }

        return finalSize;
    }
}
