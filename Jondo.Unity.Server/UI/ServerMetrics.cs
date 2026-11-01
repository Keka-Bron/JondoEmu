using System.Diagnostics;
using Jondo.Unity.Launcher;
using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Server.UI;

/// <summary>Collects real server/process counters without depending on a window or UI thread.</summary>
internal sealed class ServerMetrics : IDisposable
{
    internal sealed record Metric(Func<LauncherTexts, string> Label, uint Color, Func<string> Read, bool Group = false);
    internal readonly List<Metric> Items = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _previousTime;
    private TimeSpan _previousCpu;
    private long _previousBytes;
    private double _cpu, _rate;

    internal ServerMetrics()
    {
        _previousCpu = _process.TotalProcessorTime;
        _previousBytes = Jondo.Protocol.NetworkMessage.BytesFuera + Jondo.Protocol.NetworkMessage.BytesDentro;
        Group(t => t.GroupWorld);
        Add(t => t.StatPlayers, LauncherPalette.OnlineGreen, () => $"{Network.SessionRegistry.Count}/{Contract.ClientesEnTotal}");
        Add(t => t.StatInWorld, LauncherPalette.DotGreen, () => Network.SessionRegistry.All().Count(s => s.IsInWorld).ToString());
        Add(t => t.StatFights, LauncherPalette.Red, () => Handlers.FightHandler.CombatesEnCurso.ToString());
        Add(t => t.StatMaps, LauncherPalette.LogHaapi, () => Network.SessionRegistry.All().Where(s => s.IsInWorld).Select(s => s.MapId).Distinct().Count().ToString());
        Add(t => t.StatClients, LauncherPalette.LightGold, () => Network.ClientLaunchRegistry.ActiveCount.ToString());
        Group(t => t.GroupNetwork);
        Add(t => t.StatSent, LauncherPalette.LogSuccess, () => $"{Bytes(Jondo.Protocol.NetworkMessage.BytesFuera)} ({Jondo.Protocol.NetworkMessage.PaquetesFuera:N0})");
        Add(t => t.StatReceived, LauncherPalette.LogZaap, () => $"{Bytes(Jondo.Protocol.NetworkMessage.BytesDentro)} ({Jondo.Protocol.NetworkMessage.PaquetesDentro:N0})");
        Add(t => t.StatRate, LauncherPalette.LightGold, () => $"{_rate:0.0} KB/s");
        Group(t => t.GroupMachine);
        Add(t => t.StatCpu, LauncherPalette.LogHaapi, () => $"{_cpu:0.0} %");
        Add(t => t.StatMemory, LauncherPalette.SoftGold, () => Bytes(_process.WorkingSet64));
        Add(t => t.StatThreads, LauncherPalette.LogZaap, () => _process.Threads.Count.ToString());
        Add(t => t.StatUptime, LauncherPalette.OnlineGreen, () =>
        {
            var t = _clock.Elapsed;
            return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" :
                t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
        });
        Group(t => t.GroupLoaded);
        Add(t => t.StatWorldMaps, LauncherPalette.SoftGold, () => $"{Managers.MobSpawnManager.MapasConGrupos:N0}");
        Add(t => t.StatWorldGroups, LauncherPalette.MutedGold, () => $"{Managers.MobSpawnManager.TotalGrupos:N0}");
        Add(t => t.StatWorldNpcs, LauncherPalette.HighlightText, () => $"{Managers.Npcs.Count:N0}");
    }

    private void Group(Func<LauncherTexts, string> label) => Items.Add(new(label, LauncherPalette.SoftGold, () => "", true));
    private void Add(Func<LauncherTexts, string> label, uint color, Func<string> read) => Items.Add(new(label, color, read));

    internal void Sample()
    {
        _process.Refresh();
        var now = _clock.Elapsed;
        double seconds = (now - _previousTime).TotalSeconds;
        if (seconds <= 0) return;
        var cpu = _process.TotalProcessorTime;
        long bytes = Jondo.Protocol.NetworkMessage.BytesFuera + Jondo.Protocol.NetworkMessage.BytesDentro;
        _cpu = Math.Clamp((cpu - _previousCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 0, 100);
        _rate = Math.Max(0, (bytes - _previousBytes) / 1024.0 / seconds);
        _previousCpu = cpu;
        _previousBytes = bytes;
        _previousTime = now;
    }

    private static string Bytes(long value) => value >= 1024L * 1024 * 1024 ? $"{value / (1024.0 * 1024 * 1024):0.0} GB" :
        value >= 1024 * 1024 ? $"{value / (1024.0 * 1024):0} MB" : value >= 1024 ? $"{value / 1024.0:0} KB" : $"{value} B";
    public void Dispose() => _process.Dispose();
}
