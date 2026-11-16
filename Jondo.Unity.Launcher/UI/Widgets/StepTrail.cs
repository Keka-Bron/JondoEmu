using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Jondo.Unity.Launcher.Setup;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// The steps of an installation or an update as a trail across the card: one numbered node
    /// per step, joined by a line that fills as they are done.
    /// </summary>
    /// <remarks>
    /// Every state has a shape of its own, not only a colour: done is a filled green disc with a
    /// tick, the running step a gold ring with an arc of its progress and a halo that breathes,
    /// a pending one an empty dim ring with its number, a skipped one a dim tick, a failed one a
    /// red disc with a cross. Pointing at a node raises <see cref="HoveredChanged"/> so the window
    /// can show that step in its card.
    /// </remarks>
    internal sealed class StepTrail : Control
    {
        private IReadOnlyList<SetupStep> _steps = Array.Empty<SetupStep>();
        private int _hovered = -1;
        private double _breath;
        private TimeSpan _last;
        private bool _frameRequested;
        private readonly Dictionary<int, double> _shownArc = new();

        public StepTrail()
        {
            Height = 46;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        /// <summary>The node under the pointer, or -1.</summary>
        public int Hovered => _hovered;

        /// <summary>The node the window's card is showing, ringed.</summary>
        public int Focused { get; set; } = -1;

        public event EventHandler? HoveredChanged;

        public void SetSteps(IReadOnlyList<SetupStep> steps)
        {
            _steps = steps;
            _hovered = -1;
            _shownArc.Clear();
            Refresh();
        }

        /// <summary>The steps moved on: draw again, and keep breathing while one runs.</summary>
        public void Refresh()
        {
            InvalidateVisual();
            RequestFrame();
        }

        // ─── Colours ────────────────────────────────────────────────────────

        private static readonly IBrush GreenFill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(LauncherSkin.Of(LauncherPalette.GreenTop), 0),
                new GradientStop(LauncherSkin.Of(LauncherPalette.GreenBottom), 1),
            },
        };
        private static readonly IBrush RedFill = new SolidColorBrush(Color.FromRgb(0xB8, 0x3A, 0x2A));
        private static readonly IBrush Well = new SolidColorBrush(Color.FromArgb(0xE6, 0x0E, 0x08, 0x04));
        private static readonly IBrush Track = new SolidColorBrush(Color.FromArgb(0x55, 0x96, 0x7E, 0x5C));
        private static readonly IBrush TrackDone = new SolidColorBrush(LauncherSkin.Of(LauncherPalette.GreenTop));
        private static readonly IBrush Gold = new SolidColorBrush(LauncherSkin.Of(LauncherPalette.Gold));
        private static readonly IBrush LightGold = new SolidColorBrush(LauncherSkin.Of(LauncherPalette.LightGold));
        private static readonly IBrush Dim = new SolidColorBrush(LauncherSkin.Of(LauncherPalette.TextTertiary));
        private static readonly IBrush White = Brushes.White;

        // ─── Geometry ───────────────────────────────────────────────────────

        private double Radius => Math.Clamp(Spacing * 0.28, 9, 14);

        private double Spacing => _steps.Count > 1 ? (Bounds.Width - 2 * Inset) / (_steps.Count - 1) : 0;

        private double Inset => 16;

        private Point Centre(int i) => new(_steps.Count == 1 ? Bounds.Width / 2 : Inset + i * Spacing, Bounds.Height / 2);

        // ─── The pointer ────────────────────────────────────────────────────

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            int nearest = -1;
            double best = double.MaxValue;
            for (int i = 0; i < _steps.Count; i++)
            {
                double d = Math.Abs(Centre(i).X - p.X);
                if (d < best) { best = d; nearest = i; }
            }
            int now = best <= Math.Max(Radius + 6, Spacing / 2) ? nearest : -1;
            if (now != _hovered)
            {
                _hovered = now;
                HoveredChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (_hovered == -1) return;
            _hovered = -1;
            HoveredChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        // ─── Animation ──────────────────────────────────────────────────────

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RequestFrame();
        }

        private bool Moving()
        {
            foreach (var step in _steps)
                if (step.State == StepState.Running) return true;
            for (int i = 0; i < _steps.Count; i++)
                if (_shownArc.TryGetValue(i, out double shown) && Math.Abs(shown - _steps[i].Fraction) > 0.002) return true;
            return false;
        }

        private void RequestFrame()
        {
            if (_frameRequested || !Moving()) return;
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            _frameRequested = true;
            top.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan now)
        {
            _frameRequested = false;
            double dt = _last == TimeSpan.Zero ? 0.016 : Math.Clamp((now - _last).TotalSeconds, 0, 0.1);
            _last = now;
            _breath = (_breath + dt * 1.4) % (Math.PI * 2);
            for (int i = 0; i < _steps.Count; i++)
            {
                double target = _steps[i].Fraction;
                double shown = _shownArc.TryGetValue(i, out double s) ? s : 0;
                if (target < shown) shown = target;
                else shown += (target - shown) * (1 - Math.Exp(-dt * 6));
                _shownArc[i] = shown;
            }
            InvalidateVisual();
            if (Moving()) RequestFrame();
            else _last = TimeSpan.Zero;
        }

        // ─── Drawing ────────────────────────────────────────────────────────

        public override void Render(DrawingContext context)
        {
            int n = _steps.Count;
            if (n == 0 || Bounds.Width < 40) return;
            double r = Radius;

            // The track between nodes: green where both ends are behind, gold where the running
            // step is filling it, dim ahead.
            for (int i = 0; i + 1 < n; i++)
            {
                var a = Centre(i);
                var b = Centre(i + 1);
                double x0 = a.X + r + 3, x1 = b.X - r - 3;
                if (x1 <= x0) continue;
                var groove = new Rect(x0, a.Y - 1.5, x1 - x0, 3);
                context.DrawRectangle(Track, null, groove, 1.5, 1.5);
                var left = _steps[i].State;
                double filled = left is StepState.Done or StepState.Skipped ? 1
                              : left == StepState.Running ? Arc(i) : 0;
                if (filled > 0)
                {
                    var brush = left == StepState.Running ? Gold : TrackDone;
                    context.DrawRectangle(brush, null, new Rect(x0, a.Y - 1.5, (x1 - x0) * filled, 3), 1.5, 1.5);
                }
            }

            for (int i = 0; i < n; i++) DrawNode(context, i, Centre(i), r);
        }

        private double Arc(int i) => _shownArc.TryGetValue(i, out double s) ? s : _steps[i].Fraction;

        private void DrawNode(DrawingContext context, int i, Point c, double r)
        {
            var step = _steps[i];
            bool hovered = i == _hovered, focused = i == Focused;

            if (focused || hovered)
            {
                var ring = new Pen(new SolidColorBrush(LauncherSkin.Of(LauncherPalette.SoftGold), hovered ? 0.75 : 0.45), 1.5);
                context.DrawEllipse(null, ring, c, r + 5, r + 5);
            }

            switch (step.State)
            {
                case StepState.Done:
                    context.DrawEllipse(GreenFill, new Pen(new SolidColorBrush(LauncherSkin.Of(LauncherPalette.GreenBorder)), 1.2), c, r, r);
                    Tick(context, c, r * 0.5, White, 2);
                    break;

                case StepState.Skipped:
                    context.DrawEllipse(Well, new Pen(Dim, 1.4), c, r, r);
                    Tick(context, c, r * 0.45, Dim, 1.8);
                    break;

                case StepState.Failed:
                    context.DrawEllipse(RedFill, new Pen(new SolidColorBrush(LauncherSkin.Of(LauncherPalette.Danger)), 1.4), c, r, r);
                    double k = r * 0.38;
                    var cross = new Pen(White, 2, lineCap: PenLineCap.Round);
                    context.DrawLine(cross, new Point(c.X - k, c.Y - k), new Point(c.X + k, c.Y + k));
                    context.DrawLine(cross, new Point(c.X + k, c.Y - k), new Point(c.X - k, c.Y + k));
                    break;

                case StepState.Running:
                    double breath = 0.5 + 0.5 * Math.Sin(_breath);
                    context.DrawEllipse(new SolidColorBrush(LauncherSkin.Of(LauncherPalette.LightGold), 0.10 + 0.14 * breath), null, c,
                                        r + 4 + 3 * breath, r + 4 + 3 * breath);
                    context.DrawEllipse(Well, new Pen(new SolidColorBrush(LauncherSkin.Of(LauncherPalette.Gold), 0.35), 2.4), c, r, r);
                    ProgressArc(context, c, r, Arc(i));
                    Number(context, c, i + 1, LightGold, true);
                    break;

                default:
                    context.DrawEllipse(Well, new Pen(new SolidColorBrush(LauncherSkin.Of(LauncherPalette.BorderBrown)), 1.4), c, r, r);
                    Number(context, c, i + 1, Dim, false);
                    break;
            }
        }

        private static void Tick(DrawingContext context, Point c, double s, IBrush brush, double width)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(c.X - s, c.Y + s * 0.05), false);
                g.LineTo(new Point(c.X - s * 0.3, c.Y + s * 0.7));
                g.LineTo(new Point(c.X + s, c.Y - s * 0.65));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(brush, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }

        private static void ProgressArc(DrawingContext context, Point c, double r, double fraction)
        {
            fraction = Math.Clamp(fraction, 0, 1);
            if (fraction <= 0.005) return;
            var pen = new Pen(new SolidColorBrush(LauncherSkin.Of(LauncherPalette.LightGold)), 2.4, lineCap: PenLineCap.Round);
            if (fraction >= 0.999)
            {
                context.DrawEllipse(null, pen, c, r, r);
                return;
            }
            double end = -Math.PI / 2 + fraction * Math.PI * 2;
            var start = new Point(c.X, c.Y - r);
            var stop = new Point(c.X + r * Math.Cos(end), c.Y + r * Math.Sin(end));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(start, false);
                g.ArcTo(stop, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }

        private static readonly Typeface NumberFace = new(LauncherSkin.Ui, FontStyle.Normal, FontWeight.Bold);

        private static void Number(DrawingContext context, Point c, int number, IBrush brush, bool strong)
        {
            var text = new FormattedText(number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                                         FlowDirection.LeftToRight, NumberFace, strong ? 12 : 11, brush);
            context.DrawText(text, new Point(c.X - text.Width / 2, c.Y - text.Height / 2));
        }
    }
}
