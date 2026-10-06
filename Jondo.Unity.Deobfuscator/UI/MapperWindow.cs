using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Reversing;

namespace Jondo.Unity.Deobfuscator.UI;

/// <summary>
/// One screen: two protocols, one button, the mapping.
///
/// Before there was a nine-step wizard. It was superfluous: what is needed on patch day is
/// two paths and pressing a button. The steps were ceremony around a call to
/// <see cref="Mapper.Build"/> that takes three seconds.
///
/// What is kept from that, because it was not ceremony but honesty:
///
///   · each pair's ORIGIN goes in its row. What the structure resolved —which does not
///     get it wrong— is not the same as what a model chose among five candidates. Showing them with the same
///     look would be lying by omission.
///   · what is not resolved comes out marked, never invented.
///   · the figure that decides is «of the ones the emulator uses, how many», not the percentage over two thousand.
/// </summary>
internal sealed class MapperWindow : Form, IBackgroundWindow
{
    private readonly Mapper _mapper = new();
    private readonly Settings _settings;
    private readonly float _scale;

    private Image? _photo;
    private Bitmap? _composed;
    public Image? ComposedBackground => _composed;

    private readonly LauncherLogo _logo;
    private readonly LauncherPanel _top;
    private readonly LauncherPanel _card;
    private readonly LauncherPanel _footer;
    private readonly LauncherField _old;
    private readonly LauncherField _new;
    private readonly LauncherButton _pickOld;
    private readonly LauncherButton _pickNew;
    private readonly LauncherButton _go;
    private readonly LauncherButton _ask;
    private readonly LauncherButton _export;
    private readonly LauncherButton _model;
    private readonly ListView _list;
    private readonly Label _status;
    private readonly ProgressBar _progress;

    private List<Mapper.Row> _rows = new();
    private CancellationTokenSource? _running;

    private int E(int pixels) => (int)Math.Round(pixels * _scale);
    private Font Letter(float pixels, FontStyle style = FontStyle.Regular)
        => LauncherTheme.CreateFont(pixels, style);
    private Font Mono(float pixels) => LauncherTheme.CreateMonoFont(pixels);
    private Texts Texts => Texts.Get(_settings.Language);

    public MapperWindow(Settings settings)
    {
        _settings = settings;
        _scale = DeviceDpi / 96f;

        Text = "Jondo Desofuscador";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(E(900), E(560));
        Size = new Size(E(1180), E(780));
        WindowState = FormWindowState.Maximized;
        BackColor = LauncherTheme.Background;
        ForeColor = LauncherTheme.BaseText;
        DoubleBuffered = true;
        _photo = LauncherTheme.LoadImage("servidor_fondo.jpg") ?? LauncherTheme.LoadImage("bg.jpg");

        _logo = new LauncherLogo
        {
            Primera = "JONDO",
            Segunda = "DESOFUSCADOR",
            BackColor = Color.Transparent,
            Height = E(84),
            Dock = DockStyle.Top,
        };

        // ─── Top: the two paths and the button ──────────────────────────────────────────
        _top = new LauncherPanel
        {
            Dock = DockStyle.Top,
            Height = E(150),
            CornerRadius = E(8),
            BorderColor = LauncherTheme.BorderBrown,
            BorderWidth = 1,
        };
        _top.Layers.Add(LauncherTheme.CardFill);

        _old = Field(settings.OldProtocolDll);
        _new = Field(settings.ClientFolder);
        _pickOld = Small(Texts.Choose);
        _pickNew = Small(Texts.Choose);
        _pickOld.Click += (_, _) => Pick(_old);
        _pickNew.Click += (_, _) => Pick(_new);

        _go = Big(Texts.MapRun, LauncherTheme.GreenTop, LauncherTheme.GreenBottom, LauncherTheme.GreenBorder);
        _go.Click += async (_, _) => await GoAsync();

        _model = Small(Texts.ModelStep);
        _model.Click += (_, _) => new ModelDialog(_settings, _scale).ShowDialog(this);

        _top.Controls.AddRange(new Control[]
        {
            Caption(Texts.MapOld, E(24), E(14)), _old, _pickOld,
            Caption(Texts.MapNew, E(24), E(72)), _new, _pickNew,
            _go, _model,
        });

        // ─── The middle: the table ──────────────────────────────────────────────────────
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            VirtualMode = true,
            OwnerDraw = true,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
            BorderStyle = BorderStyle.None,
            BackColor = LauncherTheme.ConsoleBackground,
            Font = Mono(10f),
        };
        _list.Columns.Add("", E(900));
        _list.RetrieveVirtualItem += (_, e) => e.Item = new ListViewItem("");
        _list.DrawColumnHeader += (_, e) => e.DrawDefault = false;
        _list.DrawSubItem += (_, e) => e.DrawDefault = false;
        _list.DrawItem += DrawRow;
        _list.SizeChanged += (_, _) =>
        {
            if (_list.Columns.Count > 0) _list.Columns[0].Width = Math.Max(E(200), _list.ClientSize.Width - E(4));
        };

