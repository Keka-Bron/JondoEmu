using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// Texto con las letras separadas, que es el <c>letter-spacing</c> de la hoja de estilos.
    /// </summary>
    /// <remarks>
    /// Ni GDI+ ni Avalonia lo traen de serie, asi que las letras se pintan de una en una. Es el
    /// mismo apano que hacia DrawSpacedText en la version de Windows Forms y por la misma razon:
    /// las pestanas y los botones de accion del lanzador van espaciados, y sin esto se ven
    /// apretados y dejan de parecer los de antes.
    ///
    /// Con separacion cero se pinta de una sola vez a proposito: partirlo letra a letra mueve los
    /// caracteres por el redondeo a pixel, y se nota en los textos largos.
    /// </remarks>
    internal sealed class SpacedText : Control
    {
        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<SpacedText, string>(nameof(Text), "");

        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<SpacedText, double>(nameof(Spacing));

        public static readonly StyledProperty<IBrush?> ForegroundProperty =
            AvaloniaProperty.Register<SpacedText, IBrush?>(nameof(Foreground));

        /// <summary>La sombra de debajo, que en la web era el text-shadow de los botones.</summary>
        public static readonly StyledProperty<bool> ShadowProperty =
            AvaloniaProperty.Register<SpacedText, bool>(nameof(Shadow));

        public static readonly StyledProperty<double> FontSizeProperty =
            TextBlock.FontSizeProperty.AddOwner<SpacedText>();

        public static readonly StyledProperty<FontFamily> FontFamilyProperty =
            TextBlock.FontFamilyProperty.AddOwner<SpacedText>();

        public static readonly StyledProperty<FontWeight> FontWeightProperty =
            TextBlock.FontWeightProperty.AddOwner<SpacedText>();

        static SpacedText()
        {
            AffectsRender<SpacedText>(TextProperty, SpacingProperty, ForegroundProperty,
                                      ShadowProperty, FontSizeProperty, FontFamilyProperty,
                                      FontWeightProperty);
            AffectsMeasure<SpacedText>(TextProperty, SpacingProperty, FontSizeProperty,
                                       FontFamilyProperty, FontWeightProperty);
        }

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public double Spacing
        {
            get => GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        public IBrush? Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public bool Shadow
        {
            get => GetValue(ShadowProperty);
            set => SetValue(ShadowProperty, value);
        }

        public double FontSize
        {
            get => GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public FontFamily FontFamily
        {
            get => GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public FontWeight FontWeight
        {
            get => GetValue(FontWeightProperty);
            set => SetValue(FontWeightProperty, value);
        }

        private Typeface Face => new Typeface(FontFamily, FontStyle.Normal, FontWeight);

        private FormattedText Piece(string text, IBrush? brush) => new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face,
            FontSize <= 0 ? 12 : FontSize, brush);

        protected override Size MeasureOverride(Size availableSize)
        {
            string text = Text ?? "";
            if (text.Length == 0) return default;

            var whole = Piece(text, Foreground);
            double width = whole.Width + Spacing * Math.Max(0, text.Length - 1);
            return new Size(width, whole.Height);
        }

        public override void Render(DrawingContext context)
        {
            string text = Text ?? "";
            if (text.Length == 0) return;

            if (Shadow) Draw(context, text, new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), 1);
            Draw(context, text, Foreground, 0);
        }

        private void Draw(DrawingContext context, string text, IBrush? brush, double dy)
        {
            var whole = Piece(text, brush);
            double top = (Bounds.Height - whole.Height) / 2 + dy;

            if (Spacing <= 0.01)
            {
                context.DrawText(whole, new Point((Bounds.Width - whole.Width) / 2, top));
                return;
            }

            var widths = new double[text.Length];
            double total = 0;
            for (int i = 0; i < text.Length; i++)
            {
                widths[i] = Piece(text[i].ToString(), brush).WidthIncludingTrailingWhitespace;
                total += widths[i] + Spacing;
            }
            if (text.Length > 0) total -= Spacing;

            double x = (Bounds.Width - total) / 2;
            for (int i = 0; i < text.Length; i++)
            {
                context.DrawText(Piece(text[i].ToString(), brush), new Point(x, top));
                x += widths[i] + Spacing;
            }
        }
    }
}
