using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.Studio.Ui;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Studio.Pages
{
    /// <summary>
    /// Every kind of packet that has been seen, what is known about it, and how far along it is.
    /// </summary>
    /// <remarks>
    /// What has been <em>observed</em> — counts, timestamps, samples — stays in
    /// <c>bases/paquetes.db</c> and in the traffic log, both of which the server owns and both of
    /// which are regenerable. What somebody <em>concluded</em> goes to
    /// <c>content/packets/shapes.json</c>, which is versioned text. Wiping the database costs a few
    /// counters; losing the conclusions would cost everything anybody ever worked out.
    ///
    /// The traffic log is walked when this screen is first opened rather than on a button, because
    /// a screen that opens showing two rows and a button reads as a broken screen — which is
    /// exactly what it was taken for. It is a few seconds on 110 MB, once per session.
    /// </remarks>
    public sealed class PacketShapesPage : IStudioPage
    {
        /// <summary>
        /// What has been seen, walked once per session rather than once per page.
        /// </summary>
        /// <remarks>
        /// Shared because the page is rebuilt whenever the language changes, and a fresh instance
        /// would walk 110 MB of log again to arrive at the same numbers in a different tongue.
        /// </remarks>
        private static PacketObservations? _shared;

        private readonly WorldData _world;
        private PacketObservations _observations = new PacketObservations();
        private readonly Dictionary<PacketShapeKey, PacketNote> _notes
            = new Dictionary<PacketShapeKey, PacketNote>();

        private bool _loaded;
        private bool _dirty;

        public PacketShapesPage(WorldData world) => _world = world;

        public string TitleKey => "nav.packets";

        public override string ToString() => Words.T(TitleKey);

        private sealed record Row(string Opcode, string Shape, string Seen, long Count,
                                  PacketStatus Status, string Name, string Reason,
                                  PacketObservation? Observed);

        public Control Build()
        {
            if (!_loaded)
            {
                if (_shared == null)
                {
                    _shared = new PacketObservations();
                    _shared.LoadServerTable(Paths.PacketTelemetryConnectionString);

                    // Walked once, up front. See the note on the field for why this is not a button.
                    _shared.ScanTrafficLog(Paths.TrafficLog);
                }

                _observations = _shared;
                foreach (var pair in _world.PacketNotes.Rows) _notes[pair.Key] = pair.Value.Value;
                _loaded = true;
            }

            var list = new ListBox
            {
                ItemTemplate = new FuncDataTemplate<Row>((row, _) => Line(row), supportsRecycling: true),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

            var search = new TextBox { Watermark = Words.T("common.search"), Width = 200, FontSize = 12.5 };
            var onlyUnknown = new CheckBox { Content = Words.T("packets.onlyUnknown") };
            var status = new TextBlock { Foreground = Skin.TextSoftBrush, VerticalAlignment = VerticalAlignment.Center };

            var rescan = new Button { Content = Words.T("packets.scan") };
            var save = new Button { Content = Words.T("common.save"), IsEnabled = false };

            var opcodeBox = new TextBlock { FontFamily = Skin.Mono, Foreground = Skin.TextBrush, FontSize = 14 };
            var shapeBox = new SelectableTextBlock
            {
                FontFamily = Skin.Mono, FontSize = 12, Foreground = Skin.TextSoftBrush,
                TextWrapping = TextWrapping.Wrap,
            };

            var statusPick = new ComboBox
            {
                ItemsSource = Enum.GetValues<PacketStatus>().Select(Say).ToList(),
                Width = 190,
                IsEnabled = false,
            };

            var chosenName = new TextBlock { Foreground = Skin.AuthoredBrush, FontFamily = Skin.Mono };
            var notesBox = new TextBox
            {
                Watermark = Words.T("packets.notes"),
                AcceptsReturn = true,
                Height = 110,
                TextWrapping = TextWrapping.Wrap,
                IsEnabled = false,
            };

            var wholeOpcode = new CheckBox { Content = Words.T("packets.wholeOpcode"), IsEnabled = false };
            var sample = new SelectableTextBlock
            {
                FontFamily = Skin.Mono, FontSize = 12.5, Foreground = Skin.TextSoftBrush,
                TextWrapping = TextWrapping.NoWrap,
            };

            Row? editing = null;
            bool filling = false;
            string picked = "";

            var namePicker = Picker.Of(OfficialNames.All(_world.Complaints.Add),
                                       name => name.ToString(),
                                       name => name.Order,
                                       Words.T("packets.officialName"),
                                       name => { picked = name.Short; chosenName.Text = name.Short; Edited(); },
                                       300);
            namePicker.IsEnabled = false;

            List<Row> Rows()
            {
                var rows = new Dictionary<PacketShapeKey, Row>();

                foreach (var seen in _observations.Rows)
                {
                    var key = new PacketShapeKey(seen.Opcode, seen.Shape);
                    var note = Lookup(key);
                    rows[key] = new Row(seen.Opcode, seen.Shape, seen.Seen, seen.Occurrences,
                                        note?.Status ?? PacketStatus.Unknown, note?.Name ?? "",
                                        seen.Reason, seen);
                }

                // A note with nothing observed under it is not a mistake: somebody can write down
                // what a packet is before it has ever turned up here.
                foreach (var pair in _notes)
                {
                    if (rows.ContainsKey(pair.Key)) continue;
                    rows[pair.Key] = new Row(pair.Key.Opcode, pair.Key.Shape, "note", 0,
                                             pair.Value.Status, pair.Value.Name, "", null);
                }

                return rows.Values
                    .OrderByDescending(row => row.Count)
                    .ThenBy(row => row.Opcode, StringComparer.Ordinal)
                    .ToList();
            }

            void Show()
            {
                string needle = (search.Text ?? "").Trim();
                var all = Rows();
                var shown = all.Where(row =>
                        (!onlyUnknown.IsChecked!.Value || row.Status == PacketStatus.Unknown) &&
                        (needle.Length == 0 ||
                         row.Opcode.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                         row.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                         row.Shape.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                list.ItemsSource = shown;

                int known = all.Count(row => row.Status != PacketStatus.Unknown);
                var text = new StringBuilder();
                text.Append(Words.T("packets.kinds", shown.Count.ToString("N0"), all.Count.ToString("N0"),
                                    known.ToString("N0"),
                                    all.Select(row => row.Opcode).Distinct().Count().ToString("N0")));
                if (!_observations.ScannedTheLog) text.Append("   ·   ").Append(Words.T("packets.notScanned"));
                if (_dirty) text.Append("   ·   ").Append(Words.T("common.unsaved"));

                status.Text = text.ToString();
                save.IsEnabled = _dirty;
            }

            void Fill(Row? row)
            {
                editing = row;
                filling = true;

                bool has = row != null;
                statusPick.IsEnabled = has;
                namePicker.IsEnabled = has;
                notesBox.IsEnabled = has;
                wholeOpcode.IsEnabled = has;

                if (row == null)
                {
                    opcodeBox.Text = Words.T("packets.pickOne");
                    shapeBox.Text = "";
                    chosenName.Text = "";
                    notesBox.Text = "";
                    sample.Text = "";
                    picked = "";
                    filling = false;
                    return;
                }

                var note = Lookup(new PacketShapeKey(row.Opcode, row.Shape));
                opcodeBox.Text = row.Opcode + (PacketObservations.Named.Contains(row.Opcode) ? "   ·   Op.cs" : "");
                shapeBox.Text = row.Shape;
                statusPick.SelectedIndex = (int)(note?.Status ?? PacketStatus.Unknown);
                picked = note?.Name ?? "";
                chosenName.Text = picked;
                notesBox.Text = note?.Notes ?? "";
                wholeOpcode.IsChecked = note != null && note.Shape == PacketShapeKey.AnyShape;
                sample.Text = Sample(row);
                filling = false;
            }

            void Edited()
            {
                if (filling || editing == null) return;

                bool whole = wholeOpcode.IsChecked == true;
                var key = new PacketShapeKey(editing.Opcode, whole ? PacketShapeKey.AnyShape : editing.Shape);

                var chosen = statusPick.SelectedIndex >= 0
                    ? (PacketStatus)statusPick.SelectedIndex
                    : PacketStatus.Unknown;

                string notes = (notesBox.Text ?? "").Trim();

                if (chosen == PacketStatus.Unknown && picked.Length == 0 && notes.Length == 0)
                {
                    // An empty note is not a note. Writing rows that say nothing would fill the
                    // authored file with the whole protocol and make the diffs useless.
                    _notes.Remove(key);
                }
                else
                {
                    _notes[key] = new PacketNote
                    {
                        Opcode = key.Opcode,
                        Shape = key.Shape,
                        Status = chosen,
                        Name = picked,
                        Notes = notes,
                    };
                }

                _dirty = true;
                Show();
            }

            list.SelectionChanged += (_, _) => Fill(list.SelectedItem as Row);
            statusPick.SelectionChanged += (_, _) => Edited();
            notesBox.LostFocus += (_, _) => Edited();
            wholeOpcode.IsCheckedChanged += (_, _) => Edited();

            search.TextChanged += (_, _) => Show();
            onlyUnknown.IsCheckedChanged += (_, _) => Show();

            rescan.Click += (_, _) =>
            {
                rescan.IsEnabled = false;
                rescan.Content = Words.T("packets.scanning");
                try
                {
                    _observations.ScanTrafficLog(Paths.TrafficLog);
                }
                finally
                {
                    rescan.Content = Words.T("packets.scan");
                    rescan.IsEnabled = true;
                    Show();
                }
            };

            save.Click += (_, _) =>
            {
                try
                {
                    PacketShapeContent.Save(Paths.ContentFile(PacketShapeContent.AuthoredFile), _notes.Values);
                    _world.ReloadPacketNotes();
                    _dirty = false;
                    Show();
                    status.Text += "   ·   " + Words.T("common.saved");
                }
                catch (Exception ex)
                {
                    status.Text = Words.T("common.couldNotSave", ex.Message);
                }
            };

            var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var control in new Control[] { save, rescan, search, onlyUnknown, status })
            {
                control.Margin = new Thickness(0, 0, 12, 6);
                if (control is not TextBox) control.VerticalAlignment = VerticalAlignment.Center;
                top.Children.Add(control);
            }

            var editor = new StackPanel { Spacing = 6 };
            editor.Children.Add(opcodeBox);
            editor.Children.Add(shapeBox);
            editor.Children.Add(Skin.Label(Words.T("packets.status")));
            editor.Children.Add(statusPick);
            editor.Children.Add(Skin.Label(Words.T("packets.officialName")));
            editor.Children.Add(namePicker);
            editor.Children.Add(chosenName);
            editor.Children.Add(wholeOpcode);
            editor.Children.Add(Skin.Label(Words.T("packets.notes")));
            editor.Children.Add(notesBox);
            editor.Children.Add(Skin.Label(Words.T("packets.sample")));
            editor.Children.Add(new ScrollViewer
            {
                Content = sample,
                Height = 210,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            });

            var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,440") };
            Grid.SetColumn(list, 0);
            var card = Skin.Card(new ScrollViewer { Content = editor });
            Grid.SetColumn(card, 2);
            split.Children.Add(list);
            split.Children.Add(card);

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(top, Dock.Top);
            layout.Children.Add(top);
            layout.Children.Add(split);

            Fill(null);
            Show();
            return layout;
        }

        private static string Say(PacketStatus status) => status switch
        {
            PacketStatus.Named => Words.T("status.named"),
            PacketStatus.Documented => Words.T("status.documented"),
            PacketStatus.Handled => Words.T("status.handled"),
            PacketStatus.Ignored => Words.T("status.ignored"),
            _ => Words.T("status.unknown"),
        };

        private PacketNote? Lookup(PacketShapeKey key)
        {
            if (_notes.TryGetValue(key, out var exact)) return exact;
            return _notes.TryGetValue(new PacketShapeKey(key.Opcode, PacketShapeKey.AnyShape), out var any)
                ? any
                : null;
        }

        private string Sample(Row row)
        {
            if (row.Observed == null || row.Observed.SampleHex.Length == 0)
            {
                return Words.T("packets.nothingCaptured");
            }

            byte[] bytes;
            try { bytes = Convert.FromHexString(row.Observed.SampleHex); }
            catch (FormatException) { return Words.T("packets.nothingCaptured"); }

            var text = new StringBuilder();
            text.AppendLine($"{row.Count:N0} ×" +
                            (row.Reason.Length > 0 ? $"   ·   {row.Reason}" : "") +
                            (row.Observed.Direction.Length > 0 ? $"   ·   {row.Observed.Direction}" : ""));

            var declared = _world.Protocol.Message(row.Opcode);
            text.AppendLine(declared == null
                ? "not in the protocol file, so the fields below are guesses"
                : $"{declared.Name} declares {declared.Fields.Count} field(s)");
            text.AppendLine();

            foreach (var line in FrameDecoder.Decode(bytes, row.Opcode, _world.Protocol))
            {
                text.Append(new string(' ', line.Depth * 3));
                text.Append(line.Number.ToString().PadLeft(3)).Append("  ");
                text.Append((line.Name.Length > 0 ? line.Name : "-").PadRight(9)).Append("  ");
                text.Append(line.Type.PadRight(14)).Append("  ");
                text.AppendLine(line.Value);
            }

            text.AppendLine();
            text.AppendLine(FrameDecoder.Hex(bytes, 128));
            return text.ToString();
        }

        private static IBrush ByStatus(PacketStatus status) => status switch
        {
            PacketStatus.Named => Skin.MeasuredBrush,
            PacketStatus.Documented => Skin.AuthoredBrush,
            PacketStatus.Handled => Skin.DoneBrush,
            PacketStatus.Ignored => Skin.TextFaintBrush,
            _ => Skin.TextSoftBrush,
        };

        private static Control Line(Row row)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("52,66,72,96,*") };

            Cell(line, 0, row.Opcode, Skin.TextBrush);
            Cell(line, 1, row.Count > 0 ? row.Count.ToString("N0") : "—", Skin.TextFaintBrush);
            Cell(line, 2, row.Seen, Skin.TextFaintBrush);
            Cell(line, 3, Say(row.Status), ByStatus(row.Status));

            var tail = new TextBlock
            {
                Text = row.Name.Length > 0 ? row.Name : row.Shape,
                Foreground = row.Name.Length > 0 ? Skin.TextBrush : Skin.TextFaintBrush,
                FontFamily = row.Name.Length > 0 ? FontFamily.Default : Skin.Mono,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(tail, 4);
            line.Children.Add(tail);
            return line;
        }

        private static void Cell(Grid line, int column, string text, IBrush colour)
        {
            var block = Skin.Fixed(text, colour);
            Grid.SetColumn(block, column);
            line.Children.Add(block);
        }
    }
}