        _card = new LauncherPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = E(8),
            BorderColor = LauncherTheme.BorderBrown,
            BorderWidth = 1,
            Padding = new Padding(E(14), E(12), E(14), E(12)),
        };
        _card.Layers.Add(LauncherTheme.CardFill);
        _card.Controls.Add(_list);

        // ─── Bottom: the figure and what can be done with it ────────────────────────────
        _footer = new LauncherPanel
        {
            Dock = DockStyle.Bottom,
            Height = E(70),
            CornerRadius = E(8),
            BorderColor = LauncherTheme.BorderBrown,
            BorderWidth = 1,
        };
        _footer.Layers.Add(LauncherTheme.BarFill);

        _status = new Label
        {
            BackColor = Color.Transparent,
            ForeColor = LauncherTheme.SoftGold,
            Font = Letter(12f),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false,
        };
        _progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 26 };

        _ask = Small(Texts.MapAsk, E(300));
        _export = Small(Texts.ExportRun, E(180));
        _ask.Click += async (_, _) => await AskAsync();
        _export.Click += (_, _) => Export();
        _ask.Visible = false;
        _export.Visible = false;

        _footer.Controls.AddRange(new Control[] { _status, _progress, _ask, _export });

        Controls.Add(_card);
        Controls.Add(_footer);
        Controls.Add(_top);
        Controls.Add(_logo);

        Resize += (_, _) => { Compose(); LayOut(); };
        Compose();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LayOut();
        _status.Text = Texts.MapReady;
    }

    // ─── The button ─────────────────────────────────────────────────────────────────────

    private async Task GoAsync()
    {
        if (_running != null) { _running.Cancel(); return; }

        // It is passed what the user wrote, not the already resolved .dll: the version comes from the folder's
        // name —«Cliente 3.6.10.10»— and the assembly is always called the same.
        string oldPath = _old.Value.Trim();
        string newPath = _new.Value.Trim();

        if (!File.Exists(Mapper.ProtocolDll(oldPath)) || !File.Exists(Mapper.ProtocolDll(newPath)))
        {
            Say(Texts.MapMissing, LauncherTheme.Red);
            return;
        }

        _settings.OldProtocolDll = _old.Value.Trim();
        _settings.ClientFolder = _new.Value.Trim();
        _settings.Save();

        _running = new CancellationTokenSource();
        _progress.Visible = true;
        _go.Text = Texts.Cancel;

        try
        {
            var mine = Emulator();
            // Progress goes through IProgress: the work runs on another thread and cannot touch the
            // controls. Report() is explicit on the interface, not on the class, so it is declared
            // as IProgress and not as Progress.
            IProgress<string> report = new Progress<string>(line => Say(line, LauncherTheme.SoftGold));
            await Task.Run(() => _mapper.Build(oldPath, newPath, Folder, mine, report.Report),
                           _running.Token);

            Fill();
            _ask.Visible = _mapper.Doubts().Count > 0;
            _export.Visible = _mapper.Rows.Any(r => r.New.Length > 0);
            _ask.Text = string.Format(Texts.MapAskFormat, _mapper.Doubts().Count);
            Say(_mapper.Tally(), LauncherTheme.SoftGold);
        }
        catch (OperationCanceledException) { Say(Texts.Cancel, LauncherTheme.LightBrownText); }
        catch (Exception e) { Say(Texts.Failed + ": " + e.Message, LauncherTheme.Red); }
        finally
        {
            _running.Dispose();
            _running = null;
            _progress.Visible = false;
            _go.Text = Texts.MapRun;
            LayOut();
        }
    }

    /// <summary>The doubts, to the model. It is the only thing a key is needed for.</summary>
    private async Task AskAsync()
    {
        var doubts = _mapper.Doubts();
        if (doubts.Count == 0) return;

        using var llm = new Llm(Path.Combine(Folder, "respuestas"), _settings.Endpoint());
        if (!llm.Ready)
        {
            Say(Texts.MapNoKey, LauncherTheme.Red);
            new ModelDialog(_settings, _scale).ShowDialog(this);
            return;
        }

        _running = new CancellationTokenSource();
        _progress.Visible = true;
        _ask.Text = Texts.AskStop;

        try
        {
            await _mapper.ResolveAsync(llm, doubts, line => Say(line, LauncherTheme.SoftGold), _running.Token);
            Fill();
            Say(_mapper.Tally(), LauncherTheme.SoftGold);
        }
        catch (OperationCanceledException) { Say(Texts.Cancel, LauncherTheme.LightBrownText); }
        catch (Exception e) { Say(Texts.Failed + ": " + e.Message, LauncherTheme.Red); }
        finally
        {
            _running?.Dispose();
            _running = null;
            _progress.Visible = false;
            _ask.Text = string.Format(Texts.MapAskFormat, _mapper.Doubts().Count);
            _ask.Visible = _mapper.Doubts().Count > 0;
            LayOut();
        }
    }

    private void Export()
    {
        string table = _mapper.Export(Folder);
        string sniffer = _mapper.ExportSniffer(Folder);
        Say(string.Format(Texts.ExportDoneFormat, table) + "   ·   " +
            string.Format(Texts.ExportDoneFormat, sniffer), LauncherTheme.OnlineGreen);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Folder) { UseShellExecute = true });
    }

    // ─── The table ──────────────────────────────────────────────────────────────────────

    private void Fill()
    {
        // First what the emulator uses, and within that the doubts on top: it is what has to be looked at.
        _rows = _mapper.Rows
            .Where(r => r.New.Length > 0 || r.How == Mapper.How.Doubt)
            .OrderByDescending(r => r.Mine)
            .ThenBy(r => r.How == Mapper.How.Structure ? 1 : 0)
            .ThenBy(r => r.Old, StringComparer.Ordinal)
            .ToList();

        _list.VirtualListSize = _rows.Count;
        _list.Invalidate();
    }

    private void DrawRow(object? sender, DrawListViewItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count) return;
        var row = _rows[e.ItemIndex];
        var g = e.Graphics;

        using (var back = new SolidBrush(LauncherTheme.ConsoleBackground)) g.FillRectangle(back, e.Bounds);

        Color tone = row.How switch
        {
            Mapper.How.Structure => LauncherTheme.OnlineGreen,
            Mapper.How.Model => LauncherTheme.LightGold,
            Mapper.How.Doubt => LauncherTheme.MutedGold,
            _ => LauncherTheme.GrayText,
        };

        using var brush = new SolidBrush(tone);
        using var dim = new SolidBrush(LauncherTheme.LightBrownText);
        var font = _list.Font!;
        int y = e.Bounds.Y + E(3);
        int x = e.Bounds.X + E(8);

        string arrow = row.New.Length > 0 ? $"{row.Old}  →  {row.New}" : $"{row.Old}  →  ?";
        g.DrawString(arrow, font, brush, x, y);
        g.DrawString(Word(row), font, dim, x + E(150), y);

        string what = row.Name.Length > 0 ? row.Name : row.Meaning;
        if (what.Length > 0) g.DrawString(Cut(what, 92), font, dim, x + E(250), y);

        if (row.Mine)
        {
            using var mark = new SolidBrush(LauncherTheme.GoldBorder);
            g.FillRectangle(mark, e.Bounds.X, e.Bounds.Y + E(2), E(3), e.Bounds.Height - E(4));
        }
    }

    private string Word(Mapper.Row row) => row.How switch
    {
        Mapper.How.Structure => Texts.MapByStructure,
        Mapper.How.Model => Texts.MapByModel,
        Mapper.How.Doubt => string.Format(Texts.MapDoubtFormat, row.Candidates.Count),
        _ => Texts.MapGone,
    };

    private static string Cut(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    // ─── Plumbing ───────────────────────────────────────────────────────────────────────

    private static string Folder => Path.Combine(Paths.Root, "datos");

    /// <summary>The opcodes the emulator really uses, for the figure that matters.</summary>
    private IReadOnlyCollection<string> Emulator()
    {
        var opcodes = new HashSet<string>(StringComparer.Ordinal);
        string path = Path.Combine(Folder, $"opcodes_emulador_{Mapper.VersionOf(_old.Value)}.tsv");
        if (!File.Exists(path)) return opcodes;

        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] cells = line.Split('\t');
            if (cells.Length >= 2 && cells[0].Length == 3 && cells[1] != "descartado") opcodes.Add(cells[0]);
        }
        return opcodes;
    }

    private void Pick(LauncherField field)
    {
        using var picker = new FolderBrowserDialog
        {
            Description = Texts.MapOld,
            UseDescriptionForTitle = true,
            SelectedPath = field.Value.Length > 0 && Directory.Exists(field.Value) ? field.Value : Paths.ClientDir,
        };
        if (picker.ShowDialog(this) == DialogResult.OK) field.Value = picker.SelectedPath;
    }

    private void Say(string text, Color tone)
    {
        _status.Text = text;
        _status.ForeColor = tone;
    }

    private void LayOut()
    {
        if (_top.ClientSize.Width <= E(200)) return;

        int margin = E(24);
        int wide = _top.ClientSize.Width - margin * 2 - E(430);

        _old.SetBounds(margin, E(34), wide, E(34));
        _pickOld.SetBounds(margin + wide + E(8), E(34), E(120), E(34));
        _new.SetBounds(margin, E(92), wide, E(34));
        _pickNew.SetBounds(margin + wide + E(8), E(92), E(120), E(34));

        int right = _top.ClientSize.Width - margin;
        _go.SetBounds(right - E(270), E(34), E(270), E(48));
        _model.SetBounds(right - E(270), E(92), E(270), E(34));

        int y = (_footer.Height - E(38)) / 2;
        int edge = _footer.ClientSize.Width - margin;

        if (_export.Visible) { edge -= _export.Width; _export.SetBounds(edge, y, _export.Width, E(38)); edge -= E(10); }
        if (_ask.Visible) { edge -= _ask.Width; _ask.SetBounds(edge, y, _ask.Width, E(38)); edge -= E(10); }

        _status.SetBounds(margin, y, Math.Max(E(100), edge - margin - E(12)), E(38));
        _progress.SetBounds(margin, y + E(32), Math.Max(E(100), edge - margin - E(12)), E(5));
    }

    private void Compose()
    {
        int width = Math.Max(1, ClientSize.Width);
        int height = Math.Max(1, ClientSize.Height);
        if (_composed != null && _composed.Width == width && _composed.Height == height) return;

        _composed?.Dispose();
        _composed = new Bitmap(width, height);

        using var g = Graphics.FromImage(_composed);
        g.Clear(LauncherTheme.Background);
        if (_photo != null)
        {
            float factor = Math.Max((float)width / _photo.Width, (float)height / _photo.Height);
            int w = (int)Math.Ceiling(_photo.Width * factor);
            int h = (int)Math.Ceiling(_photo.Height * factor);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_photo, (width - w) / 2, (height - h) / 2, w, h);
        }
        using var veil = new SolidBrush(Color.FromArgb(112, 8, 4, 2));
        g.FillRectangle(veil, 0, 0, width, height);
    }

    private Label Caption(string text, int x, int y) => new()
    {
        Text = text,
        Bounds = new Rectangle(x, y, E(500), E(18)),
        BackColor = Color.Transparent,
        ForeColor = LauncherTheme.MutedGold,
        Font = Letter(10f, FontStyle.Bold),
    };

    private LauncherField Field(string value) => new()
    {
        Font = Letter(11f),
        Value = value,
    };

    private LauncherButton Small(string text, int width = 0) => new()
    {
        Text = text,
        Width = width > 0 ? width : E(120),
        Height = E(34),
        Font = Letter(10.5f, FontStyle.Bold),
        CornerRadius = E(5),
        BackgroundTop = Color.FromArgb(200, 34, 21, 12),
        BackgroundBottom = Color.FromArgb(200, 22, 13, 8),
        BackgroundTopHighlight = LauncherTheme.LightBrown,
        BackgroundBottomHighlight = Color.FromArgb(130, 75, 35),
        BorderColor = LauncherTheme.BorderBrown,
        BorderColorHighlight = LauncherTheme.GoldBorder,
        TextColor = LauncherTheme.SoftGold,
        TextColorHighlight = Color.White,
        Cursor = Cursors.Hand,
    };

    private LauncherButton Big(string text, Color top, Color bottom, Color border) => new()
    {
        Text = text,
        Font = Letter(13f, FontStyle.Bold),
        LetterSpacing = 1f,
        CornerRadius = E(6),
        BackgroundTop = top,
        BackgroundBottom = bottom,
        BackgroundTopHighlight = LauncherTheme.GreenTopHover,
        BackgroundBottomHighlight = LauncherTheme.GreenBottomHover,
        BorderColor = border,
        BorderColorHighlight = border,
        TextColor = Color.White,
        TextColorHighlight = Color.White,
        TextShadow = true,
        Cursor = Cursors.Hand,
    };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _running?.Cancel();
        _settings.OldProtocolDll = _old.Value.Trim();
        _settings.ClientFolder = _new.Value.Trim();
        _settings.Save();
        base.OnFormClosing(e);
    }
}
