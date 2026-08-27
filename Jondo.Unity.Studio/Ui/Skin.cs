using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace Jondo.Unity.Studio.Ui
{
    /// <summary>
    /// What the editor looks like: one palette, in one place.
    /// </summary>
    /// <remarks>
    /// Called Skin and not Theme because every Avalonia control already has a Theme property, and
    /// a static class of the same name is invisible from inside one: the compiler resolves the
    /// instance member first and the error it gives points nowhere near the cause.
    /// </summary>
    /// <remarks>
    /// The first version was black with grey text, which read as a terminal rather than as a tool —
    /// and a terminal is exactly the thing this editor exists to stop people needing. The problem
    /// was not the darkness, it was that everything was the same darkness: with one flat background
    /// and one flat foreground there is nothing to tell a list from its container, or a row that
    /// was measured from a row somebody typed.
    ///
    /// So the ground is a warm charcoal rather than #000, it is built in three steps so panels sit
    /// on something, and the accent is the bronze of the icon. That last part is not decoration
    /// either: bronze, blue and green each carry a meaning that repeats on every screen —
    ///
    /// <code>
    ///   bronze   authored: somebody decided this
    ///   blue     measured: this came off a capture or out of the client
    ///   green    done: handled, answered, working
    ///   red      wrong: this would break something
    /// </code>
    ///
    /// which is the same distinction the provenance column makes, made visible without reading.
    /// </remarks>
    public static class Skin
    {
        // ─── The ground, in three steps ───────────────────────────────────────────

        /// <summary>The window. Warm charcoal, never black.</summary>
        public static readonly Color Base = Color.FromRgb(0x15, 0x17, 0x1C);

        /// <summary>A panel sitting on the window.</summary>
        public static readonly Color Surface = Color.FromRgb(0x1C, 0x1F, 0x26);

        /// <summary>Something sitting on a panel: a row, a field, a card.</summary>
        public static readonly Color Raised = Color.FromRgb(0x25, 0x29, 0x32);

        /// <summary>The line between two things.</summary>
        public static readonly Color Line = Color.FromRgb(0x32, 0x37, 0x42);

        // ─── The meanings ─────────────────────────────────────────────────────────

        /// <summary>Bronze. Authored: a person decided this. The colour of the icon.</summary>
        public static readonly Color Authored = Color.FromRgb(0xE8, 0x93, 0x3A);

        public static readonly Color AuthoredSoft = Color.FromRgb(0xF5, 0xB1, 0x5C);

        /// <summary>Blue. Measured: this came off a capture or out of the client.</summary>
        public static readonly Color MeasuredBlue = Color.FromRgb(0x5F, 0xA8, 0xD3);

        /// <summary>
        /// Violet. Derived: worked out from the client's own data rather than seen happening.
        /// </summary>
        /// <remarks>
        /// Its own colour because it carries its own warning. A blue row is a row somebody watched
        /// go past on the wire; a violet one is a conclusion — for an NPC placement, the map came
        /// out of the quest catalogue and the cell is a placeholder. Painting the two the same
        /// would be claiming a precision that is not there, and the whole point of the provenance
        /// column is that six months from now nobody can tell them apart without it.
        /// </remarks>
        public static readonly Color Derived = Color.FromRgb(0x9B, 0x8C, 0xD8);

        /// <summary>Green. Done: handled, answered, working.</summary>
        public static readonly Color Done = Color.FromRgb(0x6F, 0xCF, 0x8E);

        /// <summary>Red. Wrong: this would break something.</summary>
        public static readonly Color Wrong = Color.FromRgb(0xE0, 0x70, 0x5F);

        // ─── Text ─────────────────────────────────────────────────────────────────

        public static readonly Color Text = Color.FromRgb(0xE6, 0xE9, 0xEF);
        public static readonly Color TextSoft = Color.FromRgb(0x9A, 0xA2, 0xB0);
        public static readonly Color TextFaint = Color.FromRgb(0x6B, 0x73, 0x82);

        // ─── The brushes everything actually uses ─────────────────────────────────

        public static readonly IBrush BaseBrush = new SolidColorBrush(Base);
        public static readonly IBrush SurfaceBrush = new SolidColorBrush(Surface);
        public static readonly IBrush RaisedBrush = new SolidColorBrush(Raised);
        public static readonly IBrush LineBrush = new SolidColorBrush(Line);

        public static readonly IBrush TextBrush = new SolidColorBrush(Text);
        public static readonly IBrush TextSoftBrush = new SolidColorBrush(TextSoft);
        public static readonly IBrush TextFaintBrush = new SolidColorBrush(TextFaint);

        public static readonly IBrush AuthoredBrush = new SolidColorBrush(Authored);
        public static readonly IBrush AuthoredSoftBrush = new SolidColorBrush(AuthoredSoft);
        public static readonly IBrush MeasuredBrush = new SolidColorBrush(MeasuredBlue);
        public static readonly IBrush DerivedBrush = new SolidColorBrush(Derived);
        public static readonly IBrush DoneBrush = new SolidColorBrush(Done);
        public static readonly IBrush WrongBrush = new SolidColorBrush(Wrong);

        /// <summary>A wash of the accent, for a selected row.</summary>
        public static readonly IBrush AuthoredWash = new SolidColorBrush(Color.FromArgb(0x3A, 0xE8, 0x93, 0x3A));

        public static readonly IBrush HoverWash = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        /// <summary>
        /// The title bar's gradient. The one place the editor is allowed to be decorative, because
        /// it is the one place nothing has to be read off it.
        /// </summary>
        public static readonly IBrush Banner = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x22, 0x1A, 0x14), 0),
                new GradientStop(Color.FromRgb(0x1A, 0x1C, 0x24), 0.55),
                new GradientStop(Color.FromRgb(0x16, 0x1E, 0x28), 1),
            },
        };

        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace");

        public const double Radius = 6;

        /// <summary>
        /// The control styles, built from code because the whole editor is.
        /// </summary>
        /// <remarks>
        /// Fluent underneath, with the parts that made it look like a form from 2012 replaced:
        /// flat corners, no hover, and a selected row painted in the system accent — which on a
        /// machine whose accent is red made every selection look like an error.
        /// </remarks>
        public static Styles Build()
        {
            var styles = new Styles();

            styles.Add(Style<Window>(
                (Window.BackgroundProperty, BaseBrush),
                (Window.FontFamilyProperty, new FontFamily("Segoe UI Variable Text, Segoe UI, Inter, sans-serif"))));

            styles.Add(Style<TextBlock>(
                (TextBlock.ForegroundProperty, TextBrush),
                (TextBlock.FontSizeProperty, 13.0)));

            // Lists: no border of their own, rows that breathe, and a selection that reads as
            // "this one" rather than as a warning.
            styles.Add(Style<ListBox>(
                (ListBox.BackgroundProperty, SurfaceBrush),
                (ListBox.BorderThicknessProperty, new Thickness(1)),
                (ListBox.BorderBrushProperty, LineBrush),
                (ListBox.CornerRadiusProperty, new CornerRadius(Radius)),
                (ListBox.PaddingProperty, new Thickness(2))));

            styles.Add(Style<ListBoxItem>(
                (ListBoxItem.PaddingProperty, new Thickness(10, 6)),
                (ListBoxItem.CornerRadiusProperty, new CornerRadius(4)),
                (ListBoxItem.MinHeightProperty, 0.0)));

            var selected = new Style(x => x.OfType<ListBoxItem>().Class(":selected").Template()
                                            .OfType<ContentPresenter>());
            selected.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, AuthoredWash));
            selected.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, AuthoredBrush));
            selected.Setters.Add(new Setter(ContentPresenter.BorderThicknessProperty, new Thickness(0, 0, 0, 0)));
            styles.Add(selected);

            var hovered = new Style(x => x.OfType<ListBoxItem>().Class(":pointerover").Template()
                                           .OfType<ContentPresenter>());
            hovered.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, HoverWash));
            styles.Add(hovered);

            styles.Add(Style<Button>(
                (Button.BackgroundProperty, RaisedBrush),
                (Button.ForegroundProperty, TextBrush),
                (Button.BorderBrushProperty, LineBrush),
                (Button.BorderThicknessProperty, new Thickness(1)),
                (Button.CornerRadiusProperty, new CornerRadius(Radius)),
                (Button.PaddingProperty, new Thickness(14, 7))));

            styles.Add(Style<TextBox>(
                (TextBox.BackgroundProperty, RaisedBrush),
                (TextBox.ForegroundProperty, TextBrush),
                (TextBox.BorderBrushProperty, LineBrush),
                (TextBox.BorderThicknessProperty, new Thickness(1)),
                (TextBox.CornerRadiusProperty, new CornerRadius(Radius)),
                (TextBox.PaddingProperty, new Thickness(10, 6))));

            styles.Add(Style<ComboBox>(
                (ComboBox.BackgroundProperty, RaisedBrush),
                (ComboBox.ForegroundProperty, TextBrush),
                (ComboBox.BorderBrushProperty, LineBrush),
                (ComboBox.CornerRadiusProperty, new CornerRadius(Radius)),
                (ComboBox.PaddingProperty, new Thickness(10, 6))));

            styles.Add(Style<CheckBox>((CheckBox.ForegroundProperty, TextBrush)));

            styles.Add(Style<AutoCompleteBox>(
                (AutoCompleteBox.BackgroundProperty, RaisedBrush),
                (AutoCompleteBox.ForegroundProperty, TextBrush),
                (AutoCompleteBox.BorderBrushProperty, LineBrush),
                (AutoCompleteBox.CornerRadiusProperty, new CornerRadius(Radius))));

            return styles;
        }

        private static Style Style<T>(params (AvaloniaProperty Property, object Value)[] setters)
            where T : Control
        {
            var style = new Style(x => x.OfType<T>());
            foreach (var (property, value) in setters)
            {
                style.Setters.Add(new Setter(property, value));
            }

            return style;
        }

        // ─── Small pieces every page builds ───────────────────────────────────────

        /// <summary>A heading, the same size everywhere.</summary>
        public static TextBlock Heading(string text) => new TextBlock
        {
            Text = text,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextBrush,
        };

        /// <summary>A quiet label above a field.</summary>
        public static TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = TextSoftBrush,
            Margin = new Thickness(0, 6, 0, 3),
        };

        /// <summary>Monospaced text, for anything that is really a number.</summary>
        public static TextBlock Fixed(string text, IBrush? colour = null) => new TextBlock
        {
            Text = text,
            FontFamily = Mono,
            FontSize = 12.5,
            Foreground = colour ?? TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        /// <summary>A panel with a border, for grouping.</summary>
        public static Border Card(Control inside, double pad = 12) => new Border
        {
            Background = SurfaceBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            Padding = new Thickness(pad),
            Child = inside,
        };

        /// <summary>A little coloured square with a word next to it.</summary>
        public static Control Key(IBrush colour, string label)
        {
            var panel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 0, 16, 0),
            };

            panel.Children.Add(new Border
            {
                Background = colour,
                Width = 11,
                Height = 11,
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });

            panel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = TextSoftBrush,
                FontSize = 12,
            });

            return panel;
        }
    }
}
