using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Reversing;

namespace Jondo.Unity.Deobfuscator.UI;

/// <summary>Portable mapping workshop; all analysis stays in the Reversing library.</summary>
internal sealed class MapperWindow : Window
{
    private Mapper _mapper = new();
    private readonly Settings _settings;
    private readonly TextBox _old;
    private readonly TextBox _new;
    private readonly Button _go;
    private readonly Button _ask;
    private readonly Button _export;
    private readonly Button _openExport;
    private readonly Button _model;
    private readonly List<Button> _pickers = new();
    private readonly ListBox _list = new() { Name = "MappingResults" };
    private readonly TextBlock _status = new() { Name = "MappingStatus", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, IsVisible = false, Height = 5 };
    private CancellationTokenSource? _running;
    private bool _closeAfterWork;
    private string _exportStatus = "";
    private Texts Texts => Texts.Get(_settings.Language);
    private static string Folder => Path.Combine(Paths.Root, "datos");

    public MapperWindow(Settings settings)
    {
        _settings = settings;
        DesktopTheme.Apply(this);
        Title = "Jondo Deobfuscator";
        Width = 1180; Height = 780; MinWidth = 720; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowState = WindowState.Maximized;
        _old = new TextBox { Name = "OldProtocolPath", Text = settings.OldProtocolDll };
        _new = new TextBox { Name = "NewProtocolPath", Text = settings.ClientFolder };
        _go = DesktopTheme.Button(Texts.MapRun, primary: true);
        _go.Name = "BuildMapping";
        _ask = DesktopTheme.Button(Texts.MapAsk);
        _export = DesktopTheme.Button(Texts.ExportRun);
        _openExport = DesktopTheme.Button(Texts.ExportOpen);
        _openExport.Name = "OpenExportFolder";
        _model = DesktopTheme.Button(Texts.ModelStep);
        _ask.IsVisible = _export.IsVisible = false;
        _openExport.IsVisible = false;
        _go.Click += async (_, _) => { if (_running != null) _running.Cancel(); else await MapAsync(); };
        _ask.Click += async (_, _) => { if (_running != null) _running.Cancel(); else await AskAsync(); };
        _export.Click += async (_, _) => await ExportAsync();
        _openExport.Click += async (_, _) => await ShowExportFolderAsync();
        _model.Click += async (_, _) => await new ModelDialog(_settings).ShowDialog(this);

        var top = new StackPanel { Spacing = 10 };
        top.Children.Add(PathRow(Texts.MapOld, _old));
        top.Children.Add(PathRow(Texts.MapNew, _new));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { _go, _model, _ask, _export, _openExport })
        { button.Margin = new Thickness(0, 0, 10, 6); actions.Children.Add(button); }
        top.Children.Add(actions);

