namespace Jondo.Unity.Server.UI;

/// <summary>Editing never changes this run's rules. A failed save retains the unsaved state.</summary>
internal sealed class ServerSettingsEditor
{
    private ServerSettings _saved;
    private readonly ServerSettings _running;
    internal ServerSettings Edited { get; }
    internal bool Dirty => Edited.DiffersFrom(_saved);
    internal bool Pending => _saved.DiffersFrom(_running);

    internal ServerSettingsEditor(ServerSettings saved, ServerSettings running)
    {
        _saved = saved.Copy();
        _running = running.Copy();
        Edited = saved.Copy();
    }

    internal void Save(Action<ServerSettings> write)
    {
        var next = Edited.Copy();
        next.WelcomeMessage = next.WelcomeMessage.Trim();
        write(next);
        _saved = next.Copy();
        Edited.WelcomeMessage = next.WelcomeMessage;
    }
}
