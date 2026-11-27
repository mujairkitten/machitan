using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Machitan.Widgets;

/// <summary>
/// The sidebar's cards in one or more columns of equal width, each column filling on its own:
/// every card, in order, goes under the column that is shortest so far (the leftmost of equal
/// ones), so a short card never leaves a gap beside a tall one (Sam's screenshot, 2026-11-26:
/// "fix ... the empty spaces"). A card that grows or shrinks (a note's text, lyrics coming)
/// moves the cards after it at once. One column is a plain list.
/// </summary>
public sealed partial class MasonryPanel : Panel
{
    private int _columns = 1;
    private double _spacing = 12;

    /// <summary>How many columns (1 or more).</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            int columns = Math.Max(1, value);
            if (columns != _columns)
            {
                _columns = columns;
                InvalidateMeasure();
            }
        }
    }

    /// <summary>The gap between columns and between cards (DIP).</summary>
    public double Spacing
    {
        get => _spacing;
        set
        {
            if (Math.Abs(value - _spacing) > 0.01)
            {
                _spacing = Math.Max(0, value);
                InvalidateMeasure();
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double columnWidth = ColumnWidth(width);
        var heights = new double[_columns];
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(columnWidth > 0 ? columnWidth : double.PositiveInfinity, double.PositiveInfinity));
            Place(child, heights);
            if (columnWidth <= 0)
            {
                width = Math.Max(width, child.DesiredSize.Width);
            }
        }

        return new Size(width, Tallest(heights));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double columnWidth = ColumnWidth(finalSize.Width);
        var heights = new double[_columns];
        foreach (UIElement child in Children)
        {
            (int column, double top) = Place(child, heights);
            child.Arrange(new Rect(column * (columnWidth + _spacing), top, columnWidth, child.DesiredSize.Height));
        }

        return finalSize;
    }

    private double ColumnWidth(double width)
    {
        return Math.Max(0, (width - (_spacing * (_columns - 1))) / _columns);
    }

    /// <summary>The shortest column takes the card (a hidden one takes no room); returns where it goes.</summary>
    private (int Column, double Top) Place(UIElement child, double[] heights)
    {
        int column = 0;
        for (int i = 1; i < heights.Length; i++)
        {
            if (heights[i] < heights[column] - 0.5)
            {
                column = i;
            }
        }

        double top = heights[column] > 0 ? heights[column] + _spacing : 0;
        if (child.Visibility == Visibility.Visible && child.DesiredSize.Height > 0)
        {
            heights[column] = top + child.DesiredSize.Height;
        }

        return (column, top);
    }

    private static double Tallest(double[] heights)
    {
        double tallest = 0;
        foreach (double height in heights)
        {
            tallest = Math.Max(tallest, height);
        }

        return tallest;
    }
}
