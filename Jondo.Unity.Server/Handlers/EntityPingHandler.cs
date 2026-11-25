using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// A ping on somebody standing on the map: the mark the client draws over an entity
    /// (Core.Rendering.World.EntityPing) for the player's party to see.
    /// </summary>
    /// <remarks>
    /// MEASURED in «Sueños Infinitos/entrar a sueño mediante invitacion-pelear-elegir camino-tormenta
    /// astral», frames 65-67, on the side of the one who pings:
    /// <code>
    ///   C->S  ijg  { 1: 4, 2: the entity }            fhr::qnq(entity, the ping's kind)
    ///   S->C  ijq  { 3: { the entity: 4 } }           the pings standing on the map
    ///   S->C  ijf  { 1: the entity, 3: who pinged }
    /// </code>
    /// The kind is the client's enum ur, 0 to 6, which EntityPing and CellPing draw. The party mates
    /// on the map get the same two: INFERRED, the capture only has the one who pinged, and a ping
    /// nobody else sees would mean nothing. The same ijq goes out as an empty
    /// <see cref="Jondo.Unity.Protocol.Game.FightPingsEvent"/> when loading a fight board, wiping the marks off the map.
    /// </remarks>
    public static class EntityPingHandler
    {
        /// <summary>The kinds of ping the client draws (ur).</summary>
        public const int MaxKind = 6;

        public static async Task PingAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ijg = ConnectionProtocol.ReadPayload(payload, Op.Ijg);
            if (ijg == null) return;

            long kind = -1, target = 0;
            foreach (var field in ProtoMessage.Parse(ijg).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) kind = (long)field.VarIntValue;
                else if (field.FieldNumber == 2) target = (long)field.VarIntValue;
            }
            if (kind < 0 || kind > MaxKind || target == 0) return;

            var me = SessionContext.Current;
            byte[] standing = ConnectionProtocol.Push(Op.Ijq, BuildStanding(target, (int)kind));
            byte[] pinged = ConnectionProtocol.Push(Op.Ijf, BuildPinged(target, me.State.CharacterId));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, standing);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, pinged);
            foreach (var mate in PartyMatesOnMap(me))
            {
                try
                {
                    await mate.SendAsync(standing);
                    await mate.SendAsync(pinged);
                }
                catch (Exception) { /* gone meanwhile */ }
            }
        }

        /// <summary>ijq { 3: { 1: the entity, 2: the kind } }: the ping standing over it.</summary>
        public static byte[] BuildStanding(long target, int kind)
            => Pb.New().Msg(3, Pb.New().Var(1, target).Var(2, kind)).Build();

        /// <summary>ijf { 1: the entity, 3: who pinged it }.</summary>
        public static byte[] BuildPinged(long target, long pinger)
            => Pb.New().Var(1, target).Var(3, pinger).Build();

        /// <summary>The player's party mates standing on his map.</summary>
        public static IReadOnlyList<GameSession> PartyMatesOnMap(GameSession session)
        {
            var party = Parties.Of(session.State.CharacterId);
            if (party == null) return Array.Empty<GameSession>();
            return Parties.MembersOf(party)
                .Where(id => id != session.State.CharacterId)
                .Select(SessionRegistry.FindByCharacter)
                .Where(mate => mate != null && mate.State.MapId == session.State.MapId)
                .ToList()!;
        }
    }
}
