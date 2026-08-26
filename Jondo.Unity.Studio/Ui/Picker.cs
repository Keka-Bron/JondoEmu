using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Jondo.Unity.Studio.Ui
{
    /// <summary>
    /// A field you type into to find one thing out of thousands.
    /// </summary>
    /// <remarks>
    /// The first version of these screens used a plain drop-down. With 6,468 NPCs and 5,134
    /// monsters in it, finding "Milubo Grankrok" meant scrolling a list the height of a book —
    /// which is not a small annoyance, it is the difference between the screen being usable and
    /// not.
    ///
    /// Matching is on the name <em>and</em> on the id, because both are how people refer to these
    /// things here: a name when reading, an id when it came out of a log or a capture.
    /// </remarks>
    public static class Picker
    {
        /// <summary>How many matches are offered at once. More is a wall, fewer is a guess.</summary>
        private const int Shown = 40;

        /// <summary>
        /// Builds the field.
        /// </summary>
        /// <param name="items">Everything it can find.</param>
        /// <param name="line">What one item reads as.</param>
        /// <param name="key">The id, matched as well as the name.</param>
        /// <param name="chosen">Called with the item once one is picked.</param>
        public static AutoCompleteBox Of<T>(IEnumerable<T> items, Func<T, string> line, Func<T, long> key,
                                            string placeholder, Action<T> chosen, double width = 300,
                                            Func<T, IImage?>? picture = null)
            where T : class
        {
            var box = new AutoCompleteBox
            {
                ItemsSource = items,
                Width = width,
                Watermark = placeholder,
                MinimumPrefixLength = 1,
                MaxDropDownHeight = 420,
                FilterMode = AutoCompleteFilterMode.None,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // Matching is done here rather than by FilterMode so that the id counts too, and so
            // that the list is cut off before it becomes a wall.
            int matched = 0;
            box.ItemFilter = (search, item) =>
            {
                if (item is not T typed) return false;

                search = (search ?? "").Trim();
                if (search.Length == 0) return false;

                bool hit = line(typed).Contains(search, StringComparison.CurrentCultureIgnoreCase)
                        || key(typed).ToString().Contains(search, StringComparison.Ordinal);

                if (!hit) return false;
                return matched++ < Shown;
            };

            // The counter has to start again for every keystroke, and the filter is called once
            // per item per keystroke, so it is reset when the text changes.
            box.TextChanged += (_, _) => matched = 0;

            box.ItemTemplate = new FuncDataTemplate<T>((item, _) =>
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("30,58,*") };

                // The picture when there is one, and nothing at all when there is not: a row of
                // empty boxes reads as a list that failed to load.
                var image = picture?.Invoke(item);
                if (image != null)
                {
                    var shown = new Image
                    {
                        Source = image,
                        Width = 24,
                        Height = 24,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(shown, 0);
                    row.Children.Add(shown);
                }

                var id = Skin.Fixed(key(item).ToString(), Skin.TextFaintBrush);
                Grid.SetColumn(id, 1);
                row.Children.Add(id);

                var name = new TextBlock
                {
                    Text = line(item),
                    Foreground = Skin.TextBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(name, 2);
                row.Children.Add(name);

                return row;
            }, supportsRecycling: true);

            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is T picked) chosen(picked);
            };

            return box;
        }

        /// <summary>Empties the field, so the next search starts clean.</summary>
        public static void Clear(AutoCompleteBox box)
        {
            box.SelectedItem = null;
            box.Text = "";
        }
    }
}
