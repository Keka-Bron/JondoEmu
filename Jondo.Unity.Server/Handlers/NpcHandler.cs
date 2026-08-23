using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Hablar con un NPC y comprarle.
    ///
    /// Todo esto está medido de la captura del servidor de torneos, donde hay cincuenta y un
    /// vendedores repartidos por siete mapas y la montaña de kamas. Son cuatro opcodes para la
    /// tienda y cuatro para el diálogo, y el servidor contesta siempre por EMPUJE, sin emparejar
    /// ids de petición:
    ///
    ///   cliente  iov { f1: acción, f2: mapa, f3: id contextual }   ha clicado al NPC
    ///
    ///   si la acción es 1 u 11 (comprar):
    ///   servidor kbd { f1 (repetido): el catálogo entero, f2: id contextual }
    ///   cliente  kea { f1: objeto, f2: cantidad }                  comprar
    ///   servidor lqn, ivf, iua, iun, kdg, ivf, iun
    ///   cliente  kla (vacío)                                       cerrar
    ///   servidor khd { f3: 11 }
    ///
    ///   si la acción es 3 (hablar):
    ///   servidor ioc { f4: mapa, f5: id contextual }
    ///   servidor ios { f1: pregunta, f2 (repetido): las respuestas }
    ///   cliente  ioy { f1: la respuesta elegida }
    ///   servidor kld { f1: 1 }  y lo que dé esa respuesta
    ///
    /// El f1 del iov no es un tipo de mensaje: es el id de acción de la plantilla del NPC, el mismo
    /// número que sale en su actions[]. Cuadra en los sesenta y cinco iov de la captura. Un NPC que
    /// no declare la acción ni siquiera la ofrece en el menú.
    /// </summary>
    /// <remarks>
    /// Lo que había aquí antes era de la 3.6.4.3 y usaba ilr, ilu, ilq, kjl, kjn, lxh y kns. Ni uno
    /// de esos siete opcodes aparece una sola vez en la captura de la 3.6.10.10.
    /// </remarks>
    public static class NpcHandler
    {
        /// <summary>Qué NPC tiene la tienda abierta ahora mismo, o cero.</summary>
        private static long OpenShop
        {
            get => SessionContext.State.OpenNpcShopId;
            set => SessionContext.State.OpenNpcShopId = value;
        }

        /// <summary>Qué vendedor es, para saber si vende lo que el cliente pide.</summary>
        private static int OpenShopNpc
        {
            get => SessionContext.State.OpenNpcShopNpcId;
            set => SessionContext.State.OpenNpcShopNpcId = value;
        }

        public static bool IsShopOpen => OpenShop != 0;

        /// <summary>
        /// Desde dónde se numeran los objetos comprados.
        ///
        /// Cada cosa que fabrica objetos tiene su tramo: 900.000.000 el inventario de prueba,
        /// 950.000.000 la lotería del merkasako y 960.000.000 las apariencias regaladas. Ese último
        /// tramo lo BORRA entero dotar_apariencias.py cada vez que se relanza, así que lo comprado
        /// no puede caer ahí o desaparecería sin avisar.
        /// </summary>
        private const long FirstUid = 970000000L;

        /// <summary>
        /// Lo que da la montaña de kamas.
        ///
        /// La cifra no está en ningún dato del cliente ni en la plantilla del NPC: es una constante
        /// del servidor. En la captura se cobró tres veces y las tres subió exactamente lo mismo,
        /// de cero a 50.000.000, de 49.999.998 a 99.999.998 y de ahí a 149.999.998.
        /// </summary>
        private const long KamasMountainReward = 50_000_000L;

        /// <summary>
        /// La respuesta que paga. La 70285 es "Hacerte con esos millones de kamas que no sirven a
        /// nadie" y la 70286, la de al lado, se marcha sin cobrar: el servidor contesta el kld y
        /// nada más.
        /// </summary>
        private const long KamasMountainReply = 70285;

        /// <summary>
        /// El cliente ha clicado un NPC (iov). Según la acción, se le abre la tienda o el diálogo.
        /// </summary>
        public static async Task InteractAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? iov = ConnectionProtocol.ReadPayload(payload, Op.Iov);
            if (iov == null) return;

            long action = 0, mapId = 0, contextualId = 0;
            foreach (var field in ProtoMessage.Parse(iov).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) action = field.VarIntValue;
                else if (field.FieldNumber == 2) mapId = field.VarIntValue;
                else if (field.FieldNumber == 3) contextualId = field.VarIntValue;
            }

            var npc = Npcs.Find(mapId, contextualId);
            if (npc == null)
            {
                Console.WriteLine($"[NPC] El cliente clica el {contextualId} del mapa {mapId}, " +
                                  "que aquí no es nadie.");
                return;
            }

            if (action == Npcs.Trade || action == Npcs.TradeCosmetics)
            {
                await OpenShopAsync(stream, npc);
                return;
            }

            if (action == Npcs.Talk)
            {
                await OpenDialogAsync(stream, npc, mapId);
                return;
            }

            Console.WriteLine($"[NPC] Acción {action} sobre el NPC {npc.NpcId}, que no está hecha.");
        }

        /// <summary>El catálogo entero, de una sola vez, que es como lo manda el servidor real.</summary>
        private static async Task OpenShopAsync(NetworkStream stream, Npcs.Spawn npc)
        {
            var catalogue = NpcShops.CatalogueOf(npc.NpcId);
            if (catalogue.Count == 0)
            {
                Console.WriteLine($"[NPC] El {npc.NpcId} tiene acción de tienda pero no vende nada.");
                return;
            }

            OpenShop = npc.ContextualId;
            OpenShopNpc = npc.NpcId;

            byte[] kbd = ConnectionProtocol.BuildShop(npc.ContextualId, catalogue);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kbd, kbd));

            Console.WriteLine($"[NPC] Tienda del {npc.NpcId}: {catalogue.Count} objetos, " +
                              $"{kbd.Length} bytes.");
        }

        /// <summary>La ventana de diálogo y su pregunta, sacadas de la plantilla del NPC.</summary>
        private static async Task OpenDialogAsync(NetworkStream stream, Npcs.Spawn npc, long mapId)
        {
            var template = Npcs.TemplateOf(npc.NpcId);
            if (template == null || template.DialogMessageId == 0)
            {
                Console.WriteLine($"[NPC] El {npc.NpcId} no tiene diálogo en su plantilla.");
                return;
            }


            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, npc.ContextualId)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ios, ConnectionProtocol.BuildNpcQuestion(
                    template.DialogMessageId, template.Replies)));

            Console.WriteLine($"[NPC] Diálogo del {npc.NpcId}: pregunta {template.DialogMessageId}, " +
                              $"{template.Replies.Length} respuestas.");
        }

        /// <summary>
        /// El jugador ha elegido una respuesta (ioy).
        ///
        /// El diálogo se cierra siempre, se acepte o se rechace: el kld sale las cuatro veces de la
        /// captura y va DELANTE de los kamas.
        /// </summary>
        public static async Task ReplyAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ioy = ConnectionProtocol.ReadPayload(payload, Op.Ioy);
            if (ioy == null) return;

            long reply = 0;
            foreach (var field in ProtoMessage.Parse(ioy).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) reply = field.VarIntValue;
            }


            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                    ConnectionProtocol.NpcDialogCloseReason)));

            if (reply != KamasMountainReply)
            {
                Console.WriteLine($"[NPC] Respuesta {reply}: no da nada.");
                return;
            }

            GameState.Kamas += KamasMountainReward;
            DatabaseManager.SaveCurrentCharacter();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(GameState.Kamas)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                    ConnectionProtocol.KamasReceivedMessage, KamasMountainReward.ToString())));

            Console.WriteLine($"[NPC] La montaña de kamas paga {KamasMountainReward}; " +
                              $"ahora tiene {GameState.Kamas}.");
        }

        /// <summary>
        /// Comprar (kea). El cliente manda sólo el objeto y la cantidad: el precio lo pone el
        /// servidor, que es el que mandó el catálogo.
        /// </summary>
        public static async Task BuyAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? kea = ConnectionProtocol.ReadPayload(payload, Op.Kea);
            if (kea == null) return;

            int gid = 0;
            long quantity = 1;
            foreach (var field in ProtoMessage.Parse(kea).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) gid = (int)field.VarIntValue;
                else if (field.FieldNumber == 2) quantity = field.VarIntValue;
            }

            if (gid == 0 || quantity <= 0) return;

            if (OpenShop == 0)
            {
                Console.WriteLine($"[NPC] Compra del objeto {gid} sin tienda abierta.");
                return;
            }

            // Y que el vendedor siga estando donde está el jugador.
            //
            // La tienda se quedaba abierta al cambiar de mapa —Forget() existía pero no lo
            // llamaba nadie— así que se podía hablar con un vendedor, irse andando tres mapas y
            // seguir comprando de su catálogo desde el otro lado del mundo. Mirar sólo
            // «OpenShop != 0» no basta: eso dice que hubo una tienda, no que la haya ahora.
            //
            // Se comprueba aquí y no sólo al cambiar de mapa a propósito. Acordarse de llamar a
            // Forget() en los siete sitios desde los que se cambia de mapa —andar, zaap, zaapi,
            // puerta de mazmorra, casa, anomalía, fin de combate— es acordarse siete veces; esto
            // es una sola y no depende de por dónde se haya salido.
            if (Managers.Npcs.Find(SessionContext.State.MapId, OpenShop) == null)
            {
                Console.WriteLine($"[NPC] El vendedor {OpenShopNpc} no está en el mapa " +
                                  $"{SessionContext.State.MapId}: la tienda se cierra.");
                Forget();
                return;
            }

            // Que el vendedor que está abierto lo tenga de verdad: el catálogo lo mandamos nosotros,
            // así que pedir otra cosa no es una compra válida.
            bool onSale = false;
            foreach (int sold in NpcShops.CatalogueOf(OpenShopNpc))
            {
                if (sold == gid) { onSale = true; break; }
            }
            if (!onSale)
            {
                Console.WriteLine($"[NPC] El vendedor {OpenShopNpc} no vende el objeto {gid}.");
                return;
            }

            long price = NpcShops.PriceOf(gid) * quantity;
            if (GameState.Kamas < price)
            {
                Console.WriteLine($"[NPC] El objeto {gid} cuesta {price} y sólo hay {GameState.Kamas}.");
                return;
            }

            long uid = NextUid();
            string effects = NpcShops.EffectsOf(gid);

            if (!DatabaseManager.InsertCharacterItem(uid, GameState.CharacterId, gid, (int)quantity,
                                                     Equipment.Bag, effects))
            {
                Console.WriteLine($"[NPC] No se ha podido guardar el objeto {gid}.");
                return;
            }

            Equipment.Add(uid, gid, (int)quantity, Equipment.Bag, effects);

            GameState.Kamas -= price;
            DatabaseManager.SaveCurrentCharacter();

            var bought = new HavenBagStore.StoredItem
            {
                Uid = uid,
                Gid = gid,
                Quantity = (int)quantity,
                Effects = effects,
            };

            // El orden es el medido, y las dos tandas de ivf/iun también: el servidor real las manda
            // idénticas antes y después del kdg. Como las dos llevan el total y no un incremento,
            // repetirlas no descuadra nada.
            long capacity = 1000 + 5L * GameState.StatStrength;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                    ConnectionProtocol.PurchaseMessage,
                    gid.ToString(), uid.ToString(), quantity.ToString(), price.ToString())));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(GameState.Kamas)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iua, ConnectionProtocol.BuildItemArrived(3, bought)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iun, ConnectionProtocol.BuildPods(0, capacity)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kdg));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(GameState.Kamas)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iun, ConnectionProtocol.BuildPods(0, capacity)));

            Console.WriteLine($"[NPC] Comprado el objeto {gid} x{quantity} (uid {uid}) por {price}; " +
                              $"quedan {GameState.Kamas} kamas.");
        }

        /// <summary>
        /// El botón de cerrar de la tienda (kla vacío).
        ///
        /// El cliente lo manda DOS veces seguidas, separadas menos de un milisegundo, y el servidor
        /// real contesta un solo khd. Por eso se cierra a la primera y la segunda se cae sola: al
        /// no haber ya tienda abierta, GameNodeProxy la lleva al zaap y ése tampoco tiene nada
        /// abierto.
        /// </summary>
        public static async Task CloseShopAsync(NetworkStream stream)
        {
            OpenShop = 0;
            OpenShopNpc = 0;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Khd, ConnectionProtocol.BuildShopClosed()));
        }

        /// <summary>
        /// Al cambiar de mapa no queda nada abierto.
        ///
        /// Lo llama WorldMoveHandler al mudarse y FightHandler al empezar un combate. Aun así,
        /// BuyAsync vuelve a comprobar que el vendedor esté en el mapa: esto es por orden, no
        /// por seguridad, y de la seguridad se encarga quien cobra.
        /// </summary>
        public static void Forget()
        {
            OpenShop = 0;
            OpenShopNpc = 0;
        }

        /// <summary>
        /// El uid del objeto que se acaba de comprar.
        ///
        /// Esto leia MAX(Uid) de la base en cada compra, lo cual repartia bien pero no era
        /// atomico: dos compras a la vez leen el mismo maximo y devuelven el mismo numero, y la
        /// segunda pisa la fila de la primera. Ahora lo reparte DatabaseManager para todo el
        /// servidor, de una vez y con un contador.
        /// </summary>
        private static long NextUid() => DatabaseManager.NextItemUid();
    }
}
