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
    /// The fights an alliance may come to defend -- its tax collectors' and its prisms' --, as
    /// its window's fights tab shows them, and the way in for its defenders.
    /// </summary>
    /// <remarks>
    /// The client keeps them in its alliance fights service (ffc): a list it is given whole when
    /// the tab opens (mdu, after a meq) and then moved one fight (meh, met, mdv) or one fighter (mdx,
    /// meu) at a time. Each fight's line has its defenders -- the tax collector or the prism first,
    /// whose look the line shows -- its attackers and a countdown while it waits; a free slot
    /// among the defenders is the button to join (mer), one's own to leave (mea). Every member
    /// connected is told every move, so the list is right whenever the tab opens.
    /// </remarks>
    public static class AllianceFights
    {
        /// <summary>A fight its alliance may defend.</summary>
        internal sealed class Defence
        {
            /// <summary>A tax collector's or a prism's: the lci's f1.</summary>
            public int Kind { get; init; }

            /// <summary>The map it is about, what the client keys the fight by (the lci's f2).</summary>
            public long MapId { get; init; }

            public long AllianceId { get; init; }
            public long FightId { get; init; }
            public long PlacementEndsMs { get; init; }
            public bool Fighting { get; set; }

            /// <summary>What it is that is defended, as the first defender of its line.</summary>
            public Func<Pb> Defended { get; init; }
        }

        private static readonly ConcurrentDictionary<long, Defence> _open = new();

        internal static Defence Of(FightInstance fight) => fight != null && _open.TryGetValue(fight.FightId, out var d) ? d : null;

        internal static IEnumerable<Defence> All => _open.Values;

        /// <summary>
        /// A fight opening against something an alliance holds: its side will take the alliance's
        /// members, and its line will be in the alliance's fights.
        /// </summary>
        internal static Defence Open(FightInstance fight, int kind, long mapId, long allianceId, Func<Pb> defended)
        {
            fight.DefendingAllianceId = allianceId;
            var defence = new Defence
            {
                Kind = kind,
                MapId = mapId,
                AllianceId = allianceId,
                FightId = fight.FightId,
                PlacementEndsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + fight.Reglas.RelojDeColocacion * 100L,
                Defended = defended,
            };
            _open[fight.FightId] = defence;
            return defence;
        }

        /// <summary>
        /// The fight in the alliance's list: the fight (meh), what is defended as its first
        /// defender and each attacker (mdx).
        /// </summary>
        internal static async Task AnnounceAsync(FightInstance fight)
        {
            var defence = Of(fight);
            var alliance = defence == null ? null : AllianceStore.ById(defence.AllianceId);
            if (alliance == null) return;
            var key = KeyOf(defence);

            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Meh,
                TaxCollectorProtocol.BuildFightAdded(key, PhaseOf(defence))));
            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Mdx,
                TaxCollectorProtocol.BuildFighterAdded(defence.Defended(), key, TaxCollectorProtocol.SideDefenders)));
            foreach (var attacker in fight.Azul.Where(f => !f.IsMonster && !f.EsInvocado).ToList())
            {
                await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Mdx,
                    TaxCollectorProtocol.BuildFighterAdded(PersonOf(attacker.Id), key, TaxCollectorProtocol.SideAttackers)));
            }
        }

        /// <summary>The placement is over: the line stops waiting (met).</summary>
        internal static async Task StartedAsync(FightInstance fight)
        {
            var defence = Of(fight);
            if (defence == null) return;
            defence.Fighting = true;
            var alliance = AllianceStore.ById(defence.AllianceId);
            if (alliance == null) return;
            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Met,
                TaxCollectorProtocol.BuildPhase(KeyOf(defence), PhaseOf(defence))));
        }

        /// <summary>The fight is over: off the list (mdv). Returns what it was, or null.</summary>
        internal static async Task<Defence> CloseAsync(FightInstance fight)
        {
            if (fight == null || !_open.TryRemove(fight.FightId, out var defence)) return null;
            var alliance = AllianceStore.ById(defence.AllianceId);
            if (alliance != null)
            {
                await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Mdv,
                    TaxCollectorProtocol.BuildFightRemoved(KeyOf(defence))));
            }
            return defence;
        }

        /// <summary>Whether this character is of the alliance that defends this fight.</summary>
        internal static bool IsDefender(FightInstance fight, long characterId)
            => fight.DefendingAllianceId != 0 && AllianceStore.AllianceOf(characterId)?.Id == fight.DefendingAllianceId;

        /// <summary>
        /// The fights tab opened (meq): the alliance's fights, each with its defenders -- what is
        /// defended first -- its attackers and its phase (mdu).
        /// </summary>
        public static async Task ListenAsync(NetworkStream stream)
        {
            var alliance = AllianceStore.AllianceOf(SessionContext.State.CharacterId);
            if (alliance == null) return;

            var lines = new List<Pb>();
            foreach (var defence in _open.Values.Where(d => d.AllianceId == alliance.Id).OrderBy(d => d.FightId))
            {
                var fight = FightHandler.FightById(defence.FightId);
                if (fight == null) continue;
                var defenders = new List<Pb> { defence.Defended() };
                defenders.AddRange(fight.Rojo.Where(f => !f.IsMonster && !f.EsInvocado).Select(f => PersonOf(f.Id)));
                var attackers = fight.Azul.Where(f => !f.IsMonster && !f.EsInvocado).Select(f => PersonOf(f.Id));
                lines.Add(TaxCollectorProtocol.FightLine(KeyOf(defence), PhaseOf(defence), defenders, attackers));
            }
            await SendAsync(stream, ConnectionProtocol.Push(Op.Mdu, TaxCollectorProtocol.BuildFights(lines)));
        }

        /// <summary>«No puedes unirte al recaudador. Estás ocupado.», info 1/217.</summary>
        private const int Busy = 217;

        /// <summary>
        /// A member comes to defend (mer, a free slot of the fight's line): into the fight on the
        /// defenders' side while it is being placed, from wherever he stands.
        /// </summary>
        public static async Task JoinAsync(NetworkStream stream, byte[] payload)
        {
            var (_, fightId) = TaxCollectorProtocol.ReadFightKey(ConnectionProtocol.ReadPayload(payload, Op.Mer));
            var session = SessionContext.Current;
            var me = session.State;
            if (!_open.TryGetValue(fightId, out var defence)) return;
            var fight = FightHandler.FightById(fightId);
            var alliance = AllianceStore.ById(defence.AllianceId);
            if (fight == null || alliance == null || AllianceStore.AllianceOf(me.CharacterId)?.Id != alliance.Id) return;
            await FightHandler.InTurnAsync(fight, () => DefendAsync(stream, session, fight, fightId, defence, alliance));
        }

        /// <summary>Joining the defence, under the fight's turn: the check and the place taken at once.</summary>
        private static async Task DefendAsync(NetworkStream stream, GameSession session, FightInstance fight, long fightId,
                                              Defence defence, AllianceStore.Alliance alliance)
        {
            var me = session.State;
            if (me.IsInFight || me.FightId != 0 || FightHandler.FightOf(me.CharacterId) != null)
            {
                await SendAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, Busy)));
                return;
            }
            if (fight.CanJoin(FightInstance.Rojos, FightHandler.CapOf(fight)) != FightInstance.JoinRefusal.None) return;

            var leftMap = session.Map;
            if (!await FightHandler.JoinFightAsync(session, fight, FightInstance.Rojos, followingLeader: false)) return;
            await leftMap.SendAsync(new Jondo.Unity.Protocol.Game.ContextRemoveElementEvent(me.CharacterId),
                session.Id, fightId: 0);

            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Mdx,
                TaxCollectorProtocol.BuildFighterAdded(PersonOf(me.CharacterId), KeyOf(defence),
                                                       TaxCollectorProtocol.SideDefenders)));
            Program.LogDebug($"[AllianceFights] {me.CharacterName} comes to defend in fight #{fightId}.");
        }

        /// <summary>
        /// «Swap places» on a defender of the fights tab (med { f1 the fight, f3 the defender, f2
        /// left empty }; fgu.beub from JoinFightUtils.SwapPlaces, which first refuses one already in
        /// another fight): taking that defender's place.
        /// </summary>
        /// <remarks>
        /// INFERRED. The client says only whose place. Dofus 2 swapped places in a list of defenders
        /// waiting to go in; here defenders join the fight itself, so with room on the side it is a
        /// join, and with the side full the defender named steps out of the placement and the one
        /// asking steps in.
        /// </remarks>
        public static async Task SwapAsync(NetworkStream stream, byte[] payload)
        {
            byte[] med = ConnectionProtocol.ReadPayload(payload, Op.Med);
            var (_, fightId) = TaxCollectorProtocol.ReadFightKey(med);
            long target = 0;
            if (med != null)
                foreach (var field in ProtoMessage.Parse(med).Fields)
                    if (field.FieldNumber == 3 && field.WireType == 0) target = field.VarIntValue;

            var session = SessionContext.Current;
            var me = session.State;
            if (target == 0 || target == me.CharacterId || !_open.TryGetValue(fightId, out var defence)) return;
            var fight = FightHandler.FightById(fightId);
            var alliance = AllianceStore.ById(defence.AllianceId);
            if (fight == null || alliance == null || AllianceStore.AllianceOf(me.CharacterId)?.Id != alliance.Id) return;

            await FightHandler.InTurnAsync(fight, async () =>
            {
                if (fight.State != FightState.Placement) return;
                if (fight.CanJoin(FightInstance.Rojos, FightHandler.CapOf(fight)) == FightInstance.JoinRefusal.TeamFull
                    && fight.EquipoDe(target) == FightInstance.Rojos)
                {
                    var other = SessionRegistry.FindByCharacter(target);
                    if (other == null) return;
                    using (SessionContext.Push(other)) await FightHandler.LeavePlacementAsync(other.Stream, fight);
                    Program.LogDebug($"[AllianceFights] {me.CharacterName} takes the place of {target} in fight #{fightId}.");
                }
                await DefendAsync(stream, session, fight, fightId, defence, alliance);
            });
        }

        /// <summary>
        /// A defender takes himself off (mea, his own slot of the line): out of the fight as
        /// anybody leaving its placement.
        /// </summary>
        public static async Task LeaveAsync(NetworkStream stream, byte[] payload)
        {
            var (_, fightId) = TaxCollectorProtocol.ReadFightKey(ConnectionProtocol.ReadPayload(payload, Op.Mea));
            var fight = FightHandler.FightById(fightId);
            long me = SessionContext.State.CharacterId;
            if (fight == null) return;
            await FightHandler.InTurnAsync(fight, async () =>
            {
                if (!_open.ContainsKey(fightId) || fight.State != FightState.Placement
                    || fight.EquipoDe(me) != FightInstance.Rojos)
                    return;
                await FightHandler.LeavePlacementAsync(stream, fight);
            });
        }

        /// <summary>Somebody left one of these fights in its placement: off its line (meu).</summary>
        internal static async Task FighterLeftAsync(FightInstance fight, long characterId)
        {
            var defence = Of(fight);
            var alliance = defence == null ? null : AllianceStore.ById(defence.AllianceId);
            if (alliance == null) return;
            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Meu,
                TaxCollectorProtocol.BuildFighterRemoved(characterId, KeyOf(defence))));
        }

        // ─── Pieces ─────────────────────────────────────────────────────────────

        internal static Pb KeyOf(Defence defence)
            => TaxCollectorProtocol.FightKey(defence.Kind, defence.MapId, defence.FightId);

        internal static Pb PhaseOf(Defence defence)
            => defence.Fighting
                ? TaxCollectorProtocol.Phase(TaxCollectorProtocol.PhaseFighting, 0)
                : TaxCollectorProtocol.Phase(TaxCollectorProtocol.PhaseWaiting, defence.PlacementEndsMs);

        /// <summary>A character as a fighter of the list: his name, level and look.</summary>
        internal static Pb PersonOf(long characterId)
        {
            var character = DatabaseManager.GetCharacterById(characterId);
            if (character == null) return TaxCollectorProtocol.Fighter(characterId, "", 1, null, 0);
            byte[] look = CharacterLookBuilder.BuildLook(character);
            return TaxCollectorProtocol.Fighter(characterId, character.Name, character.Level, look, character.Breed);
        }

        private static async Task SendAsync(NetworkStream stream, byte[] frame)
        {
            if (stream == null) return;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
        }
    }
}
