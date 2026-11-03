using Avalonia;
using Avalonia.Controls;

namespace Jondo.Unity.Server.UI;

/// <summary>Fit settings into the fewest columns possible, then compact hints before scrolling.</summary>
internal sealed class SettingsCardsPanel : Panel
{
    private const double Gap = 14, NarrowestColumn = 280;

    /// <summary>How many placements the search for a better spread may try before keeping the best so far.</summary>
    private const int SearchBudget = 200_000;

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
        var heights = new double[Children.Count];
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(columnWidth, double.PositiveInfinity));
            heights[i] = Children[i].DesiredSize.Height;
        }

        // Each card in turn to the shortest column keeps the reading order. When that leaves a
        // column below the window and another spread of the same cards would not -- one tall card
        // landing on a full column --, that spread.
        var column = InOrder(heights, columns);
        double tallest = Tallest(heights, column, columns);
        if (tallest > ViewportHeight && columns > 1)
        {
            column = Shortest(heights, columns, column);
            tallest = Tallest(heights, column, columns);
        }

        var tops = new double[columns];
        _positions = new Rect[Children.Count];
        for (int i = 0; i < Children.Count; i++)
        {
            int c = column[i];
            _positions[i] = new Rect(c * (columnWidth + Gap), tops[c], columnWidth, heights[i]);
            tops[c] += heights[i] + Gap;
        }
        return tallest;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < Children.Count; i++) Children[i].Arrange(_positions[i]);
        return finalSize;
    }

    /// <summary>Each card in turn to the shortest column.</summary>
    internal static int[] InOrder(IReadOnlyList<double> heights, int columns)
    {
        var tops = new double[columns];
        var column = new int[heights.Count];
        for (int i = 0; i < heights.Count; i++)
        {
            int shortest = Array.IndexOf(tops, tops.Min());
            column[i] = shortest;
            tops[shortest] += heights[i] + Gap;
        }
        return column;
    }

    /// <summary>How tall the tallest column of a spread is.</summary>
    internal static double Tallest(IReadOnlyList<double> heights, int[] column, int columns)
    {
        var tops = new double[columns];
        for (int i = 0; i < heights.Count; i++) tops[column[i]] += heights[i] + Gap;
        return Math.Max(0, tops.Max() - Gap);
    }

    /// <summary>
    /// The spread whose tallest column is the shortest, from <paramref name="start"/>: the cards
    /// placed tallest first, a branch dropped as soon as a column passes the best found, and two
    /// columns of the same height tried as one. The columns are then numbered by their first card,
    /// so the first card stays at the top left.
    /// </summary>
    internal static int[] Shortest(IReadOnlyList<double> heights, int columns, int[] start)
    {
        var best = (int[])start.Clone();
        double bestHeight = Tallest(heights, best, columns);
        var order = Enumerable.Range(0, heights.Count).OrderByDescending(i => heights[i]).ToArray();
        var tops = new double[columns];
        var column = new int[heights.Count];
        int steps = 0;

        void Place(int k)
        {
            if (k == order.Length)
            {
                double tallest = Math.Max(0, tops.Max() - Gap);
                if (tallest < bestHeight)
                {
                    bestHeight = tallest;
                    best = (int[])column.Clone();
                }
                return;
            }
            int card = order[k];
            for (int c = 0; c < columns && steps < SearchBudget; c++)
            {
                bool same = false;
                for (int d = 0; d < c && !same; d++) same = tops[d] == tops[c];
                if (same) continue;
                double top = tops[c] + heights[card] + Gap;
                if (top - Gap >= bestHeight) continue;
                steps++;
                tops[c] = top;
                column[card] = c;
                Place(k + 1);
                tops[c] -= heights[card] + Gap;
            }
        }

        Place(0);

        var rank = new int[columns];
        Array.Fill(rank, -1);
        int next = 0;
        foreach (int c in best)
            if (rank[c] < 0) rank[c] = next++;
        return best.Select(c => rank[c]).ToArray();
    }
}