        _list.FontFamily = DesktopTheme.Mono;
        _list.Background = DesktopTheme.Brush(LauncherPalette.ConsoleFill);
        _list.ItemTemplate = new FuncDataTemplate<Mapper.Row>((row, _) =>
        {
            if (row == null) return new TextBlock();
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("160,170,*"), Margin = new Thickness(6) };
            IBrush tone = row.How switch
            {
                Mapper.How.Structure => DesktopTheme.Green,
                Mapper.How.Model => DesktopTheme.Gold,
                _ => DesktopTheme.Muted,
            };
            var names = new TextBlock { Text = $"{row.Old} → {(row.New.Length > 0 ? row.New : "?")}", Foreground = tone };
            var origin = new TextBlock { Text = Word(row), Foreground = tone };
            var meaning = new TextBlock { Text = row.Name.Length > 0 ? row.Name : row.Meaning,
                TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(origin, 1); Grid.SetColumn(meaning, 2);
            line.Children.Add(names); line.Children.Add(origin); line.Children.Add(meaning);
            var border = new Border { Child = line, BorderThickness = new Thickness(row.Mine ? 3 : 0, 0, 0, 0),
                BorderBrush = DesktopTheme.Gold };
            ToolTip.SetTip(border, string.Join("\n", new[] { row.Meaning, row.Because,
                row.Candidates.Count > 0 ? string.Join(", ", row.Candidates) : "" }.Where(s => s.Length > 0)));
            return border;
        });
        var footer = new StackPanel { Spacing = 6 };
        footer.Children.Add(_progress); footer.Children.Add(_status);
        var layout = new DockPanel { Margin = new Thickness(24, 0, 24, 24) };
        var logo = new DesktopTheme.Logo();
        var topCard = DesktopTheme.Card(top);
        topCard.Margin = new Thickness(0, 0, 0, 10);
        var footerCard = DesktopTheme.Card(footer, LauncherPalette.BarFill);
        footerCard.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(logo, Dock.Top); DockPanel.SetDock(topCard, Dock.Top); DockPanel.SetDock(footerCard, Dock.Bottom);
        layout.Children.Add(logo); layout.Children.Add(topCard); layout.Children.Add(footerCard);
        layout.Children.Add(DesktopTheme.Card(_list));
        Content = DesktopTheme.Backdrop(this, layout);
        Say(Texts.MapReady);
        Closing += (_, e) =>
        {
            Keep();
            if (_running == null) return;
            e.Cancel = true;
            _closeAfterWork = true;
            _running.Cancel();
        };
    }

    private Control PathRow(string label, TextBox field)
    {
        var picker = DesktopTheme.Button(Texts.Choose);
        _pickers.Add(picker);
        picker.Click += async (_, _) =>
        {
            string? start = PickerStartDirectory(field.Text, Paths.ClientDir);
            var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = label,
                SuggestedStartLocation = start == null ? null : await StorageProvider.TryGetFolderFromPathAsync(start),
            });
            if (selected.Count > 0 && selected[0].TryGetLocalPath() is { } path) field.Text = path;
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        picker.Margin = new Thickness(10, 0, 0, 0); Grid.SetColumn(picker, 1);
        row.Children.Add(field); row.Children.Add(picker);
        var panel = new StackPanel(); panel.Children.Add(DesktopTheme.Caption(label)); panel.Children.Add(row);
        return panel;
    }

    internal static string? PickerStartDirectory(string? path, string fallback)
    {
        path = path?.Trim();
        if (Directory.Exists(path)) return Path.GetFullPath(path);
        if (File.Exists(path)) return Path.GetDirectoryName(Path.GetFullPath(path!));
        return Directory.Exists(fallback) ? Path.GetFullPath(fallback) : null;
    }

    private void Keep()
    {
        _settings.OldProtocolDll = _old.Text?.Trim() ?? "";
        _settings.ClientFolder = _new.Text?.Trim() ?? "";
        _settings.Save();
    }

    private async Task MapAsync()
    {
        Keep();
        string oldPath = _settings.OldProtocolDll, newPath = _settings.ClientFolder;
        if (!File.Exists(Mapper.ProtocolDll(oldPath)) || !File.Exists(Mapper.ProtocolDll(newPath)))
        { Say(Texts.MapMissing, DesktopTheme.Red); return; }
        await WorkAsync(async token =>
        {
            var mapper = new Mapper();
            IProgress<string> progress = new Progress<string>(line => Say(line));
            await Task.Run(() => mapper.Build(oldPath, newPath, Folder, Emulator(oldPath), progress.Report, token), token);
            _mapper = mapper;
        });
    }

    private async Task AskAsync()
    {
        var doubts = _mapper.Doubts();
        if (doubts.Count == 0) return;
        using var llm = new Llm(Path.Combine(Folder, "respuestas"), _settings.Endpoint());
        if (!llm.Ready)
        {
            Say(Texts.MapNoKey, DesktopTheme.Red);
            await new ModelDialog(_settings).ShowDialog(this);
            return;
        }
        await WorkAsync(token => _mapper.ResolveAsync(llm, doubts,
            line => Dispatcher.UIThread.Post(() => Say(line)), token));
    }

    internal async Task WorkAsync(Func<CancellationToken, Task> work, bool cancellable = true, bool summarize = true)
    {
        if (_running != null) return;
        _running = new CancellationTokenSource();
        _progress.IsVisible = true;
        if (cancellable) _go.Content = _ask.Content = Texts.Cancel;
        _go.IsEnabled = _ask.IsEnabled = cancellable;
        _model.IsEnabled = _export.IsEnabled = _old.IsEnabled = _new.IsEnabled = false;
        _openExport.IsEnabled = false;
        foreach (var picker in _pickers) picker.IsEnabled = false;
        try { await work(_running.Token); if (summarize) Say(_mapper.Tally()); }
        catch (OperationCanceledException) { Say(Texts.Cancel); }
        catch (Exception error) { Say(Texts.Failed + ": " + error.Message, DesktopTheme.Red); }
        finally
        {
            _running.Dispose(); _running = null;
            _progress.IsVisible = false;
            _go.Content = Texts.MapRun;
            _ask.Content = string.Format(Texts.MapAskFormat, _mapper.Doubts().Count);
            _ask.IsVisible = _mapper.Doubts().Count > 0;
            _export.IsVisible = _mapper.Rows.Any(row => row.New.Length > 0);
            _model.IsEnabled = _export.IsEnabled = _old.IsEnabled = _new.IsEnabled = true;
            _openExport.IsEnabled = true;
            _go.IsEnabled = _ask.IsEnabled = true;
            foreach (var picker in _pickers) picker.IsEnabled = true;
            Fill(_mapper.Rows);
            if (_closeAfterWork) Close();
        }
    }

    internal void Fill(IEnumerable<Mapper.Row> rows)
    {
        _list.ItemsSource = rows.Where(row => row.New.Length > 0 || row.How == Mapper.How.Doubt)
            .OrderByDescending(row => row.Mine).ThenBy(row => row.How == Mapper.How.Structure ? 1 : 0)
            .ThenBy(row => row.Old, StringComparer.Ordinal).ToArray();
    }

    internal Task ExportAsync(Func<Task<(string Table, string Sniffer)>>? export = null,
        Func<Task<bool>>? openFolder = null)
    {
        export ??= () => Task.Run(() => (_mapper.Export(Folder), _mapper.ExportSniffer(Folder)));
        // Export cannot be cancelled halfway through publishing its outputs. Closing waits for
        // this work just as it waits for mapping, including the failure path.
        return WorkAsync(async _ =>
        {
            var exported = await export();
            _exportStatus = string.Format(Texts.ExportDoneFormat, exported.Table) + " · " +
                string.Format(Texts.ExportDoneFormat, exported.Sniffer);
            _openExport.IsVisible = true;
            Say(_exportStatus, DesktopTheme.Green);
            if (!_closeAfterWork) await ShowExportFolderAsync(openFolder);
        }, cancellable: false, summarize: false);
    }

    private async Task ShowExportFolderAsync(Func<Task<bool>>? openFolder = null)
    {
        _openExport.IsEnabled = false;
        try
        {
            bool opened;
            if (openFolder != null) opened = await openFolder();
            else
            {
                opened = await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Folder));
            }
            Say(_exportStatus + (opened ? "" : "\n" + Texts.ExportOpenFailed),
                opened ? DesktopTheme.Green : DesktopTheme.Gold);
        }
        catch (Exception)
        {
            // A missing desktop file manager does not undo a successful export.
            Say(_exportStatus + "\n" + Texts.ExportOpenFailed);
        }
        finally { _openExport.IsEnabled = _running == null; }
    }

    private string Word(Mapper.Row row) => row.How switch
    {
        Mapper.How.Structure => Texts.MapByStructure,
        Mapper.How.Model => Texts.MapByModel,
        Mapper.How.Doubt => string.Format(Texts.MapDoubtFormat, row.Candidates.Count),
        _ => Texts.MapGone,
    };

    private IReadOnlyCollection<string> Emulator(string oldPath)
    {
        var opcodes = new HashSet<string>(StringComparer.Ordinal);
        string path = Path.Combine(Folder, $"opcodes_emulador_{Mapper.VersionOf(oldPath)}.tsv");
        if (!File.Exists(path)) return opcodes;
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] cells = line.Split('\t');
            if (cells.Length >= 2 && cells[0].Length == 3 && cells[1] != "descartado") opcodes.Add(cells[0]);
        }
        return opcodes;
    }

    private void Say(string text, IBrush? tone = null)
    { _status.Text = text; _status.Foreground = tone ?? DesktopTheme.Gold; }
}
