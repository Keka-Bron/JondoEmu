using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Watches a fight finish and moves the member's guild missions («Encargos») that it served.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE RULES are the client's own guide (Translations 1189942): twelve missions a week common to
    /// every member; doing one gives tokens («gremichas», item 32441, up to 250 a week or 300 from
    /// guild level 13) and activity points; «un miembro solo puede realizar una vez cada encargo»;
    /// nobody has to sign up first, «basta con realizar el contenido asociado».
    /// </para>
    /// <para>
    /// What each kind asks, in the guide's words and the objective's data:
    ///   - dungeons and guild contracts (expeditions): a monster, the boss -- beat it, once;
    ///   - regulation: a family of monsters (familyMonsters) «en su territorio (quitando
    ///     mazmorras)», fifty of them, in the area or subarea when the objective names one;
    ///   - Infinite Dreams: «completar un sueño, especificando la intensidad y la franja que hay
    ///     que alcanzar» -- a band ends on its fountain, the last one with the Fin du rêve, and the
    ///     intensity is the dream's difficulty (DreamData keys both alike);
    ///   - the dream trials: «Terminar la prueba de sueños FONSOCAC», the objective's seedId the
    ///     trial's id (DreamTrials), done with the trial's Fin du rêve.
    ///   - the anomalies (an anomaly's zone, Anomalies): «Vencer a {0} monstruos en un territorio {1} o
    ///     superior con anomalía con un elixir ucrónico» -- the zone's level at least the objective's
    ///     minLevel, an uchronic elixir's state (3383 to 3385) on the member --, «Vencer a un guardián
    ///     de mazmorra con anomalía de nivel {0} o superior» the same with a dungeon's guardian, and
    ///     «Obtener {0} {1} en una anomalía» the items the fight gave him there.
    /// The anomalies' own guardians (no data says where they stand) and the live events' kinds are not counted.
    /// </para>
    /// <para>
    /// THE WIRE, read off the client: the mission changed goes as a jdc with the member's standing
    /// (<see cref="GuildProtocol.Mission"/>), the mission done as a jen, which is the client's
    /// «¡ENCARGO TERMINADO!» notice (fft.beig). The tokens arrive as the contributing capture's do:
    /// the stack (ivj), the «Has conseguido» line (lqn 21 with 32441) and the week (jex).
    /// </para>
    /// </remarks>
    public static class GuildMissionWatcher
    {
        /// <summary>The guild's tokens are an item: the contributing capture's lqn 21 names it.</summary>
        public const int TokenItem = 32441;

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        /// <summary>
        /// A fight is over for the member: what it beat goes to his guild's missions. Only a won
        /// fight counts, and only monsters -- summons are not beaten, they are made up.
        /// </summary>
        public static async Task FightEndedAsync(NetworkStream stream, FightInstance fight, bool won)
        {
            try
            {
                await CountFightAsync(stream, fight, won);
            }
            catch (Exception ex)
            {
                // The guild's missions are not worth the end of a fight.
                Console.WriteLine($"[Guild] The missions could not count fight {fight.FightId}: {ex.Message}");
            }
        }

        private static async Task CountFightAsync(NetworkStream stream, FightInstance fight, bool won)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;

            // The Fin du rêve finishes the dream's last band, won or with its minimum of waves down.
            var dream = DreamHandler.PayOf(fight, won);
            if (dream != null && dream.Finished)
            {
                await DreamBandDoneAsync(stream, dream.Difficulty, Dreams.Bands);
                if (dream.Trial > 0) await MoveAsync(stream, guild, who, objective => TrialWorth(objective, dream.Trial), DateTimeOffset.UtcNow);
            }
            if (!won) return;

            await CountAnomalyAsync(stream, guild, who, fight);

            var beaten = new Dictionary<int, int>();
            foreach (var monster in fight.Rojo)
            {
                if (monster.EsInvocado || monster.MonsterId <= 0) continue;
                beaten.TryGetValue(monster.MonsterId, out int already);
                beaten[monster.MonsterId] = already + 1;
            }
            if (beaten.Count == 0) return;

            // The map the fight was about, not the arena it was fought in (see QuestWatcher).
            long mapId = fight.RoleplayMapId != 0 ? fight.RoleplayMapId : SessionContext.State.MapId;
            await CountAsync(stream, guild, who, beaten, mapId, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// What beating these monsters is worth to one objective: one for its boss, one per monster
        /// of its family beaten outside a dungeon and inside its area or subarea, nothing otherwise.
        /// </summary>
        public static int Worth(GuildMissions.Objective objective, IReadOnlyDictionary<int, int> beaten,
                                bool inDungeon, int subArea, int area)
        {
            if (objective == null) return 0;
            if (objective.MonsterId > 0) return beaten.ContainsKey(objective.MonsterId) ? 1 : 0;
            if (objective.FamilyId > 0)
            {
                if (inDungeon) return 0;
                if (objective.SubareaId > 0 && objective.SubareaId != subArea) return 0;
                if (objective.AreaId > 0 && objective.AreaId != area) return 0;
                return objective.FamilyMonsters.Distinct().Sum(m => beaten.TryGetValue(m, out int n) ? n : 0);
            }
            return 0;
        }

        /// <summary>
        /// The member's missions moved by what he beat on a map: each one told as it now stands, and
        /// the ones this did rewarded.
        /// </summary>
        public static Task CountAsync(NetworkStream stream, GuildStore.Guild guild, long who,
                                      IReadOnlyDictionary<int, int> beaten, long mapId, DateTimeOffset now)
        {
            bool inDungeon = DungeonManager.IsRoom(mapId);
            int subArea = DatabaseManager.SubAreaOfMap(mapId);
            int area = subArea > 0 ? Interactives.AreaOfSubArea(subArea) : 0;
            return MoveAsync(stream, guild, who, objective => Worth(objective, beaten, inDungeon, subArea, area), now);
        }

        /// <summary>
        /// A band of a dream finished at a difficulty: the dream missions that asked for that band
        /// at that intensity are done. Not the trials' (a seed): see <see cref="TrialWorth"/>.
        /// </summary>
        public static async Task DreamBandDoneAsync(NetworkStream stream, int difficulty, int band)
        {
            try
            {
                long who = SessionContext.State.CharacterId;
                var guild = who == 0 ? null : GuildStore.GuildOf(who);
                if (guild == null) return;
                await MoveAsync(stream, guild, who, objective => DreamWorth(objective, difficulty, band), DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Guild] The missions could not count band {band} of a dream: {ex.Message}");
            }
        }

        /// <summary>The anomalies' super category.</summary>
        public const int AnomaliesSuperCategory = 3;

        /// <summary>An uchronic elixir's states: «mineur», «amélioré», «majeur» (their spells 23438 to 23440).</summary>
        public static readonly int[] ElixirStates = { 3383, 3384, 3385 };

        /// <summary>
        /// What a won fight in an anomaly's zone is worth to one anomaly objective: see the class
        /// remarks. <paramref name="loot"/> is what the fight gave the member.
        /// </summary>
        public static int AnomalyWorth(GuildMissions.Objective objective, int zoneLevel, bool elixir, int monsters,
                                       bool dungeonGuardian, IReadOnlyDictionary<int, int> loot)
        {
            if (objective == null || objective.SuperCategoryId != AnomaliesSuperCategory) return 0;
            if (objective.ObjectId > 0) return loot.TryGetValue(objective.ObjectId, out int got) ? got : 0;
            if (objective.MinLevel <= 0 || !elixir || zoneLevel < objective.MinLevel) return 0;
            if (objective.Quantity > 0) return monsters;
            return dungeonGuardian ? 1 : 0;
        }

        private static async Task CountAnomalyAsync(NetworkStream stream, GuildStore.Guild guild, long who, FightInstance fight)
        {
            long mapId = fight.RoleplayMapId != 0 ? fight.RoleplayMapId : SessionContext.State.MapId;
            if (!Anomalies.TryGet(DatabaseManager.SubAreaOfMap(mapId), out var anomaly)) return;

            var me = fight.Buscar(who);
            bool elixir = me != null && ElixirStates.Any(me.Buffs.TieneEstado);
            var monsters = fight.Rojo.Where(m => !m.EsInvocado && m.MonsterId > 0).ToList();
            bool guardian = monsters.Any(m => DungeonManager.IsBoss(m.MonsterId));
            var loot = FightHandler.RewardOf(fight, who)?.Loot ?? new Dictionary<int, int>();
            await MoveAsync(stream, guild, who,
                objective => AnomalyWorth(objective, anomaly.Level, elixir, monsters.Count, guardian, loot),
                DateTimeOffset.UtcNow);
        }

        /// <summary>One for the objective of this trial (its seedId), nothing for any other.</summary>
        public static int TrialWorth(GuildMissions.Objective objective, int trial)
            => objective != null && trial > 0 && objective.SeedId == trial ? 1 : 0;

        /// <summary>One for a dream objective of this intensity and band, nothing for any other.</summary>
        public static int DreamWorth(GuildMissions.Objective objective, int difficulty, int band)
            => objective != null && objective.MonsterId == 0 && objective.FamilyId == 0 && objective.SeedId == 0 &&
               objective.Intensity > 0 && objective.Intensity == difficulty && objective.Stage == band ? 1 : 0;

        /// <summary>
        /// The member's missions moved by what each objective says something was worth: each one
        /// told as it now stands, and the ones this did rewarded.
        /// </summary>
        private static async Task MoveAsync(NetworkStream stream, GuildStore.Guild guild, long who,
                                            Func<GuildMissions.Objective, int> worthOf, DateTimeOffset now)
        {
            var missions = GuildMissions.Of(guild.Id, now);
            foreach (var mission in missions)
            {
                int worth = worthOf(GuildMissions.ObjectiveOf(mission.ObjectiveId));
                var moved = worth > 0 ? GuildMissions.Advance(guild.Id, who, mission, worth, now) : null;
                if (moved == null) continue;

                var interested = SubscribersOf(guild.Id, now);
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jdc,
                    GuildProtocol.BuildMissionChanged(mission, moved.Value.Now, interested.GetValueOrDefault(mission.Slot))));
                if (!moved.Value.JustDone) continue;
                string doneBy = DatabaseManager.GetCharacterById(who)?.Name ?? "";
                GuildStore.Log(guild.Id, GuildLogProtocol.MissionComplete,
                    GuildLogProtocol.MissionCompleteLine(who, doneBy, mission.MissionId), who, doneBy);
                await RewardAsync(stream, guild, who, mission, now);
                // Done shows as a tick by his name in the others' list of the interested.
                if (GuildMissions.InterestOf(guild.Id, now).GetValueOrDefault(mission.Slot)?.Contains(who) == true)
                    await TellMissionAsync(guild, mission, now, except: who);
            }
        }

        /// <summary>
        /// A mission done: the notice, the mission's tokens to the member and its points to the
        /// guild (<see cref="GuildActivity.Credit"/>), the week to everybody, and the header when a
        /// milestone gave the guild experience.
        /// </summary>
        private static async Task RewardAsync(NetworkStream stream, GuildStore.Guild guild, long who,
                                              GuildMissions.Drawn mission, DateTimeOffset now)
        {
            var data = GuildMissions.MissionOf(mission.MissionId);
            var credited = GuildActivity.Credit(guild, who, data?.Tokens ?? 0, data?.ActivityPoints ?? 0, now);
            Console.WriteLine($"[Guild] {who} does mission {mission.MissionId} of «{guild.Name}»: " +
                              $"{credited.Tokens} token(s), {credited.Points} activity point(s).");

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jen, GuildProtocol.BuildMissionDone(mission.MissionId)));
            if (data != null) await Achievements.CountAsync(stream, "NC", data.SuperCategoryId);   // «Aspirante de los encargos»
            await GiveTokensAsync(stream, credited.Tokens);
            await TellTheWeekAsync(guild, now);
            if (credited.Experience > 0)
            {
                var fresh = GuildStore.ById(guild.Id) ?? guild;
                await GuildStateService.Live.TellHeaderAsync(fresh);
            }
        }

        /// <summary>
        /// The members interested in each mission of the week, as the subscribers window shows them:
        /// connected or not, their class, level, and whether they have done it.
        /// </summary>
        public static Dictionary<int, List<GuildProtocol.Subscriber>> SubscribersOf(long guildId, DateTimeOffset now)
        {
            var found = new Dictionary<int, List<GuildProtocol.Subscriber>>();
            foreach (var (slot, characters) in GuildMissions.InterestOf(guildId, now))
            {
                var list = new List<GuildProtocol.Subscriber>();
                foreach (long id in characters)
                {
                    var character = DatabaseManager.GetCharacterById(id);
                    if (character == null) continue;
                    bool done = GuildMissions.StandingsOf(guildId, id, now).TryGetValue(slot, out var standing) && standing.Done;
                    list.Add(new GuildProtocol.Subscriber(id, character.Name, character.Breed, character.Sex, character.Level,
                                                          SessionRegistry.FindByCharacter(id) != null, done));
                }
                found[slot] = list;
            }
            return found;
        }

        /// <summary>One mission (jdc) to every member connected, each with his own standing on it.</summary>
        public static async Task TellMissionAsync(GuildStore.Guild guild, GuildMissions.Drawn mission, DateTimeOffset now,
                                                  long except = 0)
        {
            var interested = SubscribersOf(guild.Id, now).GetValueOrDefault(mission.Slot);
            foreach (var member in GuildStore.Members(guild.Id))
            {
                if (member.CharacterId == except) continue;
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session == null) continue;
                var standing = GuildMissions.StandingsOf(guild.Id, member.CharacterId, now)
                                            .GetValueOrDefault(mission.Slot, GuildMissions.Standing.None);
                await session.SendAsync(ConnectionProtocol.Push(Op.Jdc,
                    GuildProtocol.BuildMissionChanged(mission, standing, interested)));
            }
        }

        /// <summary>
        /// Tokens into the bag, as the contributing capture has them: the stack of 32441 (ivj, or a
        /// new one), the «Has conseguido» line, and the weight unless the caller weighs later.
        /// </summary>
        public static async Task GiveTokensAsync(NetworkStream stream, int tokens, bool weigh = true)
        {
            if (tokens <= 0) return;
            if (!await WorkshopHandler.GiveAsync(stream, TokenItem, tokens)) return;
            await Achievements.CountAsync(stream, "NT", 0, tokens);   // «Gremichas obtenidas»
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained, tokens.ToString(), TokenItem.ToString())));
            if (weigh) await ItemUseHandler.WeighAsync(stream);
        }

        /// <summary>
        /// The guild's week (jex) to every member connected: the same points, and each his own tokens
        /// against his cap -- the bar of the missions tab.
        /// </summary>
        public static async Task TellTheWeekAsync(GuildStore.Guild guild, DateTimeOffset now, long except = 0)
        {
            var week = GuildActivity.Of(guild.Id, now);
            if (!week.HasActivity) return;
            int cap = GuildActivity.TokenCapOf(GuildStore.ById(guild.Id) ?? guild);
            foreach (var member in GuildStore.Members(guild.Id))
            {
                if (member.CharacterId == except) continue;
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session == null) continue;
                await session.SendAsync(ConnectionProtocol.Push(Op.Jex,
                    GuildProtocol.BuildWeekChanged(week, cap, GuildActivity.TokensOf(member.CharacterId, now))));
            }
        }
    }
}
