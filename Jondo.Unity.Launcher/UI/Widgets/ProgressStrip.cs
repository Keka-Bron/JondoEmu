using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Jondo.Unity.Launcher.UI.Widgets
{
    /// <summary>
    /// A progress bar in the launcher's colours: a dark groove, the green of its buttons filling
    /// it, and while it is working a gleam that runs along what is filled. The fill glides to each
    /// new value instead of jumping, and never goes back.
    /// </summary>
    internal sealed class ProgressStrip : Control
    {
        public static readonly StyledProperty<double> FractionProperty =
            AvaloniaProperty.Register<ProgressStrip, double>(nameof(Fraction));

        public static readonly StyledProperty<bool> WorkingProperty =
            AvaloniaProperty.Register<ProgressStrip, bool>(nameof(Working));

        public static readonly StyledProperty<bool> FailedProperty =
            AvaloniaProperty.Register<ProgressStrip, bool>(nameof(Failed));

        static ProgressStrip()
        {
            AffectsRender<ProgressStrip>(FractionProperty, WorkingProperty, FailedProperty);
        }

        /// <summary>From 0 to 1.</summary>
        public double Fraction
        {
            get => GetValue(FractionProperty);
            set => SetValue(FractionProperty, value);
        }

        /// <summary>Whether it is moving now: the gleam runs only then.</summary>
        public bool Working
        {
            get => GetValue(WorkingProperty);
            set => SetValue(WorkingProperty, value);
        }

        /// <summary>Red instead of green: the step stopped there.</summary>
        public bool Failed
        {
            get => GetValue(FailedProperty);
            set => SetValue(FailedProperty, value);
        }

        private double _shown;
        private double _gleam;
        private TimeSpan _last;
        private bool _frameRequested;

        private static readonly IBrush Groove = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        private static readonly IPen GrooveEdge = new Pen(new SolidColorBrush(LauncherSkin.Of(LauncherPalette.LightBrown), 0.7), 1);
        private static readonly IBrush Green = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(LauncherSkin.Of(LauncherPalette.GreenTop), 0),
                new GradientStop(LauncherSkin.Of(LauncherPalette.GreenBottom), 1),
            },
        };
        private static readonly IBrush Red = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xD8, 0x4A, 0x3A), 0),
                new GradientStop(Color.FromRgb(0x8E, 0x22, 0x18), 1),
            },
        };
        private static readonly IBrush Shine = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 246, 206), 0),
                new GradientStop(Color.FromArgb(0x8C, 255, 246, 206), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 246, 206), 1),
            },
        };
        private static readonly IBrush Top = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == FractionProperty || change.Property == WorkingProperty) RequestFrame();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _shown = Math.Clamp(Fraction, 0, 1);
            RequestFrame();
        }

        private void RequestFrame()
        {
            if (_frameRequested) return;
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

            double target = Math.Clamp(Fraction, 0, 1);
            if (target < _shown) _shown = target;                          // a reset, not a step back
            else _shown += (target - _shown) * (1 - Math.Exp(-dt * 6));   // glides there
            if (Math.Abs(target - _shown) < 0.0005) _shown = target;

            if (Working) _gleam = (_gleam + dt * 0.55) % 1.6;
            InvalidateVisual();
            if (Working || _shown != target) RequestFrame();
            else _last = TimeSpan.Zero;
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            if (bounds.Width <= 2 || bounds.Height <= 1) return;
            double radius = bounds.Height / 2;
            context.DrawRectangle(Groove, GrooveEdge, bounds.Deflate(0.5), radius, radius);

            double width = Math.Round(bounds.Width * _shown, 1);
            if (width < 1) return;
            var fill = new Rect(0, 0, Math.Max(width, bounds.Height), bounds.Height).Deflate(1);
            using (context.PushClip(new RoundedRect(fill, radius)))
            {
                context.DrawRectangle(Failed ? Red : Green, null, fill);
                context.DrawRectangle(Top, null, new Rect(fill.X, fill.Y, fill.Width, Math.Max(1, fill.Height * 0.35)));
                if (Working && !Failed)
                {
                    double band = Math.Max(40, bounds.Width * 0.18);
                    double x = (_gleam - 0.3) * fill.Width * 1.0;
                    context.DrawRectangle(Shine, null, new Rect(x - band / 2, fill.Y, band, fill.Height));
                }
            }
        }
    }
}
