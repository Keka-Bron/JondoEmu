using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Server.UI;

internal sealed class ServerSettingsActions : StackPanel
{
    internal const int CountdownWithPlayers = 60, CountdownEmpty = 3;
    private readonly ServerSettingsEditor _editor;
    private readonly Action<ServerSettings> _write;
    private readonly Action _leave;
    private readonly Func<bool> _valid;
    private readonly bool _embedded;
    private readonly TextBlock _status = ServerTheme.Text("");
    private readonly Button _save, _restart, _close;
    private Language _language;
    private bool _confirming;
    private int _restartSeconds;

    internal ServerSettingsActions(Language language, ServerSettingsEditor editor, Action<ServerSettings> write,
        Action leave, Func<bool> valid, bool embedded, string name)
    {
        _language = language;
        _editor = editor;
        _write = write;
        _leave = leave;
        _valid = valid;
        _embedded = embedded;
        Margin = new Thickness(24, 12, 24, 18);
        Spacing = 12;
        _status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        Children.Add(_status);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _close = ServerTheme.Button("", CloseClicked);
        _save = ServerTheme.Button("", SaveClicked);
        _restart = ServerTheme.Button("", RestartClicked, LauncherPalette.OnlineGreen);
        _close.Name = "Close" + name;
        _save.Name = "Save" + name;
        _restart.Name = name == "Settings" ? "RestartServer" : "Restart" + name;
        foreach (var button in new[] { _close, _save, _restart })
        {
            button.Margin = new Thickness(6, 0, 0, 6);
            buttons.Children.Add(button);
        }
        Children.Add(buttons);
        Refresh();
    }

    internal static void AttachShortcuts(Control root, Func<ServerSettingsActions?> actions) =>
        root.AddHandler(InputElement.KeyDownEvent, (_, e) => actions()?.HandleShortcut(e),
            RoutingStrategies.Bubble, handledEventsToo: true);

    private void HandleShortcut(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (!_embedded || _confirming)) { CloseClicked(); e.Handled = true; }
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { SaveClicked(); e.Handled = true; }
    }

    internal void ApplyLanguage(Language language)
    {
        _language = language;
        Refresh();
    }

    internal void Refresh()
    {
        _confirming = false;
        _save.Content = T("Save");
        _restart.Content = T("SaveRestart");
        _save.IsVisible = true;
        bool valid = _valid();
        bool dirty = _editor.Dirty;
        bool pending = _editor.Pending;
        _save.IsEnabled = dirty && valid;
        _restart.IsEnabled = (dirty || pending) && valid;
        _close.Content = T(dirty ? "Cancel" : "Close");
        _close.IsVisible = !_embedded || dirty;
        Status(dirty ? T("Unsaved") : pending ? T("Pending") : "");
    }

    private string T(string key) => ServerSettingsTexts.Get(_language, key);

    private void Status(string text, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = ServerTheme.Brush(error ? LauncherPalette.Red : LauncherPalette.HighlightText);
    }

    private bool Save()
    {
        try
        {
            _editor.Save(_write);
            return true;
        }
        catch (Exception ex) { Status(T("SaveError") + ex.Message, true); return false; }
    }

    private void SaveClicked()
    {
        if (_save.IsEnabled && _valid() && _editor.Dirty && Save()) Refresh();
    }

    private void RestartClicked()
    {
        if (!_restart.IsEnabled || !_valid()) return;
        if (ServerRestart.Counting) { Status(T("Counting"), true); return; }
        if (!_confirming)
        {
            int players = ServerRestart.PlayersInside;
            _restartSeconds = players > 0 ? CountdownWithPlayers : CountdownEmpty;
            _confirming = true;
            _restart.Content = T("ConfirmRestart");
            _close.Content = T("Back");
            _close.IsVisible = true;
            _save.IsVisible = false;
            Status(players > 0 ? string.Format(T("RestartAsk"), players, _restartSeconds) : string.Format(T("RestartAskEmpty"), _restartSeconds));
            return;
        }
        if (_editor.Dirty && !Save()) return;
        ServerRestart.Begin(_restartSeconds);
        _leave();
    }

    private void CloseClicked()
    {
        if (_confirming) Refresh();
        else _leave();
    }
}
