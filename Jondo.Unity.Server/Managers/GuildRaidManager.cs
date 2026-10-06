using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Guild raids in progress: buying them, launching them, the clock, and answering the criteria of the
    /// content already in the database.
    ///
    /// A bought raid is stored -- it survives a restart --, but one IN PROGRESS is not: it lives in memory
    /// and goes down with the server, and whoever was inside stays on the map he was on. It is the honest
    /// thing while the clock and the score have not been measured in any capture: storing half of a
    /// half-played raid would be worse than not storing it.
    ///
    /// What CANNOT be done yet, and is stated: the raid panel -- the clock, the score and the salt on screen
    /// -- needs its own messages, and no capture carries them. Meanwhile what there is is told in the chat.
    /// </summary>
    public static class GuildRaidManager
    {
        /// <summary>The ones running, one per guild at most.</summary>
        private static readonly ConcurrentDictionary<long, RaidInstance> _running = new();

        /// <summary>Where each one came from, to send him back there when it ends.</summary>
        private static readonly ConcurrentDictionary<long, (long MapId, int CellId)> _cameFrom = new();

        /// <summary>Each raid's clock, to be able to stop it if it ends early.</summary>
        private static readonly ConcurrentDictionary<long, CancellationTokenSource> _clocks = new();

        private static long _nextId;

        // ─── Comprar ────────────────────────────────────────────────────────────

        /// <summary>
        /// Buys a raid with the guild's kamas. Returns the key of why it could not be done, or null when it
        /// could.
        /// </summary>
        public static string Buy(long characterId, int raidId)
        {
            var kind = Raids.Of(raidId);
            if (kind == null) return "raid.unknown";
            var guild = GuildStore.GuildOf(characterId);
            if (guild == null) return "raid.noguild";
            if (GuildStore.OwnsRaid(guild.Id, raidId)) return "raid.owned";
            if (!GuildStore.SpendGuildKamas(guild.Id, kind.Price)) return "raid.nokamas";

            GuildStore.BuyRaid(guild.Id, raidId);
            return null;
        }

        // ─── Launching and entering ────────────────────────────────────────────

        /// <summary>The raid this character's guild is playing, or null.</summary>
        public static RaidInstance RunningOf(long characterId)
        {
            var guild = GuildStore.GuildOf(characterId);
            if (guild == null) return null;
            return _running.TryGetValue(guild.Id, out var raid) && raid.Running ? raid : null;
        }

        /// <summary>The raid this character is IN, which is not the same.</summary>
        public static RaidInstance RaidOf(long characterId)
        {
            var raid = RunningOf(characterId);
            return raid != null && raid.Has(characterId) ? raid : null;
        }

        /// <summary>
        /// Launches the raid: the instance, the captain inside, and to the first floor.
        /// </summary>
        /// <remarks>
        /// The game sheet's minimum of eight players is NOT required here. With eight clients a whole guild
        /// connected would be needed to test a line of code, and what is gained by requiring it is zero: the
        /// minimum protects the balance of a server with people on it, not the correctness of this. It is
        /// written down for the day there are people.
        /// </remarks>
        public static async Task<string> LaunchAsync(long captainId, int raidId)
        {
            var kind = Raids.Of(raidId);
            if (kind == null) return "raid.unknown";
            var guild = GuildStore.GuildOf(captainId);
            if (guild == null) return "raid.noguild";
            if (!GuildStore.OwnsRaid(guild.Id, raidId)) return "raid.notbought";
            if (_running.TryGetValue(guild.Id, out var already) && already.Running) return "raid.already";

            long id = Interlocked.Increment(ref _nextId);
            var raid = new RaidInstance(id, raidId, guild.Id, captainId, DateTimeOffset.UtcNow, kind.RunsFor);
            _running[guild.Id] = raid;

            // It is spent on launching: a bought raid is one use, not a permanent key.
            GuildStore.DropRaid(guild.Id, raidId);

            StartClock(raid, kind);
            string fallo = await EnterAsync(captainId);
            return fallo ?? null;
        }

        /// <summary>Puts someone into his guild's raid and takes him to the first floor.</summary>
        public static async Task<string> EnterAsync(long characterId)
        {
            var raid = RunningOf(characterId);
            if (raid == null) return "raid.none";
            var kind = Raids.Of(raid.RaidId);
            if (raid.Has(characterId)) return "raid.inside";
            if (raid.Members.Count >= kind.MaxPlayers) return "raid.full";

            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null) return "raid.offline";

            raid.Add(characterId);
            await MoveAsync(session, EntryMapOf(kind), remember: true);
            return null;
        }

        /// <summary>Takes someone out of the raid and sends him back to where he was.</summary>
        public static async Task<string> LeaveAsync(long characterId)
        {
            var raid = RaidOf(characterId);
            if (raid == null) return "raid.notinside";
            raid.Remove(characterId);
            await SendHomeAsync(characterId);
            return null;
        }

        /// <summary>
        /// Closes the raid: because of the clock, because the captain closes it, or because it has been won.
        /// Everyone inside goes back to where he was and the score is kept.
        /// </summary>
        public static async Task FinishAsync(RaidInstance raid, RaidInstance.Ending how)
        {
            if (raid == null || !raid.Running) return;
            var now = DateTimeOffset.UtcNow;
            raid.Finish(how, now);

            // And to the week's ranking, however it ended: a raid cut short by the clock with
            // twenty thousand points is worth those twenty thousand. What does not count is not
            // having played.
            if (raid.Score > 0)
            {
                GuildStore.RecordRaidScore(raid.GuildId, raid.RaidId, raid.Score, now);
            }

            if (_clocks.TryRemove(raid.GuildId, out var clock))
            {
                try { clock.Cancel(); } catch (ObjectDisposedException) { }
                clock.Dispose();
            }
            _running.TryRemove(raid.GuildId, out _);

            foreach (long member in raid.Members.ToList())
            {
                await SendHomeAsync(member);
            }

            int puesto = GuildStore.PlaceOf(raid.GuildId, raid.RaidId, now);
            Console.WriteLine($"[Raid] {Raids.Of(raid.RaidId)?.Name} del gremio {raid.GuildId} " +
                              $"acabada ({how}), {raid.Score} puntos" +
                              (puesto > 0 ? $", puesto {puesto} de la semana." : "."));
        }

        /// <summary>The captain closes it early, which is what his sheet lets him do.</summary>
        public static async Task<string> CloseAsync(long characterId)
        {
            var raid = RunningOf(characterId);
            if (raid == null) return "raid.none";
            if (raid.CaptainId != characterId) return "raid.notcaptain";
            await FinishAsync(raid, RaidInstance.Ending.Captain);
            return null;
        }

        // ─── The clock ──────────────────────────────────────────────────────────

        /// <summary>
        /// The raid's clock: one hour for the Gigalodón's, two for the Santuario's. When it goes
        /// off, the raid ends with whatever score it has.
        /// </summary>
        private static void StartClock(RaidInstance raid, RaidKind kind)
        {
            var clock = new CancellationTokenSource();
            _clocks[raid.GuildId] = clock;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(kind.RunsFor, clock.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                try
                {
                    await FinishAsync(raid, RaidInstance.Ending.TimeUp);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Raid] El reloj no pudo cerrarla: {ex.Message}");
                }
            });
        }

        // ─── The resolver ───────────────────────────────────────────────────────

        /// <summary>
        /// What answers the content's criteria for this character: his raid's variables and the
        /// subarea he is in. With no raid it answers nothing, which is right: outside a raid, a
        /// raid criterion is neither met nor failed, it is unknown.
        /// </summary>
        public static Criterion.Resolver ResolverFor(long characterId)
        {
            var raid = RaidOf(characterId);
            if (raid == null) return _ => Answer.Unknown;
            return raid.ResolverFor(SubAreaOf(SessionRegistry.FindByCharacter(characterId)?.State.MapId ?? 0));
        }

        /// <summary>
        /// Whether the monsters where this character is are going to jump on him, reading the
        /// criterion THE MONSTER ITSELF CARRIES in the database.
        /// </summary>
        /// <remarks>
        /// The eight of the Sima carry written
        /// <c>(PB=1131&amp;RV!7,n1_worldlight,0)|…</c>: they are immune to aggression while the light
        /// of their floor is not zero. There is no raid rule written by hand here -- the rule
        /// is in the database, like everything else --, only whoever answers its questions.
        ///
        /// The emulator does not yet have monsters that aggress, so for now this is only
        /// queried and counted; the day it has them, this is the door.
        /// </remarks>
        public static bool MonsterWouldAggress(long characterId, int monsterTemplate)
        {
            string criterion = DatabaseManager.MonsterAggressiveImmunity(monsterTemplate);
            if (string.IsNullOrWhiteSpace(criterion)) return false;

            // The criterion says when it is IMMUNE: it aggresses exactly when it is not met. And
            // what is not known does not aggress, which is the side it pays to be wrong on.
            return Criterion.Evaluate(criterion, ResolverFor(characterId)) == Answer.False;
        }

        // ─── The maps ───────────────────────────────────────────────────────────

        /// <summary>
        /// The map a raid is entered through.
        /// </summary>
        /// <remarks>
        /// It is NOT measured: no capture enters a raid, and neither the NPCs nor the interactives
        /// of those floors are in the database, so there is no door to point at. The first one
        /// of the first floor is taken, by number, which is deterministic and can be walked from
        /// there. The day it is measured, this line changes.
        /// </remarks>
        public static long EntryMapOf(RaidKind kind)
        {
            if (kind == null || kind.Floors.Count == 0) return 0;
            var maps = DatabaseManager.MapsOfSubArea(kind.Floors[0]);
            return maps.Count == 0 ? 0 : maps.Min();
        }

        /// <summary>A map's subarea, which is what the «PB» criterion asks for.</summary>
        public static int SubAreaOf(long mapId) => DatabaseManager.SubAreaOfMap(mapId);

        /// <summary>Lleva a alguien a un mapa, en su propia sesión.</summary>
        private static async Task MoveAsync(GameSession session, long mapId, bool remember)
        {
            if (session == null || mapId <= 0) return;
            using (SessionContext.Push(session))
            {
                if (remember)
                {
                    _cameFrom[session.CharacterId] = (SessionContext.State.MapId, SessionContext.State.CellId);
                }
                await TeleportHandler.ToMapAsync(session.Stream, mapId);
            }
        }

        /// <summary>Sends him back to the map he left, if it is known which one and he is still connected.</summary>
        private static async Task SendHomeAsync(long characterId)
        {
            if (!_cameFrom.TryRemove(characterId, out var where)) return;
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null) return;
            using (SessionContext.Push(session))
            {
                await TeleportHandler.ToMapAsync(session.Stream, where.MapId, where.CellId);
            }
        }

        /// <summary>For the tests: forgets everything in progress.</summary>
        internal static void Forget()
        {
            foreach (var clock in _clocks.Values)
            {
                try { clock.Cancel(); clock.Dispose(); } catch (ObjectDisposedException) { }
            }
            _clocks.Clear();
            _running.Clear();
            _cameFrom.Clear();
        }

        /// <summary>For the tests: puts in a raid already set up.</summary>
        internal static void Remember(RaidInstance raid) => _running[raid.GuildId] = raid;
    }
}
