using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Handlers
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
        /// Si hay una conversación de NPC abierta ahora mismo.
        /// </summary>
        /// <remarks>
        /// Lo pregunta la cadena del kla para saber de quién es la X que se acaba de pulsar. Sin
        /// esto, la X de una conversación se la quedaba el zaap —que es el caso por defecto de esa
        /// cadena— y salía un kld con la razón 10; la de cerrar una conversación es la 1, así que
        /// el cliente dejaba la ventana puesta y no había forma de salir salvo eligiendo una
        /// respuesta.
        /// </remarks>
        public static bool IsDialogueOpen => SessionContext.State.OpenDialogueNpcId != 0;

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

            // Si este vendedor cobra en fichas, se le dice al cliente cuál es la moneda. Sin
            // esto el f3 no viaja y el cliente pinta el precio en kamas, que es lo de siempre.
            var tokenShop = TokenShops.Of(npc.NpcId);
            byte[] kbd = ConnectionProtocol.BuildShop(npc.ContextualId, catalogue, tokenShop);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kbd, kbd));

            string moneda = tokenShop == null ? "kamas" : $"la ficha {tokenShop.TokenGid}";
            Console.WriteLine($"[NPC] Tienda del {npc.NpcId}: {catalogue.Count} objetos, " +
                              $"{kbd.Length} bytes, se paga en {moneda}.");
        }

        /// <summary>
        /// «Hasta luego.», la despedida que se le pone a quien no trae ninguna respuesta.
        ///
        /// No es un número inventado: es la respuesta MÁS USADA de todo el juego, la llevan 62
        /// NPCs de los 6.467, y su texto en el catálogo del cliente es exactamente «Hasta luego.».
        /// Se eligió por eso y no por lo que dice: hace falta un id de respuesta que el cliente
        /// sepa resolver a un texto, y éste lo es en los cinco idiomas.
        /// </summary>
        private const long RespuestaDeDespedida = 7846;

        /// <summary>
        /// La ventana de diálogo y su primera pregunta.
        ///
        /// Si hay una conversación escrita para este NPC, se abre por donde ella diga y con las
        /// respuestas que ella diga. Si no, se hace lo de siempre: la frase de la plantilla y TODAS
        /// sus respuestas de golpe, que es lo que hace que Snori Nairb ofrezca treinta y nueve.
        /// </summary>
        private static async Task OpenDialogAsync(NetworkStream stream, Npcs.Spawn npc, long mapId)
        {
            var template = Npcs.TemplateOf(npc.NpcId);
            var escrito = NpcDialogues.For(npc.NpcId, mapId);
            var primera = escrito?.First();

            if (primera == null && (template == null || template.DialogMessageId == 0))
            {
                Console.WriteLine($"[NPC] El {npc.NpcId} no tiene diálogo en su plantilla.");
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, npc.ContextualId)));

            long pregunta = primera?.Message ?? template!.DialogMessageId;
            long[] respuestas = primera != null
                ? LasQueTocan(primera)
                : SinArbolEscrito(template!);

            // Y si este NPC guarda la puerta de una mazmorra, sus dos opciones por delante.
            //
            // Sin esto, un guardian sin arbol escrito cae en SinArbolEscrito, que devuelve LA
            // ULTIMA respuesta de la plantilla y nada mas: Mawy Ingals declara diecinueve y lo
            // unico que salia en pantalla era "No.". El manojo va siempre que la mazmorra lo
            // acepte; la llave suelta solo si esta en la bolsa, porque una opcion que no puede
            // funcionar no se distingue de una puerta rota. Ver DungeonHandler.DoorReplies.
            long[] puerta = DungeonHandler.DoorReplies(npc.NpcId, mapId);
            if (puerta.Length > 0)
            {
                var todas = new List<long>(puerta.Length + respuestas.Length);
                todas.AddRange(puerta);

                // Detras, lo que ya se iba a decir, sin repetir ninguna. La despedida importa:
                // sin una salida el jugador se queda con una ventana que no cierra.
                foreach (long r in respuestas)
                {
                    if (!todas.Contains(r)) todas.Add(r);
                }

                respuestas = todas.ToArray();
            }

            // Se apunta por dónde va la conversación. Sin esto el ioy que llega después no se puede
            // situar: trae el id de la respuesta y nada más, ni de qué NPC ni de qué frase venía.
            SessionContext.State.OpenDialogueNpcId = npc.NpcId;
            SessionContext.State.OpenDialogueMapId = mapId;
            SessionContext.State.OpenDialogueMessage = pregunta;

            await PreguntarAsync(stream, pregunta, respuestas, template);

            // Y si alguna misión en curso pedía justamente venir a ver a éste, ya está.
            await Managers.Quests.OnTalkingToAsync(stream, npc.NpcId);

            Console.WriteLine($"[NPC] Diálogo del {npc.NpcId}: pregunta {pregunta}, " +
                              $"{Math.Max(respuestas.Length, 1)} respuestas" +
                              (escrito != null ? $" (escrito, {escrito.Lines.Count} frases)" : " (de la plantilla)") + ".");
        }

        /// <summary>
        /// Manda una pregunta con sus respuestas, y se asegura de que haya al menos una.
        /// </summary>
        /// <remarks>
        /// UN DIÁLOGO SIN RESPUESTAS NO SE PUEDE CERRAR. Cuando la lista va vacía, el cliente pinta
        /// él solo un «Marcharte.», y ese botón NO manda el ioy: la ventana se queda puesta y no hay
        /// forma de salir más que reconectando. Se ve con el Bontariano enfadado, que tiene un
        /// mensaje y cero respuestas en su plantilla.
        ///
        /// Así que siempre va al menos una respuesta de verdad, porque una respuesta de verdad sí
        /// manda el ioy y entonces contestamos con el kld que cierra. Está aquí y no en los dos
        /// sitios que preguntan porque ahora hay dos: la primera frase y cada una de las que siguen.
        /// </remarks>
        private static async Task PreguntarAsync(NetworkStream stream, long pregunta, long[] respuestas,
                                                 Npcs.Template? plantilla = null)
        {
            if (respuestas.Length == 0)
            {
                // Una de las suyas, si tiene alguna. El cliente resuelve el texto de una respuesta
                // desde la plantilla DEL NPC con el que habla, así que mandarle una que ese NPC no
                // declara le pinta un botón en blanco: es lo que salía con la Brakmariana enfadada.
                //
                // Y si no tiene ninguna, no se inventa nada: se manda la lista vacía y el cliente
                // pinta su propio «Marcharte.». Salir de ahí es cosa de la X, que ahora sí se
                // atiende.
                respuestas = plantilla != null && plantilla.Replies.Length > 0
                    ? new[] { plantilla.Replies[^1] }
                    : Array.Empty<long>();
            }

            if (respuestas.Length == 0)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ios, ConnectionProtocol.BuildNpcQuestion(pregunta,
                        Array.Empty<long>())));
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ios, ConnectionProtocol.BuildNpcQuestion(pregunta, respuestas)));
        }

        /// <summary>
        /// El jugador ha cerrado la ventana con la X (kla).
        /// </summary>
        /// <remarks>
        /// Sin esto la X no cerraba nada. El opcode no estaba ni declarado, así que el paquete
        /// caía en la rama de desconocidos y el servidor no contestaba; el cliente se queda con la
        /// ventana puesta hasta que llega el kld. Con los NPCs que no ofrecen ninguna respuesta
        /// —la Brakmariana enfadada, el Bontariano enfadado— eso dejaba al jugador encerrado en la
        /// conversación sin más salida que reconectar.
        ///
        /// Sale 192 veces en las 401 capturas y siempre va vacío: no dice de qué NPC viene, así
        /// que se cierra lo que hubiera abierto, que es lo único que puede haber.
        /// </remarks>
        public static async Task CloseAsync(NetworkStream stream, byte[] payload)
        {
            CerrarConversacion();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                    ConnectionProtocol.NpcDialogCloseReason)));
        }

        /// <summary>
        /// Las respuestas de una frase que este personaje debe ver.
        /// </summary>
        /// <remarks>
        /// Una respuesta que pertenece a una misión no se le ofrece a quien no la lleva, y una que
        /// pertenece a un paso no se ofrece antes de estar en ese paso. Lo dice el árbol escrito;
        /// aquí sólo se le pregunta al diario del personaje.
        /// </remarks>
        private static long[] LasQueTocan(DialogueLine linea)
        {
            var diario = SessionContext.State.Quests;
            if (diario == null) return linea.Replies();

            return linea.RepliesFor(
                diario.Active,
                diario.Finished,
                (mision, paso) => diario.Run(mision)?.StepId == paso);
        }

        /// <summary>
        /// Qué ofrecer cuando no hay conversación escrita para este NPC.
        /// </summary>
        /// <remarks>
        /// <b>Una sola respuesta, no las que declare la plantilla.</b> Un NPC declara TODAS las
        /// respuestas de TODOS sus árboles juntas —Snori Nairb tiene treinta y nueve— y mandarlas
        /// de golpe le enseña al jugador respuestas de misiones que no ha empezado, de fases a las
        /// que no ha llegado y de tres conversaciones distintas mezcladas. Y además ninguna lleva a
        /// ningún sitio, porque sin árbol no hay a dónde llevar: cualquiera de las treinta y nueve
        /// cierra la ventana igual.
        ///
        /// Así que se ofrece una para despedirse y ya está. Es menos de lo que había y es lo único
        /// que no miente. Lo que hace falta de verdad es el árbol, y eso se escribe en el editor.
        /// </remarks>
        private static long[] SinArbolEscrito(Npcs.Template plantilla)
            => plantilla.Replies.Length > 0
                ? new[] { plantilla.Replies[^1] }
                : Array.Empty<long>();

        /// <summary>Deja de haber conversación abierta.</summary>
        private static void CerrarConversacion()
        {
            SessionContext.State.OpenDialogueNpcId = 0;
            SessionContext.State.OpenDialogueMapId = 0;
            SessionContext.State.OpenDialogueMessage = 0;
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

            // ¿La frase en la que está reparte alguna misión? Se mira ANTES de seguir, porque
            // seguir cambia OpenDialogueMessage, y antes de CerrarConversacion, que lo pone a cero.
            //
            // Va después de elegir y no al llegar a la frase porque así está en la captura: el
            // servidor baja la conversación hasta la 50071, el jugador elige la 66788, y sólo
            // entonces sale el ief con la misión 2432.
            // ¿Esta respuesta concreta da una misión? Lo dice el árbol escrito. Si no hay árbol,
            // se cae en la regla vieja: cualquier respuesta de la frase que el paso nombra.
            var frase = NpcDialogues.For(SessionContext.State.OpenDialogueNpcId,
                                         SessionContext.State.OpenDialogueMapId)
                                    ?.Line(SessionContext.State.OpenDialogueMessage);
            var elegidaAhora = frase?.Choice(reply);

            if (elegidaAhora != null && elegidaAhora.StartsQuest != 0)
            {
                await Managers.Quests.StartAsync(stream, elegidaAhora.StartsQuest);
            }
            else if (frase == null)
            {
                await Managers.Quests.OnReplyAsync(stream, SessionContext.State.OpenDialogueMessage);
            }

            // ¿Y este NPC guarda la puerta de una mazmorra? Si entra, la conversación ha acabado en
            // un cambio de mapa y no hay nada más que decirle.
            long dondeHabla = SessionContext.State.OpenDialogueMapId;
            if (dondeHabla == 0) dondeHabla = SessionContext.State.MapId;
            if (await DungeonHandler.AtTheDoorAsync(stream, dondeHabla, reply))
            {
                CerrarConversacion();
                return;
            }

            // ¿Esta respuesta lleva a otra frase? Es lo único que hace de esto una conversación en
            // vez de una pregunta suelta, y sólo lo puede decir el árbol escrito a mano: el cliente
            // trae las frases y las respuestas pero nunca cuál va con cuál.
            if (await SeguirLaConversacionAsync(stream, reply)) return;

            CerrarConversacion();

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
        /// Si la respuesta elegida lleva a otra frase, la manda y dice que sí.
        /// </summary>
        /// <remarks>
        /// Devuelve <c>false</c> cuando la conversación termina aquí, que es lo que pasa con todo
        /// NPC sin árbol escrito: entonces el que llama cierra con el kld como siempre.
        ///
        /// El ioy no dice de qué NPC viene ni de qué frase, así que se sitúa por el estado de
        /// sesión. Si no cuadra —el jugador tiene otra ventana abierta, o ninguna— se cierra, que
        /// es lo seguro: dejar la ventana puesta es dejarla sin salida.
        /// </remarks>
        private static async Task<bool> SeguirLaConversacionAsync(NetworkStream stream, long reply)
        {
            var estado = SessionContext.State;
            if (estado.OpenDialogueNpcId == 0 || reply == 0) return false;

            var conversacion = NpcDialogues.For(estado.OpenDialogueNpcId, estado.OpenDialogueMapId);
            var frase = conversacion?.Line(estado.OpenDialogueMessage);
            var elegida = frase?.Choice(reply);
            if (elegida == null || elegida.Ends) return false;

            var siguiente = conversacion!.Line(elegida.Next);
            if (siguiente == null)
            {
                // El editor comprueba esto antes de guardar, así que llegar aquí quiere decir que
                // el fichero se editó a mano. Se dice y se cierra en vez de dejar al jugador
                // mirando una ventana que no responde.
                Console.WriteLine($"[NPC] La respuesta {reply} del {estado.OpenDialogueNpcId} lleva " +
                                  $"a la frase {elegida.Next}, que no está escrita. Se cierra.");
                return false;
            }

            estado.OpenDialogueMessage = siguiente.Message;
            await PreguntarAsync(stream, siguiente.Message, LasQueTocan(siguiente),
                                 Npcs.TemplateOf(estado.OpenDialogueNpcId));

            Console.WriteLine($"[NPC] La respuesta {reply} lleva a la frase {siguiente.Message}, " +
                              $"con {Math.Max(siguiente.Choices.Count, 1)} respuestas.");
            return true;
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

            // Con qué se paga aquí. Sin tienda de fichas es lo de siempre: kamas.
            var tokenShop = TokenShops.Of(OpenShopNpc);
            long price = tokenShop == null
                ? NpcShops.PriceOf(gid) * quantity
                : TokenShops.PriceOf(tokenShop, gid) * quantity;

            // La pila de fichas del jugador, si es que la tiene. Se busca por plantilla en el
            // inventario: una ficha es un recurso y se apila, así que hay una sola.
            long tokenUid = 0;
            int tokenLeft = 0;
            if (tokenShop != null)
            {
                foreach (var item in GameState.GetInventoryCopy())
                {
                    if (item.ItemId != tokenShop.TokenGid || item.Position != Equipment.Bag) continue;
                    tokenUid = item.Uid;
                    tokenLeft = item.Quantity;
                    break;
                }

                if (tokenUid == 0 || tokenLeft < price)
                {
                    Console.WriteLine($"[NPC] El objeto {gid} cuesta {price} ficha(s) de " +
                                      $"{tokenShop.TokenGid} y sólo hay {tokenLeft}.");
                    return;
                }
            }
            else if (GameState.Kamas < price)
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

            if (tokenShop == null)
            {
                GameState.Kamas -= price;
                DatabaseManager.SaveCurrentCharacter();
            }
            else
            {
                // Las fichas se gastan quitándolas del inventario, igual que al destruir parte de
                // una pila. Si la compra se lleva la última, DestroyCharacterItem borra la fila.
                DatabaseManager.DestroyCharacterItem(GameState.CharacterId, tokenUid, (int)price);
                Equipment.Remove(tokenUid, (int)price);
                tokenLeft -= (int)price;
                GameState.SetInventory(DatabaseManager.LoadInventory(GameState.CharacterId));
            }

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

            if (tokenShop == null)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                        ConnectionProtocol.PurchaseMessage,
                        gid.ToString(), uid.ToString(), quantity.ToString(), price.ToString())));

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(GameState.Kamas)));
            }
            else
            {
                // El aviso de la compra en fichas y el nuevo total de la pila. El ivj lleva LO QUE
                // QUEDA, no lo gastado: se ve en el mercadillo de runas de la captura, donde una
                // misma pila va 107 -> 117 -> 217 -> 1217.
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                        ConnectionProtocol.TokenPurchaseMessage,
                        gid.ToString(), uid.ToString(), quantity.ToString(), price.ToString(),
                        tokenShop.TokenGid.ToString(), "0")));

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ivj,
                        ConnectionProtocol.BuildItemQuantity(tokenUid, tokenLeft)));
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iua, ConnectionProtocol.BuildItemArrived(3, bought)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iun, ConnectionProtocol.BuildPods(0, capacity)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kdg));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(GameState.Kamas)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iun, ConnectionProtocol.BuildPods(0, capacity)));

            Console.WriteLine(tokenShop == null
                ? $"[NPC] Comprado el objeto {gid} x{quantity} (uid {uid}) por {price}; " +
                  $"quedan {GameState.Kamas} kamas."
                : $"[NPC] Comprado el objeto {gid} x{quantity} (uid {uid}) por {price} ficha(s) " +
                  $"de {tokenShop.TokenGid}; quedan {tokenLeft}.");
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
        /// Al cambiar de mapa no queda nada abierto: ni la tienda ni la conversación.
        /// </summary>
        /// <remarks>
        /// La conversación se limpia aquí desde que la X de un diálogo se atiende ANTES que la del
        /// zaap. Con el estado rancio —el jugador abre una conversación y se va sin cerrarla— la
        /// siguiente X, la que era del zaap, se la quedaría el diálogo y la lista de destinos no
        /// se cerraría. Es el mismo fallo que tenía la X del diálogo, del revés.
        ///
        /// Y se llama desde donde se manda la lista de actores, que es por donde pasan las cinco
        /// maneras de llegar a un mapa. El comentario anterior decía que lo llamaban
        /// WorldMoveHandler y FightHandler; lo de FightHandler no era cierto, no hay ninguna
        /// llamada suya en todo el repositorio.
        ///
        /// BuyAsync vuelve a comprobar que el vendedor esté en el mapa: esto es por orden, no por
        /// seguridad, y de la seguridad se encarga quien cobra.
        /// </remarks>
        public static void Forget()
        {
            OpenShop = 0;
            OpenShopNpc = 0;
            CerrarConversacion();
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
