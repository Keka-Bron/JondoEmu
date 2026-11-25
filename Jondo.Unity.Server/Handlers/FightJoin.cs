using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using static Jondo.Protocol.NetworkMessage;
using Game = Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Coming into somebody else's fight while it is being placed, and the fight as the rest of
    /// the map sees it: the swords, the teams, the count.
    /// </summary>
    /// <remarks>
    /// Three ways in, all measured:
    ///
    /// <list type="bullet">
    /// <item>A player clicks the swords of a fight on the map and his client sends a kay naming
    /// a fighter of the side he wants and the fight («meterse en combate de otra persona haciendo
    /// click en la espadita», frame 0).</item>
    /// <item>A party member with the automatic entry switched on is put in by the SERVER when his
    /// leader opens a fight: his client sends nothing («entrar a combate con listo automatico y
    /// entrada automatica siguiendo a lider de grupo»). Standing still, his entry leaves 39 ms
    /// behind the swords (frames 529-537, fight 488); walking, the server waits for the end of his
    /// walk -- the kml leaves one round trip after his jqi (frames 142-143, fight 487).</item>
    /// <item>The same by kay from the party window or the map, which is what the fourth player of
    /// the dungeon capture did after the ilh told him his leader was fighting («busqueda
    /// automatica de grupo...», frames 95 and 125).</item>
    /// </list>
    ///
    /// The two party switches of that capture are four requests, each answered on root 3 with an
    /// empty message. Which is which is read off the clock of the capture, and it is an inference:
    ///
    /// <code>
    ///   ilf -> ikm   0.7 s    switched on at the start; fight 487 at 18.3 s takes him in
    ///   ikr -> inn  39.5 s    switched on after the leader's ready of 36.1 s: nothing happens
    ///   inp -> ilr  44.9 s    switched off; he presses ready by hand at 48.8 s
    ///   ikr -> inn  72.8 s    on again; in fight 488 his kah leaves with his leader's (92.97 s)
    ///   int -> ilv 113.5 s    and both off at the end of the capture, with the imo that stops
    ///   inp -> ilr 114.3 s    following
    /// </code>
    ///
    /// The client's own list of message names (datos/nombres_reales_3.6.10.10.tsv) has the
    /// matching pairs: FightAutoJoinActivationRequest/Response, FightAutoJoinDeactivationRequest
    /// and FightAutoJoinDeactivatedResponse, FightAutoReadyActivationRequest/Response and
    /// FightAutoReadyDeactivationResponse. So both are the server's to keep and to act on.
    ///
    /// Who a member follows is his party's leader: the party is where "X follows your movement"
    /// comes from (PartyHandler) and the leader is who both captures follow. The map-following
    /// itself is not this file's.
    /// </remarks>
    public static partial class FightHandler
    {
        /// <summary>
        /// How many people a side takes: the eight of a party (the f10 of the ing, the f3 of the
        /// ijz), which is also the most a dungeon's monster side grows to. No capture fills a side,
        /// so this is the party's number and not a measured refusal.
        /// </summary>
        public const int MaxPeoplePerTeam = Parties.MaxMembers;

        /// <summary>The most people a side of this fight takes: its own cap, never above the usual.</summary>
        internal static int CapOf(FightInstance fight)
            => fight.PeopleCap > 0 ? Math.Min(fight.PeopleCap, MaxPeoplePerTeam) : MaxPeoplePerTeam;

        /// <summary>
        /// How far a spell reaches before its own modifiers: the level's maximum, the characteristic
        /// -- equipment and buffs -- and the 281 aimed at this spell.
        /// </summary>
        /// <remarks>
        /// Only a spell whose range can be boosted takes the characteristic, for better or worse
        /// (SpellLevelFlags.RangeCanBeBoosted, 64): the client adds characteristic 19 to the level's
        /// range only then (hv.nhu, read in Ghidra). Absorción (12734, flags 516) has a fixed 1-6,
        /// and three Pío azul taking 2 range each left it at «1 to 0»: no spell could be cast.
        /// </remarks>
        internal static int ReachBeforeModifiers(Fighter caster, int spell, int levelMax, int flags, int round)
        {
            bool boostable = (flags & Managers.SpellLevelFlags.RangeCanBeBoosted) != 0;
            return levelMax
                 + (boostable ? caster.Range + caster.Buffs.De(AlcanceCaracteristica, round) : 0)
                 + caster.Buffs.DelHechizo(spell, Jondo.Unity.World.Fights.SpellAspect.AlcanceMaximo, round);
        }

        /// <summary>What a fight keeps for the map: its flags, and its group while there is one.</summary>
        private sealed class OnTheMap
        {
            public int AttackersFlag;
            public int DefendersFlag;
            public MobSpawnManager.MobGroup? Group;

            /// <summary>The group put in its place when the fight was won, for the map to see.</summary>
            public MobSpawnManager.MobGroup? Replacement;

            /// <summary>Set by the first of the fighters whose end removes the group.</summary>
            public int Settled;
        }

        private static readonly ConcurrentDictionary<long, OnTheMap> _onTheMap = new();

        /// <summary>Who has the automatic entry switched on (ilf/int), by character.</summary>
        private static readonly ConcurrentDictionary<long, bool> _autoJoin = new();

        /// <summary>Who has the automatic ready switched on (ikr/inp), by character.</summary>
        private static readonly ConcurrentDictionary<long, bool> _autoReady = new();

        /// <summary>Members to put in a fight as soon as their walk ends: character -> (fight, side).</summary>
        private static readonly ConcurrentDictionary<long, (FightInstance Fight, int Team)> _joinAfterWalk = new();

        // ─── Looking fights up ──────────────────────────────────────────────────

        /// <summary>A fight by its id, or null.</summary>
        public static FightInstance? FightById(long fightId)
            => _activeFights.TryGetValue(fightId, out var fight) ? fight : null;

        /// <summary>
        /// The fights that stand on a roleplay map: being placed or being fought, not over and not
        /// the koliseo's, which takes people from anywhere and stands on no map.
        /// </summary>
        public static List<FightInstance> FightsOnMap(long roleplayMapId)
            => _activeFights.Values
                .Where(f => f.RoleplayMapId == roleplayMapId && f.State != FightState.Ended
                            && !f.Reglas.PagaElKoliseo)
                .OrderBy(f => f.FightId)
                .ToList();

        /// <summary>The ones still in their placement: the only ones with swords on the map.</summary>
        public static List<FightInstance> PlacementFightsOnMap(long roleplayMapId)
            => FightsOnMap(roleplayMapId).Where(f => f.State == FightState.Placement).ToList();

        /// <summary>
        /// Whether a monster group is being fought. It went off the map with a kmu when the fight
        /// opened: it is not drawn for whoever arrives, and nobody else can attack it.
        /// </summary>
        public static bool IsGroupFighting(long groupId)
            => groupId != 0 && _activeFights.Values.Any(f => f.State != FightState.Ended
                                                             && f.Reglas.EnfrenteHayMonstruos
                                                             && f.DefenderLeaderId == groupId);

        /// <summary>The jqz a map's jss is followed by when it has fights; nothing otherwise.</summary>
        /// <remarks>
        /// Behind the jss and before the lva: "1003" at frame 54 of the sword capture, "1001" at
        /// 79 of the follow capture. Over the 1,114 jss of all the captures, 102 are followed by a
        /// jqz, and the ten that carry a fight in their f12 all are; the other seven of the follow
        /// capture have none.
        /// </remarks>
        public static async Task SendFightCountAsync(GameSession session, long mapId)
        {
            int count = FightsOnMap(mapId).Count;
            if (count == 0) return;
            await session.SendAsync(new Game.MapFightCountEvent(count));
        }

        // ─── The teams, for the map ─────────────────────────────────────────────

        // A companion does not make its master's team a monster side. The group's actor leads
        // monsters; the tax collector's actor leads its defenders.
        private static (Game.TeamType Type, long Leader) MapTeamLeader(FightInstance fight, int team)
        {
            var side = fight.Bando(team);
            if (team == FightInstance.Rojos && fight.TaxCollectorUuid != null)
                return (Game.TeamType.TaxCollector, fight.DefenderLeaderId);
            if (side.Exists(f => f.IsMonster && !f.EsInvocado && !f.IsCompanion)
                || (team == FightInstance.Rojos && fight.Reglas.EnfrenteHayMonstruos))
                return (Game.TeamType.Monster, fight.DefenderLeaderId);
            return (Game.TeamType.Player, side.FirstOrDefault(f => !f.IsMonster && !f.EsInvocado)?.Id
                ?? (team == FightInstance.Rojos ? fight.DefenderLeaderId : 0));
        }

        /// <summary>
        /// The monsters' side with only its first <paramref name="howMany"/> monsters: the kae
        /// that grows one by one behind the swords (follow capture 141, dungeon 91-94). A tax
        /// collector's side is the tax collector, whole.
        /// </summary>
        private static Game.FightTeamInformation MonstersSoFar(FightInstance fight, IReadOnlyList<Fighter> monsters, int howMany)
            => fight.TaxCollectorUuid != null
                ? TeamSnapshotOf(fight, FightInstance.Rojos, withMembers: true)
                : TeamSnapshotOf(fight, FightInstance.Rojos, withMembers: true, monsters.Take(howMany));

        /// <summary>The same placement state, constructed directly for typed map snapshots.</summary>
        internal static Game.FightCommonInformation MapSnapshotOf(FightInstance fight) => MapSnapshotOf(fight, true);

        private static Game.FightCommonInformation MapSnapshotOf(FightInstance fight, bool withDefenders)
        {
            _onTheMap.TryGetValue(fight.FightId, out var where);
            return new(checked((int)fight.FightId), FightTypeOf(fight),
                [TeamSnapshotOf(fight, FightInstance.Azules, true), TeamSnapshotOf(fight, FightInstance.Rojos, withDefenders)],
                [where?.AttackersFlag ?? 0, where?.DefendersFlag ?? 0],
                [new(false, fight.OptionOn(FightInstance.Azules, FightInstance.OptionPartyOnly), false, false),
                 new(false, fight.OptionOn(FightInstance.Rojos, FightInstance.OptionPartyOnly), false, false)]);
        }

        private static Game.FightType FightTypeOf(FightInstance fight) => fight.Reglas.TipoDelKam switch
            {
                0 => Game.FightType.Challenge, 1 => Game.FightType.Aggression, 2 => Game.FightType.PvMa,
                3 => Game.FightType.MxvM, 4 => Game.FightType.PvM, 5 => Game.FightType.PvT,
                6 => Game.FightType.PvMu, 7 => Game.FightType.PvpArena, 8 => Game.FightType.Koh,
                9 => Game.FightType.PvPr, 10 => Game.FightType.Breach,
                _ => throw new InvalidOperationException($"Unsupported map fight type {fight.Reglas.TipoDelKam}."),
            };

        internal static Game.FightTeamInformation TeamSnapshotOf(FightInstance fight, int team, bool withMembers,
            IEnumerable<Fighter>? selectedMembers = null)
        {
            var side = fight.Bando(team).ToArray();
            bool collector = team == FightInstance.Rojos && fight.TaxCollectorUuid != null;
            bool monsters = side.Any(f => f.IsMonster && !f.EsInvocado)
                || team == FightInstance.Rojos && fight.Reglas.EnfrenteHayMonstruos;
            long leader = collector || monsters ? fight.DefenderLeaderId
                : side.FirstOrDefault(f => !f.IsMonster && !f.EsInvocado)?.Id
                    ?? (team == FightInstance.Rojos ? fight.DefenderLeaderId : 0);
            var members = (withMembers ? selectedMembers ?? side : []).Where(f => !f.EsInvocado && f.IsAlive)
                .Select(f => new Game.FightTeamMemberInformation(f.Id,
                collector && f.TaxCollectorFirstName > 0
                    ? new Game.FightTeamMemberInformationCaseTaxCollectorMember(new(f.TaxCollectorFirstName, f.TaxCollectorLastName))
                    : !collector && f.IsMonster
                        ? new Game.FightTeamMemberInformationCaseMonsterMember(new(f.MonsterId, f.GradeIndex + 1))
                        : new Game.FightTeamMemberInformationCaseCharacter(new(f.Level, f.Name ?? "")))).ToArray();
            return new(collector ? Game.TeamType.TaxCollector : monsters ? Game.TeamType.Monster : Game.TeamType.Player,
                leader, Game.Alignment.Without, TeamIdentity(team),
                0, new Game.FightTeamInformationCaseMembers(new(members)));
        }

        private static Game.Team TeamIdentity(int team) => team switch
        {
            FightInstance.Azules => Game.Team.Challenger,
            FightInstance.Rojos => Game.Team.Defender,
            _ => throw new ArgumentOutOfRangeException(nameof(team)),
        };

        private static Game.FightTeamUpdateEvent TeamUpdate(FightInstance fight, int team, bool withMembers)
            => new(TeamSnapshotOf(fight, team, withMembers), checked((int)fight.FightId));

        private static Game.FightOptionUpdateEvent OptionUpdate(FightInstance fight, int team, int option, bool on)
            => new(TeamIdentity(team), option switch
            {
                0 => Game.FightOption.Unknown0, 1 => Game.FightOption.Unknown1,
                2 => Game.FightOption.Unknown2, 3 => Game.FightOption.Unknown3,
                _ => throw new ArgumentOutOfRangeException(nameof(option)),
            }, on, checked((int)fight.FightId));

        /// <summary>
        /// A frame for the people standing on the fight's map, and only them: whoever is in a
        /// fight hears his fight and not the street (SessionRegistry.Hears with fight 0).
        /// </summary>
        private static Task<int> ToTheMapAsync(FightInstance fight, Game.GameEvent message)
            => fight.Reglas.PagaElKoliseo
                ? Task.FromResult(0)
                : SessionRegistry.BroadcastToMapAsync(fight.RoleplayMapId, message, fightId: 0);

        /// <summary>
        /// Where the defenders' flag goes: on the group, unless the attacker stands on it -- then
        /// on the nearest cell of the map somebody can stand on. The follow capture has both on
        /// 71 and the flags on 71 and 73; the dungeon one has them apart and the flags on them.
        /// Which neighbour the real server picks is not measured.
        /// </summary>
        private static int DefendersFlagCell(long mapId, int groupCell, int attackerCell)
        {
            if (groupCell != attackerCell) return groupCell;
            if (!MapManager.WalkableCells.TryGetValue(mapId, out var cells) || cells.Count == 0)
                return groupCell;

            int best = groupCell, bestDistance = int.MaxValue;
            foreach (int cell in cells)
            {
                if (cell == attackerCell) continue;
                int distance = Jondo.Unity.World.Maps.MapGeometry.Distance(cell, groupCell);
                if (distance < bestDistance) { best = cell; bestDistance = distance; }
            }
            return best;
        }

        // ─── A fight opens ──────────────────────────────────────────────────────

        /// <summary>
        /// Everything a fight owes once its first people are in: their own team lists before the
        /// board, the swords for the map, the ilh for their parties and the members who follow
        /// them in. Runs in the context of the one who opened it.
        /// </summary>
        /// <remarks>
        /// The two kae before the board are measured on the attacker's side in «hablar con poutch
        /// ingball» (frames 52-53: his side with him in it, the monsters' side empty) and in both
        /// views of the challenge (both sides with their person).
        /// </remarks>
        private static async Task AfterFightOpenedAsync(FightInstance fight, MobSpawnManager.MobGroup? group,
                                                        int attackerCell)
        {
            var where = new OnTheMap { Group = group, AttackersFlag = attackerCell };
            where.DefendersFlag = group != null
                ? DefendersFlagCell(fight.RoleplayMapId, group.CellId, attackerCell)
                : DefenderStandsOn(fight, attackerCell);
            _onTheMap[fight.FightId] = where;

            // Not in the koliseo: its capture has the board's kae and no other (frame 251).
            if (!fight.Reglas.PagaElKoliseo)
            {
                var ours = TeamUpdate(fight, FightInstance.Azules, withMembers: true);
                var theirs = TeamUpdate(fight, FightInstance.Rojos,
                    withMembers: !fight.Reglas.EnfrenteHayMonstruos || fight.TaxCollectorUuid != null);
                foreach (var session in Publico(fight))
                {
                    await session.SendAsync(ours);
                    await session.SendAsync(theirs);
                }
            }

            await ShowFightOnMapAsync(fight);

            foreach (var person in fight.Todos.Where(f => !f.IsMonster && !f.EsInvocado).ToList())
            {
                await PartyAfterEntryAsync(fight, person, opening: true);
            }
        }

        /// <summary>
        /// Whether a side opens restricted to its party: when a person of it is in a party, as the
        /// real server does by itself in every party fight captured -- the follow capture, the
        /// party search, both challenges -- with no jzx from the client first.
        /// </summary>
        internal static bool RestrictToPartyOnOpening(FightInstance fight, int side)
        {
            bool party = fight.Bando(side).Any(f => !f.IsMonster && !f.EsInvocado && Parties.IsInParty(f.Id));
            if (party) fight.SetOption(side, FightInstance.OptionPartyOnly, true);
            return party;
        }

        /// <summary>
        /// Whether this character may come into a side restricted to its party: only a member of
        /// the party of someone on it. A side not restricted takes anybody.
        /// </summary>
        internal static bool MayJoinSide(FightInstance fight, int side, long characterId)
        {
            if (!fight.OptionOn(side, FightInstance.OptionPartyOnly)) return true;
            var party = Parties.Of(characterId);
            if (party == null || !Parties.IsInParty(characterId)) return false;
            return fight.Bando(side).Any(f => !f.IsMonster && !f.EsInvocado && Parties.Of(f.Id) == party);
        }

        /// <summary>
        /// jzx: a side's option switched by its leader -- the first person on it -- on or off, and
        /// told to the fight and to the map. Closing it says lqn 95 ("0802" and "105f" in the poutch
        /// capture, 124, and the perceptor one, 69); the other switches say nothing measured.
        /// </summary>
        internal static Task FightOptionAsync(GameSession session, Game.FightOptionToggleRequest request)
        {
            int option = request.Option switch
            {
                Game.FightOption.Unknown0 => 0, Game.FightOption.Unknown1 => 1,
                Game.FightOption.Unknown2 => 2, Game.FightOption.Unknown3 => 3, _ => -1,
            };
            return option < 0 ? Task.CompletedTask : InParticipantTurnAsync(session, fight =>
                fight.State is FightState.Placement or FightState.Ongoing
                    ? SwitchOptionAsync(fight, session.CharacterId, option) : Task.CompletedTask);
        }

        private static async Task SwitchOptionAsync(FightInstance fight, long me, int option)
        {
            int side = fight.EquipoDe(me);
            var leader = side < 0 ? null : fight.Bando(side).FirstOrDefault(f => !f.IsMonster && !f.EsInvocado);
            if (leader == null || leader.Id != me) return;

            bool on = !fight.OptionOn(side, option);
            fight.SetOption(side, option, on);
            var kau = OptionUpdate(fight, side, option, on);
            await ATodosAsync(fight, kau);
            if (fight.State == FightState.Placement) await ToTheMapAsync(fight, kau);
            if (option == FightInstance.OptionClosed && on)
            {
                await ATodosAsync(fight, new Game.TextInformationEvent(Game.TextInformationType.TextInformationMessage, ClosedMessage, []));
            }
            // No spectators: those watching go home.
            if (option == FightInstance.OptionSecret && on) await SpectatorsOutAsync(fight);
            Program.LogDebug($"[Fight] {GameState.CharacterName} turns option {option} of side {side} " +
                             $"of fight #{fight.FightId} {(on ? "on" : "off")}.");
        }

        /// <summary>lqn 95, what follows closing a fight.</summary>
        private const int ClosedMessage = 95;

        /// <summary>Where a challenged person stood when the fight opened: his roleplay cell.</summary>
        private static int DefenderStandsOn(FightInstance fight, int fallback)
        {
            var defender = fight.Bando(FightInstance.Rojos).FirstOrDefault(f => !f.IsMonster);
            return defender != null && fight.DeDondeVenian.TryGetValue(defender.Id, out var from)
                ? from.Casilla
                : fallback;
        }

        /// <summary>
        /// The swords, as the bystanders get them (follow capture 132-141, Zobal 2630-2636,
        /// dungeon 81-94): kmu of the group and of each person, hpy, jqz, the two sides and the
        /// monsters' side growing one by one.
        /// </summary>
        private static async Task ShowFightOnMapAsync(FightInstance fight)
        {
            if (fight.Reglas.PagaElKoliseo) return;
            bool monsters = fight.Reglas.EnfrenteHayMonstruos;

            if (monsters)
            {
                await ToTheMapAsync(fight, new Game.ContextRemoveElementEvent(fight.DefenderLeaderId));
            }
            foreach (var person in fight.Todos.Where(f => !f.IsMonster && !f.EsInvocado).ToList())
            {
                await ToTheMapAsync(fight, new Game.ContextRemoveElementEvent(person.Id));
            }

            foreach (int side in new[] { FightInstance.Azules, FightInstance.Rojos })
            {
                if (!RestrictToPartyOnOpening(fight, side)) continue;
                await ToTheMapAsync(fight, OptionUpdate(fight, side, FightInstance.OptionPartyOnly, true));
            }

            // The hpy has the monsters' side empty, as all three captures do: its members come in
            // the kae behind it, one at a time.
            await ToTheMapAsync(fight, new Game.ShowChallengeEvent(MapSnapshotOf(fight, withDefenders: !monsters)));

            await ToTheMapAsync(fight, new Game.MapFightCountEvent(FightsOnMap(fight.RoleplayMapId).Count));

            await ToTheMapAsync(fight, TeamUpdate(fight, FightInstance.Azules, withMembers: true));
            await ToTheMapAsync(fight, TeamUpdate(fight, FightInstance.Rojos, withMembers: !monsters));

            if (!monsters) return;
            var theMonsters = fight.Rojo.Where(f => f.IsMonster && !f.EsInvocado).ToList();
            for (int k = 1; k <= theMonsters.Count; k++)
            {
                await ToTheMapAsync(fight, new Game.FightTeamUpdateEvent(MonstersSoFar(fight, theMonsters, k), checked((int)fight.FightId)));
            }
        }

        // ─── Coming in by kay ───────────────────────────────────────────────────

        /// <summary>
        /// The client asks to come into a fight (kay): the swords clicked on the map, or the
        /// party window. Only the party restriction has a measured refusal notification.
        /// </summary>
        internal static async Task JoinRequestAsync(GameSession me, Game.FightJoinRequest request)
        {
            if (!me.IsAuthenticated || !me.HasCharacter || !me.IsInWorld) return;
            var (named, fightId) = request;
            var fight = FightById(fightId);
            if (fight == null) return;
            long character = me.CharacterId;
            using var context = SessionContext.Push(me);

            // The check and the join under the fight's turn: two coming in for its last place at
            // once both found it free.
            await InTurnAsync(fight, async () =>
            {
                if (FightById(fightId) != fight || !me.IsInWorld || me.CharacterId != character || me.State.SpectatingFightId != 0) return;
                if (named == 0) await WatchAsync(me, fight, fromTheMap: true);
                else if (!await Managers.HeadHunts.HoldsAsync(me.Stream, Managers.HeadHunts.Hold.Fight))
                    await JoinCheckedAsync(me, fight, named);
            });
        }

        private static async Task JoinCheckedAsync(GameSession me, FightInstance fight, long named)
        {
            string? why = WhyNotJoin(fight, named, me.State, out int team);
            if (why == null && !MayJoinSide(fight, team, me.CharacterId))
            {
                // The one refusal with a measured answer: jxs { f1: whom he asked for, f2: 16 },
                // at J 856, the side restricted to its party.
                await me.SendAsync(new Game.FightChallengeJoinRefuseEvent(Game.FighterRefusedReason.TeamLimitedByMainCharacter));
                why = "the side is restricted to its party";
            }
            if (why != null)
            {
                Program.LogDebug($"[Fight] {me.State.CharacterName} does not join fight #{fight.FightId} " +
                                 $"(asked for the side of {named}): {why}.");
                return;
            }

            await JoinFightAsync(me, fight, team, autoJoining: false);
        }

        /// <summary>
        /// Why this character cannot come into that fight on the side of <paramref name="named"/>,
        /// or null when he can; <paramref name="team"/> is the side.
        /// </summary>
        internal static string? WhyNotJoin(FightInstance? fight, long named, SessionState who, out int team)
        {
            team = -1;
            if (who == null || who.CharacterId <= 0) return "no character";
            if (fight == null) return "no such fight";
            if (fight.Reglas.PagaElKoliseo) return "a koliseo fight is not joined from the map";
            if (who.IsInFight || who.FightId != 0 || fight.EquipoDe(who.CharacterId) >= 0
                || FightOf(who.CharacterId) != null)
                return "already in a fight";
            if (who.MapId != fight.RoleplayMapId) return "not on the map of the fight";

            team = named == fight.DefenderLeaderId && fight.Reglas.EnfrenteHayMonstruos
                ? FightInstance.Rojos
                : fight.EquipoDe(named);
            if (team < 0) return "the fighter named is not in the fight";

            // A tax collector or a prism is defended through its alliance's fights (mer), not from
            // the map; and its alliance does not attack it.
            if (fight.DefendingAllianceId != 0)
            {
                if (team == FightInstance.Rojos) return "the defenders come through the alliance's fights";
                if (AllianceFights.IsDefender(fight, who.CharacterId)) return "what is attacked is his alliance's";
            }

            var refusal = fight.CanJoin(team, CapOf(fight));
            return refusal == FightInstance.JoinRefusal.None ? null : refusal.ToString();
        }

        /// <summary>
        /// Puts one person into a fight being placed, on <paramref name="team"/>, from wherever he
        /// is standing on its map. False when there was no room after all.
        /// </summary>
        /// <remarks>
        /// The order is the one of the follow capture (143-157) and of the dungeon one (126-168):
        /// his own entry, then what the fight's people are told -- where he stands, his side, the
        /// challenges, his figure, the carousel -- then, in a dungeon, the monster side rebuilt,
        /// and the map. He is in the fight before any of it goes out, so he gets all of it too:
        /// both captures show him receiving those frames between his lva and his ijm. Then his
        /// client asks for the board with ijm/kmv, as at any fight entry, and PendingPreparation
        /// gives it to him.
        /// </remarks>
        internal static async Task<bool> JoinFightAsync(GameSession who, FightInstance fight, int team,
                                                        bool autoJoining)
        {
            Fighter fighter;
            Fighter? companion = null;
            using (SessionContext.Push(who))
            {
                long me = GameState.CharacterId;
                foreach (var par in _activeFights)
                {
                    if (par.Value != fight && par.Value.EquipoDe(me) >= 0) _activeFights.TryRemove(par.Key, out _);
                }

                int casillaDeRol = GameState.CellId;
                long mapaDeRol = GameState.MapId;

                fighter = BuildPlayerFighter(fight);
                bool joined;
                lock (fight) joined = fight.JoinTeam(fighter, team, CapOf(fight));
                if (!joined) return false;

                // And the companion he wears, against monsters, beside him (FightCompanions.cs).
                if (fight.Reglas.EnfrenteHayMonstruos)
                    lock (fight) companion = AddCompanion(fight, fighter, fight.SiguienteIdDeInvocado());

                var suyo = SessionContext.State;
                suyo.MapId = fight.MapId;
                suyo.CellId = fighter.CellId;
                suyo.FightId = fight.FightId;
                suyo.RoleplayMapId = mapaDeRol;
                suyo.RoleplayCellId = casillaDeRol;
                suyo.IsInFight = true;
                suyo.CurrentFightMobId = fight.Reglas.EnfrenteHayMonstruos ? fight.DefenderLeaderId : 0;
                suyo.PendingMovementMapId = 0;
                suyo.PendingMovementCellId = -1;
                fight.DeDondeVenian[me] = (mapaDeRol, casillaDeRol);
                fight.ForgetPreparation(me);
                _joinAfterWalk.TryRemove(me, out _);

                await SendFightEntryAsync(who, fight, joining: true);
            }

            // Where he stands, with the side he faces: the enemies first and him last, as every
            // kmk of a placement move is (follow 152, dungeon 135, challenge 51).
            await ATodosAsync(fight, PlacedFacing(fight, fighter));

            await ATodosAsync(fight, TeamUpdate(fight, team, withMembers: true));

            if (fight.Reglas.HayRetos)
            {
                await ACadaUnoAsync(fight, sesion => ChallengeHandler.SendCountAsync(sesion, fight, primeraVez: true));
            }

            await ATodosAsync(fight, PlacementAppearance(fight, fighter));

            // His companion after him, placed and drawn as he is.
            if (companion != null)
            {
                await ATodosAsync(fight, new Game.EntitiesDispositionEvent([PlacementSpot(fight, companion)]));
                await ATodosAsync(fight, PlacementAppearance(fight, companion));
            }

            // The carousel behind him -- unless a dungeon's monsters are rebuilt, which send one
            // behind each new monster: the dungeon capture goes from his jxg (139) straight to
            // the first kar (140), the follow capture from his jxg (156) to the jzu (157).
            var regrown = await RegrowMonstersAsync(fight);
            if (regrown.Added.Count == 0)
            {
                await ATodosAsync(fight, PlacementTurnList(fight));
            }

            await ToTheMapAsync(fight, new Game.ContextRemoveElementEvent(fighter.Id));
            await ToTheMapAsync(fight, TeamUpdate(fight, team, withMembers: true));
            await RegrowthOnTheMapAsync(fight, regrown);

            Program.LogDebug($"[Fight] {fighter.Name} joins fight #{fight.FightId}, side " +
                             $"{team}, cell {fighter.CellId}: {fight.PeopleIn(team)} on that side.");

            // An automatic entry, including the leader's, must not recruit a second wave.
            if (!autoJoining) await PartyAfterEntryAsync(fight, fighter, opening: false);
            return true;
        }

        /// <summary>
        /// A fighter's kmk entry after the ones of the side he faces, whose facing turns to him.
        /// </summary>
        private static Game.EntitiesDispositionEvent PlacedFacing(FightInstance fight, Fighter who)
        {
            return new(fight.Enemigos(who.Id).Where(enemy => enemy.IsAlive && !enemy.EsInvocado)
                .Append(who).Select(f => PlacementSpot(fight, f)).ToArray());
        }

        private static Game.EntityDisposition PlacementSpot(FightInstance fight, Fighter f)
            => new(f.CellId, MapActorMessages.Facing(FacingOf(fight, f)), f.Id, null);

        private static Game.FightTurnListEvent PlacementTurnList(FightInstance fight)
            => new(CarouselOrder(fight).Select(id => new Game.FightTurnEntityCaseWithFlag(new(false, id))).ToArray());

        // ─── A dungeon's monster side follows the number of people ──────────────

        /// <summary>
        /// The monster side rebuilt for the people now on the other one: a dungeon room fights
        /// the first clamp(people, 4, 8) of its eight (MobSpawnManager.MembersFor). An ordinary
        /// group is left alone -- the follow capture keeps its poutch as -1 after the second
        /// player comes in -- and so is any fight with no group behind it.
        /// </summary>
        internal static (List<Fighter> Removed, List<Fighter> Added) RegrowMonsters(
            FightInstance fight, MobSpawnManager.MobGroup group)
        {
            var none = (new List<Fighter>(), new List<Fighter>());
            if (group == null || !group.Modular || !fight.Reglas.EnfrenteHayMonstruos) return none;

            var members = MobSpawnManager.MembersFor(group, fight.PeopleIn(FightInstance.Azules));
            lock (fight)
            {
                return fight.ReplaceMonsters(members.Count,
                    (index, id, cell) => BuildMonsterFighter(members[index], id, cell));
            }
        }

        /// <summary>
        /// <see cref="RegrowMonsters"/> and what the fight's people are told of it, in the order
        /// of the dungeon capture (140-167): for each monster that goes kar, kmu and jzw; for each
        /// that comes kmk twice -- the capture sends it twice every time --, jxg and jzu.
        /// </summary>
        private static async Task<(List<Fighter> Removed, List<Fighter> Added)> RegrowMonstersAsync(FightInstance fight)
        {
            if (!_onTheMap.TryGetValue(fight.FightId, out var where) || where.Group == null)
                return (new List<Fighter>(), new List<Fighter>());

            var (removed, added) = RegrowMonsters(fight, where.Group);
            if (removed.Count == 0 && added.Count == 0) return (removed, added);

            foreach (var gone in removed)
            {
                await ATodosAsync(fight, new Game.FightLeaveEvent(gone.Id));
                await ATodosAsync(fight, new Game.ContextRemoveElementEvent(gone.Id));
                await ATodosAsync(fight, new Game.FightTeamRemoveMemberEvent(checked((int)fight.FightId), Game.Team.Defender, gone.Id));
            }

            foreach (var monster in added)
            {
                var placed = PlacedFacing(fight, monster);
                await ATodosAsync(fight, placed);
                await ATodosAsync(fight, placed);
                await ATodosAsync(fight, PlacementAppearance(fight, monster));
                await ATodosAsync(fight, PlacementTurnList(fight));
            }

            Program.LogDebug($"[Dungeon] Fight #{fight.FightId} now has {added.Count} monsters " +
                             $"for {fight.PeopleIn(FightInstance.Azules)} people.");
            return (removed, added);
        }

        /// <summary>The same rebuild for the map: a jzw per monster gone, a growing kae (dungeon 100-107).</summary>
        private static async Task RegrowthOnTheMapAsync(FightInstance fight, (List<Fighter> Removed, List<Fighter> Added) regrown)
        {
            foreach (var gone in regrown.Removed)
            {
                await ToTheMapAsync(fight, new Game.FightTeamRemoveMemberEvent(checked((int)fight.FightId), Game.Team.Defender, gone.Id));
            }
            for (int k = 1; k <= regrown.Added.Count; k++)
            {
                await ToTheMapAsync(fight, new Game.FightTeamUpdateEvent(MonstersSoFar(fight, regrown.Added, k), checked((int)fight.FightId)));
            }
        }

        // ─── The party: the ilh, the automatic entry, the automatic ready ───────

        /// <summary>
        /// A person has just come into a fight: his party's members who follow him in are put in,
        /// and the others get the ilh (dungeon 95; both challenge captures). The follower of the
        /// follow capture gets no ilh: he is taken in instead. And nobody is told about a member
        /// who came in behind his leader: the dungeon capture's recorder got the ilh of the
        /// leader who opened fight 471 and none for the two who were put in behind him 0.1 s
        /// later (frames 95, 96 and 108).
        /// </summary>
        internal static async Task PartyAfterEntryAsync(FightInstance fight, Fighter person, bool opening)
        {
            if (fight.Reglas.PagaElKoliseo) return;
            var party = Parties.Of(person.Id);
            if (party == null || !Parties.IsInParty(person.Id)) return;

            var followers = FollowersInto(fight, person, opening);
            var told = PartyMemberInFight(fight, person);
            foreach (long member in Parties.MembersOf(party))
            {
                if (member == person.Id || followers.Contains(member)) continue;
                var session = SessionRegistry.FindByCharacter(member);
                if (session == null || !session.IsInWorld) continue;
                try { await session.SendAsync(told); }
                catch (Exception ex) { Program.LogDebug($"[Party] ilh to {member}: {ex.Message}"); }
            }

            foreach (long member in followers)
            {
                var session = SessionRegistry.FindByCharacter(member);
                if (session == null) continue;

                // Walking: in when the walk ends, as fight 487 of the capture.
                if (session.State.PendingMovementMapId != 0)
                {
                    _joinAfterWalk[member] = (fight, fight.EquipoDe(person.Id));
                    Program.LogDebug($"[Party] {session.State.CharacterName} joins fight " +
                                     $"#{fight.FightId} when his walk ends.");
                    continue;
                }

                await JoinFightAsync(session, fight, fight.EquipoDe(person.Id), autoJoining: true);
            }
        }

        /// <summary>
        /// Members may follow their leader into a fight. When another member opens the fight,
        /// only the party leader may follow that member. In both cases the follower must opt in.
        /// </summary>
        internal static List<long> FollowersInto(FightInstance fight, Fighter person, bool opening)
        {
            var followers = new List<long>();
            var party = Parties.Of(person.Id);
            if (party == null) return followers;
            bool leaderEntering = party.LeaderId == person.Id;
            if (!leaderEntering && !opening) return followers;

            int team = fight.EquipoDe(person.Id);
            if (team < 0) return followers;

            int room = CapOf(fight) - fight.PeopleIn(team);
            foreach (long member in Parties.MembersOf(party))
            {
                if (followers.Count >= room) break;
                if (!leaderEntering && member != party.LeaderId) continue;
                if (member == person.Id || !AutoJoinOn(member)) continue;

                var session = SessionRegistry.FindByCharacter(member);
                if (session == null || !session.IsInWorld) continue;
                if (WhyNotFollow(fight, session.State) != null) continue;
                if (!leaderEntering && WhyNotJoin(fight, person.Id, session.State, out _) != null) continue;
                followers.Add(member);
            }
            return followers;
        }

        /// <summary>Why a follower stays out, or null. The side's room is the caller's.</summary>
        internal static string? WhyNotFollow(FightInstance fight, SessionState who)
        {
            if (fight.State != FightState.Placement) return "the placement is over";
            if (who.IsInFight || who.FightId != 0 || fight.EquipoDe(who.CharacterId) >= 0) return "fighting";
            if (who.MapId != fight.RoleplayMapId) return "not on the map of the fight";
            return null;
        }

        /// <summary>The ilh of a person, for his party.</summary>
        private static Game.PartyMemberInFightEvent PartyMemberInFight(FightInstance fight, Fighter person)
        {
            var map = MapManager.GetMapInfo(fight.RoleplayMapId);
            int left = fight.Reglas.KaaConCuentaAtras
                ? fight.PlacementDecisecondsLeft(fight.Reglas.RelojDeColocacion, DateTime.UtcNow)
                : 0;

            return new(person.Id, left, person.Name ?? "", checked((int)fight.FightId),
                fight.Reglas.EnfrenteHayMonstruos
                    ? Game.PartyMemberInFightCause.MonsterAttack
                    : Game.PartyMemberInFightCause.MemberChallenge,
                new(fight.RoleplayMapId, map?.PosX ?? 0, map?.SubAreaId ?? 0, map?.PosY ?? 0));
        }

        /// <summary>Whether this character has the automatic entry on. Off until the client says.</summary>
        public static bool AutoJoinOn(long characterId) => _autoJoin.ContainsKey(characterId);

        /// <summary>Whether this character has the automatic ready on. Off until the client says.</summary>
        public static bool AutoReadyOn(long characterId) => _autoReady.ContainsKey(characterId);

        /// <summary>For tests: sets the two switches by hand.</summary>
        internal static void SetAutoOptions(long characterId, bool join, bool ready)
        {
            if (join) _autoJoin[characterId] = true; else _autoJoin.TryRemove(characterId, out _);
            if (ready) _autoReady[characterId] = true; else _autoReady.TryRemove(characterId, out _);
        }

        /// <summary>
        /// The four switches, each answered on root 3 with its empty response, as the follow
        /// capture does (frames 8-9, 203-208, 482-483, 996-1001).
        /// </summary>
        internal static Task AutoOptionAsync(GameSession session, Game.GameRequest request, int uid)
        {
            if (!session.IsAuthenticated || !session.HasCharacter || !session.IsInWorld) return Task.CompletedTask;
            long me = session.CharacterId;
            switch (request)
            {
                case Game.FightAutoJoinActivationRequest:
                    _autoJoin[me] = true;
                    return session.SendAsync(new Game.FightAutoJoinActivationResponse(), uid);
                case Game.FightAutoJoinDeactivationRequest:
                    _autoJoin.TryRemove(me, out _);
                    _joinAfterWalk.TryRemove(me, out _);
                    return session.SendAsync(new Game.FightAutoJoinDeactivationResponse(), uid);
                case Game.FightAutoReadyActivationRequest:
                    _autoReady[me] = true;
                    return session.SendAsync(new Game.FightAutoReadyActivationResponse(), uid);
                case Game.FightAutoReadyDeactivationRequest:
                    _autoReady.TryRemove(me, out _);
                    return session.SendAsync(new Game.FightAutoReadyDeactivationResponse(), uid);
                default: throw new ArgumentOutOfRangeException(nameof(request));
            }
        }

        /// <summary>
        /// The end of a walk (jqi): apply the deferred party auto-join. The follow capture:
        /// jqi at 18.606 s, his kml at 18.644 s.
        /// </summary>
        public static async Task AfterWalkAsync(NetworkStream stream)
        {
            var me = SessionContext.Current;
            if (!_joinAfterWalk.TryRemove(me.State.CharacterId, out var pending)) return;

            var fight = pending.Fight;
            await InTurnAsync(fight, async () =>
            {
                if (FightById(fight.FightId) != fight || WhyNotFollow(fight, me.State) != null) return;
                if (fight.CanJoin(pending.Team, CapOf(fight)) != FightInstance.JoinRefusal.None) return;
                if (!MayJoinSide(fight, pending.Team, me.CharacterId)) return;
                await JoinFightAsync(me, fight, pending.Team, autoJoining: true);
            });
        }

        /// <summary>
        /// The party leader can ready opted-in party allies. A member who opened the fight
        /// can ready only their opted-in leader. Each ready event follows the clicker's.
        /// </summary>
        /// <remarks>
        /// Fight 488 of the follow capture: the leader's kah and the follower's leave together at
        /// 92.973 s with nothing from the follower's client in between. In fight 487 the switch
        /// was turned on AFTER the leader was ready and nothing happened: it acts on the leader's
        /// ready, not on its own.
        /// </remarks>
        internal static List<long> ReadyAlongWith(FightInstance fight, long characterId)
        {
            var along = new List<long>();
            var party = Parties.Of(characterId);
            if (party == null) return along;
            long leaderId = party.LeaderId;
            if (characterId != leaderId && characterId != fight.OpenerId) return along;

            var members = new HashSet<long>(Parties.MembersOf(party));
            if (!members.Contains(characterId)) return along; // Of also finds pending invitees.
            foreach (var ally in fight.Aliados(characterId).ToList())
            {
                if (characterId != leaderId && ally.Id != leaderId) continue;
                if (ally.Id == characterId || ally.IsMonster || ally.EsInvocado || ally.IsReady) continue;
                if (!members.Contains(ally.Id) || !AutoReadyOn(ally.Id)) continue;
                ally.IsReady = true;
                along.Add(ally.Id);
            }
            return along;
        }

        // ─── Leaving from the placement ─────────────────────────────────────────

        internal static async Task QuitAsync(GameSession session)
        {
            if (!session.IsAuthenticated || !session.HasCharacter || !session.IsInWorld) return;
            long id = session.State.SpectatingFightId;
            if (id != 0)
            {
                var watched = FightById(id);
                if (watched == null) await StopWatchingAsync(session);
                else await InTurnAsync(watched, async () =>
                {
                    if (session.State.SpectatingFightId == id && FightById(id) == watched) await StopWatchingAsync(session);
                });
                return;
            }
            await InParticipantTurnAsync(session, async fight =>
            {
                if (!await FinishPendingFightAsync(fight)) await AbandonAsync(session.Stream, fight);
            });
        }

        /// <summary>
        /// A person leaves during the placement (kme). What he gets is measured in the sword
        /// capture, frames 37-43: jxa, then the way back as at the end of a fight -- kml, kmp,
        /// ktz, jru, lqu -- and his client asks for the map. What the others get is not measured:
        /// the kar and jzu of a monster taken off (dungeon 140) and his side's kae, the map the
        /// same kae and his figure back. If his departure defeats his side, everyone gets the normal result instead.
        /// </summary>
        internal static async Task LeavePlacementAsync(FightInstance fight)
        {
            long me = GameState.CharacterId;
            int team = fight.EquipoDe(me);
            bool left;
            lock (fight) left = fight.LeavePlacement(me);
            if (!left) return;

            if (fight.State == FightState.Ended)
            {
                await SwordsGoneAsync(fight);
                await EndFightAsync(fight);
                return;
            }

            var suyo = SessionContext.State;
            suyo.IsInFight = false;
            suyo.FightId = 0;
            suyo.CurrentFightMobId = 0;
            _joinAfterWalk.TryRemove(me, out _);
            long back = suyo.RoleplayMapId != 0 ? suyo.RoleplayMapId : fight.RoleplayMapId;
            if (suyo.RoleplayMapId != 0) BackToRoleplayMap();
            else suyo.MapId = back;

            var session = SessionContext.Current;
            await SendLeaveContextAsync(session);
            await session.SendAsync(new Game.LifePointsRegenBeginEvent(ConnectionProtocol.RegenerationRateNow()));
            suyo.RegenerationStartedUtc = DateTime.UtcNow;
            await session.SendAsync(new Game.MapCurrentEvent(suyo.MapId));
            await session.SendAsync(ServerTimeMessages.Snapshot());

            Program.LogDebug($"[Fight] {suyo.CharacterName} leaves fight #{fight.FightId} " +
                             "during its placement.");

            await BackOnTheMapAsync(SessionContext.Current);

            bool anybodyLeft = fight.Todos.Any(f => !f.IsMonster && !f.EsInvocado);
            if (!anybodyLeft)
            {
                fight.CancelPlacementTimer();
                fight.CancelTurnTimer();
                _activeFights.TryRemove(fight.FightId, out _);
                await ToTheMapAsync(fight, new Game.RemoveChallengeEvent(checked((int)fight.FightId)));
                await FightOffTheMapAsync(fight);
                // A tax collector or a prism left with nobody attacking it stands again.
                await FightOverForTheWorldAsync(fight, attackersWon: false);
                return;
            }

            // And a defended fight's alliance sees him leave its fights window.
            await AllianceFights.FighterLeftAsync(fight, me);

            // Off the others' board as the dungeon capture takes a fighter off it (140-167): kar,
            // kmu and jzw. The kar alone only cancels his pending swaps in the placement frame
            // (fjw.bfvx); it is the jzw that takes him off (fjw.bfwh), and without it he stayed
            // standing there for the whole fight, struck by spells that did nothing.
            // And his companion, taken off with him (FightInstance.LeavePlacement).
            List<long> gone;
            lock (fight)
            {
                gone = new List<long> { me };
                gone.AddRange(fight.CompanionsGone);
                fight.CompanionsGone.Clear();
            }
            foreach (long fighter in gone)
            {
                await ATodosAsync(fight, new Game.FightLeaveEvent(fighter));
                await ATodosAsync(fight, new Game.ContextRemoveElementEvent(fighter));
                await ATodosAsync(fight, new Game.FightTeamRemoveMemberEvent(checked((int)fight.FightId), TeamIdentity(team), fighter));
            }
            await ATodosAsync(fight, PlacementTurnList(fight));

            if (team >= 0 && fight.PeopleIn(team) > 0)
            {
                var side = TeamUpdate(fight, team, withMembers: true);
                await ATodosAsync(fight, side);
                await ToTheMapAsync(fight, side);
            }

            var regrown = await RegrowMonstersAsync(fight);
            await RegrowthOnTheMapAsync(fight, regrown);
        }

        // ─── The placement ends, the fight ends ─────────────────────────────────

        /// <summary>The swords go when the placement is over (Zobal 2650-2651, jalatós 857).</summary>
        private static async Task SwordsGoneAsync(FightInstance fight)
        {
            foreach (var pending in _joinAfterWalk.Where(p => p.Value.Fight == fight).ToList())
            {
                _joinAfterWalk.TryRemove(pending);
            }

            await ToTheMapAsync(fight, new Game.RemoveChallengeEvent(checked((int)fight.FightId)));
        }

        /// <summary>
        /// The placement ran out: whoever has not pressed ready is made ready and the fight
        /// starts. The countdown used to ready only the one who opened the fight, which with a
        /// second person in it left the fight waiting for ever.
        /// </summary>
        private static async Task StartWhoeverIsLeftAsync(FightInstance fight)
        {
            if (fight.State != FightState.Placement) return;

            var people = fight.Todos.Where(f => !f.IsMonster && !f.EsInvocado && !f.IsReady).ToList();
            bool allReady = people.Count == 0 && fight.Todos.All(f => f.IsMonster || f.EsInvocado || f.IsReady);
            foreach (var person in people)
            {
                allReady = fight.MarkFighterReady(person.Id, clockRanOut: true);
                await ATodosAsync(fight, new Game.FightHumanReadyStateEvent(person.Id, true));
            }

            if (allReady) await StartFightAsync(fight);
        }

        /// <summary>
        /// Whether this fighter's end is the one that removes the group: the first of the people
        /// of the fight. Every person runs the end in his own context, and each used to remove
        /// the group and put a new one: two people, two new groups.
        /// </summary>
        private static bool SettlesTheGroup(FightInstance fight)
        {
            var where = _onTheMap.GetOrAdd(fight.FightId, _ => new OnTheMap());
            return System.Threading.Interlocked.Exchange(ref where.Settled, 1) == 0;
        }

        /// <summary>Remembers the group put back after a win, for the map to see it.</summary>
        private static void Replaced(FightInstance fight, MobSpawnManager.MobGroup? group)
        {
            if (group != null && _onTheMap.TryGetValue(fight.FightId, out var where)) where.Replacement = group;
        }

        /// <summary>
        /// The fight is over for the map: the count drops (Zobal 7345-7358), the people are drawn
        /// again where they came back, and the group is drawn again if it is still there (a
        /// lost fight) or its replacement if it was beaten. The group's jsn is the same actor
        /// block as its entry in the jss (follow capture 131).
        /// </summary>
        private static async Task FightOffTheMapAsync(FightInstance fight)
        {
            _activeFights.TryRemove(fight.FightId, out _);
            _onTheMap.TryRemove(fight.FightId, out var where);
            await SpectatorsHomeAsync(fight);
            if (fight.Reglas.PagaElKoliseo) return;

            await ToTheMapAsync(fight, new Game.MapFightCountEvent(FightsOnMap(fight.RoleplayMapId).Count));

            var groupNow = where?.Replacement
                           ?? (where?.Group != null && MobSpawnManager.GetMobsForMap(fight.RoleplayMapId)
                                   .Any(g => g.MobId == where.Group.MobId)
                               ? where.Group
                               : null);
            if (groupNow != null && groupNow.Members.Count > 0)
            {
                await SessionRegistry.BroadcastToMapAsync(fight.RoleplayMapId,
                    new Game.GameRolePlayShowActorsEvent([MapActorMessages.MonsterGroup(groupNow)]),
                    fightId: 0);
            }
        }

        /// <summary>
        /// A person is back on a roleplay map: the others there see him again (the jsn of an
        /// arrival, as SessionRegistry.AnunciarMudanzaAsync sends it). He went off with a kmu.
        /// </summary>
        private static async Task BackOnTheMapAsync(GameSession? who)
        {
            if (who == null || !who.IsInWorld || who.CharacterId <= 0 || who.State.FightId != 0) return;
            var ficha = DatabaseManager.GetCharacterById(who.CharacterId);
            if (ficha == null) return;

            await who.Map.SendAsync(MapActorMessages.Refreshed(ficha, who.State.CellId, who.State.Orientation, who.AccountId),
                exceptSessionId: who.Id, fightId: 0);
        }
    }
}
