using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.Studio.Pages;
using Jondo.Unity.Studio.Ui;
using Jondo.Unity.World.Client;

namespace Jondo.Unity.Studio
{
    /// <summary>
    /// The editor's only window: the sections across the top, the section underneath.
    /// </summary>
    /// <remarks>
    /// The sections used to be a column down the left, and that column was 210 pixels of the
    /// working area held permanently for seven words. It cost the screens that need width most —
    /// the placement list has six columns and the dialogue tree has whole sentences in it — and
    /// both were being trimmed with ellipses to pay for a menu nobody looks at twice a minute.
    /// Across the top it costs one row of height and gives all of it back.
    ///
    /// Everything writes to <c>content/</c>. Nothing here opens <c>world.db</c> for writing and
    /// nothing here talks to a running server.
    /// </remarks>
    public sealed class Shell : Window
    {
        private readonly ContentControl _content = new ContentControl();
        private readonly List<IStudioPage> _pages = new List<IStudioPage>();
        private readonly List<Button> _tabs = new List<Button>();
        private readonly WorldData _world;

        private int _showing;

        public Shell()
        {
            Title = "Jondo Studio";

            // Maximised from the start. This is a workbench, not a dialog: the cell grid alone is
            // 560 cells wide and the placement list has six columns.
            WindowState = WindowState.Maximized;
            Width = 1280;
            Height = 820;
            MinWidth = 980;
            MinHeight = 620;
            Background = Skin.BaseBrush;

            _world = WorldData.Load();
            _pages.AddRange(Sections(_world));

            var layout = new DockPanel { LastChildFill = true };
            var bar = BuildBar();
            DockPanel.SetDock(bar, Dock.Top);
            layout.Children.Add(bar);

            _content.Margin = new Thickness(18, 14, 18, 16);
            _content.HorizontalAlignment = HorizontalAlignment.Stretch;
            _content.VerticalAlignment = VerticalAlignment.Stretch;
            layout.Children.Add(_content);

            Content = layout;
            Show(0);

            // A change of language changes both halves at once: the editor's own words and the
            // game's, because an NPC is not called the same thing in Spanish and in French and a
            // tree built against one set of names has to be readable from the other.
            Words.Changed += () =>
            {
                _world.UseLanguage(Words.Current);
                Relabel();
                Show(_showing);
            };
        }

        private Control BuildBar()
        {
            var bar = new Border
            {
                Background = Skin.Banner,
                BorderBrush = Skin.LineBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(18, 10),
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            // ─── The name ─────────────────────────────────────────────────────────
            var wordmark = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 26, 0),
            };

