using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Gathering: reaping wheat, felling an ash, fishing, mining.
    ///
    /// ─── The cycle, measured in four captures ───────────────────────────────────────────────
    ///
    /// The client sends ONE single <c>iwo</c> and does not speak again. Everything else is put by
    /// the server, in two bursts separated by exactly three seconds:
    ///
    ///   at once      iwf  the resource goes to «in use»
    ///                iwm  it is declared again with the skill no longer clickable
    ///                iwn  the gesture starts, with its duration
    ///   after 3 s    iwi  the gesture is over
    ///                iua or ivj  the item: iua if it is a new stack, ivj if you already had it
    ///                iun  the pods
    ///                itn  how much was gathered
    ///                irq  the profession experience
    ///                iwf  the resource is left exhausted
    ///                iwm  and it is declared one last time, switched off
    ///
    /// The three seconds are not a round number chosen by us: the <c>iwn</c> sends them in its field
    /// 3 as 30 tenths, and the real time between that message and the <c>iwi</c> measured 2,987,
    /// 2,996, 2,999, 3,008, 3,038, 3,064 and 3,091 milliseconds in the seven captured gatherings.
    ///
    /// ─── How much is gathered ───────────────────────────────────────────────────────────────
    ///
    /// This is NOT in the client. It was looked for in skills.json (sixteen fields, none of
    /// quantity), in InteractivesDataRoot (only id and name), in JobsDataRoot (four fields) and in
    /// CollectablesDataRoot (which is about pets). It is a server rule, like the zaap's cost.
    ///
    /// But the captures leave six measurements, and a simple rule fits all six:
    ///
    ///   Ash wood          resource level   1, profession 200  ->  20, 17, 14
    ///   Pike              resource level  80, profession 200  ->  13, 12
    ///   Perch             resource level 120, profession 200  ->   8
    ///   Wheat             resource level   1, profession   1  ->   4
    ///
    ///   ceiling = max(4, 1 + (profession level − resource level) / 10)
    ///
    /// which gives 20.9 · 13 · 9 · 4, and what is observed always falls between 70 % of that ceiling
    /// and the ceiling. So a number is drawn in that range. It goes up with the profession's level
    /// and down with how demanding the resource is, which is what is seen in the game.
    ///
    /// ─── What is needed to gather ───────────────────────────────────────────────────────────
    ///
    /// A high enough profession level. A level 10 fisherman does not get a perch, which asks for 120,
    /// and here he is not even allowed to try: he is told in the chat and the resource is not
    /// touched. Levels go up by themselves when gathering, and on going up more units are gathered by
    /// the formula above.
    /// </summary>
    public static class GatheringHandler
    {
        /// <summary>The least that is gathered, as a percentage of the ceiling.</summary>
        private const int FloorPercent = 70;

        /// <summary>The ceiling's floor: it does not go below this even with a freshly started profession.</summary>
        private const int MinimumYield = 4;

        /// <summary>
        /// The randomness of the quantity. It is seeded per character and element so that two players
        /// reaping at the same time do not get the same, but without depending on a shared static.
        /// </summary>
        [ThreadStatic] private static Random? _random;

        private static Random Dice => _random ??= new Random();

        /// <summary>The ceiling of units for a profession of this level on this resource.</summary>
        public static int Ceiling(int jobLevel, int resourceLevel)
            => Math.Max(MinimumYield, 1 + (jobLevel - resourceLevel) / 10);

        /// <summary>How much is gathered this time.</summary>
        public static int Roll(int jobLevel, int resourceLevel)
        {
            int techo = Ceiling(jobLevel, resourceLevel);
            int suelo = Math.Max(1, techo * FloorPercent / 100);
            return Dice.Next(suelo, techo + 1);
        }

        /// <summary>
        /// The client has clicked a resource.
        ///
        /// The gesture lasts three seconds and during them the player can leave, so the second burst is
        /// sent from a separate task that first checks he is still where he was. The network thread is
        /// not blocked: blocking it would leave the player unable even to walk.
        /// </summary>
        public static async Task GatherAsync(NetworkStream stream, int elementId, int skillId)
        {
            long mapId = SessionContext.State.MapId;
            if (!Resources.TryGet(mapId, elementId, out var resource))
            {
                Console.WriteLine($"[Oficios] Recurso desconocido: mapa {mapId}, elemento {elementId}.");
                return;
            }

            // The level has already been filtered by the jss: a resource beyond him reaches him with
            // the skill switched off and the client does not even let him click it, the same as if it
            // were exhausted. This is the safety net in case an iwo arrives anyway, and it answers
            // with the message the client itself has for this -- «No tienes el nivel de oficio
            // necesario» -- and not with a chat line, which would go out on the general channel.
            int jobLevel = SessionContext.State.JobLevel(resource.JobId);
            if (jobLevel < resource.LevelMin)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                        InfoMessages.Warning, InfoMessages.JobLevelTooLow)));

                Console.WriteLine($"[Oficios] Oficio {resource.JobId} nivel {jobLevel} no llega a " +
                                  $"{resource.LevelMin}; no se recolecta el elemento {elementId}. " +
                                  $"Se le dice: «{InfoMessages.Text(InfoMessages.Warning, InfoMessages.JobLevelTooLow)}»");
                return;
            }

            if (!Resources.TryHold(mapId, elementId))
            {
                Console.WriteLine($"[Oficios] El recurso {elementId} del mapa {mapId} no está disponible.");
                return;
            }

            int instance = Interactives.SkillInstanceOf(elementId);
            long characterId = SessionContext.State.CharacterId;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwf, ConnectionProtocol.BuildElementState(
                    resource.Cell, elementId, (int)ResourceState.Busy)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwm, ConnectionProtocol.BuildElementRedeclared(
                    instance, skillId, elementId, resource.Type, usable: false)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildGatherStarted(
                    elementId, Resources.GatherTenths, skillId, characterId)));

            var session = SessionContext.Current;
            _ = Task.Run(() => FinishAsync(session, stream, resource, skillId, instance, jobLevel));
        }

        /// <summary>La segunda tanda, tres segundos después.</summary>
        private static async Task FinishAsync(GameSession session, NetworkStream stream,
                                              Resources.Resource resource, int skillId,
                                              int instance, int jobLevel)
        {
            try
            {
                await Task.Delay(Resources.GatherTenths * 100);

                using var _ = SessionContext.Push(session);

                // If he has left the map, the resource is released and he is given nothing: the client
                // no longer has that element on screen and he would get messages from a place where he
                // is not.
                if (SessionContext.State.MapId != resource.MapId)
                {
                    Resources.Release(resource.MapId, resource.ElementId);
                    Console.WriteLine($"[Oficios] El jugador dejó el mapa {resource.MapId}; " +
                                      "recolección cancelada.");
                    return;
                }

                int cuantos = Roll(jobLevel, resource.LevelMin);
                long characterId = SessionContext.State.CharacterId;

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Iwi, ConnectionProtocol.BuildGatherFinished(
                        resource.ElementId, skillId)));

                // Into the inventory of whoever gathered it, and into the database, otherwise it is lost
                // on logging out.
                var item = DatabaseManager.AddItemToInventory(characterId, resource.ItemId, cuantos);
                bool pilaNueva = item.Quantity == cuantos;

                if (pilaNueva)
                {
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Iua, ConnectionProtocol.BuildItemArrived(3,
                            new HavenBagStore.StoredItem
                            {
                                Uid = item.Uid,
                                Gid = resource.ItemId,
                                Quantity = item.Quantity,
                            })));
                }
                else
                {
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Ivj, ConnectionProtocol.BuildItemQuantity(
                            item.Uid, item.Quantity)));
                }

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Iun, ConnectionProtocol.BuildPods(
                        0, 1000 + 5L * SessionContext.State.TotalStrength)));

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Itn, ConnectionProtocol.BuildGathered(
                        resource.ItemId, cuantos)));

                bool subeNivel = SessionContext.State.AddJobExperience(
                    resource.JobId,
                    Managers.Almanax.WithBonus(Managers.Almanax.BonusType.JobExperience, JobExperience.PerGather),
                    out long total, out int nivel);
                DatabaseManager.SaveJobExperience(characterId, resource.JobId, total);

                // The new level goes before the experience, as the wheat that took the farmer to
                // level 2 sends it: isz, then irq.
                if (subeNivel) await WorkshopHandler.SendLevelUpAsync(stream, resource.JobId, nivel);

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Irq, ConnectionProtocol.BuildJobExperience(
                        resource.JobId, JobExperience.Next(nivel), nivel,
                        JobExperience.Floor(nivel), total)));

                // The job achievements: "Alcanzar el nivel 10 en 1 oficio" and its kind.
                if (subeNivel) await Managers.Achievements.AfterJobLevelAsync(stream);

                Resources.Spend(resource.MapId, resource.ElementId);

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Iwf, ConnectionProtocol.BuildElementState(
                        resource.Cell, resource.ElementId, (int)ResourceState.Depleted)));
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Iwm, ConnectionProtocol.BuildElementRedeclared(
                        instance, skillId, resource.ElementId, resource.Type, usable: false)));

                Console.WriteLine($"[Oficios] Oficio {resource.JobId}: {cuantos} de {resource.ItemId}, " +
                                  $"+{JobExperience.PerGather} exp, nivel {nivel}" +
                                  (subeNivel ? " (¡sube!)" : "") + $", mapa {resource.MapId}.");
            }
            catch (Exception ex)
            {
                Resources.Release(resource.MapId, resource.ElementId);
                Console.WriteLine($"[Oficios] Se ha cortado la recolección: {ex.Message}");
            }
        }
    }
}
