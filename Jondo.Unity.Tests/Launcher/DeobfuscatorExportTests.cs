using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Jondo.Unity.Deobfuscator;
using Jondo.Unity.Deobfuscator.UI;
using Jondo.Unity.Reversing;
using Xunit;

namespace Jondo.Unity.Tests.Launcher;

public sealed class DeobfuscatorExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jondo-export-" + Guid.NewGuid().ToString("N"));
    public DeobfuscatorExportTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_waits_for_export_completion_even_when_export_fails(bool fail)
    {
        var window = new MapperWindow(new Settings(Path.Combine(_root, "preferences.cfg")));
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool workFinished = false;
        bool closed = false;
        bool closedAfterExport = false;
        bool closedBeforeWork = false;
        bool folderOpened = false;
        window.Closed += (_, _) => { closed = true; closedBeforeWork = !workFinished; };
        window.Show();
        Task exporting = window.ExportAsync(async () =>
        {
            try
            {
                await finish.Task;
                if (fail) throw new IOException("Simulated export failure");
                var mapper = new Mapper();
                mapper.Rows.Add(new Mapper.Row { Old = "old", New = "new", Name = "ExampleMessage" });
                return (mapper.Export(_root), mapper.ExportSniffer(_root));
            }
            finally { workFinished = true; }
        }, () => { folderOpened = true; return Task.FromResult(true); });
        try
        {
            window.Close();
            Assert.False(closed);
            Assert.True(window.IsVisible);
            Assert.False(exporting.IsCompleted);
            window.Close(); // Repeated close requests must still wait.
            Assert.False(closed);
        }
        finally
        {
            finish.TrySetResult();
            await exporting.WaitAsync(TimeSpan.FromSeconds(10));
            closedAfterExport = closed;
            window.Close();
        }
        Assert.True(closedAfterExport);
        Assert.False(closedBeforeWork);
        Assert.False(folderOpened);
        Assert.Equal(!fail, File.Exists(Path.Combine(_root, "mapping_.json")));
        if (!fail) Assert.Contains("ExampleMessage", File.ReadAllText(Path.Combine(_root, "mapping_.json")));
    }

    [AvaloniaTheory]
    [InlineData("success")]
    [InlineData("unavailable")]
    [InlineData("throws")]
    [InlineData("export-fails")]
    public async Task Export_opens_the_folder_only_after_both_outputs_and_preserves_success_if_opening_fails(string outcome)
    {
        var settings = new Settings(Path.Combine(_root, "preferences.cfg"));
        var texts = Texts.Get(settings.Language);
        var window = new MapperWindow(settings);
        int opens = 0;
        window.Show();
        try
        {
            var mapper = new Mapper();
            mapper.Rows.Add(new Mapper.Row { Old = "old", New = "new", Name = "ExampleMessage" });
            string table = "", sniffer = "";
            await window.ExportAsync(() =>
            {
                table = mapper.Export(_root);
                if (outcome == "export-fails") throw new IOException("Simulated second export failure");
                sniffer = mapper.ExportSniffer(_root);
                return Task.FromResult((table, sniffer));
            }, () =>
            {
                opens++;
                Assert.Contains("ExampleMessage", File.ReadAllText(table));
                Assert.Contains("ExampleMessage", File.ReadAllText(sniffer));
                if (outcome == "throws") throw new IOException("No file manager");
                return Task.FromResult(outcome == "success");
            });
            var controls = window.GetLogicalDescendants().OfType<Control>().ToArray();
            var status = controls.OfType<TextBlock>().Single(c => c.Name == "MappingStatus").Text!;
            var open = controls.OfType<Button>().Single(c => c.Name == "OpenExportFolder");
            Assert.Equal(outcome == "export-fails" ? 0 : 1, opens);
            Assert.Equal(outcome != "export-fails", open.IsVisible);
            Assert.True(open.IsEnabled);
            if (outcome == "export-fails") Assert.Contains(texts.Failed, status);
            else
            {
                Assert.Contains(string.Format(texts.ExportDoneFormat, table), status);
                Assert.Contains(string.Format(texts.ExportDoneFormat, sniffer), status);
                Assert.Equal(outcome != "success", status.Contains(texts.ExportOpenFailed));
            }
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_export_writes_do_not_publish_partial_output(bool existing)
    {
        string path = Path.Combine(_root, "mapping.json");
        if (existing) File.WriteAllText(path, "previous complete output");
        Assert.Throws<IOException>(() => Mapper.WriteExport(path, writer =>
        {
            writer.Write("partial output");
            writer.Flush();
            Assert.Equal(existing, File.Exists(path));
            if (existing) Assert.Equal("previous complete output", File.ReadAllText(path));
            throw new IOException("Simulated disk failure");
        }));
        Assert.Equal(existing, File.Exists(path));
        if (existing) Assert.Equal("previous complete output", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Successful_export_replaces_the_destination_only_after_writing_completes()
    {
        string path = Path.Combine(_root, "mapping.json");
        File.WriteAllText(path, "previous complete output");
        Mapper.WriteExport(path, writer =>
        {
            writer.Write("new complete output");
            writer.Flush();
            Assert.Equal("previous complete output", File.ReadAllText(path));
        });
        Assert.Equal("new complete output", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
}
