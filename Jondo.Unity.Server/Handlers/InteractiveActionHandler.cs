using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>Puerta unica de las peticiones <c>iwo</c> que manda el cliente.</summary>
    public static class InteractiveActionHandler
    {
        public static async Task UseAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? iwo = ConnectionProtocol.ReadPayload(payload, Op.Iwo);
            if (iwo == null) return;

            int skillInstanceId = 0;
            int elementId = 0;
            foreach (var field in ProtoMessage.Parse(iwo).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.VarIntValue < 0 || field.VarIntValue > int.MaxValue)
                {
                    Console.WriteLine("[Interactives] Petición iwo con identificador fuera de rango.");
                    return;
                }
                if (field.FieldNumber == 1) skillInstanceId = (int)field.VarIntValue;
                else if (field.FieldNumber == 2) elementId = (int)field.VarIntValue;
            }

            // Dentro de un sueno, las puertas son interactivos que no existen en el mapa de rol,
            // asi que se prueban ANTES: el registro de siempre no sabria que hacer con ellas y las
            // dejaria en «uso desconocido».
            if (await DreamHandler.TryDoorAsync(stream, elementId)) return;

            long mapId = SessionContext.State.MapId;

            var lectura = Readables.Of(mapId, elementId);

            // Queda apuntado antes de decidir que hace, y a proposito: hay conversaciones que
            // dependen de haber leido algo, y si esto fuera detras del despacho un elemento que
            // acabe en «uso desconocido» -como el cartel de la taberna, que no es zaap ni recurso
            // ni objetivo- no dejaria rastro nunca.
            //
            // SALVO si la lectura pregunta. Entonces el apunte espera a que se acepte: un cartel
            // con boton de aceptar en el que mirarlo bastase para haberlo aceptado convertiria el
            // boton en un adorno.
            bool apuntaAhora = lectura == null || !lectura.Asks;
            if (apuntaAhora && elementId != 0 && SessionContext.State.ElementsUsed.Add(elementId))
            {
                DatabaseManager.RememberElement(GameState.CharacterId, elementId);
            }

            // ¿Es algo que se lee? Va antes de las misiones porque un cartel no es un objetivo:
            // la oferta de trabajo de la taberna no sale en ningún paso de la misión que abre, y
            // aun así hay que enseñarla.
            if (lectura != null)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kkt, Pb.New().Var(2, lectura.Document).Build()));

                // Y detrás, si pregunta, la pregunta. Después del documento porque primero se lee
                // y luego se decide, que es el orden en que lo hace una persona.
                if (lectura.Asks)
                {
                    var respuestas = new System.Collections.Generic.List<long> { lectura.Accept };
                    if (lectura.Decline != 0) respuestas.Add(lectura.Decline);

                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Ios,
                            ConnectionProtocol.BuildNpcQuestion(lectura.Question, respuestas)));
                }

                Console.WriteLine($"[Lecturas] Se lee el documento {lectura.Document} del elemento " +
                                  $"{elementId}{(lectura.Asks ? ", con pregunta" : "")}.");
                return;
            }

            // ¿Es algo que una misión pide pinchar? Se mira ANTES del registro, porque una estela
            // no es un zaap ni un recurso: no está en el registro y su única razón de existir es la
            // misión. Es lo que hace la captura del tutorial —seis clics en los elementos
            // 541424-541429 y detrás de cada uno el cliente preguntando «ieo {1629}»—, y lo que no
            // hacía nadie: hasta ahora un clic así caía en la rama de «uso desconocido».
            if (await Managers.Quests.OnInteractiveUsedAsync(stream, mapId, elementId)) return;

            if (!InteractiveRegistry.TryResolveUse(mapId, elementId, skillInstanceId,
                                                   out var interactive, out var action))
            {
                Console.WriteLine($"[Interactives] Uso desconocido: mapa {mapId}, elemento " +
                                  $"{elementId}, instancia {skillInstanceId}.");
                return;
            }

            switch (action.Kind)
            {
                case InteractiveActionKind.Zaap:
                    await ZaapTravelHandler.OpenAsync(stream, interactive.Element, action.SkillId);
                    break;
                case InteractiveActionKind.Chest:
                    await ChestHandler.OpenAsync(stream, interactive.Element.Id, action.SkillId);
                    break;
                case InteractiveActionKind.Lottery:
                    await LotteryHandler.DrawAsync(stream, interactive.Element.Id, action.SkillId);
                    break;
                case InteractiveActionKind.Zaapi:
                    await ZaapiTravelHandler.OpenAsync(stream, interactive.Element, action.SkillId);
                    break;
                case InteractiveActionKind.Bin:
                    await BinHandler.OpenAsync(stream, interactive.Element.Id, action.SkillId);
                    break;
                case InteractiveActionKind.HouseDoor:
                    await HouseHandler.EnterAsync(stream, interactive.Element.Id, action.SkillId);
                    break;
                case InteractiveActionKind.HouseExit:
                    await HouseHandler.LeaveAsync(stream, interactive.Element.Id, action.SkillId);
                    break;
                case InteractiveActionKind.Teleport:
                    await TeleportHandler.UseAsync(stream, interactive.Element.Id, action.SkillId);
                    break;
                case InteractiveActionKind.Gather:
                    await GatheringHandler.GatherAsync(stream, interactive.Element.Id, action.SkillId);
                    break;
                default:
                    throw new InvalidOperationException($"Acción interactiva no gestionada: {action.Kind}.");
            }
        }
    }
}
