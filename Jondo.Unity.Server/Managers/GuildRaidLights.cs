using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Sima del Gigalodón's light, drawn: each floor's maps as lit as the raid's band for it,
    /// fading a band at a time and brightening when salt goes into its luminomachine.
    /// </summary>
    /// <remarks>
    /// The floors' maps are dark in the client's data -- their "T_filtre" overlay drawn, their
    /// lamps at alpha 0, the machine's screen at 0 -- and their stagings light them (read off the
    /// client's maps, tools/extract_raid_stagings.py):
    ///
    ///   "Light_0" and "Light_4" set the darkest and the brightest band; "Light_N_UP" and
    ///   "Light_N_DOWN" the bands between, coming up or going down (the up one with its sound),
    ///   over five seconds, the machine's screen showing the band's number;
    ///   "machineON" and "machineOFF" light the machine's own lamp and halo;
    ///   "#SD_Timer_ON" blinks the lamps for ten seconds and "#SD_Timer_OFF" stops it;
    ///   "#SD_TvButton" is the machine's button.
    ///
    /// The band is the raid's (RaidInstance.LightAt, the guides' 2 minutes a band). A map load shows
    /// its floor's band at once (<see cref="OnMapLoadedAsync"/>); a sweep each second plays the
    /// change to whoever stands on the floor when the band moves.
    ///
    /// INFERRED, not measured -- no capture goes into a raid: the machine is lit while its floor has
    /// light, and the lamps blink in the ten seconds before a band fades, as the timer's ten
    /// seconds of blinking suggest.
    /// </remarks>
    public static class GuildRaidLights
    {
        /// <summary>How often the sweep looks at the bands.</summary>
        public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(1);

        /// <summary>How long before a band fades the lamps blink: the timer staging's own ten seconds.</summary>
        public static readonly TimeSpan WarningBefore = TimeSpan.FromSeconds(10);

        /// <summary>The stagings, by their names in the client's maps.</summary>
        public const string MachineOn = "machineON";
        public const string MachineOff = "machineOFF";
        public const string TimerOn = "#SD_Timer_ON";
        public const string TimerOff = "#SD_Timer_OFF";
        public const string MachineButton = "#SD_TvButton";

        /// <summary>The staging that leaves a map at a band, coming up to it or going down to it.</summary>
        public static string LightName(long mapId, int level, bool rising)
        {
            if (level <= 0) return "Light_0";
            if (level >= Luminomachine.MostLight) return "Light_" + Luminomachine.MostLight;
            string up = $"Light_{level}_UP", down = $"Light_{level}_DOWN";
            string first = rising ? up : down, second = rising ? down : up;
            return GuildRaidStagings.Has(mapId, first) ? first : second;
        }

        /// <summary>The lit floor a map belongs to for a raid, or 0.</summary>
        public static int FloorOf(RaidInstance raid, long mapId)
        {
            var kind = raid == null ? null : Raids.Of(raid.RaidId);
            if (kind == null || !kind.HasLight) return 0;
            int floor = kind.FloorOf(GuildRaidManager.SubAreaOf(mapId));
            return floor >= 1 && floor <= Luminomachine.Machines ? floor : 0;
        }

        /// <summary>A floor's band for a raid now, 0 to 4.</summary>
        public static int LevelOf(RaidInstance raid, int floor, DateTimeOffset now)
            => Math.Clamp(raid.LightAt(floor, now), 0, Luminomachine.MostLight);

        /// <summary>A map loaded by a raid member: its floor's band and its machine, at once.</summary>
        public static async Task OnMapLoadedAsync(GameSession session, RaidInstance raid, long mapId)
        {
            int floor = FloorOf(raid, mapId);
            if (floor == 0) return;
            int level = LevelOf(raid, floor, DateTimeOffset.UtcNow);
            foreach (string name in new[] { LightName(mapId, level, rising: false), level > 0 ? MachineOn : MachineOff })
            {
                int id = GuildRaidStagings.IdOf(mapId, name);
                if (id != GuildRaidStagings.None)
                    await session.SendAsync(GuildRaidStagings.StandingEvent(id));
            }
        }

        /// <summary>
        /// The staging a fight's arena is lit with: its floor's band as the fight began, coming up
        /// from the dark the arena's data draws. Null outside the Sima's lit floors.
        /// </summary>
        public static string ArenaLight(Jondo.Unity.World.Fights.FightInstance fight)
            => fight == null || fight.RaidLight < 0 ? null : LightName(fight.MapId, fight.RaidLight, rising: true);

        // ─── The sweep ──────────────────────────────────────────────────────────────────────

        /// <summary>The band each raid's floor was last shown at, and when its fading was warned of.</summary>
        private static readonly ConcurrentDictionary<(long Raid, int Floor), int> _shown = new();
        private static readonly ConcurrentDictionary<(long Raid, int Floor), byte> _warned = new();

        private static int _sweeping;

        /// <summary>Starts the sweep, once, for as long as a raid runs.</summary>
        public static void EnsureSweeping()
        {
            if (Interlocked.Exchange(ref _sweeping, 1) != 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (GuildRaidManager.AnyRunning)
                    {
                        try { await SweepAsync(DateTimeOffset.UtcNow); }
                        catch (Exception ex) { Program.LogDebug($"[Raids] Light sweep: {ex.Message}"); }
                        await Task.Delay(SweepEvery);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _sweeping, 0);
                }
            });
        }

        /// <summary>Every running raid's lit floors looked at once.</summary>
        public static async Task SweepAsync(DateTimeOffset now)
        {
            foreach (var raid in GuildRaidManager.RunningRaids)
            {
                var kind = Raids.Of(raid.RaidId);
                if (kind == null || !kind.HasLight) continue;
                for (int floor = 1; floor <= Math.Min(Luminomachine.Machines, kind.Floors.Count); floor++)
                    if (raid.IsOpen(floor)) await SweepFloorAsync(raid, floor, now);
            }
        }

        /// <summary>
        /// A floor's band looked at: moved since it was last shown, the floor's people see it move --
        /// the machine going out with the last band, or lighting again --; about to fade, its lamps
        /// blink.
        /// </summary>
        public static async Task SweepFloorAsync(RaidInstance raid, int floor, DateTimeOffset now)
        {
            var key = (raid.Id, floor);
            int level = LevelOf(raid, floor, now);
            if (!_shown.TryGetValue(key, out int last))
            {
                _shown[key] = level;
                return;
            }
            if (level != last)
            {
                _shown[key] = level;
                _warned.TryRemove(key, out _);
                bool rising = level > last;
                await TellFloorAsync(raid, floor, map => LightName(map, level, rising));
                if (rising) await TellFloorAsync(raid, floor, _ => TimerOff);
                if ((last == 0) != (level == 0)) await TellFloorAsync(raid, floor, _ => level > 0 ? MachineOn : MachineOff);
                Program.LogDebug($"[Raids] {raid.Uuid}: floor {floor} goes from band {last} to {level}.");
                return;
            }
            if (level > 0 && raid.UntilFade(floor, now) <= WarningBefore && _warned.TryAdd(key, 0))
                await TellFloorAsync(raid, floor, _ => TimerOn);
        }

        /// <summary>A luminomachine worked: its button heard on its map, and the floor's new band shown at once.</summary>
        public static async Task MachineUsedAsync(long characterId, int floor)
        {
            var raid = GuildRaidManager.RaidOf(characterId);
            var session = SessionRegistry.FindByCharacter(characterId);
            if (raid == null) return;
            if (session != null) await GuildRaidStagings.PlayAsync(raid, session.State.MapId, MachineButton, keep: false);
            await SweepFloorAsync(raid, floor, DateTimeOffset.UtcNow);
        }

        /// <summary>A staging to the raid's members standing on a floor's maps, each map giving it its own number.</summary>
        private static async Task TellFloorAsync(RaidInstance raid, int floor, Func<long, string> nameFor)
        {
            foreach (long member in raid.Members.ToList())
            {
                var session = SessionRegistry.FindByCharacter(member);
                if (session == null || session.State.IsInFight) continue;
                long map = session.State.MapId;
                if (FloorOf(raid, map) != floor) continue;
                int id = GuildRaidStagings.IdOf(map, nameFor(map));
                if (id != GuildRaidStagings.None) await session.SendAsync(ConnectionProtocol.Push(Op.Hpm, GuildRaidStagings.BuildPlay(id, GuildRaidStagings.FromStart)));
            }
        }

        /// <summary>A raid over: its floors are forgotten.</summary>
        public static void Forget(RaidInstance raid)
        {
            if (raid == null) return;
            foreach (var key in _shown.Keys.Where(k => k.Raid == raid.Id).ToList()) _shown.TryRemove(key, out _);
            foreach (var key in _warned.Keys.Where(k => k.Raid == raid.Id).ToList()) _warned.TryRemove(key, out _);
        }
    }
}
