using Avalonia;
using Avalonia.Controls;

namespace Jondo.Unity.Server.UI;

/// <summary>Fit settings into the fewest columns possible, then compact hints before scrolling.</summary>
internal sealed class SettingsCardsPanel : Panel
{
    private const double Gap = 14, NarrowestColumn = 280;
    internal readonly List<TextBlock> Hints = new();
    internal double ViewportHeight { get; set; }
    internal bool Compact { get; private set; }
    private Rect[] _positions = Array.Empty<Rect>();

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 960;
        int most = Math.Max(1, (int)((width + Gap) / (NarrowestColumn + Gap)));
        foreach (bool compact in new[] { false, true })
        {
            SetCompact(compact);
            for (int columns = 1; columns <= most; columns++)
            {
                double height = MeasureColumns(width, columns);
                if (height <= ViewportHeight) return new Size(width, height);
            }
        }
        // When scrolling is unavoidable, restore inline hints.
        SetCompact(false);
        return new Size(width, MeasureColumns(width, most));
    }

    private void SetCompact(bool compact)
    {
        Compact = compact;
        foreach (var hint in Hints) hint.IsVisible = !compact;
    }

    private double MeasureColumns(double width, int columns)
    {
        double columnWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var heights = new double[columns];
        _positions = new Rect[Children.Count];
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            int shortest = Array.IndexOf(heights, heights.Min());
            _positions[i] = new Rect(shortest * (columnWidth + Gap), heights[shortest], columnWidth, child.DesiredSize.Height);
            heights[shortest] += child.DesiredSize.Height + Gap;
        }
        return Math.Max(0, heights.Max() - Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < Children.Count; i++) Children[i].Arrange(_positions[i]);
        return finalSize;
    }
}
