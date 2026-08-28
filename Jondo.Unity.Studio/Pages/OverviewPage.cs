using System;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Jondo.Unity.Launcher;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.Studio.Ui;
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

        public string TitleKey => "nav.overview";

        public override string ToString() => Words.T(TitleKey);

        public Control Build()
        {
            var columns = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,16,*"),
                RowDefinitions = new RowDefinitions("Auto,*"),
            };

            var where = new StackPanel { Spacing = 8 };
            where.Children.Add(Skin.Heading(Words.T("overview.where")));
            where.Children.Add(Mono(new StringBuilder()
                .AppendLine($"root       {Paths.Root}")
                .AppendLine($"content    {Paths.ContentDir}")
                .AppendLine($"client     {Paths.ClientDir}")
                .AppendLine($"npc spawns {Paths.WorldNpcsJson}")
                .AppendLine($"map cells  {Paths.WalkableCellsJson}")
                .AppendLine($"protocol   {Paths.ProtocolProto}")
                .AppendLine($"traffic    {Paths.TrafficLog}")
                .AppendLine($"packets    {Paths.PacketTelemetryDb}")
                .AppendLine($"quests     {Paths.QuestsJson}")
                .ToString()));

            var census = _world.NpcPlacements.Census();
            var what = new StackPanel { Spacing = 8 };
            what.Children.Add(Skin.Heading(Words.T("overview.what")));
            what.Children.Add(Mono(new StringBuilder()
                .AppendLine($"maps with cell data   {_world.MapCount:N0}")
                .AppendLine($"npc placements        {_world.NpcPlacements.Count:N0}")
                .AppendLine($"   derived            {census[ContentLayer.Base]:N0}")
                .AppendLine($"   measured           {census[ContentLayer.Measured]:N0}")
                .AppendLine($"   authored           {census[ContentLayer.Authored]:N0}")
                .AppendLine($"   erased by hand     {_world.NpcPlacements.ErasedCount:N0}")
                .AppendLine($"dialogue trees        {_world.NpcDialogues.Count:N0}")
                .AppendLine($"packet notes          {_world.PacketNotes.Count:N0}")
                .AppendLine($"protocol messages     {_world.Protocol.MessageCount:N0}")
                .AppendLine($"game texts            {Texts()}")
                .AppendLine($"traffic log           {TrafficSize()}")
                .ToString()));

            var left = Skin.Card(where);
            var right = Skin.Card(what);
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 2);
            columns.Children.Add(left);
            columns.Children.Add(right);

            var panel = new StackPanel { Spacing = 14 };
            panel.Children.Add(columns);

            if (_world.Complaints.Count > 0)
            {
                var trouble = new StackPanel { Spacing = 8 };
                trouble.Children.Add(Skin.Heading(Words.T("overview.trouble")));
                trouble.Children.Add(new SelectableTextBlock
                {
                    Text = string.Join(Environment.NewLine, _world.Complaints),
                    FontFamily = Skin.Mono,
                    FontSize = 12.5,
                    Foreground = Skin.WrongBrush,
                    TextWrapping = TextWrapping.Wrap,
                });
                panel.Children.Add(Skin.Card(trouble));
            }

            panel.Children.Add(new TextBlock
            {
                Text = Words.T("overview.writes"),
                Foreground = Skin.TextSoftBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 860,
                HorizontalAlignment = HorizontalAlignment.Left,
            });

            return new ScrollViewer
            {
                Content = panel,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };
        }

        /// <summary>How many of the game's own words were found, and in which language.</summary>
        private string Texts()
            => _world.Text == null
                ? "not there — names will show as numbers"
                : $"{_world.Text.Count:N0} in {Words.TagOf(_world.Language)}";

        private static string TrafficSize()
        {
            try
            {
                var file = new FileInfo(Paths.TrafficLog);
                return file.Exists
                    ? $"{file.Length / (1024.0 * 1024.0):N1} MB"
                    : "not there - the server has not run, or it was told not to log";
            }
            catch (Exception ex)
            {
                return $"unreadable: {ex.Message}";
            }
        }

        internal static TextBlock Heading(string text) => Skin.Heading(text);

        internal static SelectableTextBlock Mono(string text) => new SelectableTextBlock
        {
            Text = text.TrimEnd(),
            FontFamily = Skin.Mono,
            FontSize = 12.5,
            Foreground = Skin.TextSoftBrush,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }
}
