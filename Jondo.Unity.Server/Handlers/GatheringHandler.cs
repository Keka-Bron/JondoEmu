using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Recolectar: segar trigo, talar un fresno, pescar, minar.
    ///
    /// ─── El ciclo, medido en cuatro capturas ────────────────────────────────────────────────
    ///
    /// El cliente manda UN solo <c>iwo</c> y no vuelve a hablar. Todo lo demás lo pone el
    /// servidor, en dos tandas separadas por tres segundos exactos:
    ///
    ///   al momento   iwf  el recurso pasa a «en uso»
    ///                iwm  se vuelve a declarar con la habilidad ya no pulsable
    ///                iwn  arranca el gesto, con su duración
    ///   a los 3 s    iwi  se acabó el gesto
    ///                iua o ivj  el objeto: iua si es pila nueva, ivj si ya la tenías
    ///                iun  los pods
    ///                itn  cuánto se ha sacado
    ///                irq  la experiencia de oficio
    ///                iwf  el recurso queda agotado
    ///                iwm  y se declara por última vez, apagado
    ///
    /// Los tres segundos no son un número redondo elegido por nosotros: el <c>iwn</c> los manda
    /// en su campo 3 como 30 décimas, y el tiempo real entre ese mensaje y el <c>iwi</c> midió
    /// 2.987, 2.996, 2.999, 3.008, 3.038, 3.064 y 3.091 milisegundos en las siete recolecciones
    /// capturadas.
    ///
    /// ─── Cuánto se saca ─────────────────────────────────────────────────────────────────────
    ///
    /// Esto NO está en el cliente. Se buscó en skills.json (dieciséis campos, ninguno de
    /// cantidad), en InteractivesDataRoot (sólo id y nombre), en JobsDataRoot (cuatro campos) y en
    /// CollectablesDataRoot (que va de mascotas). Es regla de servidor, como el coste del zaap.
    ///
    /// Pero las capturas dejan seis medidas, y una regla sencilla las cuadra las seis:
    ///
    ///   Madera de fresno  nivel del recurso   1, oficio 200  ->  20, 17, 14
    ///   Lucio             nivel del recurso  80, oficio 200  ->  13, 12
    ///   Perca             nivel del recurso 120, oficio 200  ->   8
    ///   Trigo             nivel del recurso   1, oficio   1  ->   4
    ///
    ///   techo = max(4, 1 + (nivel del oficio − nivel del recurso) / 10)
    ///
    /// que da 20,9 · 13 · 9 · 4, y lo observado cae siempre entre el 70 % de ese techo y el techo.
    /// Así que se tira un número en ese margen. Sube con el nivel del oficio y baja con lo exigente
    /// que sea el recurso, que es lo que se ve en el juego.
    ///
    /// ─── Lo que hace falta para recolectar ──────────────────────────────────────────────────
    ///
    /// Nivel de oficio suficiente. Un pescador de nivel 10 no saca una perca, que pide 120, y aquí
    /// ni se le deja intentarlo: se le dice por el chat y no se toca el recurso. Los niveles suben
    /// solos recolectando, y al subir se sacan más unidades por la fórmula de arriba.
    /// </summary>
    public static class GatheringHandler
    {
        /// <summary>Lo que se saca como poco, en tanto por ciento del techo.</summary>
        private const int FloorPercent = 70;

        /// <summary>Suelo del techo: por debajo de esto no baja ni con un oficio recién empezado.</summary>
        private const int MinimumYield = 4;

        /// <summary>
        /// El azar de la cantidad. Va sembrado por personaje y elemento para que dos jugadores
        /// que siegan a la vez no saquen lo mismo, pero sin depender de un estático compartido.
        /// </summary>
        [ThreadStatic] private static Random? _random;

        private static Random Dice => _random ??= new Random();

        /// <summary>El techo de unidades para un oficio de este nivel sobre este recurso.</summary>
        public static int Ceiling(int jobLevel, int resourceLevel)
            => Math.Max(MinimumYield, 1 + (jobLevel - resourceLevel) / 10);

        /// <summary>Cuánto se saca esta vez.</summary>
        public static int Roll(int jobLevel, int resourceLevel)
        {
            int techo = Ceiling(jobLevel, resourceLevel);
            int suelo = Math.Max(1, techo * FloorPercent / 100);
            return Dice.Next(suelo, techo + 1);
        }

        /// <summary>
        /// El cliente ha clicado un recurso.
        ///
        /// El gesto dura tres segundos y durante ellos el jugador puede irse, así que la segunda
        /// tanda se manda desde una tarea aparte y comprueba antes que siga donde estaba. No se
        /// bloquea el hilo de red: bloquearlo dejaría al jugador sin poder ni andar.
        /// </summary>
        public static async Task GatherAsync(NetworkStream stream, int elementId, int skillId)
        {
            long mapId = SessionContext.State.MapId;
            if (!Resources.TryGet(mapId, elementId, out var resource))
            {
                Console.WriteLine($"[Oficios] Recurso desconocido: mapa {mapId}, elemento {elementId}.");
                return;
            }

            // El nivel ya lo ha filtrado el jss: un recurso que le queda grande le llega con la
            // habilidad apagada y el cliente ni deja clicarlo, igual que si estuviera agotado.
            // Esto es la red de seguridad por si llega un iwo de todas formas, y contesta con el
            // mensaje que el propio cliente tiene para esto —«No tienes el nivel de oficio
            // necesario»— y no con una línea de chat, que saldría por el canal general.
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

                // Si se ha ido del mapa, el recurso se suelta y no se le da nada: el cliente ya
                // no tiene ese elemento en pantalla y le llegarían mensajes de un sitio donde no
                // está.
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

                // Al inventario de quien lo ha recogido, y a la base, que si no se pierde al salir.
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
                        0, 1000 + 5L * SessionContext.State.StatStrength)));

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Itn, ConnectionProtocol.BuildGathered(
                        resource.ItemId, cuantos)));

                bool subeNivel = SessionContext.State.AddJobExperience(
                    resource.JobId, JobExperience.PerGather, out long total, out int nivel);
                DatabaseManager.SaveJobExperience(characterId, resource.JobId, total);

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Irq, ConnectionProtocol.BuildJobExperience(
                        resource.JobId, JobExperience.Next(nivel), nivel,
                        JobExperience.Floor(nivel), total)));

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
