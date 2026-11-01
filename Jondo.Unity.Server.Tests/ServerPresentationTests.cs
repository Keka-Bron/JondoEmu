using Jondo.Unity.Server.UI;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class ServerPresentationTests
{
    [Fact]
    public async Task Desktop_waits_for_backend_cleanup_and_keeps_startup_failure_visible_until_stopped()
    {
        var backend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task normalExit = ServerApp.WaitForExitAsync(backend.Task, () => false, shutdown.Task);
        shutdown.SetResult();
        Assert.False(normalExit.IsCompleted);
        backend.SetResult();
        await normalExit.WaitAsync(TimeSpan.FromSeconds(5));

        var failureClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task failedExit = ServerApp.WaitForExitAsync(Task.CompletedTask, () => true, failureClose.Task);
        Assert.False(failedExit.IsCompleted);
        failureClose.SetResult();
        await failedExit.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Saved_edits_are_pending_until_restart_and_failed_saves_keep_edits()
    {
        var defaults = new ServerSettings();
        var editor = new ServerSettingsEditor(defaults, defaults);
        editor.Edited.MaxActionPoints = 12;
        editor.Edited.WelcomeMessage = "  Welcome  ";
        Assert.True(editor.Dirty);
        Assert.False(editor.Pending);
        Assert.Throws<IOException>(() => editor.Save(_ => throw new IOException("read-only")));
        Assert.True(editor.Dirty);
        Assert.False(editor.Pending);
        ServerSettings? written = null;
        editor.Save(s => written = s);
        Assert.Equal("Welcome", written!.WelcomeMessage);
        Assert.False(editor.Dirty);
        Assert.True(editor.Pending);
        Assert.Equal(0, defaults.MaxActionPoints);
        written.MaxActionPoints = 30;
        Assert.Equal(12, editor.Edited.MaxActionPoints);
    }

    [Fact]
    public void Log_batches_preserve_multiline_alignment_bound_history_and_clear_cursor()
    {
        var log = new ServerLog();
        log.Append(new[] { new ConsoleLogBuffer.LogEntry(1, "[+] Ready\nsecond line\r\nthird", "12:34:56") });
        Assert.Equal(3, log.Lines.Count);
        Assert.Equal("12:34:56  ", log.Lines[0].Pieces[0].Text);
        Assert.Equal(new string(' ', 10), log.Lines[1].Pieces[0].Text);
        log.Clear();
        Assert.Equal(1, log.LastId);
        log.Append(new[] { new ConsoleLogBuffer.LogEntry(1, "old", "") });
        Assert.Empty(log.Lines);
        log.Append(Enumerable.Range(2, 4100).Select(i => new ConsoleLogBuffer.LogEntry(i, i.ToString(), "")));
        Assert.Equal(ServerLog.Capacity, log.Lines.Count);
        Assert.Equal("102", log.Lines[0].Text);
        Assert.Equal("4101", log.Lines[^1].Text);
    }

    [Theory]
    [InlineData("1579 [server>client] abc (SomeEvent) { 1: 453 } 3 B", "abc")]
    [InlineData("1579 [client>server] xyz {} 0 B", "xyz")]
    [InlineData("[+] ordinary [server>client] log", "")]
    public void Only_complete_packet_rows_offer_the_naming_dialog(string text, string opcode)
    {
        var line = Assert.Single(ServerLog.Format("", text));
        Assert.Equal(opcode, line.Opcode);
        Assert.Equal(text, string.Concat(line.Pieces.Select(p => p.Text)));
    }

    [Fact]
    public void Desktop_selection_handles_linux_displays_and_explicit_headless()
    {
        Assert.True(ServerLaunch.Headless(Array.Empty<string>(), true, ""));
        Assert.False(ServerLaunch.Headless(Array.Empty<string>(), true, ":0"));
        Assert.False(ServerLaunch.Headless(Array.Empty<string>(), false, ""));
        Assert.True(ServerLaunch.Headless(new[] { "--headless" }, false, ""));
    }

    [Fact]
    public void Restart_preserves_headless_and_quotes_framework_dependent_assembly_paths()
    {
        string assembly = Path.GetFullPath("folder with spaces/Jondo Server.dll");
        var managed = ServerLaunch.Restart("/usr/share/dotnet/dotnet", assembly, 123, true);
        Assert.Equal(new[] { assembly, "--headless", "--restart-after", "123" }, managed.ArgumentList);
        var apphost = ServerLaunch.Restart("/server/Jondo Server", assembly, 456, false);
        Assert.Equal(new[] { "--restart-after", "456" }, apphost.ArgumentList);
        Assert.False(apphost.UseShellExecute);
    }
}
