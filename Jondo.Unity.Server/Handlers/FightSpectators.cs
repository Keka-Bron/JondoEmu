using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Game = Jondo.Unity.Protocol.Game;
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

        /// <summary>
        /// The fight that character is in, watched from wherever one
        /// stands -- the lists that offer it are the friends', the guild's and the alliance's.
        /// </summary>
        internal static async Task WatchCharacterAsync(GameSession session, Game.FightSpectatePlayerRequest request)
        {
            var fight = FightOf(request.PlayerId);
            if (fight == null) return;
            // The dispatcher owns the session turn. Recheck membership after taking the fight turn.
            await InTurnAsync(fight, async () =>
            {
                if (FightById(fight.FightId) != fight || fight.Gone.Contains(request.PlayerId)
                    || !fight.Todos.Any(f => f.Id == request.PlayerId)) return;
                await WatchAsync(session, fight, fromTheMap: false);
            });
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
            await me.Map.SendAsync(new Jondo.Unity.Protocol.Game.ContextRemoveElementEvent(me.CharacterId),
                me.Id, fightId: 0);

            state.RoleplayMapId = state.MapId;
            state.RoleplayCellId = state.CellId;
            state.SpectatingFightId = fight.FightId;
            state.SpectatorBoardPending = true;
            state.FightId = fight.FightId;
            state.MapId = fight.MapId;

            await me.SendAsync(new Game.ContextDestroyEvent());
            await me.SendAsync(new Game.ContextCreationEvent(Game.GameContext.Fight));
            await me.SendAsync(new Game.MapCurrentEvent(fight.MapId));
            await me.SendAsync(ServerTimeMessages.Snapshot());
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
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Jrk, FightHandler.FightMapOf(fight)));
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

                await SendLeaveContextAsync(me);
                await me.SendAsync(new Game.MapCurrentEvent(state.MapId));
                await me.SendAsync(ServerTimeMessages.Snapshot());
                await BackOnTheMapAsync(me);
                Program.LogDebug($"[Fight] {state.CharacterName} stops watching fight #{fightId}.");
            }
        }

        // ─── The spectator window's lists ───────────────────────────────────────

        private static async Task SendLeaveContextAsync(GameSession session)
        {
            await session.SendAsync(new Game.FightEndKickedEvent());
            await session.SendAsync(new Game.ContextDestroyEvent());
            await session.SendAsync(new Game.ContextCreationEvent(Game.GameContext.RolePlay));
        }

        /// <summary>
        /// The map's fights (jrv → jyc), as SpectatorUi lists them: each with its kind, its start,
        /// whether it takes spectators, and each side's size, mean level and options.
        /// </summary>
        internal static async Task RunningFightsAsync(GameSession session, int uid)
        {
            var listed = new List<Game.RunningFightInformation>();
            foreach (var fight in FightsOnMap(session.MapId))
            {
                await InTurnAsync(fight, () =>
                {
                    if (FightById(fight.FightId) == fight && fight.State != FightState.Ended)
                    {
                        bool secret = fight.OptionOn(FightInstance.Azules, FightInstance.OptionSecret)
                                      || fight.OptionOn(FightInstance.Rojos, FightInstance.OptionSecret);
                        listed.Add(new(null, new(checked((int)fight.FightId), FightTypeOf(fight),
                            fight.StartedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                            secret, [ListTeam(fight, FightInstance.Azules), ListTeam(fight, FightInstance.Rojos)])));
                    }
                    return Task.CompletedTask;
                });
            }
            await session.SendAsync(new Game.MapRunningFightsResponse(listed), uid);
        }

        private static IEnumerable<Fighter> ListedFighters(FightInstance fight, int team)
            => fight.Bando(team).Where(f => !f.EsInvocado && f.IsAlive);

        private static Game.FightTeamsWithOptions ListTeam(FightInstance fight, int team)
        {
            var (type, leader) = MapTeamLeader(fight, team);
            var side = ListedFighters(fight, team).ToArray();
            int mean = side.Length == 0 ? 0 : (int)side.Average(f => f.Level);
            // The spectator window consumes the light case, not the map's member-list case.
            var light = new Game.FightTeamLightInformation(false, false, side.Length, false, false, false, mean);
            return new(new(type, leader, Game.Alignment.Without, TeamIdentity(team), 0,
                           new Game.FightTeamInformationCaseLightInformation(light)),
                       new(false, fight.OptionOn(team, FightInstance.OptionPartyOnly), false, false));
        }

        /// <summary>One fight's people, for the spectator window's detail.</summary>
        internal static async Task FightDetailsAsync(GameSession session, Game.MapRunningFightDetailsRequest request, int uid)
        {
            var fight = FightById(request.FightId);
            if (fight == null) return;

            Game.MapRunningFightDetailsResponse? details = null;
            await InTurnAsync(fight, () =>
            {
                if (FightById(request.FightId) == fight)
                    details = new(ListedFighters(fight, FightInstance.Rojos).Select(FighterDetail).ToArray(),
                                  ListedFighters(fight, FightInstance.Azules).Select(FighterDetail).ToArray());
                return Task.CompletedTask;
            });
            if (details != null) await session.SendAsync(details, uid);
        }

        private static Game.FighterLightInformation FighterDetail(Fighter fighter)
        {
            const int monsterBreed = -2; // The spectator portrait uses the monster ID for this breed.
            if (fighter.TaxCollectorFirstName != 0 || fighter.TaxCollectorLastName != 0)
                return new(Game.Gender.Male, fighter.IsAlive, fighter.Id, 0, fighter.Level, fighter.Wave,
                    new Game.FighterLightInformationCaseTaxCollectorInformation(
                        new(fighter.TaxCollectorFirstName, fighter.TaxCollectorLastName)));
            if (fighter.IsCompanion)
                return new(Game.Gender.Male, fighter.IsAlive, fighter.Id, fighter.Breed, fighter.Level, fighter.Wave,
                    new Game.FighterLightInformationCaseEntityInformation(new(fighter.DrivenBy, fighter.CompanionId)));
            if (fighter.IsMonster && fighter.MonsterId != 0)
                return new(Game.Gender.Male, fighter.IsAlive, fighter.Id, monsterBreed, fighter.Level, fighter.Wave,
                    new Game.FighterLightInformationCaseMonsterInformation(new(fighter.MonsterId)));

            // A character's gender comes from its stored row; a bot carries its own.
            int sex = fighter.IsMonster ? 0 : fighter.IsBot ? fighter.Sex : DatabaseManager.GetCharacterById(fighter.Id)?.Sex ?? 0;
            return new(sex == 1 ? Game.Gender.Female : Game.Gender.Male, fighter.IsAlive, fighter.Id,
                fighter.Breed, fighter.Level, fighter.Wave, new Game.FighterLightInformationCaseNamedInformation(new(fighter.Name ?? "")));
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
