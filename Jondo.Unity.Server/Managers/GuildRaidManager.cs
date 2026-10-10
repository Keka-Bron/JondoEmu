using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Guild raids in progress: launching a raid of the board once everyone has accepted, the clock,
    /// and answering the criteria of the content already in the database.
    ///
    /// The board raid is stored -- it survives a restart --, but its instance IN PROGRESS is not: it
    /// lives in memory and goes down with the server (the board then counts it as finished), and
    /// whoever was inside stays on the map he was on. Storing half of a half-played raid would be
    /// worse than not storing it.
    /// </summary>
    public static class GuildRaidManager
    {
        /// <summary>The ones running, by the id of their instance.</summary>
        private static readonly ConcurrentDictionary<long, RaidInstance> _running = new();

        /// <summary>Where each one came from, to send him back there when it ends.</summary>
        private static readonly ConcurrentDictionary<long, (long MapId, int CellId)> _cameFrom = new();

        /// <summary>Each raid's clock, by instance, to be able to stop it if it ends early.</summary>
        private static readonly ConcurrentDictionary<long, CancellationTokenSource> _clocks = new();

        private static long _nextId;

        // ─── Launching and entering ────────────────────────────────────────────

        /// <summary>
        /// The running raid this character belongs to: one he is inside, or one whose board raid
        /// has him among its participants (he may not have gone in yet, or have come out).
        /// </summary>
        public static RaidInstance RunningOf(long characterId)
        {
            foreach (var raid in _running.Values)
            {
                if (!raid.Running) continue;
                if (raid.Has(characterId)) return raid;
                if (raid.Uuid.Length > 0 && GuildRaidBoard.Find(raid.Uuid)?.Has(characterId) == true) return raid;
            }
            return null;
        }

        /// <summary>Whether any raid is running: the darkness' sweep stops when none is.</summary>
        public static bool AnyRunning => _running.Values.Any(r => r.Running);

        /// <summary>The raid this character is IN, which is not the same.</summary>
        public static RaidInstance RaidOf(long characterId)
        {
            var raid = RunningOf(characterId);
            return raid != null && raid.Has(characterId) ? raid : null;
        }

        /// <summary>The running instance of a raid on the board, by its uuid.</summary>
        public static RaidInstance RunningByUuid(string uuid)
            => string.IsNullOrEmpty(uuid) ? null : _running.Values.FirstOrDefault(r => r.Running && r.Uuid == uuid);

        /// <summary>
        /// Launches a raid of the board once all its participants have accepted: the instance, its
        /// clock, and everyone connected taken to the first floor.
        /// </summary>
        public static async Task<RaidInstance> LaunchAsync(GuildRaidBoard.Raid board)
        {
            long id = Interlocked.Increment(ref _nextId);
            long captain = board.Captain?.CharacterId ?? board.BuyerId;
            var raid = new RaidInstance(id, board.RaidId, board.GuildId, captain, DateTimeOffset.UtcNow,
                                        DurationOf(board.RaidId)) { Uuid = board.Uuid };
            _running[id] = raid;
            GuildRaidBoard.MarkRunning(board);
            StartClock(raid);

            // Its monsters jump on whoever is caught in the dark (MonsterAggression).
            MonsterAggression.EnsureSweeping();

            // Its floors as the world has them: inside a raid a beaten group does not come back
            // (the guides), so each raid starts with every group standing. Only on the floors' world
            // maps: their maps without a position are fight arenas, which the base also fills with
            // groups nobody can walk to -- they would have kept floor -1 from ever being cleared.
            var floors = Raids.Of(raid.RaidId)?.Floors ?? Array.Empty<int>();
            foreach (int subArea in floors)
            foreach (long map in DatabaseManager.MapsOfSubArea(subArea))
            {
                if (IsArena(map)) MobSpawnManager.ClearMap(map);
                else MobSpawnManager.RestoreMap(map);
            }

            // The Gigalodón's Luminarium, shuffled for this raid.
            GuildRaidLuminarium.Shuffle(raid);

            // The Santuario's health, full (the guides).
            if (Raids.Of(raid.RaidId)?.Health is int health and > 0) raid.SetHealth(health);

            // The Santuario's guardians in the middle of their zones, and its colour drawn.
            if (raid.RaidId == Raids.EternalGardens)
            {
                GuildRaidGuardians.DrawColour(raid, Random.Shared);
                GuildRaidGuardians.Place(Raids.Of(raid.RaidId));
            }

            // The Gigalodón's first floor starts fully lit; it fades from here (the guides).
            if (Raids.Of(raid.RaidId)?.HasLight == true) raid.SetLight(1, Luminomachine.MostLight, raid.StartedUtc);

            foreach (var participant in board.Participants.ToList())
            {
                await EnterAsync(participant.CharacterId);
            }
            Console.WriteLine($"[Raid] {Raids.Of(raid.RaidId)?.Name} ({board.Uuid}) of guild {board.GuildId} " +
                              $"launched with {raid.Members.Count} of {board.Participants.Count} inside.");
            return raid;
        }

        /// <summary>
        /// Puts a participant into his raid and takes him to the first floor: one connected, not
        /// inside already, who has not left it, while there is room.
        /// </summary>
        public static async Task<bool> EnterAsync(long characterId)
        {
            var raid = RunningOf(characterId);
            if (raid == null || raid.Has(characterId) || raid.HasLeft(characterId)) return false;
            if (raid.Members.Count >= (GuildRaidCatalogue.Of(raid.RaidId)?.MaxPlayers ?? 0)) return false;

            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null) return false;

            raid.Add(characterId);
            await MoveAsync(session, EntryMapOf(Raids.Of(raid.RaidId)), remember: true);
            return true;
        }

        /// <summary>
        /// A participant leaves the running raid for good (its panel's "leave"): out of it, back to
        /// where he was, and the others see him gone.
        /// </summary>
        public static async Task<int> LeaveAsync(long characterId)
        {
            var raid = RunningOf(characterId);
            if (raid == null || raid.HasLeft(characterId)) return GuildRaidProtocol.LeaveNotInRaid;
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session?.State.IsInFight == true) return GuildRaidProtocol.LeaveOccupied;

            bool wasInside = raid.Has(characterId);
            raid.Leave(characterId);
            if (wasInside) await SendHomeAsync(characterId);
            await GuildRaidHandler.TellParticipantStateAsync(raid, characterId);
            return 0;
        }

        // ─── The goals and the score ───────────────────────────────────────────

        /// <summary>
        /// Moves a goal of the raid on to this value. Reaching the value its data asks for meets it,
        /// and a goal met adds its score to the raid's: the Santuario's maximum of 50,000 is exactly
        /// its eleven goals' scores added up. The panel hears of it.
        /// </summary>
        public static async Task AdvanceGoalAsync(RaidInstance raid, int goalId, int value)
        {
            var goal = GuildRaidCatalogue.GoalsOf(raid.RaidId).FirstOrDefault(g => g.Id == goalId);
            if (goal == null || !raid.Running) return;
            int target = Math.Max(1, goal.Value);
            bool wasMet = raid.GoalAt(goalId) >= target;
            raid.SetGoal(goalId, Math.Min(value, target));
            if (!wasMet && value >= target && goal.Score > 0) raid.Add(RaidInstance.ScoreVariable, goal.Score);

            // A goal that opens a floor opens it, and a lit floor starts at one band (the guides).
            if (!wasMet && value >= target && goal.UnlocksFloor > 0 && raid.Open(goal.UnlocksFloor))
            {
                var kind = Raids.Of(raid.RaidId);
                if (kind?.HasLight == true && goal.UnlocksFloor <= Luminomachine.Machines)
                    raid.SetLight(goal.UnlocksFloor, 1, DateTimeOffset.UtcNow);
            }
            await GuildRaidHandler.TellRunningStateAsync(raid);

            // The Santuario is over once its last goals are met -- its two final bosses, the goals
            // no other goal waits for (the guides: "both bosses beaten").
            if (raid.RaidId == Raids.EternalGardens && FinalGoals(raid.RaidId).All(g => raid.GoalAt(g) >= 1))
                await FinishAsync(raid, RaidInstance.Ending.Beaten);
        }

        /// <summary>A raid's last goals: those no other goal lists among what it waits for.</summary>
        public static List<int> FinalGoals(int raidId)
        {
            var goals = GuildRaidCatalogue.GoalsOf(raidId);
            var waitedFor = goals.Where(g => g.RequisiteForDisplay != null)
                                 .SelectMany(g => g.RequisiteForDisplay.Where(r => r != g.Id)).ToHashSet();
            return goals.Where(g => !waitedFor.Contains(g.Id)).Select(g => g.Id).ToList();
        }

        // ─── Goals met by fighting ────────────────────────────────────────────────

        /// <summary>
        /// The Gigalodón's goal 13, "Eliminar a todos los monstruos de la planta -1": no group left
        /// standing on its first floor (the floor its name gives). Inside a raid nothing comes back.
        /// </summary>
        public const int ClearFirstFloorGoal = 13;

        /// <summary>
        /// The Santuario's goal 9, "Vencer a todos los monstruos del pasillo" (sixty): every monster
        /// beaten in its last zone, the Castillo del santuario, where its corridor leads to the
        /// Princesa and the Reina.
        /// </summary>
        public const int CorridorGoal = 9;

        /// <summary>
        /// The goals a monster's defeat moves, by monster: those naming it, each the only goal of its
        /// raid to name it. The Gigalodón is named by fifteen goals, all of damage dealt to it, so
        /// beating it moves none of them.
        /// </summary>
        public static Dictionary<int, List<int>> KillGoals(int raidId)
        {
            // The Cangrancio's goal is met by its statues' enigma, not by beating it (GuildRaidExecrabe).
            int enigma = GuildRaidExecrabe.EnigmaGoal(raidId)?.Id ?? 0;
            var goals = GuildRaidCatalogue.GoalsOf(raidId).Where(g => g.Id != enigma).ToList();
            var named = goals.SelectMany(g => g.Monsters ?? Array.Empty<int>()).GroupBy(m => m)
                             .Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
            var byMonster = new Dictionary<int, List<int>>();
            foreach (var goal in goals)
            foreach (int monster in goal.Monsters ?? Array.Empty<int>())
            {
                if (!named.Contains(monster)) continue;
                if (!byMonster.TryGetValue(monster, out var list)) byMonster[monster] = list = new List<int>();
                list.Add(goal.Id);
            }
            return byMonster;
        }

        /// <summary>
        /// A fight won by someone inside a raid, on one of its floors: its goals move on, once per
        /// fight however many of the raid fought it. Who beat whom is the template, never the
        /// fighter, and summons do not count.
        /// </summary>
        public static async Task FightEndedAsync(Jondo.Unity.World.Fights.FightInstance fight, long characterId, bool won)
        {
            GuildRaidExecrabe.Forget(fight.FightId);
            GuildRaidGuardians.Forget(fight.FightId);
            if (!fight.Reglas.ReparteBotin) return;
            var raid = RaidOf(characterId);
            if (raid == null) return;
            int floor = Raids.Of(raid.RaidId)?.FloorOf(SubAreaOf(fight.RoleplayMapId)) ?? 0;
            if (floor == 0 || !raid.CountFight(fight.FightId)) return;

            // The Gigalodón's: its damage is the score, and with it the raid is over (the guides).
            if (fight.Reglas == Jondo.Unity.World.Fights.FightRules.GigalodonEscape)
            {
                long damage = fight.Rojo.Where(m => m.IsMonster && !m.EsInvocado && m.MonsterId == GigalodonMonster)
                                        .Sum(m => (long)Math.Max(0, m.MaxHP - m.CurrentHP));
                await DamageDealtAsync(raid, damage);

                // Over once its people are back on the map: ending it now would send them home in the
                // middle of the fight's own end.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(EndAfterTheFight);
                    try { await FinishAsync(raid, RaidInstance.Ending.Beaten); }
                    catch (Exception ex) { Console.WriteLine($"[Raid] Could not close it after the Gigalodon: {ex.Message}"); }
                });
                return;
            }

            if (!won)
            {
                int characters = fight.Azul.Concat(fight.Rojo).Count(f => !f.IsMonster && !f.EsInvocado && !f.EsIlusion);
                await LoseHealthAsync(raid, characters);
                return;
            }

            var beaten = new Dictionary<int, int>();
            foreach (var monster in fight.Rojo)
            {
                if (monster.EsInvocado || monster.MonsterId <= 0) continue;
                beaten[monster.MonsterId] = (beaten.TryGetValue(monster.MonsterId, out int n) ? n : 0) + 1;
            }
            await AdvanceByFightAsync(raid, floor, beaten, fight.DefenderLeaderId);

            var fighters = fight.Azul.Concat(fight.Rojo).Where(f => !f.IsMonster && !f.EsInvocado && !f.EsIlusion)
                                .Select(f => f.Id).ToList();
            await AwardFragmentsAsync(raid, fighters, beaten, Roll);
        }

        // ─── The key fragments ───────────────────────────────────────────────────

        /// <summary>
        /// The four fragments of the key to the diving cage, as the client's alterations name them:
        /// "Primer/Segundo/Tercer/Cuarto fragmento de llave de la jaula para gigatiburones".
        /// </summary>
        public static readonly IReadOnlyDictionary<int, int> FragmentAlterations = new Dictionary<int, int>
        {
            [1] = 885, [2] = 888, [3] = 889, [4] = 890,
        };

        /// <summary>
        /// The Krak'Haine ("Krakenfado"), whose monsters alone drop the fourth fragment (the guides).
        /// No goal names it, so the id is the client's own.
        /// </summary>
        public const int KrakHaine = 8328;

        /// <summary>How likely a beaten monster drops the first fragment, by the raid's score (the guides).</summary>
        public static double FirstFragmentChance(long score)
            => score > 10_000 ? 0.20 : score >= 7_000 ? 0.10 : score >= 5_000 ? 0.05 : 0.01;

        /// <summary>The fourth fragment's chance, per Krak'Haine beaten.</summary>
        public const double FourthFragmentChance = 0.01;

        private static readonly Random _dice = new();

        private static bool Roll(double chance)
        {
            lock (_dice) return _dice.NextDouble() < chance;
        }

        /// <summary>
        /// The key fragments a won fight gives (the guides): the second to everyone in it when the
        /// Mureine falls, the third when the Exécrabe does -- the monsters of the goals that open
        /// floors -3 and -5 --, the first by chance from any monster, the fourth by chance from the
        /// Krak'Haine. Each is an alteration of whoever gets it, until the raid ends; with the four,
        /// the goal that opens the last floor is met.
        /// </summary>
        public static async Task AwardFragmentsAsync(RaidInstance raid, IReadOnlyList<long> fighters,
                                                     IReadOnlyDictionary<int, int> beaten, Func<double, bool> roll)
        {
            if (raid.RaidId != Raids.Gigalodon || fighters.Count == 0) return;
            var mureine = OpenerOf(raid.RaidId, 3)?.Monsters ?? Array.Empty<int>();
            var execrabe = OpenerOf(raid.RaidId, 5)?.Monsters ?? Array.Empty<int>();

            if (beaten.Keys.Any(mureine.Contains)) await GiveFragmentAsync(raid, 2, fighters);
            if (beaten.Keys.Any(execrabe.Contains)) await GiveFragmentAsync(raid, 3, fighters);

            double first = FirstFragmentChance(raid.Score);
            foreach (var (monster, count) in beaten)
            {
                for (int i = 0; i < count; i++)
                {
                    if (roll(first)) await GiveFragmentAsync(raid, 1, new[] { fighters[i % fighters.Count] });
                    if (monster == KrakHaine && roll(FourthFragmentChance))
                        await GiveFragmentAsync(raid, 4, new[] { fighters[i % fighters.Count] });
                }
            }
        }

        /// <summary>A fragment found: an alteration for each who gets it; the four of them open the last floor.</summary>
        public static async Task GiveFragmentAsync(RaidInstance raid, int fragment, IEnumerable<long> to)
        {
            if (!FragmentAlterations.TryGetValue(fragment, out int alteration)) return;
            byte[] frame = ConnectionProtocol.Push(Jondo.Unity.Protocol.Op.Lzs, GuildProtocol.BuildAlteration(alteration,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), raid.EndsUtc.ToUnixTimeMilliseconds()));
            foreach (long id in to)
            {
                var session = SessionRegistry.FindByCharacter(id);
                if (session != null) await session.SendAsync(frame);
            }
            if (!raid.FoundFragment(fragment) || raid.Fragments.Count < FragmentAlterations.Count) return;

            var opener = OpenerOf(raid.RaidId, Raids.Of(raid.RaidId)?.Floors.Count ?? 0);
            if (opener != null) await AdvanceGoalAsync(raid, opener.Id, Math.Max(1, opener.Value));
        }

        // ─── The Gigalodón ───────────────────────────────────────────────────────

        /// <summary>How long after the Gigalodón's fight the raid closes: its people back on the map first.</summary>
        public static TimeSpan EndAfterTheFight { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Where the Gigalodón is fought: the outpost's twin without a position, the same room with
        /// the floor broken open over the abyss it comes out of. Read off the client's maps (drawn
        /// with tools/dibujar_mapa.py): of floor -1's two maps with the plain arena flags, this one is
        /// the outpost redrawn for a fight; the other, 239077654, is a bare test grid.
        /// </summary>
        public const long GigalodonArena = 239077652;

        /// <summary>The Gigalodón, the monster its fifteen damage goals name.</summary>
        public static int GigalodonMonster
            => GuildRaidCatalogue.GoalsOf(Raids.Gigalodon).Where(g => g.Damage > 0)
                                 .SelectMany(g => g.Monsters ?? Array.Empty<int>()).FirstOrDefault();

        /// <summary>
        /// Taking the chest and running (the chest's last answer): the Gigalodón comes out on the
        /// chest's map and the one who took it fights it, the raid's others joining by its swords.
        /// Before, the raid simply ended there. Returns whether the fight started.
        /// </summary>
        public static async Task<bool> StartGigalodonAsync(NetworkStream stream, long characterId, long mapId)
        {
            var raid = RaidOf(characterId);
            int monster = GigalodonMonster;
            if (raid == null || raid.RaidId != Raids.Gigalodon || monster <= 0 || stream == null) return false;
            var group = MobSpawnManager.SpawnNamed(mapId, monster, 1);
            if (group == null) return false;
            await FightHandler.InitiateFightFromMobCollision(stream, group, mapId, 0,
                                                              Jondo.Unity.World.Fights.FightRules.GigalodonEscape,
                                                              arenaMapId: MapManager.GetMapInfo(GigalodonArena) != null ? GigalodonArena : 0);
            return true;
        }

        /// <summary>
        /// The damage dealt to the Gigalodón meets the highest goal it reaches, and only that one
        /// scores: 55,006 damage is worth 3,000 (the 40,000 goal), not the three goals below it added
        /// up (the guides). Each damage goal is shown only once met, which is what its data says.
        /// </summary>
        public static async Task DamageDealtAsync(RaidInstance raid, long damage)
        {
            var best = GuildRaidCatalogue.GoalsOf(raid.RaidId).Where(g => g.Damage > 0 && damage >= g.Damage)
                                         .OrderByDescending(g => g.Damage).FirstOrDefault();
            if (best != null) await AdvanceGoalAsync(raid, best.Id, Math.Max(1, best.Value));
        }

        /// <summary>
        /// The raid loses health -- a fight lost costs one per character in it --; with none left it
        /// is over, with the score it had (the guides).
        /// </summary>
        public static async Task LoseHealthAsync(RaidInstance raid, int amount)
        {
            int most = Raids.Of(raid.RaidId)?.Health ?? 0;
            if (raid.Health == null || most <= 0 || amount <= 0) return;
            int left = raid.ChangeHealth(-amount, most);
            await GuildRaidHandler.TellRunningStateAsync(raid);
            if (left <= 0) await FinishAsync(raid, RaidInstance.Ending.OutOfHealth);
        }

        /// <summary>What a fight won on a raid's floor moves: the goals of the monsters beaten, and those of the floor.</summary>
        public static async Task AdvanceByFightAsync(RaidInstance raid, int floor, IReadOnlyDictionary<int, int> beaten,
                                                     long beatenGroup = 0)
        {
            var kind = Raids.Of(raid.RaidId);
            foreach (var (monster, goals) in KillGoals(raid.RaidId))
            {
                if (!beaten.TryGetValue(monster, out int count)) continue;
                foreach (int goal in goals) await AdvanceGoalAsync(raid, goal, raid.GoalAt(goal) + count);
            }

            if (raid.RaidId == Raids.EternalGardens && kind != null && floor == kind.Floors.Count)
                await AdvanceGoalAsync(raid, CorridorGoal, raid.GoalAt(CorridorGoal) + beaten.Values.Sum());

            if (raid.RaidId == Raids.Gigalodon && floor == 1 && kind != null)
            {
                var (loaded, left) = GroupsOn(kind.Floors[0], beatenGroup);
                if (loaded > 0 && left == 0) await AdvanceGoalAsync(raid, ClearFirstFloorGoal, 1);
            }
        }

        /// <summary>
        /// A subarea's monster groups: how many the world loaded on it, and how many still stand
        /// besides the one just beaten (which leaves the map only after the fight's end).
        /// </summary>
        public static (int Loaded, int Left) GroupsOn(int subArea, long besides = 0)
        {
            int loaded = 0, left = 0;
            foreach (long map in DatabaseManager.MapsOfSubArea(subArea))
            {
                if (IsArena(map)) continue;
                loaded += MobSpawnManager.LoadedGroups(map);
                left += MobSpawnManager.GetMobsForMap(map).Count(g => g.MobId != besides);
            }
            return (loaded, left);
        }

        // ─── Losing, and the floors' monsters ─────────────────────────────────────

        /// <summary>Whether a map is one of a guild raid's floors.</summary>
        public static bool IsRaidMap(long mapId) => Raids.BySubArea(SubAreaOf(mapId)) != null;

        /// <summary>
        /// Whether a raid floor's map is a fight arena and not a map of its world: one without a
        /// position on the raid's world map, [0,0] (MapManager.ResolveArenaMapId).
        /// </summary>
        public static bool IsArena(long mapId)
            => MapManager.GetMapInfo(mapId) is { } info && info.PosX == 0 && info.PosY == 0;

        /// <summary>
        /// A raid member lost a fight on one of its floors: in the Gigalodón he goes back to the
        /// top -- its entry -- and the treasures he carried are lost (the guides). Returns the map
        /// he goes to, or 0 when this is no raid's loss.
        /// </summary>
        public static async Task<long> DefeatedAsync(NetworkStream stream, long characterId, long fightMapId)
        {
            var raid = RaidOf(characterId);
            var kind = raid == null ? null : Raids.Of(raid.RaidId);
            if (kind == null || kind.FloorOf(SubAreaOf(fightMapId)) == 0 || raid.RaidId != Raids.Gigalodon) return 0;

            if (stream != null)
            {
                foreach (var (template, count) in RaidTreasures.InTheBag())
                    await Equipment.TakeAsync(stream, template, count);
            }
            return EntryMapOf(kind);
        }

        // ─── Closed floors ───────────────────────────────────────────────────────

        /// <summary>
        /// The goal that opens a floor of a raid, or null when it is open from the start or no
        /// goal names it.
        /// </summary>
        public static GuildRaidCatalogue.Goal OpenerOf(int raidId, int floor)
            => GuildRaidCatalogue.GoalsOf(raidId).FirstOrDefault(g => g.UnlocksFloor == floor);

        /// <summary>
        /// Whether a raid member may not go to this map: one of his raid's floors that is still
        /// closed -- "Eliminar a todos los monstruos de la planta -1 para acceder a la planta -2",
        /// the goals' own words. He is told which goal opens it. Every road off a map asks it, as
        /// the jail does.
        /// </summary>
        public static async Task<bool> KeepsOutAsync(NetworkStream stream, long characterId, long targetMapId)
        {
            var raid = RaidOf(characterId);
            if (raid == null) return false;
            int floor = Raids.Of(raid.RaidId)?.FloorOf(SubAreaOf(targetMapId)) ?? 0;
            if (floor <= 1 || raid.IsOpen(floor)) return false;
            var opener = OpenerOf(raid.RaidId, floor);
            if (opener == null) return false;

            if (stream != null)
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Jondo.Unity.Protocol.Op.Lqn, ConnectionProtocol.BuildNotice(
                        CommandTexts.Get("raid.floor.closed", (object)opener.Name))));
            return true;
        }

        /// <summary>The goals met, in the order of the raid's data.</summary>
        public static List<int> MetGoals(RaidInstance raid)
            => GuildRaidCatalogue.GoalsOf(raid.RaidId)
                                 .Where(g => raid.GoalAt(g.Id) >= Math.Max(1, g.Value))
                                 .Select(g => g.Id).ToList();

        /// <summary>
        /// The raid's variables as its panel shows them, by the id the client's data gives each.
        /// The Gigalodón has one, its depths' salt: the raid's shared pool, which the guides say the
        /// panel shows and the luminomachines burn.
        /// </summary>
        public static Dictionary<int, int> VariablesOf(RaidInstance raid)
        {
            var values = new Dictionary<int, int>();
            foreach (var variable in GuildRaidCatalogue.Of(raid.RaidId)?.Variables ?? Array.Empty<GuildRaidCatalogue.Variable>())
            {
                values[variable.Id] = variable.Id == Luminomachine.SaltVariable && raid.RaidId == Raids.Gigalodon
                    ? (int)raid.Get(RaidInstance.SaltVariable)
                    : 0;
            }
            return values;
        }

        // ─── The shared salt ─────────────────────────────────────────────────────

        /// <summary>The salt in the pool of the raid this character is in, or -1 when he is in none.</summary>
        public static long SaltOf(long characterId) => RaidOf(characterId)?.Get(RaidInstance.SaltVariable) ?? -1;

        /// <summary>Burns salt from the raid's pool: false, and nothing burnt, when it does not reach.</summary>
        public static bool SpendSalt(long characterId, int cost)
        {
            var raid = RaidOf(characterId);
            if (raid == null || cost < 0 || raid.Get(RaidInstance.SaltVariable) < cost) return false;
            raid.Add(RaidInstance.SaltVariable, -cost);
            return true;
        }

        /// <summary>Gives salt back to the pool, when what it paid for could not be done.</summary>
        public static void RefundSalt(long characterId, int amount) => RaidOf(characterId)?.Add(RaidInstance.SaltVariable, amount);

        /// <summary>
        /// A raid fight's salt, shared: every winner sees the same amount in his end screen -- the
        /// guides' "every character drops the same quantity" -- and that amount goes into the raid's
        /// pool once. The most any winner rolled is what the fight dropped. Returns what went in.
        /// </summary>
        public static int ShareFightSalt(long fightId, IReadOnlyDictionary<long, Dictionary<int, int>> lootByWinner)
        {
            var raid = lootByWinner.Keys.Select(RaidOf).FirstOrDefault(r => r != null);
            if (raid == null || raid.RaidId != Raids.Gigalodon) return 0;
            int salt = lootByWinner.Values.Max(l => l.TryGetValue(Luminomachine.SaltItem, out int n) ? n : 0);
            if (salt <= 0) return 0;
            foreach (var (winner, loot) in lootByWinner)
                if (RaidOf(winner) == raid) loot[Luminomachine.SaltItem] = salt;
            raid.Add(RaidInstance.SaltVariable, salt);
            return salt;
        }

        /// <summary>
        /// What of a raid member's loot goes into his bag: all but the salt, which is the raid's pool.
        /// </summary>
        public static Dictionary<int, int> ForTheBag(long characterId, Dictionary<int, int> loot)
        {
            var raid = RaidOf(characterId);
            if (raid == null || raid.RaidId != Raids.Gigalodon || !loot.ContainsKey(Luminomachine.SaltItem)) return loot;
            var bag = new Dictionary<int, int>(loot);
            bag.Remove(Luminomachine.SaltItem);
            return bag;
        }

        /// <summary>
        /// Closes the raid: because of the clock, because the captain closes it, or because it has been won.
        /// Everyone inside goes back to where he was and the score is kept.
        /// </summary>
        public static async Task FinishAsync(RaidInstance raid, RaidInstance.Ending how)
        {
            if (raid == null || !raid.Running) return;
            var now = DateTimeOffset.UtcNow;

            // Taking the chest out is the Gigalodón's goal of escaping with it in time.
            if (how == RaidInstance.Ending.Beaten && raid.RaidId == Raids.Gigalodon)
                raid.SetGoal(RaidChest.EscapeGoal, 1);
            raid.Finish(how, now);

            // And to the week's ranking, however it ended: a raid cut short by the clock with
            // twenty thousand points is worth those twenty thousand. What does not count is not
            // having played.
            long bestBefore = GuildStore.BestRaidScore(raid.GuildId, raid.RaidId, now);
            long seconds = (long)(raid.EndsUtc - raid.StartedUtc).TotalSeconds;
            if (raid.Score > 0)
            {
                GuildStore.RecordRaidScore(raid.GuildId, raid.RaidId, raid.Score, now, seconds * 1000);
            }
            var goals = MetGoals(raid);

            if (_clocks.TryRemove(raid.Id, out var clock))
            {
                try { clock.Cancel(); } catch (ObjectDisposedException) { }
                clock.Dispose();
            }
            _running.TryRemove(raid.Id, out _);
            GuildRaidLuminarium.Forget(raid);

            foreach (long member in raid.Members.ToList())
            {
                await SendHomeAsync(member);
            }

            var board = GuildRaidBoard.Find(raid.Uuid);
            if (board != null)
            {
                GuildRaidBoard.MarkFinished(board, raid.Score, now, seconds, goals);
                await GuildRaidHandler.TellFinishedAsync(board, raid, seconds, goals, raid.Score > bestBefore);
                await GuildRaidHandler.TellUpdatedAsync(board);
            }

            int puesto = GuildStore.PlaceOf(raid.GuildId, raid.RaidId, now);
            Console.WriteLine($"[Raid] {Raids.Of(raid.RaidId)?.Name} of guild {raid.GuildId} " +
                              $"finished ({how}), {raid.Score} points" +
                              (puesto > 0 ? $", place {puesto} of the week." : "."));
        }

        /// <summary>
        /// The captain ends it early (its panel's "finish"), in a raid whose data allows it -- the
        /// Santuario; the Gigalodón ends by its clock or its chest.
        /// </summary>
        public static async Task<GuildRaidProtocol.RunningResult> CloseAsync(long characterId)
        {
            var raid = RunningOf(characterId);
            if (raid == null || raid.HasLeft(characterId)) return GuildRaidProtocol.RunningResult.NotInRaid;
            if (GuildRaidCatalogue.Of(raid.RaidId)?.CanFinish != true) return GuildRaidProtocol.RunningResult.ImpossibleForThisRaid;
            if (raid.CaptainId != characterId) return GuildRaidProtocol.RunningResult.NotCaptain;
            await FinishAsync(raid, RaidInstance.Ending.Captain);
            return GuildRaidProtocol.RunningResult.Done;
        }

        /// <summary>
        /// The captain starts it over (its panel's "restart"), where the raid's data allows it: the
        /// counters and goals at nothing, the whole time again, everyone inside back at the entry.
        /// </summary>
        public static async Task<GuildRaidProtocol.RunningResult> RestartAsync(long characterId)
        {
            var raid = RunningOf(characterId);
            if (raid == null || raid.HasLeft(characterId)) return GuildRaidProtocol.RunningResult.NotInRaid;
            if (GuildRaidCatalogue.Of(raid.RaidId)?.CanRestart != true) return GuildRaidProtocol.RunningResult.ImpossibleForThisRaid;
            if (raid.CaptainId != characterId) return GuildRaidProtocol.RunningResult.NotCaptain;
            var board = GuildRaidBoard.Find(raid.Uuid);
            if (board == null) return GuildRaidProtocol.RunningResult.FailedRestart;

            if (_clocks.TryRemove(raid.Id, out var clock))
            {
                try { clock.Cancel(); } catch (ObjectDisposedException) { }
                clock.Dispose();
            }
            raid.Restart(DateTimeOffset.UtcNow, DurationOf(raid.RaidId));
            GuildRaidLuminarium.Shuffle(raid);
            StartClock(raid);

            long entry = EntryMapOf(Raids.Of(raid.RaidId));
            foreach (long member in raid.Members.ToList())
            {
                await MoveAsync(SessionRegistry.FindByCharacter(member), entry, remember: false);
            }
            await GuildRaidHandler.TellRestartedAsync(board, raid);
            return GuildRaidProtocol.RunningResult.Done;
        }

        /// <summary>
        /// Hands the running raid's captaincy to another participant (its panel, on a member): only
        /// the captain may, to someone connected who has not left.
        /// </summary>
        public static async Task<GuildRaidProtocol.RunningCaptainResult> HandCaptaincyAsync(long characterId, long targetId)
        {
            var raid = RunningOf(characterId);
            if (raid == null || raid.HasLeft(characterId)) return GuildRaidProtocol.RunningCaptainResult.NotInRaid;
            if (raid.CaptainId != characterId) return GuildRaidProtocol.RunningCaptainResult.NotCaptain;
            var board = GuildRaidBoard.Find(raid.Uuid);
            if (board == null || targetId == characterId || !board.Has(targetId))
                return GuildRaidProtocol.RunningCaptainResult.InvalidTarget;
            if (raid.HasLeft(targetId)) return GuildRaidProtocol.RunningCaptainResult.TargetHasLeft;
            if (SessionRegistry.FindByCharacter(targetId) == null) return GuildRaidProtocol.RunningCaptainResult.TargetDisconnected;

            raid.CaptainId = targetId;
            GuildRaidBoard.HandCaptaincy(board, targetId);
            await GuildRaidHandler.TellRunningCaptainAsync(board, raid);
            return GuildRaidProtocol.RunningCaptainResult.Done;
        }

        // ─── The clock ──────────────────────────────────────────────────────────

        /// <summary>How long a raid runs, from the client's data.</summary>
        internal static TimeSpan DurationOf(int raidId) => GuildRaidCatalogue.Of(raidId)?.Duration ?? TimeSpan.Zero;

        /// <summary>
        /// The raid's clock: one hour for the Gigalodón's, two for the Santuario's. When it goes
        /// off, the raid ends with whatever score it has.
        /// </summary>
        private static void StartClock(RaidInstance raid)
        {
            var clock = new CancellationTokenSource();
            _clocks[raid.Id] = clock;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DurationOf(raid.RaidId), clock.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                try
                {
                    await FinishAsync(raid, RaidInstance.Ending.TimeUp);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Raid] The clock could not close it: {ex.Message}");
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
            // A raid with a hub -- the Santuario's castle map with a portal to each zone -- starts there.
            if (GuildRaidPassages.HubOf(kind) is { } hub) return hub.MapId;
            // The Gigalodón's outpost, where the hatch from the surface comes out. It used to be the
            // lowest map of the floor by number: 239077652, the outpost's fight twin with no position,
            // no way out, and the player drawn in the world map's corner.
            long entry = GuildRaidPassages.EntryOf(kind);
            if (entry != 0) return entry;
            var maps = DatabaseManager.MapsOfSubArea(kind.Floors[0]);
            return maps.Count == 0 ? 0 : maps.Min();
        }

        /// <summary>A map's subarea, which is what the «PB» criterion asks for.</summary>
        public static int SubAreaOf(long mapId) => DatabaseManager.SubAreaOfMap(mapId);

        /// <summary>Takes somebody to a map, in their own session.</summary>
        private static async Task MoveAsync(GameSession session, long mapId, bool remember)
        {
            if (session == null || mapId <= 0) return;
            using (SessionContext.Push(session))
            {
                if (remember)
                {
                    _cameFrom[session.CharacterId] = (SessionContext.State.MapId, SessionContext.State.CellId);
                }
                if (session.Stream == null) return;
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
        internal static void Remember(RaidInstance raid) => _running[raid.Id] = raid;
    }
}
