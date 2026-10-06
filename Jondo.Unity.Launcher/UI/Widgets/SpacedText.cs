using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// Text with the letters spaced out, which is the style sheet's <c>letter-spacing</c>.
    /// </summary>
    /// <remarks>
    /// Neither GDI+ nor Avalonia bring it out of the box, so the letters are painted one by one. It is the
    /// same workaround DrawSpacedText did in the Windows Forms version and for the same reason:
    /// the launcher's tabs and action buttons are spaced, and without this they look
    /// cramped and stop looking like the earlier ones.
    ///
    /// With zero spacing it is painted in one go on purpose: splitting it letter by letter moves the
    /// characters through pixel rounding, and it shows in long texts.
    /// </remarks>
    internal sealed class SpacedText : Control
    {
        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<SpacedText, string>(nameof(Text), "");

        public static readonly StyledProperty<double> SpacingProperty =
            AvaloniaProperty.Register<SpacedText, double>(nameof(Spacing));

        public static readonly StyledProperty<IBrush?> ForegroundProperty =
            AvaloniaProperty.Register<SpacedText, IBrush?>(nameof(Foreground));

        /// <summary>The shadow underneath, which on the website was the buttons' text-shadow.</summary>
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
