using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Studio.Controls
{
    /// <summary>
    /// One map's 560 cells, drawn as the isometric diamond grid the client draws.
    /// </summary>
    /// <remarks>
    /// The layout is the client's own, not an interpretation of it: a cell's row is its index over
    /// the map width and its column the remainder, and odd rows are shifted half a cell to the
    /// right. That shift is the whole of the isometry — everything else is a rectangle.
    ///
    /// This is the one place in the editor that paints pixels rather than moving domain objects
    /// around, and it is the only thing a browser canvas would have made shorter.
    /// </remarks>
    public sealed class CellGrid : Control
    {
        private const double CellWidth = 34;
        private const double CellHeight = 17;

        private static readonly IBrush Solid = new SolidColorBrush(Color.FromRgb(0x2B, 0x2E, 0x35));
        private static readonly IBrush Walkable = new SolidColorBrush(Color.FromRgb(0x4C, 0x7A, 0x5A));
        private static readonly IBrush SeenThrough = new SolidColorBrush(Color.FromRgb(0x36, 0x4B, 0x73));
        private static readonly IBrush BlockedInFight = new SolidColorBrush(Color.FromRgb(0x8A, 0x5A, 0x2E));
        private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x21)), 1);
        private static readonly IPen Highlight = new Pen(new SolidColorBrush(Colors.White), 2);

        private MapCells? _cells;
        private int _hovered = -1;

        /// <summary>Which cell the pointer is over, or minus one.</summary>
        public int Hovered => _hovered;

        /// <summary>Raised as the pointer moves from cell to cell.</summary>
        public event Action<int>? HoveredChanged;

        public CellGrid()
        {
            Width = CellWidth * (MapGeometry.MapWidth + 1);
            Height = CellHeight * (MapGeometry.MapHeight + 1);
        }

        public void Show(MapCells? cells)
        {
            _cells = cells;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            for (int cell = 0; cell < MapGeometry.MaxCells; cell++)
            {
                var centre = CentreOf(cell);
                var shape = DiamondAt(centre);

                context.DrawGeometry(BrushFor(cell), Edge, shape);
                if (cell == _hovered) context.DrawGeometry(null, Highlight, shape);
            }
        }

        private IBrush BrushFor(int cell)
        {
            if (_cells == null) return Solid;

            bool walkable = _cells.Walkable.Contains(cell);
            bool inFight = _cells.WalkableInFight.Contains(cell);
            bool blocksSight = _cells.SightBlockers.Contains(cell);

            // Walkable outside a fight but not inside one is worth its own colour: it is the case
            // that surprises people, and the one the fight pathfinder has to respect.
            if (walkable && !inFight) return BlockedInFight;
            if (walkable) return Walkable;
            if (!blocksSight) return SeenThrough;
            return Solid;
        }

        private static Point CentreOf(int cell)
        {
            int row = cell / MapGeometry.MapWidth;
            int column = cell % MapGeometry.MapWidth;

            double x = column * CellWidth + (row % 2) * (CellWidth / 2) + CellWidth / 2;
            double y = row * (CellHeight / 2) + CellHeight / 2;
            return new Point(x, y);
        }

        private static StreamGeometry DiamondAt(Point centre)
        {
            var shape = new StreamGeometry();
            using (var draw = shape.Open())
            {
                draw.BeginFigure(new Point(centre.X, centre.Y - CellHeight / 2), isFilled: true);
                draw.LineTo(new Point(centre.X + CellWidth / 2, centre.Y));
                draw.LineTo(new Point(centre.X, centre.Y + CellHeight / 2));
                draw.LineTo(new Point(centre.X - CellWidth / 2, centre.Y));
                draw.EndFigure(isClosed: true);
            }
            return shape;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            var at = e.GetPosition(this);
            int found = CellAt(at);
            if (found == _hovered) return;

            _hovered = found;
            HoveredChanged?.Invoke(found);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);

            if (_hovered == -1) return;
            _hovered = -1;
            HoveredChanged?.Invoke(-1);
            InvalidateVisual();
        }

        /// <summary>
        /// Which cell a point falls in.
        /// </summary>
        /// <remarks>
        /// Walked rather than solved. Inverting the diamond layout is a page of algebra with two
        /// off-by-one traps at the row shift, and 560 cheap tests per pointer move is nothing on a
        /// grid this size. If the editor ever paints a map the size of a continent this is the
        /// first thing to replace.
        /// </remarks>
        private static int CellAt(Point at)
        {
            for (int cell = 0; cell < MapGeometry.MaxCells; cell++)
            {
                var centre = CentreOf(cell);

                // A diamond is |dx|/halfWidth + |dy|/halfHeight <= 1.
                double dx = Math.Abs(at.X - centre.X) / (CellWidth / 2);
                double dy = Math.Abs(at.Y - centre.Y) / (CellHeight / 2);
                if (dx + dy <= 1) return cell;
            }
            return -1;
        }
    }
}
