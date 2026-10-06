using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>Single door for the <c>iwo</c> requests the client sends.</summary>
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

            // Inside a dream, the doors are interactives that do not exist on the roleplay map,
            // so they are tried FIRST: the usual registry would not know what to do with them and
            // would leave them as «unknown use».
            if (await DreamHandler.TryDoorAsync(stream, elementId)) return;

            long mapId = SessionContext.State.MapId;

            var lectura = Readables.Of(mapId, elementId);

            // It is noted before deciding what it does, and on purpose: there are conversations
            // that depend on having read something, and if this went after the dispatch an element
            // that ends up in «unknown use» -- like the tavern's notice, which is neither a zaap nor
            // a resource nor an objective -- would never leave a trace.
            //
            // UNLESS the reading asks something. Then the note waits for it to be accepted: a notice
            // with an accept button where looking at it were enough to have accepted it would turn
            // the button into decoration.
            bool apuntaAhora = lectura == null || !lectura.Asks;
            if (apuntaAhora && elementId != 0 && SessionContext.State.ElementsUsed.Add(elementId))
            {
                DatabaseManager.RememberElement(GameState.CharacterId, elementId);
            }

            // Is it something to be read? It goes before quests because a notice is not an
            // objective: the tavern's job offer does not appear in any step of the quest it opens,
            // and it still has to be shown.
            if (lectura != null)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kkt, Pb.New().Var(2, lectura.Document).Build()));

                // And after it, if it asks, the question. After the document because first one reads
                // and then decides, which is the order a person does it in.
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

            // Is it something a quest asks to click? It is looked at BEFORE the registry, because a
            // stele is neither a zaap nor a resource: it is not in the registry and its only reason
            // to exist is the quest. It is what the tutorial capture does -- six clicks on elements
            // 541424-541429 and after each one the client asking «ieo {1629}» --, and what nobody did:
            // until now a click like that fell into the «unknown use» branch.
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
                case InteractiveActionKind.Dream:
                    await DreamHandler.ShowAsync(stream);
                    break;
                case InteractiveActionKind.DreamDoor:
                    // TryDoorAsync has already been tried further up, before the normal dispatch. If
                    // it gets here, that door does not belong to the room he is in: it may be one of
                    // the other two on the map, or somebody pressing a door with no dream in progress.
                    // Nothing is done, which is better than taking him to a room that is not his.
                    Console.WriteLine($"[Sueños] Puerta {interactive.Element.Id} pulsada y no " +
                                      "lleva a ninguna salida de la sala actual.");
                    break;
                case InteractiveActionKind.Zaapi:
                    await ZaapiTravelHandler.OpenAsync(stream, interactive.Element, action.SkillId);
                    break;
                case InteractiveActionKind.Bin:
                    await BinHandler.OpenAsync(stream, interactive.Element.Id, action.SkillId);
                    break;
                case InteractiveActionKind.HouseDoor:
                    await HouseHandler.UseDoorAsync(stream, interactive, action);
                    break;
                case InteractiveActionKind.HouseChest:
                    await HouseHandler.UseChestAsync(stream, interactive, action);
                    break;
                case InteractiveActionKind.GuildChest:
                    await GuildChestHandler.OpenAsync(stream, interactive.Element.Id, action.SkillId);
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
                case InteractiveActionKind.Workshop:
                    await WorkshopHandler.OpenAsync(stream, interactive.Element.Id, action.SkillId);
                    break;
                case InteractiveActionKind.GuildFounding:
                    await GuildHandler.OpenFoundingAsync(stream, interactive.Element.Id, action.SkillId);
                    break;
                case InteractiveActionKind.Marketplace:
                    await MarketplaceHandler.OpenAsync(interactive.Element.Id, action.SkillId);
                    break;
                default:
                    throw new InvalidOperationException($"Acción interactiva no gestionada: {action.Kind}.");
            }
        }
    }
}
