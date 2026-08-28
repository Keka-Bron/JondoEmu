using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Studio.Data;

namespace Jondo.Unity.Studio.Ui
{
    /// <summary>
    /// The field every screen uses to say which map it is looking at.
    /// </summary>
    /// <remarks>
    /// It takes a map id, a coordinate, or part of an area's name, and it offers what it found
    /// rather than jumping. Offering matters: more than one map sits on the same [x, y] — the
    /// square you stand on outdoors and the inside of the house on it are different maps at the
    /// same coordinate — so a search that picked one for you would be right about half the time
    /// and silent about it.
    ///
    /// A map id is a number nobody carries in their head. A coordinate is the thing that is on
    /// screen while you play, which is why this exists at all.
    /// </remarks>
    public sealed class MapField : Border
    {
        private readonly MapCatalogue _maps;
        private readonly TextBox _search;
        private readonly ListBox _found;
        private readonly TextBlock _showing;
        private readonly TextBlock _nothing;

        public MapField(MapCatalogue maps, double width = 320)
        {
            _maps = maps;

            Background = Skin.SurfaceBrush;
            BorderBrush = Skin.LineBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(Skin.Radius);
            Padding = new Thickness(10);
            Width = width;

            _search = new TextBox
            {
                Watermark = Words.T("maps.byCoordinates") + "  ·  4,-18  ·  id  ·  " + Words.T("common.name"),
                FontSize = 12.5,
            };

            _showing = new TextBlock
            {
                Foreground = Skin.TextSoftBrush,
                FontSize = 12,
                Margin = new Thickness(2, 6, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            _nothing = new TextBlock
            {
                Text = Words.T("maps.nothingHere"),
                Foreground = Skin.TextFaintBrush,
                FontSize = 12,
                Margin = new Thickness(2, 8, 0, 0),
                IsVisible = false,
            };

            _found = new ListBox
            {
                MaxHeight = 230,
                Margin = new Thickness(0, 8, 0, 0),
                IsVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                ItemTemplate = new FuncDataTemplate<MapPlace>((place, _) => Row(place), supportsRecycling: true),
            };

            _search.TextChanged += (_, _) => Look();
            _found.SelectionChanged += (_, _) =>
            {
                if (_found.SelectedItem is not MapPlace place) return;
                Take(place);
            };

            var panel = new StackPanel { Spacing = 0 };
            panel.Children.Add(Skin.Label(Words.T("maps.title")));
            panel.Children.Add(_search);
            panel.Children.Add(_showing);
            panel.Children.Add(_nothing);
            panel.Children.Add(_found);
            Child = panel;

            Describe(null);
        }

        /// <summary>The map in the field, or zero.</summary>
        public long Current { get; private set; }

        /// <summary>Raised when somebody picks one out of the list.</summary>
        public event Action<MapPlace>? Chosen;

        /// <summary>Puts a map in the field without going through the search.</summary>
        public void Set(long mapId)
        {
            Current = mapId;
            Describe(_maps.Of(mapId));
            _found.IsVisible = false;
        }

        private void Take(MapPlace place)
        {
            Current = place.MapId;
            Describe(place);

            _found.IsVisible = false;
            _nothing.IsVisible = false;
            _found.SelectedItem = null;
            _search.Text = "";

            Chosen?.Invoke(place);
        }

        private void Look()
        {
            string needle = (_search.Text ?? "").Trim();
            if (needle.Length == 0)
            {
                _found.IsVisible = false;
                _nothing.IsVisible = false;
                return;
            }

            List<MapPlace> hits = _maps.Find(needle);
            _found.ItemsSource = hits;
            _found.IsVisible = true;

            // Saying "nothing here" is worth saying: an empty list under a field looks like a
            // field that has not finished thinking. It goes in a label of its own and NOT by
            // swapping the list's template for null, which is what it used to do — that threw the
            // template away for good and every later search came out as ToString().
            _nothing.IsVisible = hits.Count == 0;
        }

        private void Describe(MapPlace? place)
        {
            if (place == null)
            {
                _showing.Text = Current == 0
                    ? Words.T("npc.hintNoMap")
                    : Current.ToString();
                return;
            }

            string what = place.Outdoor ? Words.T("maps.outdoor") : Words.T("maps.indoor");
            string counts = place.Npcs > 0 || place.Groups > 0
                ? $"  ·  {place.Npcs} NPC  ·  {place.Groups} ×"
                : "";

            _showing.Text = $"{place.MapId}  ·  {place.Where}  ·  " +
                            (place.Area.Length > 0 ? place.Area + "  ·  " : "") + what + counts;
        }

        private static Control Row(MapPlace place)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            var where = Skin.Fixed(place.Where, Skin.AuthoredSoftBrush);
            where.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(where, 0);
            row.Children.Add(where);

            var area = new TextBlock
            {
                Text = place.Area.Length > 0 ? place.Area : place.MapId.ToString(),
                Foreground = Skin.TextBrush,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(area, 1);
            row.Children.Add(area);

            var tail = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (place.Npcs > 0) tail.Children.Add(Skin.Fixed(place.Npcs + " NPC", Skin.MeasuredBrush));
            if (place.Groups > 0) tail.Children.Add(Skin.Fixed(place.Groups + " ×", Skin.WrongBrush));
            tail.Children.Add(Skin.Fixed(place.Outdoor ? "☀" : "▣", Skin.TextFaintBrush));

            Grid.SetColumn(tail, 2);
            row.Children.Add(tail);
            return row;
        }
    }
}