            wordmark.Children.Add(new Border
            {
                Width = 10,
                Height = 22,
                CornerRadius = new CornerRadius(3),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Skin.AuthoredSoft, 0),
                        new GradientStop(Skin.Authored, 1),
                    },
                },
                VerticalAlignment = VerticalAlignment.Center,
            });

            wordmark.Children.Add(new TextBlock
            {
                Text = "JONDO",
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = Skin.TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });

            wordmark.Children.Add(new TextBlock
            {
                Text = "STUDIO",
                FontSize = 15,
                FontWeight = FontWeight.Light,
                Foreground = Skin.AuthoredBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });

            Grid.SetColumn(wordmark, 0);
            row.Children.Add(wordmark);

            // ─── The sections ─────────────────────────────────────────────────────
            var tabs = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < _pages.Count; i++)
            {
                int which = i;
                var tab = new Button
                {
                    Content = _pages[i].Title,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0, 0, 0, 2),
                    BorderBrush = Brushes.Transparent,
                    Foreground = Skin.TextSoftBrush,
                    CornerRadius = new CornerRadius(0),
                    Padding = new Thickness(13, 7),
                    Margin = new Thickness(0, 0, 2, 0),
                };
                tab.Click += (_, _) => Show(which);
                _tabs.Add(tab);
                tabs.Children.Add(tab);
            }

            Grid.SetColumn(tabs, 1);
            row.Children.Add(tabs);

            // ─── The language ─────────────────────────────────────────────────────
            var languages = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };

            foreach (var language in Words.Offered)
            {
                var pick = new Button
                {
                    Content = Words.TagOf(language),
                    Padding = new Thickness(9, 5),
                    FontSize = 11.5,
                    Tag = language,
                };
                pick.Click += (_, _) => Words.Use(language);
                languages.Children.Add(pick);
            }

            _languageButtons = languages;
            Grid.SetColumn(languages, 2);
            row.Children.Add(languages);

            bar.Child = row;
            Relabel();
            return bar;
        }

        private StackPanel? _languageButtons;

        /// <summary>Puts the section names and the language buttons back in the language in use.</summary>
        private void Relabel()
        {
            for (int i = 0; i < _tabs.Count && i < _pages.Count; i++)
            {
                _tabs[i].Content = _pages[i].Title;
            }

            if (_languageButtons == null) return;
            foreach (var child in _languageButtons.Children)
            {
                if (child is not Button button || button.Tag is not GameLanguage language) continue;

                bool on = language == Words.Current;
                button.Background = on ? Skin.AuthoredWash : Skin.RaisedBrush;
                button.Foreground = on ? Skin.AuthoredSoftBrush : Skin.TextSoftBrush;
                button.BorderBrush = on ? Skin.AuthoredBrush : Skin.LineBrush;
            }
        }

        private void Show(int which)
        {
            if (which < 0 || which >= _pages.Count) return;

            _showing = which;
            for (int i = 0; i < _tabs.Count; i++)
            {
                bool on = i == which;
                _tabs[i].Foreground = on ? Skin.TextBrush : Skin.TextSoftBrush;
                _tabs[i].BorderBrush = on ? Skin.AuthoredBrush : Brushes.Transparent;
                _tabs[i].FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
            }

            _content.Content = Safely(_pages[which]);
        }

        /// <summary>
        /// Every section, in the order the bar shows them.
        /// </summary>
        /// <remarks>
        /// Out here rather than inline in the constructor so that <c>--selftest</c> can build them
        /// all without opening a window. One list, one order, and no way for the two to drift.
        /// </remarks>
        internal static List<IStudioPage> Sections(WorldData world) => new List<IStudioPage>
        {
            new OverviewPage(world),
            new TrafficPage(world),
            new PacketShapesPage(world),
            new NpcPlacementsPage(world),
            new NpcDialoguesPage(world),
            new MonsterGroupsPage(world),
            new SpellsPage(world),
            new PassagesPage(world),
            new MapCellsPage(world),
        };

        /// <summary>
        /// Builds a section, and shows what went wrong instead of taking the window down with it.
        /// </summary>
        /// <remarks>
        /// Every section reads files that are regenerated by tools outside this program, so one of
        /// them meeting something it did not expect is a matter of when. An editor that dies on
        /// startup tells you nothing; one that says which section broke and why can still be used
        /// for the other six.
        /// </remarks>
        private static Control Safely(IStudioPage page)
        {
            try
            {
                return page.Build();
            }
            catch (Exception ex)
            {
                return new ScrollViewer
                {
                    Content = new SelectableTextBlock
                    {
                        Text = $"'{page.Title}' could not be built." +
                               Environment.NewLine + Environment.NewLine + ex,
                        FontFamily = Skin.Mono,
                        Foreground = Skin.WrongBrush,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
            }
        }
    }

    /// <summary>One section of the editor.</summary>
    public interface IStudioPage
    {
        /// <summary>The key its name lives under in <see cref="Words"/>.</summary>
        string TitleKey { get; }

        /// <summary>What the bar shows, in whatever language is in use.</summary>
        string Title => Words.T(TitleKey);

        /// <summary>Builds the section's contents. Called every time it is shown.</summary>
        Control Build();
    }
}
