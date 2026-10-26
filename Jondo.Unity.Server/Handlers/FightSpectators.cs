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
    /// Watching somebody else's fight: the swords of the map's fight list (kay with a fight and no
    /// fighter, SpectatorUi.JoinFightAsSpectator) or a friend's, guild mate's or alliance mate's
    /// fight from their lists (kal { f2 the character }, fhs.egi).
    /// </summary>
    /// <remarks>
    /// <para>
    /// No capture watches a fight; this is read off the client. Its spectator join is kab { f1 the
    /// named teams, f2 ..., f3 the kaa }: fff.bdzg raises the spectator flag (+0x168) and enters the
    /// fight context, fjq.bfuk reads the kaa inside and marks the fight state spectating (| 8). The
    /// rest is a fighter's way in and a reconnection's board without what is a fighter's own: kml,
    /// kmp, the arena's map; then, when the client asks with ijm/kmv, the fight as it stands --
    /// ijq, kam, kab, every fighter (jxg), where they stand (kmk), jxb, the turn (jzc), the round,
    /// the option, the teams, the buffs, the map -- and from then on every frame the fighters all
    /// get. Leaving (kme) and the fight's end are a placement's way out: jxa, kml, kmp, the map.
    /// </para>
    /// <para>
    /// A spectator is no fighter: FightOf does not find him, so nothing he sends plays. His
    /// session carries the fight's id, as a fighter's does, so that he hears the fight and not the
    /// map around it; and his roleplay map is kept where a fighter's is, so a client closed while
    /// watching comes back where it was.
    /// </para>
    /// </remarks>
    public static partial class FightHandler
    {
        /// <summary>Who watches each fight: fight → characters.</summary>
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<long, byte>> _spectators = new();

        /// <summary>The sessions watching a fight.</summary>
        internal static List<GameSession> SpectatorsOf(FightInstance fight)
        {
            var sessions = new List<GameSession>();
            if (!_spectators.TryGetValue(fight.FightId, out var watching)) return sessions;
            foreach (long id in watching.Keys)
            {
                var session = SessionRegistry.FindByCharacter(id);
                if (session != null && session.State.SpectatingFightId == fight.FightId) sessions.Add(session);
                else watching.TryRemove(id, out _);
            }
            return sessions;
        }

        /// <summary>Why this character may not watch that fight, or null when he may.</summary>
        internal static string? WhyNotWatch(FightInstance? fight, SessionState who, bool fromTheMap)
        {
            if (who == null || who.CharacterId <= 0) return "no character";
            if (fight == null) return "no such fight";
            if (fight.State != FightState.Ongoing) return "the fight has not started";
            if (fight.Reglas.PagaElKoliseo) return "a koliseo fight is not watched";
            if (who.IsInFight || who.FightId != 0 || who.SpectatingFightId != 0 || FightOf(who.CharacterId) != null)
                return "already in a fight";
            if (fight.OptionOn(FightInstance.Azules, FightInstance.OptionSecret)
                || fight.OptionOn(FightInstance.Rojos, FightInstance.OptionSecret))
                return "the fight takes no spectators";
            if (fromTheMap && who.MapId != fight.RoleplayMapId) return "not on the map of the fight";
            return null;
        }

        /// <summary>The kay without a fighter: the map's fight, watched.</summary>
        internal static Task WatchFromTheMapAsync(GameSession me, long fightId)
            => WatchAsync(me, FightById(fightId), fromTheMap: true);

        /// <summary>
        /// kal { f2 the character }: the fight that character is in, watched from wherever one
        /// stands -- the lists that offer it are the friends', the guild's and the alliance's.
        /// </summary>
        public static async Task WatchCharacterAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? kal = ConnectionProtocol.ReadPayload(payload, Op.Kal);
            long target = 0;
            if (kal != null)
                foreach (var field in ProtoMessage.Parse(kal).Fields)
                    if (field.FieldNumber == 2 && field.WireType == 0) target = field.VarIntValue;
            await WatchAsync(SessionContext.Current, target == 0 ? null : FightOf(target), fromTheMap: false);
        }

        private static async Task WatchAsync(GameSession me, FightInstance? fight, bool fromTheMap)
        {
            string? why = WhyNotWatch(fight, me.State, fromTheMap);
            if (why != null)
            {
                Program.LogDebug($"[Fight] {me.State.CharacterName} does not watch fight #{fight?.FightId}: {why}.");
                return;
            }

            var state = me.State;
            _spectators.GetOrAdd(fight!.FightId, _ => new ConcurrentDictionary<long, byte>())[me.CharacterId] = 0;

            // Off the map he stands on, as somebody going into a fight goes off it.
            await SessionRegistry.BroadcastToMapAsync(state.MapId, ConnectionProtocol.Push(Op.Kmu,
                FightJoinProtocol.BuildActorHidden(me.CharacterId)), me.Id, fightId: 0);

            state.RoleplayMapId = state.MapId;
            state.RoleplayCellId = state.CellId;
            state.SpectatingFightId = fight.FightId;
            state.SpectatorBoardPending = true;
            state.FightId = fight.FightId;
            state.MapId = fight.MapId;

            await me.SendAsync(ConnectionProtocol.Push(Op.Kml));
            await me.SendAsync(ConnectionProtocol.Push(Op.Kmp, FightProtocol.BuildFightMapComing()));
            await me.SendAsync(ConnectionProtocol.BuildLoadMap(fight.MapId));
            await me.SendAsync(ConnectionProtocol.BuildMapClock());
            Program.LogDebug($"[Fight] {state.CharacterName} watches fight #{fight.FightId}.");
        }

        /// <summary>The fight this session still owes its spectator's board, once its client asked for it.</summary>
        public static FightInstance? PendingSpectatorBoard()
        {
            var state = SessionContext.State;
            if (!state.SpectatorBoardPending || state.SpectatingFightId == 0) return null;
            var fight = FightById(state.SpectatingFightId);
            return fight != null && fight.State == FightState.Ongoing ? fight : null;
        }

        /// <summary>The fight as it stands, to its new spectator: a reconnection's board, kab for its kaa.</summary>
        public static async Task SendSpectatorBoardAsync(NetworkStream stream, FightInstance fight)
        {
            SessionContext.State.SpectatorBoardPending = false;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ijq, FightProtocol.BuildMapReady()));

            var monsters = fight.Reglas.EnfrenteHayMonstruos && fight.TaxCollectorUuid == null
                ? fight.Rojo.ConvertAll(f => (long)f.MonsterId)
                : new List<long>();
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kam,
                FightProtocol.BuildFightAnnounced(fight.Reglas.TipoDelKam, fight.DefenderLeaderId, monsters, fight.FightId, 0)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kab,
                FightProtocol.BuildSpectatorJoined(fight.Reglas.KaaConCuentaAtras ? fight.Reglas.TipoDelKam : 0)));

            foreach (var fighter in TodosLosCombatientes(fight))
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxg,
                    fighter.IsMonster
                        ? CreatureFighter(fight, fighter)
                        : BuildPlayerAppearance(fight, fighter)));
            }

            var spots = new List<(int Cell, int Orientation, long Fighter)>();
            foreach (var fighter in TodosLosCombatientes(fight))
                if (fighter.IsAlive) spots.Add((fighter.CellId, FacingOf(fight, fighter), fighter.Id));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kmk, FightProtocol.BuildFightersPlaced(spots)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxb,
                FightProtocol.BuildAllFighters(FighterBlocksInPlayOrder(fight))));

            var announced = fight.LastAnnouncedTurn;
            if (announced.Announced)
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jzc,
                    FightProtocol.BuildTurnResumed(announced.FighterId, announced.Deciseconds,
                                                   announced.RemainingDeciseconds(DateTime.UtcNow),
                                                   announced.Round, announced.Carried)));
            }

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jxz, FightProtocol.BuildRound(fight.RoundNumber)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kau,
                FightProtocol.BuildFightOption(FightInstance.Azules, FightInstance.OptionSecret,
                    fight.OptionOn(FightInstance.Azules, FightInstance.OptionSecret), fight.FightId)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jzu, TeamsOf(fight)));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jwq, FightProtocol.BuildBuffSync(LiveBuffFrames(fight))));
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jrk, FightProtocol.BuildFightMap(fight.MapId)));
            await WriteFrameAsync(stream, ConnectionProtocol.BuildActorsComplete());
        }

        /// <summary>
        /// A spectator's kme: out of the fight, back where he was. False when the session watches
        /// nothing, so the kme is a fighter's.
        /// </summary>
        public static async Task<bool> StopWatchingAsync(GameSession me)
        {
            if (me.State.SpectatingFightId == 0) return false;
            await SendSpectatorHomeAsync(me);
            return true;
        }

        /// <summary>A fight over: its spectators go back where they were.</summary>
        private static async Task SpectatorsHomeAsync(FightInstance fight)
        {
            foreach (var session in SpectatorsOf(fight))
            {
                try { await SendSpectatorHomeAsync(session); }
                catch (Exception ex) { Program.LogDebug($"[Fight] Could not send spectator {session.CharacterId} home: {ex.Message}"); }
            }
            _spectators.TryRemove(fight.FightId, out _);
        }

        /// <summary>A side closing itself to spectators (OptionSecret) sends those watching home.</summary>
        internal static Task SpectatorsOutAsync(FightInstance fight) => SpectatorsHomeAsync(fight);

        /// <summary>One spectator out: a placement's way out, jxa, kml, kmp and the map he was on.</summary>
        private static async Task SendSpectatorHomeAsync(GameSession me)
        {
            using (SessionContext.Push(me))
            {
                var state = me.State;
                long fightId = state.SpectatingFightId;
                if (_spectators.TryGetValue(fightId, out var watching)) watching.TryRemove(me.CharacterId, out _);
                state.SpectatingFightId = 0;
                state.SpectatorBoardPending = false;
                state.FightId = 0;
                BackToRoleplayMap();

                await me.SendAsync(ConnectionProtocol.Push(Op.Jxa, FightJoinProtocol.BuildLeftFight()));
                await me.SendAsync(ConnectionProtocol.Push(Op.Kml));
                await me.SendAsync(ConnectionProtocol.Push(Op.Kmp));
                await me.SendAsync(ConnectionProtocol.BuildLoadMap(state.MapId));
                await me.SendAsync(ConnectionProtocol.BuildMapClock());
                await BackOnTheMapAsync(me);
                Program.LogDebug($"[Fight] {state.CharacterName} stops watching fight #{fightId}.");
            }
        }

        // ─── The spectator window's lists ───────────────────────────────────────

        /// <summary>
        /// The map's fights (jrv → jyc), as SpectatorUi lists them: each with its kind, its start,
        /// whether it takes spectators, and each side's size, mean level and options.
        /// </summary>
        public static async Task RunningFightsAsync(NetworkStream stream, byte[] payload)
        {
            var listed = new List<Pb>();
            foreach (var fight in FightsOnMap(SessionContext.State.MapId))
            {
                bool secret = fight.OptionOn(FightInstance.Azules, FightInstance.OptionSecret)
                              || fight.OptionOn(FightInstance.Rojos, FightInstance.OptionSecret);
                var teams = new List<(Pb, bool)>();
                foreach (int team in new[] { FightInstance.Azules, FightInstance.Rojos })
                {
                    var side = fight.Bando(team).Where(f => !f.EsInvocado && f.IsAlive).ToList();
                    var shown = TeamOnMap(fight, team, withMembers: false);
                    var kindAndLeader = ProtoMessage.Parse(shown.Build());
                    int sideKind = (int)(kindAndLeader.Fields.Find(f => f.FieldNumber == 2)?.VarIntValue ?? 0);
                    long leader = kindAndLeader.Fields.Find(f => f.FieldNumber == 3)?.VarIntValue ?? 0;
                    int mean = side.Count == 0 ? 0 : (int)side.Average(f => f.Level);
                    teams.Add((FightJoinProtocol.LightTeam(team, leader, sideKind, side.Count, mean),
                               fight.OptionOn(team, FightInstance.OptionPartyOnly)));
                }
                listed.Add(FightJoinProtocol.RunningFight(fight.FightId, fight.Reglas.TipoDelKam, fight.StartedAt, secret, teams));
            }
            await WriteFrameAsync(stream, ConnectionProtocol.Answer(Op.Jyc, FightJoinProtocol.BuildRunningFights(listed),
                                                                    ConnectionProtocol.RequestId(payload)));
        }

        /// <summary>One fight's people (jrz { f1 the fight } → jqy), for the spectator window's detail.</summary>
        public static async Task FightDetailsAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jrz = ConnectionProtocol.ReadPayload(payload, Op.Jrz);
            long fightId = 0;
            if (jrz != null)
                foreach (var field in ProtoMessage.Parse(jrz).Fields)
                    if (field.FieldNumber == 1 && field.WireType == 0) fightId = field.VarIntValue;
            var fight = FightById(fightId);
            if (fight == null) return;

            IEnumerable<Pb> PeopleOf(int team) => fight.Bando(team).Where(f => !f.EsInvocado && f.IsAlive)
                .Select(f => FightJoinProtocol.FighterInDetails(f.Id, f.Level, f.IsMonster ? f.MonsterId : 0,
                                                                f.GradeIndex + 1, f.Name));
            await WriteFrameAsync(stream, ConnectionProtocol.Answer(Op.Jqy,
                FightJoinProtocol.BuildFightDetails(fight.FightId, PeopleOf(FightInstance.Azules), PeopleOf(FightInstance.Rojos)),
                ConnectionProtocol.RequestId(payload)));
        }

        /// <summary>A spectator gone: off the fight's list. His map is put back by the save on the way out.</summary>
        public static void ForgetSpectator(GameSession me)
        {
            long fightId = me.State.SpectatingFightId;
            if (fightId == 0) return;
            if (_spectators.TryGetValue(fightId, out var watching)) watching.TryRemove(me.CharacterId, out _);
            me.State.SpectatingFightId = 0;
            me.State.SpectatorBoardPending = false;
            me.State.FightId = 0;
        }
    }
}
