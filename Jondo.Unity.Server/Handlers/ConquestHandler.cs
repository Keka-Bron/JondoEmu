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

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// A prism's life after it is beaten: weakened, then vulnerable while its zone's conquest,
    /// the King of the Hill, is fought, and then the defence's again or the winner's to take.
    /// </summary>
    /// <remarks>
    /// The cycle is the client's help (Translations 354323): a prism beaten in a fight turns
    /// weakened and becomes vulnerable at the defence's hour, 24 hours on at least
    /// (<see cref="ServerSettings.PrismWeakenedHours"/>); while vulnerable the conquest is fought
    /// in its zone, and the alliance that wins it may place its prism instead -- alone for thirty
    /// minutes, the prism then pacified a while (1149317) --; the defence winning puts its prism
    /// back to normal.
    ///
    /// The conquest is the players' of the zone with the alliance window's «Alliance fight» on:
    /// their status turns AvA aggressable (3) while they are there, which is what makes the
    /// client offer «Agredir» on an enemy (PlayerMenuMaker.AllowAggression: the subarea's prism
    /// vulnerable, both at 3, other alliances). The client opens the conquest window itself on
    /// learning its zone's prism is vulnerable (ku.fhn → fud), and the mep fills it each minute.
    /// </remarks>
    public static class ConquestHandler
    {
        /// <summary>The notices, info type 2 (datos/mensajes): «Tu territorio «$subarea{0}» es vulnerable. ¡Defiéndelo!» and the rest.</summary>
        private const int YourTerritoryVulnerable = 86;
        private const int TerritoryVulnerable = 89;
        private const int TerritoryWeakened = 94;
        private const int YourTerritoryWeakened = 95;

        /// <summary>«Tu alianza es la única que puede colocar un prisma en el territorio $subarea{0} durante {1} minuto(s).», info 0.</summary>
        private const int OnlyYoursMayPlace = 1567;

        /// <summary>«Imposible: no eres un luchador AcA válido.», info 1.</summary>
        private const int NotAnAvaFighter = 421;

        /// <summary>«No es posible activar/desactivar el modo AcA en una zona donde se desarrolla un combate de alianza.», info 1.</summary>
        internal const int NoSwitchInAWar = 399;

        /// <summary>«Se ha alcanzado el número máximo para tu rol en esta AcA. ¡Cambia de rol antes de volver!», info 1.</summary>
        private const int RoleFull = 684;

        /// <summary>«Un exploradores no puede acumular puntos en el mapa del prisma», info 1.</summary>
        private const int ScoutOnThePrismMap = 701;

        /// <summary>«No es posible teletransportarse desde una zona en conflicto con el modo AcA activado.», info 1.</summary>
        public const int NoTeleportInAWar = 427;

        /// <summary>«No puedes teletransportarte a tu merkasako mientras participas en un conflicto AcA.», info 1.</summary>
        public const int NoHavenBagInAWar = 475;

        /// <summary>«No puedes iniciar una búsqueda de koliseo cuando estás participando en un AcA.», info 1.</summary>
        public const int NoKoliseoInAWar = 713;

        /// <summary>The PvP notices' info type.</summary>
        private const int PvpNotice = 2;

        /// <summary>
        /// The scout's potion's effect (28214, «aliviar la carga que tengas en un prisma»): drunk on
        /// the prism's map, what he carried is his alliance's.
        /// </summary>
        public const int ScoutPotionEffect = 2904;

        /// <summary>
        /// The sentinel's potion's effect (14426, «atacarás automáticamente a los enemigos que se
        /// acerquen a menos de 6 casillas»): drunk in the zone, he stands guard on his map.
        /// </summary>
        public const int SentinelPotionEffect = 209;

        /// <summary>«A menos de 6 casillas»: how near an enemy passes for a sentinel on guard to attack him.</summary>
        public const int SentinelReach = 6;

        /// <summary>The resurrector's potion's effect (14443): his allies' tombs on his map rise.</summary>
        public const int ResurrectorPotionEffect = 206;

        /// <summary>The gravedigger's potion's effect (14442): his enemies' tombs on his map have their souls freed.</summary>
        public const int GravediggerPotionEffect = 648;

        /// <summary>No role shown over a character (hof.f1, hon.f4): RoleplayEntitiesService.yab draws none for -1.</summary>
        private const int NoRole = -1;

        // ─── The clock ──────────────────────────────────────────────────────────

        /// <summary>Every few seconds, from Program: the prisms whose time has come change state.</summary>
        public static Task TickAsync() => TickAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        internal static async Task TickAsync(long nowMs)
        {
            foreach (var prism in PrismStore.All())
            {
                try
                {
                    if (prism.State == PrismStore.Weakened && nowMs >= prism.NextChangeUtcMs)
                        await BecomeVulnerableAsync(prism, nowMs);
                    else if (prism.State == PrismStore.Vulnerable)
                        await ConquestMinuteAsync(prism, nowMs);
                    else if (prism.State == PrismStore.Pacified && nowMs >= prism.NextChangeUtcMs)
                    {
                        // A crystal's hours over: it is spent, and its slot empty again.
                        if (prism.CrystalGid != 0) PrismStore.SetCrystal(prism, 0, prism.CrystalsUsed);
                        await ChangeStateAsync(prism, PrismStore.Normal, 0);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Conquest] The prism of subarea {prism.SubAreaId} could not move on: {ex.Message}");
                }
            }

            foreach (var war in Conquests.All())
                if (PrismStore.InSubArea(war.SubAreaId)?.State != PrismStore.Vulnerable) Conquests.End(war.SubAreaId);
        }

        /// <summary>A prism's state changed: the world's list (ifa) and its map (jsn) learn it.</summary>
        internal static async Task ChangeStateAsync(PrismStore.Prism prism, int state, long nextChangeUtcMs)
        {
            PrismStore.SetState(prism, state, nextChangeUtcMs);
            await ShowChangedAsync(prism);
        }

        internal static async Task ShowChangedAsync(PrismStore.Prism prism)
        {
            var alliance = AllianceStore.ById(prism.AllianceId);
            if (alliance == null) return;
            await PrismHandler.BroadcastChangedAsync(prism);
            if (!FightHandler.IsGroupFighting(prism.ContextId))
                await SessionRegistry.BroadcastToMapAsync(prism.MapId, ConnectionProtocol.Push(Op.Jsn,
                    PrismProtocol.BuildShown(prism, alliance)), fightId: 0);
        }

        // ─── Weakened ───────────────────────────────────────────────────────────

        /// <summary>
        /// A prism beaten: weakened until the vulnerability, its alliance warned (95) and the
        /// attackers' too (94).
        /// </summary>
        internal static async Task WeakenAsync(PrismStore.Prism prism, IEnumerable<long> attackerAlliances, long nowMs)
        {
            long vulnerable = nowMs + Math.Max(0, ServerSettings.Current.PrismWeakenedHours) * 3_600_000L;
            await ChangeStateAsync(prism, PrismStore.Weakened, vulnerable);

            var owner = AllianceStore.ById(prism.AllianceId);
            if (owner != null)
                await AllianceHandler.TellMembersAsync(owner, Notice(YourTerritoryWeakened, prism.SubAreaId));
            foreach (long id in attackerAlliances.Distinct().Where(a => a != prism.AllianceId))
            {
                var attacker = AllianceStore.ById(id);
                if (attacker != null) await AllianceHandler.TellMembersAsync(attacker, Notice(TerritoryWeakened, prism.SubAreaId));
            }
            Program.LogDebug($"[Conquest] The prism of subarea {prism.SubAreaId} is weakened until " +
                             $"{DateTimeOffset.FromUnixTimeMilliseconds(vulnerable):u}.");
        }

        // ─── Vulnerable: the conquest ───────────────────────────────────────────

        /// <summary>
        /// The hour come: the prism vulnerable and its conquest open. Its alliance is told to
        /// defend it (86), every other alliance's fighters to take it (89), and whoever is in the
        /// zone turns aggressable.
        /// </summary>
        internal static async Task BecomeVulnerableAsync(PrismStore.Prism prism, long nowMs)
        {
            long ends = nowMs + Math.Max(Conquests.DominationToWin, ServerSettings.Current.ConquestMaxMinutes) * 60_000L;
            await ChangeStateAsync(prism, PrismStore.Vulnerable, ends);
            Conquests.Start(prism.SubAreaId, prism.AllianceId, nowMs, ends, prism.MapId);

            byte[] defend = Notice(YourTerritoryVulnerable, prism.SubAreaId);
            byte[] take = Notice(TerritoryVulnerable, prism.SubAreaId);
            foreach (var session in SessionRegistry.InWorld())
            {
                long id = session.State.CharacterId;
                var alliance = id == 0 ? null : AllianceStore.AllianceOf(id);
                if (alliance == null) continue;
                if (alliance.Id == prism.AllianceId) await session.SendAsync(defend);
                else if (AllianceStore.TakesPartInAva(id)) await session.SendAsync(take);
            }

            await RefreshZoneAsync(prism.SubAreaId);
            Program.LogDebug($"[Conquest] The prism of subarea {prism.SubAreaId} is vulnerable: its conquest is open.");
        }

        /// <summary>The conquest's minute, when it is due: the score counted and sent, and maybe its end.</summary>
        internal static async Task ConquestMinuteAsync(PrismStore.Prism prism, long nowMs)
        {
            var war = Conquests.In(prism.SubAreaId)
                      ?? Conquests.Start(prism.SubAreaId, prism.AllianceId, nowMs,
                                         prism.NextChangeUtcMs > nowMs
                                             ? prism.NextChangeUtcMs
                                             : nowMs + Math.Max(Conquests.DominationToWin, ServerSettings.Current.ConquestMaxMinutes) * 60_000L,
                                         prism.MapId);
            if (nowMs < war.NextTickUtcMs) return;

            long winner = Conquests.Tick(war, PresentIn(prism.SubAreaId), nowMs);
            if (winner == 0 && nowMs >= war.EndsUtcMs) winner = war.DefenderId;
            await SendScoreAsync(war);
            if (winner != 0) await EndAsync(prism, war, winner, nowMs);
        }

        /// <summary>
        /// The conquest over (mdz to the zone): the defence winning keeps its prism, normal again;
        /// another alliance winning, the prism falls (1581 to its alliance) and the winner has the
        /// zone alone for thirty minutes (1567).
        /// </summary>
        private static async Task EndAsync(PrismStore.Prism prism, Conquests.War war, long winnerId, long nowMs)
        {
            var winner = AllianceStore.ById(winnerId);
            var fighters = FightersIn(war.SubAreaId);

            // The conquest over, its tombs rise.
            foreach (long fallen in war.Fallen.Keys.ToList())
            {
                var session = SessionRegistry.FindByCharacter(fallen);
                if (session != null) await session.SendAsync(ConnectionProtocol.Push(Op.Kup, KothProtocol.BuildLifeState(KothProtocol.Alive)));
            }
            war.Fallen.Clear();
            if (winner != null)
            {
                byte[] end = ConnectionProtocol.Push(Op.Mdz, KothProtocol.BuildEnd(winner));
                foreach (var session in fighters) await session.SendAsync(end);
            }
            Conquests.End(war.SubAreaId);

            // The nuggets the prism held: the winner's and the dominant's (Translations 997824).
            long dominant = war.Sides.Where(s => s.AllianceId != winnerId && s.Domination > 0)
                                     .OrderByDescending(s => s.Domination).ThenByDescending(s => s.Total)
                                     .Select(s => s.AllianceId).FirstOrDefault();
            var (toWinner, toDominant) = AllianceNuggets.ShareOutAfterWar(prism.Id, winnerId, dominant);
            if (toWinner + toDominant > 0)
                Program.LogDebug($"[Conquest] The prism of subarea {prism.SubAreaId} held {toWinner + toDominant} nugget(s): " +
                                 $"{toWinner} to {winnerId}, {toDominant} to {dominant}.");

            if (winnerId == prism.AllianceId || winner == null)
            {
                await ChangeStateAsync(prism, PrismStore.Normal, 0);
                Program.LogDebug($"[Conquest] The defence keeps subarea {prism.SubAreaId}.");
            }
            else
            {
                PrismStore.Remove(prism);
                await PrismHandler.GoneAsync(prism);
                var loser = AllianceStore.ById(prism.AllianceId);
                if (loser != null)
                    await AllianceHandler.TellMembersAsync(loser, ConnectionProtocol.Push(Op.Lqn,
                        ConnectionProtocol.BuildSystemMessage(PrismHandler.OneOfYoursDestroyed)));

                Conquests.GivePriority(war.SubAreaId, winnerId, nowMs + Conquests.PriorityMinutes * 60_000L);
                await AllianceHandler.TellMembersAsync(winner, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(OnlyYoursMayPlace, war.SubAreaId.ToString(),
                                                          Conquests.PriorityMinutes.ToString())));
                Program.LogDebug($"[Conquest] «{winner.Name}» conquers subarea {war.SubAreaId}.");
            }

            await RefreshZoneAsync(war.SubAreaId, fighters);
        }

        /// <summary>The score (mep) to the zone's fighters.</summary>
        private static async Task SendScoreAsync(Conquests.War war)
        {
            byte[] score = ScoreFrame(war);
            foreach (var session in FightersIn(war.SubAreaId)) await session.SendAsync(score);
        }

        private static byte[] ScoreFrame(Conquests.War war)
            => ConnectionProtocol.Push(Op.Mep, KothProtocol.BuildUpdate(war, AllianceStore.ById, AllianceData.DefaultKothRole));

        // ─── Who is there ───────────────────────────────────────────────────────

        /// <summary>Where a session stands in the world: its fight's map when it fights.</summary>
        private static long WhereIs(GameSession session)
            => session.State.IsInFight && session.State.RoleplayMapId != 0 ? session.State.RoleplayMapId : session.State.MapId;

        private static int SubAreaOf(long mapId) => MapManager.GetMapInfo(mapId)?.SubAreaId ?? 0;

        /// <summary>Whether a subarea's prism is vulnerable: a conquest is fought there.</summary>
        public static bool IsWarZone(int subAreaId)
            => subAreaId > 0 && PrismStore.InSubArea(subAreaId)?.State == PrismStore.Vulnerable;

        /// <summary>Whether a connected character stands in a conquest's zone.</summary>
        public static bool InWarZone(long characterId)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            return session != null && IsWarZone(SubAreaOf(WhereIs(session)));
        }

        /// <summary>
        /// Whether a connected character fights in the conquest of the zone he stands in: there with
        /// «Alliance fight» on, in an alliance, and let in -- his alliance had room for his role.
        /// </summary>
        public static bool FightsInAWar(long characterId)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            return session != null && Admit(session) && Conquests.In(SubAreaOf(WhereIs(session)))?.Fallen.ContainsKey(characterId) != true;
        }

        /// <summary>Lets a session into its zone's conquest, if it may come in; whether it is in.</summary>
        private static bool Admit(GameSession session)
        {
            long id = session.State.CharacterId;
            int subArea = SubAreaOf(WhereIs(session));
            var war = IsWarZone(subArea) ? Conquests.In(subArea) : null;
            if (war == null || id == 0 || !AllianceStore.TakesPartInAva(id)) return false;
            if (war.Admitted.ContainsKey(id)) return true;
            var alliance = AllianceStore.AllianceOf(id);
            return alliance != null && Conquests.Admit(war, id, alliance.Id, KothRoles.RoleOf(session));
        }

        /// <summary>The role a fighter of a conquest holds there.</summary>
        private static int RoleIn(Conquests.War war, long characterId)
            => war != null && war.Admitted.TryGetValue(characterId, out var admitted) ? admitted.Role : NoRole;

        /// <summary>The zone's fighters: in the world on its maps, in an alliance, with «Alliance fight» on, let in.</summary>
        private static List<GameSession> FightersIn(int subAreaId)
            => SessionRegistry.InWorld()
                              .Where(s => s.State.CharacterId != 0 && SubAreaOf(WhereIs(s)) == subAreaId && Admit(s))
                              .ToList();

        /// <summary>Who is there for the minute's count: each fighter of the zone, his alliance, map and role.</summary>
        internal static List<Conquests.Presence> PresentIn(int subAreaId)
        {
            var war = Conquests.In(subAreaId);
            var present = new List<Conquests.Presence>();
            foreach (var session in FightersIn(subAreaId))
            {
                long id = session.State.CharacterId;
                if (war != null && war.Admitted.TryGetValue(id, out var admitted) && !war.Fallen.ContainsKey(id))
                    present.Add(new Conquests.Presence(admitted.Alliance, WhereIs(session), id, admitted.Role));
            }
            return present;
        }

        // ─── The fighters' status ───────────────────────────────────────────────

        /// <summary>The status each character last heard (hon), to tell him only when it changes.</summary>
        private static readonly ConcurrentDictionary<long, int> _heard = new();

        /// <summary>
        /// A character on a new map: if «Alliance fight» is on and his status changed -- he walked
        /// into a conquest's zone or out of it -- he hears it (hon) and is drawn again; walking in,
        /// the zone's prism (ifa), which opens the conquest window, and its score (mep).
        /// </summary>
        public static async Task OnMapEnteredAsync(NetworkStream stream, long characterId, int subAreaId)
        {
            if (characterId == 0 || !AllianceStore.TakesPartInAva(characterId)) return;
            if (AllianceStore.AllianceOf(characterId) == null) return;

            // Out of a zone, out of its conquest: his role's place is free again, and a tomb that
            // left is alive again.
            foreach (var other in Conquests.All())
            {
                if (other.SubAreaId == subAreaId) continue;
                if (other.Fallen.ContainsKey(characterId))
                    await SendAsync(stream, ConnectionProtocol.Push(Op.Kup, KothProtocol.BuildLifeState(KothProtocol.Alive)));
                Conquests.Release(other, characterId);
            }

            var war = IsWarZone(subAreaId) ? Conquests.In(subAreaId) : null;
            int status = AllianceProtocol.AggressableStatus(characterId);
            if (war != null) await OnWalkedAsync(SessionRegistry.FindByCharacter(characterId));
            if (war != null && status != AllianceProtocol.AvaAggressable && !war.Admitted.ContainsKey(characterId) && !war.Freed.ContainsKey(characterId))
                await SendAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, RoleFull)));

            // A scout on the prism's map gathers nothing there.
            var session = SessionRegistry.FindByCharacter(characterId);
            if (war != null && session != null && RoleIn(war, characterId) == KothRoles.Scout && WhereIs(session) == war.PrismMapId)
                await SendAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, ScoutOnThePrismMap)));

            int before = _heard.TryGetValue(characterId, out int heard) ? heard : AllianceProtocol.AvaNotAggressable;
            if (status == before) return;
            _heard[characterId] = status;
            await SendAsync(stream, ConnectionProtocol.Push(Op.Hon,
                AllianceProtocol.BuildAggressableStatus(status, RoleIn(war, characterId))));

            if (status != AllianceProtocol.AvaAggressable) return;
            var prism = PrismStore.InSubArea(subAreaId);
            var owner = prism == null ? null : AllianceStore.ById(prism.AllianceId);
            if (owner != null) await SendAsync(stream, PrismHandler.ChangedFor(prism, owner, characterId));
            if (war != null)
            {
                await SendAsync(stream, ScoreFrame(war));
                await SendRolesAsync(war);
            }
        }

        /// <summary>
        /// Every fighter of a conquest learns the others' status and role (hoh; eht.gaf draws the
        /// role over each one): his allies', and his enemies' only if he is a seer, who «puede ver
        /// de modo permanente los roles de los adversarios» (Translations 1119557).
        /// </summary>
        private static async Task SendRolesAsync(Conquests.War war)
        {
            var fighters = FightersIn(war.SubAreaId);
            foreach (var viewer in fighters)
            {
                long me = viewer.State.CharacterId;
                if (!war.Admitted.TryGetValue(me, out var mine)) continue;
                var hoh = Pb.New();
                foreach (var other in fighters)
                {
                    long them = other.State.CharacterId;
                    if (them == me || !war.Admitted.TryGetValue(them, out var theirs)) continue;
                    int role = theirs.Alliance == mine.Alliance || mine.Role == KothRoles.Seer ? theirs.Role : NoRole;
                    int status = war.Fallen.ContainsKey(them) ? AllianceProtocol.AvaNotAggressable : AllianceProtocol.AvaAggressable;
                    hoh.Msg(1, AllianceProtocol.StatusOf(them, status, role));
                }
                await viewer.SendAsync(ConnectionProtocol.Push(Op.Hoh, hoh.Build()));
            }
        }

        /// <summary>A conquest opened or closed: the zone's fighters hear their new status and are drawn again.</summary>
        private static async Task RefreshZoneAsync(int subAreaId, IEnumerable<GameSession> fighters = null)
        {
            var war = Conquests.In(subAreaId);
            foreach (var session in (fighters ?? FightersIn(subAreaId)).ToList())
            {
                long id = session.State.CharacterId;
                int status = AllianceProtocol.AggressableStatus(id);
                _heard[id] = status;
                await session.SendAsync(ConnectionProtocol.Push(Op.Hon,
                    AllianceProtocol.BuildAggressableStatus(status, RoleIn(war, id))));
                if (session.State.IsInFight) continue;

                var character = DatabaseManager.GetCharacterById(id);
                if (character == null) continue;
                byte[] jsn = ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                    character, session.State.CellId, session.State.Orientation, session.AccountId));
                await session.SendAsync(jsn);
                await SessionRegistry.BroadcastToMapAsync(session.MapId, jsn, session.Id);
            }
            if (war != null) await SendRolesAsync(war);
        }

        /// <summary>
        /// Whether the session being served takes part in a conflict -- «Alliance fight» on, in a
        /// vulnerable prism's zone --, which keeps it there: it hears why (<paramref name="refusal"/>,
        /// info 1) and true is returned.
        /// </summary>
        public static async Task<bool> HoldsInAsync(NetworkStream stream, int refusal)
        {
            long id = SessionContext.State.CharacterId;
            if (id == 0 || !AllianceStore.TakesPartInAva(id) || !InWarZone(id)) return false;
            await SendAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, refusal)));
            return true;
        }

        /// <summary>A character gone from the world: his next status is heard afresh, and his role's place is free.</summary>
        public static void Forget(long characterId)
        {
            _heard.TryRemove(characterId, out _);
            foreach (var war in Conquests.All()) Conquests.Release(war, characterId);
        }

        // ─── Aggressions ────────────────────────────────────────────────────────

        /// <summary>
        /// «Agredir» on a player (the hph without its f2): in a conquest's zone, between fighters
        /// of two alliances, the fight starts at once, an aggression. Elsewhere it is the cities'
        /// war: with PvP on, an enemy of the other city showing his wings (Alignments.MayAggress).
        /// Whoever is neither hears so (421).
        /// </summary>
        public static async Task AggressAsync(NetworkStream stream, long targetId)
        {
            var me = SessionContext.Current;
            long myId = me.State.CharacterId;
            var target = SessionRegistry.FindByCharacter(targetId);
            if (myId == 0 || target == null || !target.IsInWorld || target.MapId != me.State.MapId) return;
            if (me.State.IsInFight || me.State.FightId != 0 || target.State.IsInFight || target.State.FightId != 0) return;

            var mine = AllianceStore.AllianceOf(myId);
            if (mine == null || !AllianceStore.TakesPartInAva(myId) || !IsWarZone(SubAreaOf(me.State.MapId)) || !FightsInAWar(myId))
            {
                // The cities' war is fought where the map allows an aggression -- not in Astrub, nor
                // inside the cities -- which is also where the client offers «Agredir».
                if (Alignments.MayAggress(myId, me.State.CharacterLevel, targetId, target.State.CharacterLevel)
                    && !Alignments.MapAllowsAggression(me.State.MapId))
                {
                    await SendAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                        ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.RequirementsNotMet)));
                    return;
                }
                if (Alignments.MayAggress(myId, me.State.CharacterLevel, targetId, target.State.CharacterLevel))
                {
                    Program.LogDebug($"[Alignment] {myId} attacks {targetId} of the other city.");
                    await FightHandler.InitiatePvpAsync(new List<GameSession> { me }, new List<GameSession> { target },
                                                        me.State.MapId, rules: FightRules.Agresion);
                    return;
                }
                await SendAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, NotAnAvaFighter)));
                return;
            }
            var theirs = AllianceStore.AllianceOf(targetId);
            if (theirs == null || theirs.Id == mine.Id || !FightsInAWar(targetId)) return;

            Program.LogDebug($"[Conquest] {myId} of «{mine.Name}» attacks {targetId} of «{theirs.Name}».");
            await FightHandler.InitiatePvpAsync(new List<GameSession> { me }, new List<GameSession> { target },
                                                me.State.MapId, rules: FightRules.Agresion);
        }

        /// <summary>An aggression over: the zone's conquest counts it -- points won and lost -- and its score goes out.</summary>
        internal static async Task AggressionEndedAsync(FightInstance fight, bool attackersWon)
        {
            var war = Conquests.In(SubAreaOf(fight.RoleplayMapId));
            if (war == null) return;

            List<(long, int)> AlliancesOf(IEnumerable<Fighter> side)
                => side.Where(f => !f.IsMonster && !f.EsInvocado)
                       .Select(f => (AllianceStore.AllianceOf(f.Id)?.Id ?? 0,
                                     war.Admitted.TryGetValue(f.Id, out var admitted) ? admitted.Role : AllianceData.DefaultKothRole))
                       .Where(a => a.Item1 != 0)
                       .ToList();

            var winners = AlliancesOf(attackersWon ? fight.Azul : fight.Rojo);
            var losers = AlliancesOf(attackersWon ? fight.Rojo : fight.Azul);
            Conquests.FightOver(war, winners, losers);

            // The losers fall: a tomb where they fought, or, the champion, his soul freed at once
            // -- «cuando lo derrotan, su alma se libera, lo que impide su reencarnación in situ».
            foreach (var loser in (attackersWon ? fight.Rojo : fight.Azul).Where(f => !f.IsMonster && !f.EsInvocado))
            {
                if (StaysAsTomb(fight, loser.Id)) await FallAsync(war, loser.Id);
                else if (war.Admitted.ContainsKey(loser.Id))
                {
                    Conquests.Release(war, loser.Id);
                    war.Freed[loser.Id] = true;
                }
            }
            await SendScoreAsync(war);
            await SendRolesAsync(war);
        }

        // ─── The fallen ─────────────────────────────────────────────────────────

        /// <summary>
        /// Whether one who lost this fight stays where he fought, a tomb, instead of going back to
        /// his save point: an aggression of a running conquest, a fighter let into it, and no
        /// champion, whose soul is freed when he is beaten.
        /// </summary>
        public static bool StaysAsTomb(FightInstance fight, long characterId)
        {
            if (fight == null || fight.Reglas != FightRules.Agresion) return false;
            var war = Conquests.In(SubAreaOf(fight.RoleplayMapId));
            return war != null && war.Admitted.TryGetValue(characterId, out var admitted) && admitted.Role != KothRoles.Champion;
        }

        /// <summary>
        /// A fighter falls: a tomb (kup 1, the death popup), out of the count and of the fights --
        /// not aggressable --, what a scout carried lost and a sentinel's guard over.
        /// </summary>
        private static async Task FallAsync(Conquests.War war, long characterId)
        {
            if (!war.Admitted.TryGetValue(characterId, out var admitted)) return;
            war.Fallen[characterId] = admitted.Alliance;
            war.ScoutCarried.TryRemove(characterId, out _);
            war.Sentinels.TryRemove(characterId, out _);

            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null) return;
            var (savePoint, _) = FightHandler.SavePointOf(characterId);
            await session.SendAsync(ConnectionProtocol.Push(Op.Kup, KothProtocol.BuildLifeState(KothProtocol.Tomb, savePoint)));
            _heard[characterId] = AllianceProtocol.AvaNotAggressable;
            await session.SendAsync(ConnectionProtocol.Push(Op.Hon,
                AllianceProtocol.BuildAggressableStatus(AllianceProtocol.AvaNotAggressable)));
            Program.LogDebug($"[Conquest] {session.State.CharacterName} falls in subarea {war.SubAreaId}: a tomb.");
        }

        /// <summary>
        /// A tomb's «Resucitar» on the death popup, or «Liberar alma» on his menu (kug): his soul is
        /// freed. He is alive again (kup 0) at his save point, out of this conquest for good.
        /// </summary>
        public static async Task FreeSoulAsync(NetworkStream stream)
        {
            var me = SessionContext.Current;
            long id = me.State.CharacterId;
            var war = Conquests.All().FirstOrDefault(w => w.Fallen.ContainsKey(id));
            if (war == null) return;
            await FreeAsync(war, me);
        }

        /// <summary>A tomb's soul freed: alive again at his save point, never back in this conquest.</summary>
        private static async Task FreeAsync(Conquests.War war, GameSession session)
        {
            long id = session.State.CharacterId;
            Conquests.Release(war, id);
            war.Freed[id] = true;
            await session.SendAsync(ConnectionProtocol.Push(Op.Kup, KothProtocol.BuildLifeState(KothProtocol.Alive)));
            var (savePoint, _) = FightHandler.SavePointOf(id);
            using (SessionContext.Push(session)) await WorldMoveHandler.TeleportAsync(session.Stream, savePoint);
            Program.LogDebug($"[Conquest] The soul of {session.State.CharacterName} is freed from subarea {war.SubAreaId}.");
        }

        /// <summary>
        /// The resurrector's potion: his allies' tombs on his map rise, back in the count and the
        /// fights. With none there, nothing is spent.
        /// </summary>
        private static async Task<bool> ResurrectorPotionAsync(NetworkStream stream, Equipment.Item item)
        {
            var (war, me, mine) = RoleHolder(KothRoles.Resurrector);
            var risen = war == null ? new List<GameSession>()
                : TombsAround(war, me, alliance => alliance == mine);
            if (risen.Count == 0)
            {
                await SendAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.RequirementsNotMet)));
                return true;
            }
            if (!await ItemUseHandler.SpendOneAsync(stream, item.Uid)) return true;
            foreach (var tomb in risen)
            {
                long id = tomb.State.CharacterId;
                war.Fallen.TryRemove(id, out _);
                await tomb.SendAsync(ConnectionProtocol.Push(Op.Kup, KothProtocol.BuildLifeState(KothProtocol.Alive)));
                _heard[id] = AllianceProtocol.AvaAggressable;
                await tomb.SendAsync(ConnectionProtocol.Push(Op.Hon,
                    AllianceProtocol.BuildAggressableStatus(AllianceProtocol.AvaAggressable, RoleIn(war, id))));
            }
            Program.LogDebug($"[Conquest] Resurrector {me.State.CharacterName} raises {risen.Count} tomb(s).");
            await SendRolesAsync(war);
            return true;
        }

        /// <summary>
        /// The gravedigger's potion: his enemies' tombs on his map have their souls freed -- out of
        /// this conquest for good, sent back to their save points. With none there, nothing is spent.
        /// </summary>
        private static async Task<bool> GravediggerPotionAsync(NetworkStream stream, Equipment.Item item)
        {
            var (war, me, mine) = RoleHolder(KothRoles.Gravedigger);
            var freed = war == null ? new List<GameSession>()
                : TombsAround(war, me, alliance => alliance != mine);
            if (freed.Count == 0)
            {
                await SendAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.RequirementsNotMet)));
                return true;
            }
            if (!await ItemUseHandler.SpendOneAsync(stream, item.Uid)) return true;
            foreach (var tomb in freed) await FreeAsync(war, tomb);
            Program.LogDebug($"[Conquest] Gravedigger {me.State.CharacterName} frees {freed.Count} soul(s).");
            await SendRolesAsync(war);
            return true;
        }

        /// <summary>The conquest of the session being served, if it fights there with this role, and its alliance.</summary>
        private static (Conquests.War War, GameSession Me, long Alliance) RoleHolder(int role)
        {
            var me = SessionContext.Current;
            long id = me.State.CharacterId;
            var war = Conquests.In(SubAreaOf(WhereIs(me)));
            if (war == null || !FightsInAWar(id) || RoleIn(war, id) != role) return (null, me, 0);
            return (war, me, war.Admitted[id].Alliance);
        }

        /// <summary>The tombs on the map of the session being served whose alliance passes the test.</summary>
        private static List<GameSession> TombsAround(Conquests.War war, GameSession me, Func<long, bool> alliance)
            => war.Fallen.Where(f => alliance(f.Value))
                         .Select(f => SessionRegistry.FindByCharacter(f.Key))
                         .Where(s => s != null && s.State.MapId == me.State.MapId)
                         .ToList();

        // ─── The role potions ───────────────────────────────────────────────────

        /// <summary>
        /// A role potion used -- one whose criterion asks for a role's certificate, «PO=27659» --:
        /// the scout's, the sentinel's, the resurrector's and the gravedigger's. Only one holding
        /// the role and fighting in a conquest may drink one; anybody else hears «No cumples los
        /// requisitos». False when the item is no role potion.
        /// </summary>
        public static async Task<bool> UseRolePotionAsync(NetworkStream stream, Equipment.Item item)
        {
            string criterion = DatabaseManager.GetItemTemplateCriterion(item.Template) ?? "";
            bool forARole = AllianceData.KothRoles.Any(r => r.Certificate != 0
                && System.Text.RegularExpressions.Regex.IsMatch(criterion, $@"\bPO={r.Certificate}\b"));
            if (!forARole) return false;

            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            if (effects.Any(e => e.EffectId == ScoutPotionEffect)) return await ScoutPotionAsync(stream, item);
            if (effects.Any(e => e.EffectId == SentinelPotionEffect)) return await SentinelPotionAsync(stream, item);
            if (effects.Any(e => e.EffectId == ResurrectorPotionEffect)) return await ResurrectorPotionAsync(stream, item);
            if (effects.Any(e => e.EffectId == GravediggerPotionEffect)) return await GravediggerPotionAsync(stream, item);
            await SendAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.RequirementsNotMet)));
            return true;
        }

        /// <summary>
        /// The sentinel's potion: spent, and he stands guard on his map until he leaves it or the
        /// conquest ends (<see cref="OnWalkedAsync"/>).
        /// </summary>
        private static async Task<bool> SentinelPotionAsync(NetworkStream stream, Equipment.Item item)
        {
            var me = SessionContext.Current;
            long id = me.State.CharacterId;
            var war = Conquests.In(SubAreaOf(WhereIs(me)));
            if (war == null || !FightsInAWar(id) || RoleIn(war, id) != KothRoles.Sentinel)
            {
                await SendAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.RequirementsNotMet)));
                return true;
            }
            if (!await ItemUseHandler.SpendOneAsync(stream, item.Uid)) return true;
            war.Sentinels[id] = me.State.MapId;
            Program.LogDebug($"[Conquest] Sentinel {me.State.CharacterName} stands guard on map {me.State.MapId}.");
            return true;
        }

        /// <summary>
        /// A fighter of a conquest walked: an enemy sentinel on guard on his map, free and less than
        /// <see cref="SentinelReach"/> cells away, attacks him -- «ataca automáticamente a
        /// cualquier enemigo que pase a menos de 6 casillas de él» (Translations 1119557).
        /// </summary>
        public static async Task OnWalkedAsync(GameSession mover)
        {
            if (mover == null || mover.State.IsInFight || mover.State.FightId != 0) return;
            long id = mover.State.CharacterId;
            long map = mover.State.MapId;
            var war = Conquests.In(SubAreaOf(map));
            if (war == null || war.Sentinels.IsEmpty || !war.Admitted.TryGetValue(id, out var mine) || war.Fallen.ContainsKey(id)) return;

            foreach (var guard in war.Sentinels.ToArray())
            {
                if (guard.Value != map || guard.Key == id) continue;
                if (!war.Admitted.TryGetValue(guard.Key, out var theirs) || theirs.Alliance == mine.Alliance
                    || war.Fallen.ContainsKey(guard.Key)) continue;
                var sentinel = SessionRegistry.FindByCharacter(guard.Key);
                if (sentinel == null || sentinel.State.MapId != map)
                {
                    war.Sentinels.TryRemove(guard.Key, out _);
                    continue;
                }
                if (sentinel.State.IsInFight || sentinel.State.FightId != 0) continue;
                if (Jondo.Unity.World.Maps.MapGeometry.Distance(sentinel.State.CellId, mover.State.CellId) >= SentinelReach) continue;

                Program.LogDebug($"[Conquest] Sentinel {sentinel.State.CharacterName} attacks {mover.State.CharacterName}.");
                await FightHandler.InitiatePvpAsync(new List<GameSession> { sentinel }, new List<GameSession> { mover },
                                                    map, rules: FightRules.Agresion);
                return;
            }
        }

        /// <summary>
        /// The scout's potion: by a scout of a conquest, on its prism's map, what he carried goes to
        /// his alliance's prism points and the potion is spent; elsewhere in the zone, 701.
        /// </summary>
        private static async Task<bool> ScoutPotionAsync(NetworkStream stream, Equipment.Item item)
        {
            var me = SessionContext.Current;
            long id = me.State.CharacterId;
            var war = Conquests.In(SubAreaOf(WhereIs(me)));
            if (war == null || !FightsInAWar(id) || RoleIn(war, id) != KothRoles.Scout)
            {
                await SendAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.RequirementsNotMet)));
                return true;
            }
            if (WhereIs(me) != war.PrismMapId)
            {
                await SendAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, ScoutOnThePrismMap)));
                return true;
            }
            if (!war.ScoutCarried.TryGetValue(id, out int carried) || carried <= 0) return true;
            if (!await ItemUseHandler.SpendOneAsync(stream, item.Uid)) return true;

            long handed = Conquests.HandIn(war, id, war.Admitted[id].Alliance);
            Program.LogDebug($"[Conquest] Scout {me.State.CharacterName} hands in {handed} point(s) on the prism's map.");
            await SendScoreAsync(war);
            return true;
        }

        // ─── Placing after a conquest ───────────────────────────────────────────

        /// <summary>
        /// The state a new prism is born in: pacified when its alliance won the zone's conquest --
        /// for the setting's hours over the prisms it holds -- and the priority spent; normal otherwise.
        /// </summary>
        internal static (int State, long NextChangeUtcMs) BirthOf(long allianceId, int subAreaId, long nowMs)
        {
            var priority = Conquests.PriorityIn(subAreaId, nowMs);
            if (priority == null || priority.AllianceId != allianceId) return (PrismStore.Normal, 0);
            Conquests.TakePriority(subAreaId);
            long pacified = Conquests.InvulnerabilityMs(ServerSettings.Current.ConquestInvulnerabilityHours,
                                                        PrismStore.OfAlliance(allianceId).Count + 1);
            return pacified <= 0 ? (PrismStore.Normal, 0) : (PrismStore.Pacified, nowMs + pacified);
        }

        private static byte[] Notice(int id, int subAreaId)
            => ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(PvpNotice, id, subAreaId.ToString()));

        private static async Task SendAsync(NetworkStream stream, byte[] frame)
        {
            if (stream == null) return;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
        }
    }
}
