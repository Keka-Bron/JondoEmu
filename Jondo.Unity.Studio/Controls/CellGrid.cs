using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.Studio.Ui;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Studio.Controls
{
    /// <summary>How the ground is painted.</summary>
    public enum GridView
    {
        /// <summary>Every layer in its own colour. What you want while editing.</summary>
        Editing = 0,

        /// <summary>
        /// The ground as one surface, the way it reads in the game.
        /// </summary>
        /// <remarks>
        /// Not the real thing, and it does not pretend to be. The actual floor of a map is 2,181
        /// decorated elements out of the client's bundles and is a project of its own — the
        /// architecture document keeps it out of the first version on purpose. What this does is
        /// stop showing the four debug layers and paint the ground as one, which is what you want
        /// when the question is "does this look right" rather than "which cell blocks sight".
        /// </remarks>
        Game = 1,
    }

    /// <summary>Something standing on a cell.</summary>
    public sealed class CellMark
    {
        public IBrush Colour { get; init; } = Skin.AuthoredBrush;

        /// <summary>A word or two drawn next to it. Empty draws nothing.</summary>
        public string Label { get; init; } = "";

        /// <summary>An icon, when there is one for this thing.</summary>
        public IImage? Icon { get; init; }

        /// <summary>Drawn hollow, for something that has been taken away.</summary>
        public bool Faded { get; init; }
    }

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
        /// <summary>The cell at zoom 1. Everything else is this times <see cref="Zoom"/>.</summary>
        private const double BaseWidth = 40;

        private const double BaseHeight = 20;

        private double CellWidth => BaseWidth * _zoom;

        private double CellHeight => BaseHeight * _zoom;

        private double _zoom = 1.0;

        /// <summary>
        /// How big the map is drawn.
        /// </summary>
        /// <remarks>
        /// The map is the thing on these screens, and at zoom 1 it was 600 pixels of a 1,900 pixel
        /// window while a table of numbers took the rest. Which is backwards: the table is a way of
        /// finding something on the map, not the other way round.
        /// </remarks>
        public double Zoom
        {
            get => _zoom;
            set
            {
                double asked = Math.Clamp(value, 0.6, 3.0);
                if (Math.Abs(asked - _zoom) < 0.001) return;

                _zoom = asked;
                Resize();
                InvalidateVisual();
            }
        }

        private void Resize()
        {
            Width = CellWidth * (MapGeometry.MapWidth + 1);
            Height = CellHeight * (MapGeometry.MapHeight + 1);
        }

        /// <summary>How often the chosen cell blinks. Slow enough to read, fast enough to find.</summary>
        private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(520);

        // ─── Editing: every layer says something different ────────────────────────
        private static readonly IBrush Solid = new SolidColorBrush(Color.FromRgb(0x22, 0x25, 0x2C));
        private static readonly IBrush Walkable = new SolidColorBrush(Color.FromRgb(0x3E, 0x6B, 0x4E));
        private static readonly IBrush SeenThrough = new SolidColorBrush(Color.FromRgb(0x2E, 0x42, 0x63));
        private static readonly IBrush BlockedInFight = new SolidColorBrush(Color.FromRgb(0x7A, 0x50, 0x2A));

        // ─── As in game: one ground, one wall ─────────────────────────────────────
        private static readonly IBrush Ground = new SolidColorBrush(Color.FromRgb(0x4C, 0x6E, 0x45));
        private static readonly IBrush GroundAlt = new SolidColorBrush(Color.FromRgb(0x45, 0x66, 0x3F));
        private static readonly IBrush Wall = new SolidColorBrush(Color.FromRgb(0x1C, 0x1E, 0x24));

        private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), 1);
        private static readonly IPen FaintEdge = new Pen(new SolidColorBrush(Color.FromArgb(0x28, 0, 0, 0)), 1);
        private static readonly IPen Hover = new Pen(new SolidColorBrush(Colors.White), 2);
        private static readonly IPen ChosenPen = new Pen(new SolidColorBrush(Skin.AuthoredSoft), 3);
        private static readonly IPen ChosenGlow = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xF5, 0xB1, 0x5C)), 7);

        private MapCells? _cells;
        private int _hovered = -1;
        private int _selected = -1;
        private bool _bright = true;
        private DispatcherTimer? _blink;
        private IReadOnlyDictionary<int, CellMark>? _marks;
        private IReadOnlyDictionary<int, IBrush>? _wash;
        private CellMark? _ghost;

        /// <summary>Which cell the pointer is over, or minus one.</summary>
        public int Hovered => _hovered;

        /// <summary>Raised as the pointer moves from cell to cell.</summary>
        public event Action<int>? HoveredChanged;

        /// <summary>Raised when a cell is clicked. Minus one never comes through here.</summary>
        public event Action<int>? Clicked;

        /// <summary>
        /// Raised on press and then on every new cell while the button is held.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Clicked"/> because painting and picking are different verbs.
        /// A run of wall along the edge of a map is twenty cells, and clicking twenty times is how
        /// an editor stops being used.
        /// </remarks>
        public event Action<int>? Painted;

        private bool _painting;

        /// <summary>Which way the ground is painted.</summary>
        public GridView View
        {
            get => _view;
            set
            {
                if (_view == value) return;
                _view = value;
                InvalidateVisual();
            }
        }

        private GridView _view = GridView.Editing;

        public CellGrid()
        {
            Resize();

            // The blink only runs while the control is on screen. A timer left ticking behind an
            // abandoned view is a leak that grows one tick faster on every visit.
            AttachedToVisualTree += (_, _) =>
            {
                _blink = new DispatcherTimer { Interval = Beat };
                _blink.Tick += (_, _) =>
                {
                    if (_selected < 0) return;
                    _bright = !_bright;
                    InvalidateVisual();
                };
                _blink.Start();
            };

            DetachedFromVisualTree += (_, _) =>
            {
                _blink?.Stop();
                _blink = null;
            };
        }

        public void Show(MapCells? cells)
        {
            _cells = cells;
            InvalidateVisual();
        }

        /// <summary>
        /// What is standing on the map, painted over the ground.
        /// </summary>
        /// <remarks>
        /// Over rather than instead of, so that "there is an NPC here" and "this cell cannot be
        /// stood on" can both be seen at once — which is exactly the pair of facts somebody
        /// placing an NPC needs and would otherwise have to check twice.
        /// </remarks>
        public void Mark(IReadOnlyDictionary<int, CellMark>? marks)
        {
            _marks = marks;
            InvalidateVisual();
        }

        /// <summary>
        /// A colour laid over the ground on chosen cells: a spell's range, the cells it would hit.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Mark"/> because they answer different questions and have to be
        /// visible at the same time. A wash says "this area"; a mark says "this thing is standing
        /// here". Painting an area with marks would hide every fighter inside it, which is the one
        /// thing you are looking at when you aim a spell.
        /// </remarks>
        public void Wash(IReadOnlyDictionary<int, IBrush>? tint)
        {
            _wash = tint;
            InvalidateVisual();
        }

        /// <summary>
        /// What would be dropped where the pointer is, drawn faintly and following the cursor.
        /// </summary>
        /// <remarks>
        /// Because placing something you cannot see until after you have clicked is guessing. The
        /// ghost turns "click and check" into "look and click", which for a 560-cell grid where a
        /// cell is 40 by 20 pixels is the difference between placing an NPC in one go and placing
        /// it three times.
        /// </remarks>
        public void Preview(CellMark? ghost)
        {
            _ghost = ghost;
            InvalidateVisual();
        }

        /// <summary>
        /// The cell drawn as chosen. It blinks, and that is not decoration.
        /// </summary>
        /// <remarks>
        /// It was a one-pixel outline before, on a grid of 560 cells, and finding it meant hunting.
        /// A thing that changes is the one thing peripheral vision is actually good at.
        /// </remarks>
        public void Select(int cell)
        {
            if (_selected == cell) return;
            _selected = cell;
            _bright = true;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            for (int cell = 0; cell < MapGeometry.MaxCells; cell++)
            {
                var centre = CentreOf(cell);
                var shape = DiamondAt(centre);

                context.DrawGeometry(BrushFor(cell), _view == GridView.Game ? FaintEdge : Edge, shape);

                if (_wash != null && _wash.TryGetValue(cell, out var tint))
                {
                    context.DrawGeometry(tint, null, shape);
                }

                if (_marks != null && _marks.TryGetValue(cell, out var mark))
                {
                    Paint(context, centre, shape, mark);
                }

                // The ghost goes on last so it sits over whatever is already on that cell, which is
                // exactly the question being asked: what would this look like here.
                if (_ghost != null && cell == _hovered && (_marks == null || !_marks.ContainsKey(cell)))
                {
                    using (context.PushOpacity(0.62))
                    {
                        Paint(context, centre, shape, _ghost);
                    }
                }

                if (cell == _selected)
                {
                    if (_bright) context.DrawGeometry(null, ChosenGlow, shape);
                    context.DrawGeometry(null, ChosenPen, shape);
                }
                else if (cell == _hovered)
                {
                    context.DrawGeometry(null, Hover, shape);
                }
            }
        }

        private void Paint(DrawingContext context, Point centre, Geometry shape, CellMark mark)
        {
            if (mark.Faded)
            {
                context.DrawGeometry(null, new Pen(mark.Colour, 2, new DashStyle(new double[] { 2, 2 }, 0)), shape);
            }
            else
            {
                context.DrawGeometry(mark.Colour, null, shape);
            }

            if (mark.Icon != null)
            {
                // Its own proportions, not a square. A monster picto is 64 by 64 and a drawn NPC is
                // 96 tall by whatever it is wide, so forcing both into the same box squashes every
                // character that is not exactly as wide as it is tall - which is all of them.
                double tall = CellHeight * 2.6;
                double wide = tall * mark.Icon.Size.Width / Math.Max(1, mark.Icon.Size.Height);
                if (wide > CellWidth * 1.6)
                {
                    wide = CellWidth * 1.6;
                    tall = wide * mark.Icon.Size.Height / Math.Max(1, mark.Icon.Size.Width);
                }

                // Standing on the cell rather than centred in it: feet at the bottom of the
                // diamond is what makes a grid of them read as a scene.
                context.DrawImage(mark.Icon,
                    new Rect(centre.X - wide / 2, centre.Y + CellHeight * 0.25 - tall, wide, tall));
            }

            if (mark.Label.Length == 0) return;

            var text = new FormattedText(mark.Label, CultureInfo.CurrentCulture,
                                         FlowDirection.LeftToRight, Typeface.Default,
                                         Math.Clamp(10 * _zoom, 9, 13), Skin.TextBrush);

            // BELOW the diamond, not above it.
            //
            // Above is where the drawing is: a character stands on its cell and reaches up out of
            // it, so a name plate over the cell covers the face of the thing it is naming. Below,
            // the two never meet.
            var at = new Point(centre.X - text.Width / 2, centre.Y + CellHeight / 2 + 2);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0x12, 0x14, 0x18)),
                                  new Rect(at.X - 3, at.Y - 1, text.Width + 6, text.Height + 2), 3);
            context.DrawText(text, at);
        }

        private IBrush BrushFor(int cell)
        {
            if (_cells == null) return _view == GridView.Game ? Wall : Solid;

            bool walkable = _cells.Walkable.Contains(cell);
            bool inFight = _cells.WalkableInFight.Contains(cell);
            bool blocksSight = _cells.SightBlockers.Contains(cell);

            if (_view == GridView.Game)
            {
                // One ground, one wall, and a faint chequer so the grid still reads as a grid.
                if (!walkable) return Wall;
                return (cell / MapGeometry.MapWidth + cell % MapGeometry.MapWidth) % 2 == 0 ? Ground : GroundAlt;
            }

            // Walkable outside a fight but not inside one is worth its own colour: it is the case
            // that surprises people, and the one the fight pathfinder has to respect.
            if (walkable && !inFight) return BlockedInFight;
            if (walkable) return Walkable;
            if (!blocksSight) return SeenThrough;
            return Solid;
        }

        private Point CentreOf(int cell)
        {
            int row = cell / MapGeometry.MapWidth;
            int column = cell % MapGeometry.MapWidth;

            double x = column * CellWidth + (row % 2) * (CellWidth / 2) + CellWidth / 2;
            double y = row * (CellHeight / 2) + CellHeight / 2;
            return new Point(x, y);
        }

        private StreamGeometry DiamondAt(Point centre)
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

            int found = CellAt(e.GetPosition(this));
            if (found == _hovered) return;

            _hovered = found;
            HoveredChanged?.Invoke(found);

            // Each cell is painted once as the pointer crosses it, which is why this hangs off the
            // change of cell rather than off every move.
            if (_painting && found >= 0) Painted?.Invoke(found);

            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (!_painting) return;

            _painting = false;
            e.Pointer.Capture(null);
        }

        /// <summary>Where the pointer is, as a cell, for anybody who needs it outside an event.</summary>
        public int PointerCell()
        {
            return _hovered;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            int cell = CellAt(e.GetPosition(this));
            if (cell < 0) return;

            Clicked?.Invoke(cell);

            if (Painted == null) return;

            _painting = true;
            e.Pointer.Capture(this);
            Painted.Invoke(cell);
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
        private int CellAt(Point at)
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
