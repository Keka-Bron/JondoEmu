using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Server.UI;

/// <summary>Bounded, toolkit-neutral log rows. Packet recognition requires the complete trace shape.</summary>
internal sealed class ServerLog
{
    internal sealed record Piece(string Text, uint Color);
    internal sealed record Line(string Text, string Opcode, IReadOnlyList<Piece> Pieces);
    internal ObservableCollection<Line> Lines { get; } = new();
    internal const int Capacity = 4000;
    internal long LastId { get; private set; }
    private static readonly Regex Packet = new(
        @"^(\s*\d+) (\[(?:client>server|server>client)\]) ([a-z]{3})( \([A-Za-z0-9_]+\))?(.*?)(\s+\d+ B)\s*$",
        RegexOptions.Compiled);

    internal void Append(IEnumerable<ConsoleLogBuffer.LogEntry> entries)
    {
        foreach (var entry in entries)
        {
            // The buffer publishes entries in ID order; replaying a batch must not duplicate rows.
            if (entry.Id <= LastId) continue;
            LastId = entry.Id;
            foreach (var line in Format(entry.Time, entry.Message))
            {
                Lines.Add(line);
                if (Lines.Count > Capacity) Lines.RemoveAt(0);
            }
        }
    }

    // Wiping the display deliberately preserves the cursor and disk/backend logs.
    internal void Clear() => Lines.Clear();

    internal static IEnumerable<Line> Format(string time, string message)
    {
        string[] lines = message.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var pieces = new List<Piece>();
            if (time.Length > 0) pieces.Add(new(i == 0 ? time + "  " : new string(' ', time.Length + 2), LauncherPalette.LogTime));
            var match = Packet.Match(lines[i]);
            if (match.Success)
            {
                pieces.Add(new(match.Groups[1].Value + " ", LauncherPalette.LogTime));
                pieces.Add(new(match.Groups[2].Value + " ", match.Groups[2].Value.Contains("server>") ? LauncherPalette.LogZaap : LauncherPalette.LogServer));
                pieces.Add(new(match.Groups[3].Value, LauncherPalette.HighlightText));
                pieces.Add(new(match.Groups[4].Value, LauncherPalette.LogNormal));
                pieces.Add(new(match.Groups[5].Value, LauncherPalette.LightBrownText));
                pieces.Add(new(match.Groups[6].Value, LauncherPalette.LogTime));
            }
            else pieces.Add(new(lines[i], ColorOf(lines[i])));
            yield return new(lines[i], match.Success ? match.Groups[3].Value : "", pieces);
        }
    }

    private static uint ColorOf(string line)
    {
        if (line.Contains("[!]") || line.Contains("Error") || line.Contains("error")) return LauncherPalette.LogError;
        if (line.Contains("Rejected") || line.Contains("rejected")) return LauncherPalette.Red;
        if (line.Contains("[HAAPI]")) return LauncherPalette.LogHaapi;
        if (line.Contains("[Zaap")) return LauncherPalette.LogZaap;
        if (line.Contains("[+]")) return LauncherPalette.LogSuccess;
        if (line.Contains("[Fight]") || line.Contains("[FightHandler]")) return LauncherPalette.LogServer;
        if (line.Contains("[Control]") || line.Contains("[Commands]")) return LauncherPalette.HighlightText;
        return LauncherPalette.LogNormal;
    }
}
