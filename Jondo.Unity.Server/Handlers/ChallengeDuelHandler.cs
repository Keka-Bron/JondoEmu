using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Retar a otro jugador: ofrecer, aceptar y rechazar.
    /// </summary>
    /// <remarks>
    /// Medido en las cuatro capturas de desafío de la carpeta Combate, que entre las cuatro cubren
    /// los dos finales desde los dos lados. El intercambio entero son cuatro tramas:
    ///
    /// <code>
    ///   C-&gt;S  hph { f1: a quien se reta, f2: 1, f3: n }
    ///   S-&gt;C  hqc { f1: retador, f2: retado, f3: id }        a los dos
    ///   C-&gt;S  hpu { f1: id }                                 rechazar
    ///   C-&gt;S  hpu { f1: id, f2: 1 }                          aceptar
    ///   S-&gt;C  hpv { f1: retador, f2: id, f3: 1 si se acepto, f4: retado }
    /// </code>
    ///
    /// Lo que separa aceptar de rechazar es ese <c>f2</c> del hpu, y no hay dos opcodes: las dos
    /// capturas de rechazo mandan «08e903» y «08ea03» a secas, y la de aceptar «08ec031001». El
    /// hpv lo repite en su f3, que está en las dos aceptadas y en ninguna de las rechazadas.
    ///
    /// El <c>f3</c> del hph vale 146 en una captura y 106 en otra para el mismo par de personajes,
    /// así que no es ni el mapa ni una modalidad: se ignora. Decirlo es mejor que inventarle un
    /// significado.
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

            // En el mismo mapa. Un desafío es entre dos que se ven: retar desde otro mapa dejaría
            // al aceptar un combate que no se sabe dónde montar.
            if (otro.MapId != SessionContext.State.MapId)
            {
                Console.WriteLine($"[Desafío] {challengerId} reta a {targetId}, que está en otro mapa.");
                return;
            }

            // Uno a la vez, en cualquiera de los dos papeles. Sin esto se puede retar cien veces al
            // mismo y llenarle la pantalla, o retar a diez y aceptarlos todos.
            if (Duels.Busy(challengerId) || Duels.Busy(targetId))
            {
                Console.WriteLine($"[Desafío] {challengerId} o {targetId} ya andan en uno.");
                return;
            }

            var desafio = Duels.Open(challengerId, targetId, SessionContext.State.MapId);
            byte[] aviso = ConnectionProtocol.Push(Op.Hqc,
                FightProtocol.BuildChallengeOffered(challengerId, targetId, desafio.Id));

            // A los dos: el que reta también lo recibe, que es lo que le dibuja la espera.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, aviso);
            await otro.SendAsync(aviso);

            Console.WriteLine($"[Desafío] #{desafio.Id}: {challengerId} reta a {targetId}.");
        }

        /// <summary>La respuesta del retado (hpu): con f2 acepta, sin él rechaza.</summary>
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

            // Se saca de la lista al contestar, y de ahí sale la exclusión: dos respuestas a la vez
            // -- el hpu llega repetido en dos de las capturas -- y sólo una se lleva el desafío.
            var desafio = id == 0 ? null : Duels.Take(id);
            if (desafio == null) return;

            // Contesta el retado, o nadie. El id viaja por el cable y sin esto un tercero podría
            // aceptar por él con sólo acertar el número.
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

            // Y el combate. Se comprueba otra vez que los dos siguen ahí y en el mismo mapa: entre
            // el reto y la respuesta cabe una desconexión o un cambio de mapa, y montar un duelo
            // con alguien que ya no está deja a uno solo en una arena.
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
