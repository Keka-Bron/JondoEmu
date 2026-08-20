using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>Puerta unica de las peticiones <c>iwo</c> que manda el cliente.</summary>
    public static class InteractiveActionHandler
    {
        public static async Task UseAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? iwo = ConnectionProtocol.ReadPayload(payload, Op.InteractiveUseRequestMessage);
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

            long mapId = SessionContext.State.MapId;
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
                default:
                    throw new InvalidOperationException($"Acción interactiva no gestionada: {action.Kind}.");
            }
        }
    }
}
