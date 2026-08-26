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
using Avalonia.Threading;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol.Wire;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.Studio.Ui;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Studio.Pages
{
    /// <summary>
    /// The conversation between client and server, as it happens — and the place a packet gets its
    /// name.
    /// </summary>
    /// <remarks>
    /// This is the section that pays for itself fastest, and the naming panel is why. The loop it
    /// is built for is: play, walk through a door, see <c>jjg</c> go past, alt-tab here, pick
    /// <c>MapMovementRequest</c> off the list of 513 names the client still ships, and write down
    /// what it looked like. Before, that meant a Python script against a 110 MB log and a note in
    /// a chat window.
    ///
    /// It reads the log rather than tapping the proxy. The server already writes every frame there,
    /// so a tap would be a second copy of the same bytes plus a socket to secure, and it would only
    /// work with the server up. Reading the file works with it stopped and can look at what
    /// happened before the editor was opened — while still being live, because the file grows as
    /// you play and this follows it.
    /// </remarks>
    public sealed class TrafficPage : IStudioPage
    {
        /// <summary>How many frames are kept on screen. Beyond this the oldest fall off.</summary>
        private const int Keep = 6000;

        /// <summary>How often the file is checked while following.</summary>
        private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(600);

        private readonly WorldData _world;
        private readonly TrafficLogReader _reader = new TrafficLogReader(Paths.TrafficLog);
        private readonly List<TrafficEntry> _entries = new List<TrafficEntry>();
        private readonly Dictionary<PacketShapeKey, PacketNote> _notes
            = new Dictionary<PacketShapeKey, PacketNote>();

        private DispatcherTimer? _beat;
        private bool _following = true;
        private bool _started;
        private bool _notesLoaded;

        public TrafficPage(WorldData world) => _world = world;

        public string TitleKey => "nav.traffic";

        public override string ToString() => Words.T(TitleKey);

        public Control Build()
        {
            LoadNotes();

            var list = new ListBox
            {
                ItemTemplate = new FuncDataTemplate<TrafficEntry>((entry, _) => Line(entry),
                                                                  supportsRecycling: true),
                SelectionMode = SelectionMode.Single,
            };

            var detail = new SelectableTextBlock
            {
                FontFamily = Skin.Mono,
                FontSize = 12.5,
                Foreground = Skin.TextBrush,
                TextWrapping = TextWrapping.NoWrap,
                Text = Words.T("traffic.pickOne"),
            };

            var search = new TextBox { Watermark = Words.T("common.search"), Width = 200, FontSize = 12.5 };
            var fromClient = new CheckBox { Content = Words.T("traffic.client"), IsChecked = true };
            var fromServer = new CheckBox { Content = Words.T("traffic.server"), IsChecked = true };
            var onlyStrangers = new CheckBox { Content = Words.T("traffic.strangers"), IsChecked = false };

            var follow = new ToggleButton { Content = Words.T("traffic.following"), IsChecked = true, MinWidth = 104 };
            var status = new TextBlock { Foreground = Skin.TextSoftBrush, VerticalAlignment = VerticalAlignment.Center };

            // ─── naming ───────────────────────────────────────────────────────────
            var statusPick = new ComboBox
            {
                Width = 190,
                ItemsSource = Enum.GetValues<PacketStatus>().Select(Say).ToList(),
                IsEnabled = false,
            };

            var wholeOpcode = new CheckBox { Content = Words.T("packets.wholeOpcode"), IsEnabled = false, IsChecked = true };
            var notesBox = new TextBox
            {
                Watermark = Words.T("packets.notes"),
                AcceptsReturn = true,
                Height = 110,
                TextWrapping = TextWrapping.Wrap,
                IsEnabled = false,
            };

            var chosenName = new TextBlock { Foreground = Skin.AuthoredBrush, FontFamily = Skin.Mono };
            var saveNote = new Button { Content = Words.T("common.save"), IsEnabled = false };
            var noteStatus = new TextBlock { Foreground = Skin.TextSoftBrush, TextWrapping = TextWrapping.Wrap };

            TrafficEntry? looking = null;
            string picked = "";

            var namePicker = Picker.Of(OfficialNames.All(_world.Complaints.Add),
                                       name => name.ToString(),
                                       name => name.Order,
                                       Words.T("packets.officialName"),
                                       name => { picked = name.Short; chosenName.Text = name.Short; },
                                       320);
            namePicker.IsEnabled = false;

            void Show()
            {
                var shown = Filter(search.Text, fromClient.IsChecked == true,
                                   fromServer.IsChecked == true, onlyStrangers.IsChecked == true);

                // Newest first: what just happened is what somebody opened this to see, and a list
                // that grows downwards makes you chase the bottom of it while it moves.
                shown.Reverse();
                list.ItemsSource = shown;
                status.Text = Status(shown.Count);
            }

            void Pump()
            {
                if (!_following) return;

                var fresh = _reader.ReadNew(1500);
                if (_reader.Restarted) _entries.Clear();
                if (fresh.Count == 0 && !_reader.Restarted) return;

                _entries.AddRange(fresh);
                if (_entries.Count > Keep) _entries.RemoveRange(0, _entries.Count - Keep);
                Show();
            }

            void Editing(TrafficEntry? entry)
            {
                looking = entry;
                bool can = entry?.Frame.Found == true;

                statusPick.IsEnabled = can;
                namePicker.IsEnabled = can;
                notesBox.IsEnabled = can;
                wholeOpcode.IsEnabled = can;
                saveNote.IsEnabled = can;

                if (!can)
                {
                    chosenName.Text = "";
                    notesBox.Text = "";
                    noteStatus.Text = "";
                    return;
                }

                var note = Note(entry!.Frame.Opcode, entry.Shape);
                picked = note?.Name ?? "";
                chosenName.Text = picked;
                notesBox.Text = note?.Notes ?? "";
                statusPick.SelectedIndex = (int)(note?.Status ?? PacketStatus.Unknown);
                wholeOpcode.IsChecked = note == null || note.Shape == PacketShapeKey.AnyShape;
                noteStatus.Text = "";
            }

            list.SelectionChanged += (_, _) =>
            {
                var entry = list.SelectedItem as TrafficEntry;
                detail.Text = entry == null ? Words.T("traffic.pickOne") : Detail(entry);
                Editing(entry);
            };

            saveNote.Click += (_, _) =>
            {
                if (looking?.Frame.Found != true) return;

                var key = new PacketShapeKey(looking.Frame.Opcode,
                    wholeOpcode.IsChecked == true ? PacketShapeKey.AnyShape : looking.Shape);

                var status = statusPick.SelectedIndex >= 0
                    ? (PacketStatus)statusPick.SelectedIndex
                    : PacketStatus.Unknown;

                string notes = (notesBox.Text ?? "").Trim();

                if (status == PacketStatus.Unknown && picked.Length == 0 && notes.Length == 0)
                {
                    _notes.Remove(key);
                }
                else
                {
                    _notes[key] = new PacketNote
                    {
                        Opcode = key.Opcode,
                        Shape = key.Shape,
                        Status = status,
                        Name = picked,
                        Notes = notes,
                    };
                }

                try
                {
                    PacketShapeContent.Save(Paths.ContentFile(PacketShapeContent.AuthoredFile), _notes.Values);
                    _world.ReloadPacketNotes();
                    noteStatus.Text = Words.T("common.saved");
                    Show();
                }
                catch (Exception ex)
                {
                    noteStatus.Text = Words.T("common.couldNotSave", ex.Message);
                }
            };

            search.TextChanged += (_, _) => Show();
            fromClient.IsCheckedChanged += (_, _) => Show();
            fromServer.IsCheckedChanged += (_, _) => Show();
            onlyStrangers.IsCheckedChanged += (_, _) => Show();

            follow.IsCheckedChanged += (_, _) =>
            {
                _following = follow.IsChecked == true;
                follow.Content = _following ? Words.T("traffic.following") : Words.T("traffic.paused");
                if (_following) Pump();
                Show();
            };

            var reread = new Button { Content = Words.T("traffic.toTail") };
            reread.Click += (_, _) =>
            {
                _entries.Clear();
                _reader.SeekToTail();
                Pump();
                Show();
            };

            var more = new Button { Content = Words.T("traffic.more") };
            more.Click += (_, _) =>
            {
                // Ten times the window, which on this log is about 40,000 more frames. Bounded on
                // purpose: the file is 110 MB and reading all of it into a list box helps nobody.
                _entries.Clear();
                _reader.SeekToTail(20 * 1024 * 1024);
                Pump();
                Show();
            };

            var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var control in new Control[] { follow, reread, more, search, fromClient,
                                                    fromServer, onlyStrangers, status })
            {
                control.Margin = new Thickness(0, 0, 12, 6);
                if (control is not TextBox) control.VerticalAlignment = VerticalAlignment.Center;
                top.Children.Add(control);
            }

            // ─── the naming card ──────────────────────────────────────────────────
            var naming = new StackPanel { Spacing = 6 };
            naming.Children.Add(Skin.Heading(Words.T("traffic.name")));
            naming.Children.Add(new TextBlock
            {
                Text = Words.T("packets.officialHint"),
                Foreground = Skin.TextSoftBrush,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
            naming.Children.Add(Skin.Label(Words.T("packets.status")));
            naming.Children.Add(statusPick);
            naming.Children.Add(Skin.Label(Words.T("packets.officialName")));
            naming.Children.Add(namePicker);
            naming.Children.Add(chosenName);
            naming.Children.Add(wholeOpcode);
            naming.Children.Add(Skin.Label(Words.T("packets.notes")));
            naming.Children.Add(notesBox);

            var saveRow = new WrapPanel();
            saveNote.Margin = new Thickness(0, 8, 10, 0);
            noteStatus.Margin = new Thickness(0, 12, 0, 0);
            saveRow.Children.Add(saveNote);
            saveRow.Children.Add(noteStatus);
            naming.Children.Add(saveRow);

            var right = new Grid { RowDefinitions = new RowDefinitions("*,12,Auto") };
            var detailBox = new ScrollViewer
            {
                Content = detail,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            Grid.SetRow(detailBox, 0);
            var card = Skin.Card(naming);
            Grid.SetRow(card, 2);
            right.Children.Add(detailBox);
            right.Children.Add(card);
            right.Margin = new Thickness(14, 0, 0, 0);

            var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,460") };
            Grid.SetColumn(list, 0);
            Grid.SetColumn(right, 2);
            split.Children.Add(list);
            split.Children.Add(right);

            var layout = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(top, Dock.Top);
            layout.Children.Add(top);
            layout.Children.Add(split);

            // The timer belongs to this control, not to the page: Build runs again every time the
            // section is picked, and a timer left running behind an abandoned view is a leak.
            layout.AttachedToVisualTree += (_, _) =>
            {
                if (!_started)
                {
                    _reader.SeekToTail();
                    _started = true;
                }

                Pump();
                Show();

                _beat = new DispatcherTimer { Interval = Beat };
                _beat.Tick += (_, _) => Pump();
                _beat.Start();
            };

            layout.DetachedFromVisualTree += (_, _) =>
            {
                _beat?.Stop();
                _beat = null;
            };

            return layout;
        }

        private void LoadNotes()
        {
            if (_notesLoaded) return;

            _notes.Clear();
            foreach (var pair in _world.PacketNotes.Rows) _notes[pair.Key] = pair.Value.Value;
            _notesLoaded = true;
        }

        private static string Say(PacketStatus status) => status switch
        {
            PacketStatus.Named => Words.T("status.named"),
            PacketStatus.Documented => Words.T("status.documented"),
            PacketStatus.Handled => Words.T("status.handled"),
            PacketStatus.Ignored => Words.T("status.ignored"),
            _ => Words.T("status.unknown"),
        };

        private List<TrafficEntry> Filter(string? needle, bool client, bool server, bool strangers)
        {
            needle = (needle ?? "").Trim();
            var shown = new List<TrafficEntry>(_entries.Count);

            foreach (var entry in _entries)
            {
                if (entry.FromClient ? !client : !server) continue;
                if (strangers && Known(entry)) continue;

                if (needle.Length > 0 &&
                    entry.Opcode.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 &&
                    entry.Shape.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                shown.Add(entry);
            }

            return shown;
        }

        /// <summary>Whether anything at all is known about this packet: a constant, or a note.</summary>
        private bool Known(TrafficEntry entry)
            => PacketObservations.Named.Contains(entry.Opcode) || Note(entry.Opcode, entry.Shape) != null;

        private string Status(int shown)
        {
            if (!_reader.Exists) return $"{System.IO.Path.GetFileName(_reader.Path)} — {Words.T("common.none")}";

            var text = new StringBuilder();
            text.Append(Words.T("traffic.frames", shown.ToString("N0"), _entries.Count.ToString("N0")));
            text.Append($"   ·   {_reader.Length / (1024.0 * 1024.0):N0} MB");
            if (!_following) text.Append("   ·   ").Append(Words.T("traffic.paused"));
            if (_world.Protocol.MessageCount == 0) text.Append("   ·   ").Append(Words.T("traffic.noProtocol"));
            return text.ToString();
        }

        private string Detail(TrafficEntry entry)
        {
            var text = new StringBuilder();
            text.AppendLine($"{entry.Time:hh\\:mm\\:ss\\.fff}   {entry.Direction}   {entry.DeclaredBytes} bytes");

            if (!entry.Frame.Found)
            {
                text.AppendLine();
                text.AppendLine("No message could be found in this frame. It is either not one, or it is");
                text.AppendLine("a piece of one — the log is written from more than one place and not all");
                text.AppendLine("of them write whole frames.");
                text.AppendLine();
                text.AppendLine(FrameDecoder.Hex(entry.Raw, 512));
                return text.ToString();
            }

            var declared = _world.Protocol.Message(entry.Frame.Opcode);

            text.AppendLine($"opcode      {entry.Frame.Opcode}" +
                            (declared == null ? "   (not in the protocol file)" : $"   {declared.Fields.Count} declared fields"));
            text.AppendLine($"envelope    root field {entry.Frame.RootField}, {entry.Frame.Direction}" +
                            (entry.Frame.HadLengthPrefix ? ", length prefix stripped" : ""));
            text.AppendLine($"shape       {entry.Shape}");

            var note = Note(entry.Frame.Opcode, entry.Shape);
            if (note != null)
            {
                text.AppendLine();
                text.AppendLine($"note        {Say(note.Status)}" + (note.Name.Length > 0 ? $" · {note.Name}" : ""));
                if (note.Notes.Length > 0) text.AppendLine($"            {note.Notes}");
            }

            text.AppendLine();
            var lines = FrameDecoder.Decode(entry.Frame.Payload, entry.Frame.Opcode, _world.Protocol);
            if (lines.Count == 0)
            {
                text.AppendLine("(no body)");
            }
            else
            {
                foreach (var line in lines)
                {
                    text.Append(new string(' ', line.Depth * 3));
                    text.Append(line.Number.ToString().PadLeft(3)).Append("  ");
                    text.Append((line.Name.Length > 0 ? line.Name : "-").PadRight(9)).Append("  ");
                    text.Append(line.Type.PadRight(14)).Append("  ");
                    text.Append(line.Value);
                    if (line.Guessed) text.Append("   ← guessed, not declared");
                    text.AppendLine();
                }
            }

            text.AppendLine();
            text.AppendLine(FrameDecoder.Hex(entry.Frame.Payload, 256));
            return text.ToString();
        }

        private PacketNote? Note(string opcode, string shape)
        {
            if (_notes.TryGetValue(new PacketShapeKey(opcode, shape), out var exact)) return exact;
            return _notes.TryGetValue(new PacketShapeKey(opcode, PacketShapeKey.AnyShape), out var any) ? any : null;
        }

        private Control Line(TrafficEntry entry)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("84,24,52,50,*") };

            Cell(line, 0, entry.Time.ToString(@"hh\:mm\:ss\.fff"), Skin.TextFaintBrush);
            Cell(line, 1, entry.FromClient ? "→" : "←",
                 entry.FromClient ? Skin.MeasuredBrush : Skin.AuthoredBrush);

            var note = entry.Frame.Found ? Note(entry.Frame.Opcode, entry.Shape) : null;
            Cell(line, 2, entry.Opcode,
                 note != null ? Skin.DoneBrush
                 : (entry.Frame.Found && !PacketObservations.Named.Contains(entry.Opcode)
                        ? Skin.WrongBrush : Skin.TextBrush));

            Cell(line, 3, entry.DeclaredBytes.ToString(), Skin.TextFaintBrush);
            Cell(line, 4,
                 note is { Name.Length: > 0 }
                     ? note.Name
                     : (entry.Frame.Found ? ProtoShape.Summarise(entry.Frame.Payload, 120) : "—"),
                 note is { Name.Length: > 0 } ? Skin.DoneBrush : Skin.TextSoftBrush);
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
