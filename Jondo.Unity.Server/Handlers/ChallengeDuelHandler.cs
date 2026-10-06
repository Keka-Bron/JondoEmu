using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Challenging another player: offering, accepting and refusing.
    /// </summary>
    /// <remarks>
    /// Measured on the four challenge captures in the Combate folder, which between them cover
    /// both endings from both sides. The whole exchange is four frames:
    ///
    /// <code>
    ///   C-&gt;S  hph { f1: who is challenged, f2: 1, f3: n }
    ///   S-&gt;C  hqc { f1: challenger, f2: challenged, f3: id }   to both
    ///   C-&gt;S  hpu { f1: id }                                    refuse
    ///   C-&gt;S  hpu { f1: id, f2: 1 }                             accept
    ///   S-&gt;C  hpv { f1: challenger, f2: id, f3: 1 if accepted, f4: challenged }
    /// </code>
    ///
    /// What separates accepting from refusing is that <c>f2</c> of the hpu, and there are not two
    /// opcodes: the two refusal captures send just «08e903» and «08ea03», and the accepting one
    /// «08ec031001». The hpv repeats it in its f3, which is in both accepted ones and in none of
    /// the refused.
    ///
    /// The hph's <c>f3</c> is 146 in one capture and 106 in another for the same pair of
    /// characters, so it is neither the map nor a mode: it is ignored. Saying so is better than
    /// making up a meaning for it.
    /// </remarks>
    public static class ChallengeDuelHandler
    {
        /// <summary>El cliente reta a alguien (hph).</summary>
        public static async Task OfferAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? hph = ConnectionProtocol.ReadPayload(payload, Op.Hph);
            if (hph == null) return;

            long targetId = 0;
            foreach (var field in ProtoMessage.Parse(hph).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) targetId = field.VarIntValue;
            }

            long challengerId = GameState.CharacterId;
            if (targetId == 0 || challengerId == 0 || targetId == challengerId) return;

            var otro = SessionRegistry.FindByCharacter(targetId);
            if (otro == null || !otro.IsInWorld)
            {
                Console.WriteLine($"[Desafío] {challengerId} reta a {targetId}, que no está conectado.");
                return;
            }

            // On the same map. A challenge is between two who see each other: challenging from another
            // map would leave, on accepting, a fight with no known place to set it up.
            if (otro.MapId != SessionContext.State.MapId)
            {
                Console.WriteLine($"[Desafío] {challengerId} reta a {targetId}, que está en otro mapa.");
                return;
            }

            // One at a time, in either role. Without this the same player can be challenged a hundred
            // times and have his screen filled, or ten can be challenged and all of them accepted.
            if (Duels.Busy(challengerId) || Duels.Busy(targetId))
            {
                Console.WriteLine($"[Desafío] {challengerId} o {targetId} ya andan en uno.");
                return;
            }

            var desafio = Duels.Open(challengerId, targetId, SessionContext.State.MapId);
            byte[] aviso = ConnectionProtocol.Push(Op.Hqc,
                FightProtocol.BuildChallengeOffered(challengerId, targetId, desafio.Id));

            // To both: the challenger receives it too, which is what draws the wait for him.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, aviso);
            await otro.SendAsync(aviso);

            Console.WriteLine($"[Desafío] #{desafio.Id}: {challengerId} reta a {targetId}.");
        }

        /// <summary>The challenged player's answer (hpu): with f2 he accepts, without it he refuses.</summary>
        public static async Task AnswerAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? hpu = ConnectionProtocol.ReadPayload(payload, Op.Hpu);
            if (hpu == null) return;

            int id = 0;
            bool accepted = false;
            foreach (var field in ProtoMessage.Parse(hpu).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) id = (int)field.VarIntValue;
                else if (field.FieldNumber == 2) accepted = field.VarIntValue != 0;
            }

            // It is taken off the list on answering, and that is where the exclusion comes from: two
            // answers at once -- the hpu arrives twice in two of the captures -- and only one takes the
            // challenge.
            var desafio = id == 0 ? null : Duels.Take(id);
            if (desafio == null) return;

            // The challenged player answers, or nobody. The id travels on the wire and without this a
            // third player could accept for him just by guessing the number.
            if (GameState.CharacterId != desafio.TargetId)
            {
                Console.WriteLine($"[Desafío] #{id}: contesta {GameState.CharacterId} y no le toca.");
                return;
            }

            byte[] resultado = ConnectionProtocol.Push(Op.Hpv,
                FightProtocol.BuildChallengeAnswered(
                    desafio.ChallengerId, desafio.Id, accepted, desafio.TargetId));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, resultado);
            SessionRegistry.FindByCharacter(desafio.ChallengerId)?.SendAsync(resultado);

            Console.WriteLine($"[Desafío] #{id}: {desafio.TargetId} " +
                              $"{(accepted ? "acepta" : "rechaza")} a {desafio.ChallengerId}.");

            if (!accepted) return;

            // And the fight. It is checked again that both are still there and on the same map: a
            // disconnection or a map change fits between the challenge and the answer, and setting up a
            // duel with somebody who is no longer there leaves one alone in an arena.
            var retador = SessionRegistry.FindByCharacter(desafio.ChallengerId);
            var retado = SessionRegistry.FindByCharacter(desafio.TargetId);

            if (retador == null || retado == null || !retador.IsInWorld || !retado.IsInWorld
                || retador.MapId != retado.MapId)
            {
                Console.WriteLine($"[Desafío] #{id}: aceptado, pero ya no están los dos en el " +
                                  $"mismo sitio; no se monta el combate.");
                return;
            }

            await FightHandler.InitiateDuelAsync(retador, retado, retador.MapId, desafio.Id);
        }
    }
}
