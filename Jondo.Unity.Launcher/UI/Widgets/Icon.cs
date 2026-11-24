using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

#if JONDO_SERVER
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;
namespace Jondo.Unity.Server.UI.Skin
#else
namespace Jondo.Unity.Launcher.UI.Widgets
#endif
{
    /// <summary>
    /// A line icon of the launcher: drawn on a 24-unit grid with round ends, in the colour of the
    /// text around it, so a button's icon lights up with its label on hover.
    /// </summary>
    /// <remarks>
    /// One family, one stroke: icons from different sets side by side are what makes an
    /// interface look assembled. The shapes are plain geometry written here.
    /// </remarks>
    internal sealed class Icon : Control
    {
        public static readonly StyledProperty<string> KindProperty =
            AvaloniaProperty.Register<Icon, string>(nameof(Kind), "");

        /// <summary>The colour: the surrounding text's unless set.</summary>
        public static readonly StyledProperty<IBrush?> ForegroundProperty =
            TextElement.ForegroundProperty.AddOwner<Icon>();

        public static readonly StyledProperty<double> StrokeProperty =
            AvaloniaProperty.Register<Icon, double>(nameof(Stroke), 1.8);

        static Icon()
        {
            AffectsRender<Icon>(KindProperty, ForegroundProperty, StrokeProperty);
            AffectsMeasure<Icon>(KindProperty);
        }

        public Icon()
        {
            Width = 18;
            Height = 18;
        }

        public string Kind
        {
            get => GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        public IBrush? Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        /// <summary>The line's width in grid units (the grid is 24 wide).</summary>
        public double Stroke
        {
            get => GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        /// <summary>Each icon: its outline, and what of it is filled rather than stroked.</summary>
        private static readonly Dictionary<string, (string Line, string? Fill)> Shapes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["play"] = ("", "M8,5 L19,12 L8,19 Z"),
            ["users"] = ("M9,11 A4,4 0 1 0 9,3 A4,4 0 1 0 9,11 Z M2,21 C2,17 5,14 9,14 C13,14 16,17 16,21 " +
                         "M16,3.6 A4,4 0 0 1 16,10.4 M22,21 C22,17.6 20.4,15.2 17.6,14.4", null),
            ["sliders"] = ("M4,6 L20,6 M4,12 L20,12 M4,18 L20,18", "M9,6 m-2.6,0 a2.6,2.6 0 1 0 5.2,0 a2.6,2.6 0 1 0 -5.2,0 Z " +
                           "M15,12 m-2.6,0 a2.6,2.6 0 1 0 5.2,0 a2.6,2.6 0 1 0 -5.2,0 Z M7,18 m-2.6,0 a2.6,2.6 0 1 0 5.2,0 a2.6,2.6 0 1 0 -5.2,0 Z"),
            ["sound"] = ("M4,9 L8,9 L13,5 L13,19 L8,15 L4,15 Z M16.5,8.8 A4.5,4.5 0 0 1 16.5,15.2 M19,6.2 A8,8 0 0 1 19,17.8", null),
            ["mute"] = ("M4,9 L8,9 L13,5 L13,19 L8,15 L4,15 Z M16.5,9.5 L21.5,14.5 M21.5,9.5 L16.5,14.5", null),
            ["folder"] = ("M3,7 A2,2 0 0 1 5,5 L9.5,5 L11.5,7.5 L19,7.5 A2,2 0 0 1 21,9.5 L21,17 A2,2 0 0 1 19,19 L5,19 A2,2 0 0 1 3,17 Z", null),
            ["server"] = ("M4,4.5 L20,4.5 L20,10.5 L4,10.5 Z M4,13.5 L20,13.5 L20,19.5 L4,19.5 Z M7.5,7.5 L7.6,7.5 M7.5,16.5 L7.6,16.5", null),
            ["globe"] = ("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M3.5,9 L20.5,9 M3.5,15 L20.5,15 " +
                         "M12,3 C8.5,7 8.5,17 12,21 M12,3 C15.5,7 15.5,17 12,21", null),
            ["image"] = ("M4,5 L20,5 L20,19 L4,19 Z M4,16 L9,11 L13,15 L15.5,12.5 L20,17", "M15.5,8.5 m-1.6,0 a1.6,1.6 0 1 0 3.2,0 a1.6,1.6 0 1 0 -3.2,0 Z"),
            ["box"] = ("M12,3 L20,7.5 L20,16.5 L12,21 L4,16.5 L4,7.5 Z M4,7.5 L12,12 L20,7.5 M12,12 L12,21", null),
            ["refresh"] = ("M19.5,10 A8,8 0 0 0 5,7 M4.5,3.5 L4.5,7.5 L8.5,7.5 M4.5,14 A8,8 0 0 0 19,17 M19.5,20.5 L19.5,16.5 L15.5,16.5", null),
            ["wrench"] = ("M14.7,6.3 A4.2,4.2 0 0 0 9.4,11.6 L3.8,17.2 A1.9,1.9 0 0 0 6.8,20.2 L12.4,14.6 A4.2,4.2 0 0 0 17.7,9.3 L15.3,11.7 L12.3,11.7 L12.3,8.7 Z", null),
            ["trash"] = ("M4,7 L20,7 M9.5,7 L9.5,4.5 L14.5,4.5 L14.5,7 M6,7 L7,20 L17,20 L18,7 M10,11 L10,16 M14,11 L14,16", null),
            ["check"] = ("M5,12.5 L10,17 L19,7", null),
            ["close"] = ("M6,6 L18,18 M18,6 L6,18", null),
            ["download"] = ("M12,4 L12,15 M7,10 L12,15 L17,10 M5,20 L19,20", null),
            ["user-plus"] = ("M10,11 A4,4 0 1 0 10,3 A4,4 0 1 0 10,11 Z M3,21 C3,17 6,14 10,14 C11.8,14 13.3,14.6 14.5,15.5 M19,14 L19,20 M16,17 L22,17", null),
            ["login"] = ("M10,17 L15,12 L10,7 M15,12 L3,12 M14,4 L19,4 A1,1 0 0 1 20,5 L20,19 A1,1 0 0 1 19,20 L14,20", null),
            ["info"] = ("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,11 L12,16.5 M12,7.8 L12,7.9", null),
            ["alert"] = ("M12,3.5 L21.5,20 L2.5,20 Z M12,10 L12,14 M12,17 L12,17.1", null),
            ["monitor"] = ("M3,4.5 L21,4.5 L21,16 L3,16 Z M8,20 L16,20 M12,16 L12,20", null),
            ["branch"] = ("M6,3 L6,15 M18,9 C18,13 13,13.5 7.5,16.2 M6,21 A3,3 0 1 0 6,15 A3,3 0 1 0 6,21 Z M18,9 A3,3 0 1 0 18,3 A3,3 0 1 0 18,9 Z", null),
            ["clock"] = ("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,7 L12,12 L15.5,14", null),
            ["link"] = ("M10,14 L14,10 M8.5,11.5 L6.5,13.5 A3.5,3.5 0 0 0 11.5,18.5 L13.5,16.5 M15.5,12.5 L17.5,10.5 A3.5,3.5 0 0 0 12.5,5.5 L10.5,7.5", null),
            ["arrow-left"] = ("M19,12 L5,12 M11,6 L5,12 L11,18", null),
            ["chevron-right"] = ("M9,6 L15,12 L9,18", null),
            ["key"] = ("M8,15 A4,4 0 1 0 8,7 A4,4 0 1 0 8,15 Z M12,11 L21,11 M18,11 L18,14 M15.5,11 L15.5,13.5", null),
            ["drive"] = ("M3,13 L5.6,5.6 A1.6,1.6 0 0 1 7.1,4.5 L16.9,4.5 A1.6,1.6 0 0 1 18.4,5.6 L21,13 L21,18 " +
                         "A1.5,1.5 0 0 1 19.5,19.5 L4.5,19.5 A1.5,1.5 0 0 1 3,18 Z M3,13 L21,13 M16.5,16.3 L16.6,16.3", null),
            ["shortcut"] = ("M4,5 L20,5 L20,19 L4,19 Z M9,15 L15,9 M10.5,9 L15,9 L15,13.5", null),
            ["calendar"] = ("M4,6.5 L20,6.5 L20,20 L4,20 Z M4,10.5 L20,10.5 M8.5,4 L8.5,8.5 M15.5,4 L15.5,8.5",
                            "M8,13.5 L10,13.5 L10,15.5 L8,15.5 Z M14,13.5 L16,13.5 L16,15.5 L14,15.5 Z"),
            ["megaphone"] = ("M4,10 L4,14 L7,14 L16,19 L16,5 L7,10 Z M7,14 L8.5,19.5 L11,19.5 L10,15.6 M19,9.5 A3,3 0 0 1 19,14.5", null),
            ["terminal"] = ("M3.5,5 L20.5,5 L20.5,19 L3.5,19 Z M7,9.5 L10,12 L7,14.5 M12.5,15 L17,15", null),
            ["activity"] = ("M3,12.5 L7,12.5 L9.5,6 L14,19 L16.5,12.5 L21,12.5", null),
            ["stop"] = ("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z", "M9,9 L15,9 L15,15 L9,15 Z"),
            ["search"] = ("M10.5,4 A6.5,6.5 0 1 0 10.5,17 A6.5,6.5 0 1 0 10.5,4 Z M15.3,15.3 L20,20", null),
            ["sword"] = ("M14.5,4 L20,4 L20,9.5 L10,19.5 L7.5,17 Z M6,14.5 L9.5,18 M4,20 L7,17", null),
            ["map"] = ("M3.5,6 L9,4 L15,6 L20.5,4 L20.5,18 L15,20 L9,18 L3.5,20 Z M9,4 L9,18 M15,6 L15,20", null),
        };

        private static readonly Dictionary<string, (Geometry? Line, Geometry? Fill)> Parsed = new(StringComparer.OrdinalIgnoreCase);

        private static (Geometry? Line, Geometry? Fill) Geometries(string kind)
        {
            lock (Parsed)
            {
                if (Parsed.TryGetValue(kind, out var known)) return known;
                (Geometry?, Geometry?) made = (null, null);
                if (Shapes.TryGetValue(kind, out var shape))
                {
                    made = (shape.Line.Length > 0 ? Geometry.Parse(shape.Line) : null,
                            shape.Fill != null ? Geometry.Parse(shape.Fill) : null);
                }
                Parsed[kind] = made;
                return made;
            }
        }

        public override void Render(DrawingContext context)
        {
            var (line, fill) = Geometries(Kind);
            if (line == null && fill == null) return;
            var brush = Foreground ?? Brushes.White;
            double scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
            if (scale <= 0) return;
            double dx = (Bounds.Width - 24 * scale) / 2, dy = (Bounds.Height - 24 * scale) / 2;
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            {
                if (fill != null) context.DrawGeometry(brush, null, fill);
                if (line != null)
                {
                    var pen = new Pen(brush, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                    context.DrawGeometry(null, pen, line);
                }
            }
        }
    }
}
