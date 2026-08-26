using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Studio.Pages
{
    /// <summary>
    /// What the editor found on disk, and where.
    /// </summary>
    /// <remarks>
    /// First screen on purpose. Every time something in this project has gone wrong for an hour it
    /// has turned out to be reading a different file than anybody thought — a half-moved
    /// installation, a datos/ that had been regenerated, a client folder that moved. Saying which
    /// paths were used, out loud, costs one screen and saves that hour.
    /// </remarks>
    public sealed class OverviewPage : IStudioPage
    {
        private readonly WorldData _world;

        public OverviewPage(WorldData world) => _world = world;

        public string Title => "Overview";

        public override string ToString() => Title;

        public Control Build()
        {
            var panel = new StackPanel { Spacing = 14 };

            panel.Children.Add(Heading("Where the editor is reading from"));
            panel.Children.Add(Mono(new StringBuilder()
                .AppendLine($"root       {Paths.Root}")
                .AppendLine($"content    {Paths.ContentDir}")
                .AppendLine($"npc spawns {Paths.WorldNpcsJson}")
                .AppendLine($"map cells  {Paths.WalkableCellsJson}")
                .AppendLine($"fight      {Paths.FightCellsJson}")
                .ToString()));

            var census = _world.NpcPlacements.Census();
            panel.Children.Add(Heading("What it loaded"));
            panel.Children.Add(Mono(new StringBuilder()
                .AppendLine($"maps with cell data   {_world.MapCount:N0}")
                .AppendLine($"npc placements        {_world.NpcPlacements.Count:N0}")
                .AppendLine($"   measured           {census[ContentLayer.Measured]:N0}")
                .AppendLine($"   authored           {census[ContentLayer.Authored]:N0}")
                .AppendLine($"   erased by hand     {_world.NpcPlacements.ErasedCount:N0}")
                .ToString()));

            if (_world.Complaints.Count > 0)
            {
                panel.Children.Add(Heading("What did not load"));
                panel.Children.Add(Mono(string.Join("\n", _world.Complaints)));
            }

            panel.Children.Add(new TextBlock
            {
                Text = "Phase one is read only: nothing here writes a file.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x9A)),
                Margin = new Avalonia.Thickness(0, 10, 0, 0),
            });

            return new ScrollViewer
            {
                Content = panel,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };
        }

        internal static TextBlock Heading(string text) => new TextBlock
        {
            Text = text,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
        };

        internal static TextBlock Mono(string text) => new TextBlock
        {
            Text = text.TrimEnd(),
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD6)),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }
}
