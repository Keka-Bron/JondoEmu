using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// La máquina de la lotería del merkasako. Sin límite de tiradas: se clica y sale algo.
    ///
    /// De las dos capturas reales de usarla, una con premio y otra rechazada por haberla usado ya
    /// ese día:
    ///
    ///   cliente  iwo { f1: uid de habilidad, f2: elemento }
    ///   servidor iwn { f1: 1, f2: elemento, f4: 184, f5: quién }
    ///   servidor jbs { f2: 406096900 }    con premio
    ///   servidor jbs { f3: 1 }            rechazada
    ///
    /// El f2 tiene la forma de un identificador de objeto, así que ahí va el del premio. El f3 es el
    /// motivo del rechazo y aquí no se usa nunca: la máquina no se agota.
    ///
    /// Detrás va el objeto a la bolsa, que eso ya es cosa nuestra —el servidor real lo entrega por
    /// otro camino que la captura no llega a enseñar—, y el peso.
    ///
    /// Lo que sale lleva efectos que ningún objeto del juego tiene —+3 PA, +3 PM, 500 y pico de una
    /// característica— y va firmado por #LOTTERY#, que es como el cliente pinta un exomago: el
    /// efecto 988 es "Fabricado por: #4".
    /// </summary>
    public static class LotteryHandler
    {
        public static Task DrawAsync(NetworkStream stream, int elementId)
            => DrawAsync(stream, elementId, Lottery.Skill);

        public static async Task DrawAsync(NetworkStream stream, int elementId, int skillId)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    elementId, skillId, Jondo.Unity.Server.Network.SessionContext.State.CharacterId)));

            var prize = Lottery.Draw(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            if (prize == null)
            {
                Console.WriteLine("[Lotería] La tirada no ha dado nada.");
                return;
            }

            // Lo que la máquina contesta, con la forma de la captura.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jbs, ConnectionProtocol.BuildLotteryResult(prize.Uid)));

            // Y el premio a la BOLSA, que es el iua. El itd es el que mete en el cofre, y mandando
            // ése el objeto se creaba en la base de datos pero no aparecía por ninguna parte.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(ChestHandler.ArrivesInBag,
                    ConnectionProtocol.BuildItemArrived(
                        ChestHandler.FieldOf(ChestHandler.ArrivesInBag), prize)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iun,
                    ConnectionProtocol.BuildPods(0, 1000 + 5L * Jondo.Unity.Server.Network.SessionContext.State.StatStrength)));
        }
    }
}
