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

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Coming into somebody else's fight while it is being placed, and the fight as the rest of
    /// the map sees it: the swords, the teams, the count.
    /// </summary>
    /// <remarks>
    /// Three ways in, all measured (the frames are in Network/FightJoinProtocol.cs):
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
        private static readonly ConcurrentDictionary<long, (long FightId, int Team)> _joinAfterWalk = new();

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
        public static async Task SendFightCountAsync(NetworkStream stream, long mapId)
        {
            int count = FightsOnMap(mapId).Count;
            if (count == 0) return;
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jqz,
                FightJoinProtocol.BuildFightCount(count)));
        }

        // ─── The teams, for the map ─────────────────────────────────────────────

        /// <summary>
        /// A side as the map's messages carry it. The leader of a people's side is its first
        /// person; of the monsters', the group, as in every capture (-20002, -20000, -21200).
        /// </summary>
        internal static Pb TeamOnMap(FightInstance fight, int team, bool withMembers)
        {
            var side = fight.Bando(team);
            bool monsters = side.Exists(f => f.IsMonster && !f.EsInvocado)
                            || (team == FightInstance.Rojos && fight.Reglas.EnfrenteHayMonstruos);

            long leader = monsters
                ? fight.DefenderLeaderId
                : side.FirstOrDefault(f => !f.IsMonster && !f.EsInvocado)?.Id
                  ?? (team == FightInstance.Rojos ? fight.DefenderLeaderId : 0);

            List<Pb>? members = null;
            if (withMembers)
            {
                members = new List<Pb>();
                foreach (var f in side.ToList())
                {
                    if (f.EsInvocado || !f.IsAlive) continue;
                    members.Add(f.IsMonster
                        ? FightJoinProtocol.MonsterOnTeam(f.Id, f.MonsterId, f.GradeIndex + 1)
                        : FightJoinProtocol.PersonOnTeam(f.Id, f.Level, f.Name));
                }
            }

            return FightJoinProtocol.Team(team, leader, monsters, members);
        }

        /// <summary>
        /// The monsters' side with only its first <paramref name="howMany"/> monsters: the kae
        /// that grows one by one behind the swords (follow capture 141, dungeon 91-94).
        /// </summary>
        private static Pb MonstersSoFar(FightInstance fight, IReadOnlyList<Fighter> monsters, int howMany)
            => FightJoinProtocol.Team(FightInstance.Rojos, fight.DefenderLeaderId, true,
                monsters.Take(howMany).Select(m =>
                    FightJoinProtocol.MonsterOnTeam(m.Id, m.MonsterId, m.GradeIndex + 1)));

        /// <summary>The fight as a jss or an hpy carries it, both sides with their members.</summary>
        internal static Pb MapEntryOf(FightInstance fight)
        {
            _onTheMap.TryGetValue(fight.FightId, out var where);
            return FightJoinProtocol.FightOnMap(fight.FightId, fight.Reglas.TipoDelKam,
                where?.AttackersFlag ?? 0, where?.DefendersFlag ?? 0,
                TeamOnMap(fight, FightInstance.Azules, withMembers: true),
                TeamOnMap(fight, FightInstance.Rojos, withMembers: true),
                fight.OptionOn(FightInstance.Azules, FightInstance.OptionPartyOnly),
                fight.OptionOn(FightInstance.Rojos, FightInstance.OptionPartyOnly));
        }

        /// <summary>
        /// A frame for the people standing on the fight's map, and only them: whoever is in a
        /// fight hears his fight and not the street (SessionRegistry.Hears with fight 0).
        /// </summary>
        private static Task<int> ToTheMapAsync(FightInstance fight, byte[] frame)
            => fight.Reglas.PagaElKoliseo
                ? Task.FromResult(0)
                : SessionRegistry.BroadcastToMapAsync(fight.RoleplayMapId, frame, fightId: 0);

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
                byte[] ours = ConnectionProtocol.Push(Op.Kae, FightJoinProtocol.BuildTeamUpdate(
                    TeamOnMap(fight, FightInstance.Azules, withMembers: true), fight.FightId));
                byte[] theirs = ConnectionProtocol.Push(Op.Kae, FightJoinProtocol.BuildTeamUpdate(
                    TeamOnMap(fight, FightInstance.Rojos, withMembers: !fight.Reglas.EnfrenteHayMonstruos),
                    fight.FightId));
                foreach (var session in Publico(fight))
                {
                    await session.SendAsync(ours);
                    await session.SendAsync(theirs);
                }
            }

            await ShowFightOnMapAsync(fight);

            foreach (var person in fight.Todos.Where(f => !f.IsMonster && !f.EsInvocado).ToList())
            {
                await PartyAfterEntryAsync(fight, person, tellTheParty: true);
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
        public static async Task FightOptionAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jzx = ConnectionProtocol.ReadPayload(payload, Op.Jzx);
            if (jzx == null) return;
            int option = 0;
            foreach (var f in ProtoMessage.Parse(jzx).Fields)
                if (f.FieldNumber == 1 && f.WireType == 0) option = (int)f.VarIntValue;
            if (option is < 0 or > 3) return;

            long me = GameState.CharacterId;
            var fight = FightOf(me);
            if (fight == null) return;
            int side = fight.EquipoDe(me);
            var leader = side < 0 ? null : fight.Bando(side).FirstOrDefault(f => !f.IsMonster && !f.EsInvocado);
            if (leader == null || leader.Id != me) return;

            bool on = !fight.OptionOn(side, option);
            fight.SetOption(side, option, on);
            byte[] kau = ConnectionProtocol.Push(Op.Kau, Network.FightProtocol.BuildFightOption(side, option, on, fight.FightId));
            await ATodosAsync(fight, kau);
            if (fight.State == FightState.Placement) await ToTheMapAsync(fight, kau);
            if (option == FightInstance.OptionClosed && on)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(ClosedMessage)));
            }
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
                await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Kmu,
                    FightJoinProtocol.BuildActorHidden(fight.DefenderLeaderId)));
            }
            foreach (var person in fight.Todos.Where(f => !f.IsMonster && !f.EsInvocado).ToList())
            {
                await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Kmu,
                    FightJoinProtocol.BuildActorHidden(person.Id)));
            }

            foreach (int side in new[] { FightInstance.Azules, FightInstance.Rojos })
            {
                if (!RestrictToPartyOnOpening(fight, side)) continue;
                await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Kau,
                    Network.FightProtocol.BuildFightOption(side, FightInstance.OptionPartyOnly, true, fight.FightId)));
            }

            // The hpy has the monsters' side empty, as all three captures do: its members come in
            // the kae behind it, one at a time.
            _onTheMap.TryGetValue(fight.FightId, out var where);
            var shown = FightJoinProtocol.FightOnMap(fight.FightId, fight.Reglas.TipoDelKam,
                where?.AttackersFlag ?? 0, where?.DefendersFlag ?? 0,
                TeamOnMap(fight, FightInstance.Azules, withMembers: true),
                TeamOnMap(fight, FightInstance.Rojos, withMembers: !monsters),
                fight.OptionOn(FightInstance.Azules, FightInstance.OptionPartyOnly),
                fight.OptionOn(FightInstance.Rojos, FightInstance.OptionPartyOnly));
            await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Hpy,
                FightJoinProtocol.BuildFightShown(shown)));

            await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Jqz,
                FightJoinProtocol.BuildFightCount(FightsOnMap(fight.RoleplayMapId).Count)));

            await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Kae, FightJoinProtocol.BuildTeamUpdate(
                TeamOnMap(fight, FightInstance.Azules, withMembers: true), fight.FightId)));
            await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Kae, FightJoinProtocol.BuildTeamUpdate(
                TeamOnMap(fight, FightInstance.Rojos, withMembers: !monsters), fight.FightId)));

            if (!monsters) return;
            var theMonsters = fight.Rojo.Where(f => f.IsMonster && !f.EsInvocado).ToList();
            for (int k = 1; k <= theMonsters.Count; k++)
            {
                await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Kae,
                    FightJoinProtocol.BuildTeamUpdate(MonstersSoFar(fight, theMonsters, k), fight.FightId)));
            }
        }

        // ─── Coming in by kay ───────────────────────────────────────────────────

        /// <summary>
        /// The client asks to come into a fight (kay): the swords clicked on the map, or the
        /// party window. A refusal is only answered when its reason is measured -- see
        /// <see cref="FightJoinProtocol.BuildJoinRefused"/> -- and nothing this server refuses
        /// for has one, so the client simply stays where it is.
        /// </summary>
        public static async Task JoinRequestAsync(NetworkStream stream, byte[] payload)
        {
            var (named, fightId) = FightJoinProtocol.ReadJoinRequest(payload);
            var me = SessionContext.Current;
            var fight = FightById(fightId);

            string? why = WhyNotJoin(fight, named, me.State, out int team);
            if (why == null && !MayJoinSide(fight!, team, me.CharacterId))
            {
                // The one refusal with a measured answer: jxs { f1: whom he asked for, f2: 16 },
                // at J 856, the side restricted to its party.
                await me.SendAsync(ConnectionProtocol.Push(Op.Jxs,
                    FightJoinProtocol.BuildJoinRefused(named, FightJoinProtocol.RefusedPartyOnly)));
                why = "the side is restricted to its party";
            }
            if (why != null)
            {
                Program.LogDebug($"[Fight] {me.State.CharacterName} does not join fight #{fightId} " +
                                 $"(asked for the side of {named}): {why}.");
                return;
            }

            await JoinFightAsync(me, fight!, team, followingLeader: false);
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

            var refusal = fight.CanJoin(team, MaxPeoplePerTeam);
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
        private static async Task<bool> JoinFightAsync(GameSession who, FightInstance fight, int team,
                                                       bool followingLeader)
        {
            Fighter fighter;
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
                lock (fight) joined = fight.JoinTeam(fighter, team, MaxPeoplePerTeam);
                if (!joined) return false;

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

                if (who.Stream != null) await SendFightEntryAsync(who.Stream, fight, joining: true);
            }

            // Where he stands, with the side he faces: the enemies first and him last, as every
            // kmk of a placement move is (follow 152, dungeon 135, challenge 51).
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Kmk,
                Network.FightProtocol.BuildFightersPlaced(PlacedFacing(fight, fighter))));

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Kae, FightJoinProtocol.BuildTeamUpdate(
                TeamOnMap(fight, team, withMembers: true), fight.FightId)));

            if (fight.Reglas.HayRetos)
            {
                await ACadaUnoAsync(fight, sesion => sesion.Stream == null
                    ? Task.CompletedTask
                    : ChallengeHandler.SendCountAsync(sesion.Stream, fight, primeraVez: true));
            }

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxg, BuildPlayerAppearance(fight, fighter)));

            // The carousel behind him -- unless a dungeon's monsters are rebuilt, which send one
            // behind each new monster: the dungeon capture goes from his jxg (139) straight to
            // the first kar (140), the follow capture from his jxg (156) to the jzu (157).
            var regrown = await RegrowMonstersAsync(fight);
            if (regrown.Added.Count == 0)
            {
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jzu,
                    Network.FightProtocol.BuildTeams(CarouselOrder(fight))));
            }

            await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Kmu,
                FightJoinProtocol.BuildActorHidden(fighter.Id)));
            await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Kae, FightJoinProtocol.BuildTeamUpdate(
                TeamOnMap(fight, team, withMembers: true), fight.FightId)));
            await RegrowthOnTheMapAsync(fight, regrown);

            Program.LogDebug($"[Fight] {fighter.Name} joins fight #{fight.FightId}, side " +
                             $"{team}, cell {fighter.CellId}: {fight.PeopleIn(team)} on that side.");

            await PartyAfterEntryAsync(fight, fighter, tellTheParty: !followingLeader);
            return true;
        }

        /// <summary>
        /// A fighter's kmk entry after the ones of the side he faces, whose facing turns to him.
        /// </summary>
        private static List<(int Cell, int Orientation, long Fighter)> PlacedFacing(FightInstance fight, Fighter who)
        {
            var spots = new List<(int, int, long)>();
            foreach (var enemy in fight.Enemigos(who.Id).ToList())
            {
                if (!enemy.IsAlive || enemy.EsInvocado) continue;
                spots.Add((enemy.CellId, FacingOf(fight, enemy), enemy.Id));
            }
            spots.Add((who.CellId, FacingOf(fight, who), who.Id));
            return spots;
        }

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
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Kar, FightJoinProtocol.BuildFighterRemoved(gone.Id)));
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Kmu, FightJoinProtocol.BuildActorHidden(gone.Id)));
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jzw,
                    FightJoinProtocol.BuildTeamMemberRemoved(fight.FightId, FightInstance.Rojos, gone.Id)));
            }

            foreach (var monster in added)
            {
                byte[] placed = ConnectionProtocol.Push(Op.Kmk,
                    Network.FightProtocol.BuildFightersPlaced(PlacedFacing(fight, monster)));
                await ATodosAsync(fight, placed);
                await ATodosAsync(fight, placed);
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jxg,
                    Network.FightProtocol.BuildFighter(
                        monster.CellId, FacingOf(fight, monster), monster.Id, PlacementSheetOf(monster),
                        MonsterLook(monster),
                        Network.FightProtocol.MonsterIdentity(monster.GradeIndex + 1, monster.MonsterId,
                                                              monster.Level),
                        isMonster: true)));
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jzu,
                    Network.FightProtocol.BuildTeams(CarouselOrder(fight))));
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
                await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Jzw,
                    FightJoinProtocol.BuildTeamMemberRemoved(fight.FightId, FightInstance.Rojos, gone.Id)));
            }
            for (int k = 1; k <= regrown.Added.Count; k++)
            {
                await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Kae,
                    FightJoinProtocol.BuildTeamUpdate(MonstersSoFar(fight, regrown.Added, k), fight.FightId)));
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
        private static async Task PartyAfterEntryAsync(FightInstance fight, Fighter person, bool tellTheParty)
        {
            if (fight.Reglas.PagaElKoliseo) return;
            var party = Parties.Of(person.Id);
            if (party == null || !Parties.IsInParty(person.Id)) return;

            var followers = FollowersInto(fight, person);
            var told = ConnectionProtocol.Push(Op.Ilh, PartyMemberInFight(fight, person, party.Id));
            foreach (long member in Parties.MembersOf(party))
            {
                if (!tellTheParty) break;
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
                    _joinAfterWalk[member] = (fight.FightId, fight.EquipoDe(person.Id));
                    Program.LogDebug($"[Party] {session.State.CharacterName} joins fight " +
                                     $"#{fight.FightId} when his walk ends.");
                    continue;
                }

                await JoinFightAsync(session, fight, fight.EquipoDe(person.Id), followingLeader: true);
            }
        }

        /// <summary>
        /// The members of the person's party who follow him into this fight: he leads the party,
        /// they have the automatic entry switched on, they stand on the fight's map out of any
        /// fight, and his side has room.
        /// </summary>
        internal static List<long> FollowersInto(FightInstance fight, Fighter person)
        {
            var followers = new List<long>();
            var party = Parties.Of(person.Id);
            if (party == null || party.LeaderId != person.Id) return followers;

            int team = fight.EquipoDe(person.Id);
            if (team < 0) return followers;

            int room = MaxPeoplePerTeam - fight.PeopleIn(team);
            foreach (long member in Parties.MembersOf(party))
            {
                if (followers.Count >= room) break;
                if (member == person.Id || !AutoJoinOn(member)) continue;

                var session = SessionRegistry.FindByCharacter(member);
                if (session == null || !session.IsInWorld) continue;
                if (WhyNotFollow(fight, session.State) != null) continue;
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
        private static byte[] PartyMemberInFight(FightInstance fight, Fighter person, long partyId)
        {
            var map = MapManager.GetMapInfo(fight.RoleplayMapId);
            var session = SessionRegistry.FindByCharacter(person.Id);
            int left = fight.Reglas.KaaConCuentaAtras
                ? fight.PlacementDecisecondsLeft(fight.Reglas.RelojDeColocacion, DateTime.UtcNow)
                : 0;

            return FightJoinProtocol.BuildPartyMemberInFight(
                person.Id, left, session?.AccountId ?? 0, person.Name, fight.FightId, partyId,
                fight.Reglas.EnfrenteHayMonstruos
                    ? FightJoinProtocol.PartyFightMonsters
                    : FightJoinProtocol.PartyFightChallenge,
                fight.RoleplayMapId, map?.PosX ?? 0, map?.PosY ?? 0, map?.SubAreaId ?? 0);
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

        /// <summary>Whether a frame is one of the four switches of the party window.</summary>
        public static bool IsAutoOptionRequest(string payloadStr)
            => payloadStr.Contains(Op.Uri(Op.Ilf)) || payloadStr.Contains(Op.Uri(Op.Int))
               || payloadStr.Contains(Op.Uri(Op.Ikr)) || payloadStr.Contains(Op.Uri(Op.Inp));

        /// <summary>
        /// The four switches, each answered on root 3 with its empty response, as the follow
        /// capture does (frames 8-9, 203-208, 482-483, 996-1001).
        /// </summary>
        public static async Task AutoOptionAsync(NetworkStream stream, byte[] payload, string payloadStr)
        {
            long me = SessionContext.State.CharacterId;
            long request = ConnectionProtocol.RequestId(payload);
            string answer;

            if (payloadStr.Contains(Op.Uri(Op.Ilf))) { _autoJoin[me] = true; answer = Op.Ikm; }
            else if (payloadStr.Contains(Op.Uri(Op.Int))) { _autoJoin.TryRemove(me, out _); answer = Op.Ilv; }
            else if (payloadStr.Contains(Op.Uri(Op.Ikr))) { _autoReady[me] = true; answer = Op.Inn; }
            else if (payloadStr.Contains(Op.Uri(Op.Inp))) { _autoReady.TryRemove(me, out _); answer = Op.Ilr; }
            else return;

            await WriteFrameAsync(stream, ConnectionProtocol.Answer(answer, null, request));
            Program.LogDebug($"[Party] {SessionContext.State.CharacterName}: automatic entry " +
                             $"{(AutoJoinOn(me) ? "on" : "off")}, automatic ready {(AutoReadyOn(me) ? "on" : "off")}.");
        }

        /// <summary>
        /// The end of a walk (jqi): a member whose leader opened a fight while he was walking goes
        /// in now. The follow capture: jqi at 18.606 s, his kml at 18.644 s.
        /// </summary>
        public static async Task AfterWalkAsync(NetworkStream stream)
        {
            var me = SessionContext.Current;
            if (!_joinAfterWalk.TryRemove(me.State.CharacterId, out var pending)) return;

            var fight = FightById(pending.FightId);
            if (fight == null || WhyNotFollow(fight, me.State) != null) return;
            if (fight.CanJoin(pending.Team, MaxPeoplePerTeam) != FightInstance.JoinRefusal.None) return;

            await JoinFightAsync(me, fight, pending.Team, followingLeader: true);
        }

        /// <summary>
        /// The people who become ready with a leader: his party's members on his side with the
        /// automatic ready on. Marked here; their kah goes out behind his.
        /// </summary>
        /// <remarks>
        /// Fight 488 of the follow capture: the leader's kah and the follower's leave together at
        /// 92.973 s with nothing from the follower's client in between. In fight 487 the switch
        /// was turned on AFTER the leader was ready and nothing happened: it acts on the leader's
        /// ready, not on its own.
        /// </remarks>
        internal static List<long> ReadyAlongWith(FightInstance fight, long leaderId)
        {
            var along = new List<long>();
            var party = Parties.Of(leaderId);
            if (party == null || party.LeaderId != leaderId) return along;

            var members = new HashSet<long>(Parties.MembersOf(party));
            foreach (var ally in fight.Aliados(leaderId).ToList())
            {
                if (ally.Id == leaderId || ally.IsMonster || ally.EsInvocado || ally.IsReady) continue;
                if (!members.Contains(ally.Id) || !AutoReadyOn(ally.Id)) continue;
                ally.IsReady = true;
                along.Add(ally.Id);
            }
            return along;
        }

        // ─── Leaving from the placement ─────────────────────────────────────────

        /// <summary>
        /// A person leaves during the placement (kme). What he gets is measured in the sword
        /// capture, frames 37-43: jxa, then the way back as at the end of a fight -- kml, kmp,
        /// ktz, jru, lqu -- and his client asks for the map. What the others get is not measured:
        /// the kar and jzu of a monster taken off (dungeon 140) and his side's kae, the map the
        /// same kae and his figure back. A fight left with nobody is dropped.
        /// </summary>
        private static async Task LeavePlacementAsync(NetworkStream stream, FightInstance fight)
        {
            long me = GameState.CharacterId;
            int team = fight.EquipoDe(me);
            bool left;
            lock (fight) left = fight.LeavePlacement(me);
            if (!left) return;

            var suyo = SessionContext.State;
            suyo.IsInFight = false;
            suyo.FightId = 0;
            suyo.CurrentFightMobId = 0;
            _joinAfterWalk.TryRemove(me, out _);
            long back = suyo.RoleplayMapId != 0 ? suyo.RoleplayMapId : fight.RoleplayMapId;
            if (suyo.RoleplayMapId != 0) BackToRoleplayMap();
            else suyo.MapId = back;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxa, FightJoinProtocol.BuildLeftFight()));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kml));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmp));
            await WriteFrameAsync(stream, ConnectionProtocol.BuildRegenerationStarted(ConnectionProtocol.RegenerationRate));
            suyo.RegenerationStartedUtc = DateTime.UtcNow;
            await WriteFrameAsync(stream, ConnectionProtocol.BuildLoadMap(suyo.MapId));
            await WriteFrameAsync(stream, ConnectionProtocol.BuildMapClock());

            Program.LogDebug($"[Fight] {suyo.CharacterName} leaves fight #{fight.FightId} " +
                             "during its placement.");

            await BackOnTheMapAsync(SessionContext.Current);

            bool anybodyLeft = fight.Todos.Any(f => !f.IsMonster && !f.EsInvocado);
            if (!anybodyLeft)
            {
                fight.CancelPlacementTimer();
                fight.CancelTurnTimer();
                _activeFights.TryRemove(fight.FightId, out _);
                await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Hpr,
                    FightJoinProtocol.BuildFightHidden(fight.FightId)));
                await FightOffTheMapAsync(fight);
                return;
            }

            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Kar, FightJoinProtocol.BuildFighterRemoved(me)));
            await ATodosAsync(fight, ConnectionProtocol.Push(Op.Jzu,
                Network.FightProtocol.BuildTeams(CarouselOrder(fight))));

            if (team >= 0 && fight.PeopleIn(team) > 0)
            {
                byte[] side = ConnectionProtocol.Push(Op.Kae, FightJoinProtocol.BuildTeamUpdate(
                    TeamOnMap(fight, team, withMembers: true), fight.FightId));
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
            foreach (var pending in _joinAfterWalk.Where(p => p.Value.FightId == fight.FightId).ToList())
            {
                _joinAfterWalk.TryRemove(pending.Key, out _);
            }

            await ToTheMapAsync(fight, ConnectionProtocol.Push(Op.Hpr,
                FightJoinProtocol.BuildFightHidden(fight.FightId)));
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
                allReady = fight.SetFighterReady(person.Id);
                await ATodosAsync(fight, ConnectionProtocol.Push(Op.Kah,
                    Network.FightProtocol.BuildReadyAck(person.Id)));
            }

            if (allReady || fight.State == FightState.Ongoing) await StartFightAsync(fight);
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
            if (fight.Reglas.PagaElKoliseo) return;

            await SessionRegistry.BroadcastToMapAsync(fight.RoleplayMapId, ConnectionProtocol.Push(Op.Jqz,
                FightJoinProtocol.BuildFightCount(FightsOnMap(fight.RoleplayMapId).Count)), fightId: 0);

            var groupNow = where?.Replacement
                           ?? (where?.Group != null && MobSpawnManager.GetMobsForMap(fight.RoleplayMapId)
                                   .Any(g => g.MobId == where.Group.MobId)
                               ? where.Group
                               : null);
            if (groupNow != null && groupNow.Members.Count > 0)
            {
                await SessionRegistry.BroadcastToMapAsync(fight.RoleplayMapId,
                    ConnectionProtocol.Push(Op.Jsn, Pb.New()
                        .Msg(1, ConnectionProtocol.MonsterGroupActor(groupNow)).Build()),
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

            await SessionRegistry.BroadcastToMapAsync(who.MapId,
                ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                    ficha, who.State.CellId, who.State.Orientation, who.AccountId)),
                who.Id, fightId: 0);
        }
    }
}
