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
    /// Talking to an NPC and buying from him.
    ///
    /// All of this is measured from the tournament server's capture, where there are fifty-one vendors
    /// spread over seven maps and the kamas mountain. There are four opcodes for the shop and four for
    /// the dialogue, and the server always answers by PUSH, without pairing request ids:
    ///
    ///   client   iov { f1: action, f2: map, f3: contextual id }    clicked the NPC
    ///
    ///   if the action is 1 or 11 (buy):
    ///   server   kbd { f1 (repeated): the whole catalogue, f2: contextual id }
    ///   client   kea { f1: item, f2: quantity }                    buy
    ///   server   lqn, ivf, iua, iun, kdg, ivf, iun
    ///   client   kla (empty)                                       close
    ///   server   khd { f3: 11 }
    ///
    ///   if the action is 3 (talk):
    ///   server   ioc { f4: map, f5: contextual id }
    ///   server   ios { f1: question, f2 (repeated): the answers }
    ///   client   ioy { f1: the chosen answer }
    ///   server   kld { f1: 1 }  and whatever that answer gives
    ///
    /// The iov's f1 is not a message type: it is the action id of the NPC's template, the same number
    /// that comes in its actions[]. It fits the sixty-five iov of the capture. An NPC that does not
    /// declare the action does not even offer it in the menu.
    /// </summary>
    /// <remarks>
    /// What was here before was 3.6.4.3's and used ilr, ilu, ilq, kjl, kjn, lxh and kns. Not one of
    /// those seven opcodes appears even once in the 3.6.10.10 capture.
    /// </remarks>
    public static class NpcHandler
    {
        /// <summary>Which NPC has the shop open right now, or zero.</summary>
        private static long OpenShop
        {
            get => SessionContext.State.OpenNpcShopId;
            set => SessionContext.State.OpenNpcShopId = value;
        }

        /// <summary>Which vendor it is, to know whether he sells what the client asks for.</summary>
        private static int OpenShopNpc
        {
            get => SessionContext.State.OpenNpcShopNpcId;
            set => SessionContext.State.OpenNpcShopNpcId = value;
        }

        public static bool IsShopOpen => OpenShop != 0;

        /// <summary>
        /// Whether there is an NPC conversation open right now.
        /// </summary>
        /// <remarks>
        /// The kla chain asks it to know whose is the X just pressed. Without this, a conversation's X was
        /// taken by the zaap -- which is that chain's default case -- and a kld with reason 10 went out;
        /// the one for closing a conversation is 1, so the client left the window up and there was no way
        /// out except choosing an answer.
        /// </remarks>
        public static bool IsDialogueOpen => SessionContext.State.OpenDialogueNpcId != 0;

        /// <summary>
        /// Where bought items are numbered from.
        ///
        /// Each thing that makes items has its band: 900,000,000 the test inventory, 950,000,000 the haven
        /// bag lottery and 960,000,000 the gifted appearances. That last band is DELETED whole by
        /// dotar_apariencias.py every time it is run again, so what is bought cannot fall there or it
        /// would disappear without warning.
        /// </summary>
        private const long FirstUid = 970000000L;

        /// <summary>
        /// What the kamas mountain gives.
        ///
        /// The figure is not in any client data nor in the NPC's template: it is a server constant. In the
        /// capture it was collected three times and all three went up by exactly the same, from zero to
        /// 50,000,000, from 49,999,998 to 99,999,998 and from there to 149,999,998.
        /// </summary>
        private const long KamasMountainReward = 50_000_000L;

        /// <summary>
        /// The answer that pays. 70285 is "Hacerte con esos millones de kamas que no sirven a nadie" and
        /// 70286, the one next to it, leaves without collecting: the server answers the kld and nothing
        /// else.
        /// </summary>
        private const long KamasMountainReply = 70285;

        /// <summary>
        /// The client has clicked an NPC (iov). Depending on the action, the shop or the dialogue opens.
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

        /// <summary>The whole catalogue, in one go, which is how the real server sends it.</summary>
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

            // If this vendor charges in tokens, the client is told which the currency is. Without
            // this f3 does not travel and the client draws the price in kamas, which is the usual.
            var tokenShop = TokenShops.Of(npc.NpcId);
            byte[] kbd = ConnectionProtocol.BuildShop(npc.ContextualId, catalogue, tokenShop);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kbd, kbd));

            string moneda = tokenShop == null ? "kamas" : $"la ficha {tokenShop.TokenGid}";
            Console.WriteLine($"[NPC] Tienda del {npc.NpcId}: {catalogue.Count} objetos, " +
                              $"{kbd.Length} bytes, se paga en {moneda}.");
        }

        /// <summary>
        /// «Hasta luego.» (See you.), the farewell given to whoever carries no answer.
        ///
        /// It is not a made-up number: it is the MOST USED answer of the whole game, 62 of the 6,467 NPCs
        /// carry it, and its text in the client's catalogue is exactly «Hasta luego.». It was chosen for
        /// that and not for what it says: an answer id the client can resolve to a text is needed, and
        /// this one is, in all five languages.
        /// </summary>
        private const long RespuestaDeDespedida = 7846;

        /// <summary>
        /// The dialogue window and its first question.
        ///
        /// If there is a conversation written for this NPC, it opens where it says and with the answers it
        /// says. If not, the usual is done: the template's sentence and ALL its answers at once, which is
        /// what makes Snori Nairb offer thirty-nine.
        /// </summary>
        private static async Task OpenDialogAsync(NetworkStream stream, Npcs.Spawn npc, long mapId)
        {
            // Is it a luminomachine? Then neither template nor written tree: what it says and what it
            // offers come from the light the floor has and the salt the player carries.
            if (npc.NpcId == Luminomachine.NpcId)
            {
                await OpenMachineAsync(stream, npc, mapId);
                return;
            }

            // And the raid chest has no tree either: what it offers depends on whether you bring treasures.
            if (npc.NpcId == RaidChest.NpcId)
            {
                await OpenChestAsync(stream, npc, mapId);
                return;
            }

            // The kanojedo's master puch: the six levels, and then how many.
            if (npc.NpcId == Kanojedo.MasterNpc)
            {
                await OpenMasterAsync(stream, npc, mapId);
                return;
            }

            // The Dispensador de favores of a dream favour opens on what the favour is at.
            if (npc.NpcId == Managers.Dreams.FavorNpc)
            {
                await OpenFavorAsync(stream, npc, mapId);
                return;
            }

            var template = Npcs.TemplateOf(npc.NpcId);
            // Authored tree first; otherwise accept/refuse for quests this NPC can hand over now.
            bool arbolEscrito = NpcDialogues.For(npc.NpcId, mapId) != null;
            var escrito = NpcDialogues.ForTalk(npc.NpcId, mapId);
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

            // And if this NPC guards a dungeon's door, his two options up front.
            //
            // Without this, a guardian without a written tree falls into SinArbolEscrito, which
            // returns THE LAST answer of the template and nothing else: Mawy Ingals declares nineteen
            // and the only thing on screen was "No.". The keyring goes whenever the dungeon accepts
            // it; the loose key only if it is in the bag, because an option that cannot work is not
            // distinguishable from a broken door. See DungeonHandler.DoorReplies.
            long[] puerta = DungeonHandler.DoorReplies(npc.NpcId, mapId);
            if (puerta.Length > 0)
            {
                var todas = new List<long>(puerta.Length + respuestas.Length);
                todas.AddRange(puerta);

                // After them, whatever was going to be said, without repeating any. The farewell matters:
                // without a way out the player is left with a window that does not close.
                foreach (long r in respuestas)
                {
                    if (!todas.Contains(r)) todas.Add(r);
                }

                respuestas = todas.ToArray();
            }

            // And a banker offers the bank: his reply in front of the rest, the way the Bontarian
            // banker offers it in frame 74 of the bank capture. See BankHandler.
            var banker = Bankers.Of(npc.NpcId);
            if (banker != null) respuestas = BankHandler.WithTheBankReply(banker, respuestas);

            // Where the conversation is going is noted. Without this the ioy that comes later cannot be
            // placed: it carries the answer's id and nothing else, not which NPC nor which sentence it
            // came from.
            SessionContext.State.OpenDialogueNpcId = npc.NpcId;
            SessionContext.State.OpenDialogueMapId = mapId;
            SessionContext.State.OpenDialogueMessage = pregunta;

            await PreguntarAsync(stream, pregunta, respuestas, template,
                                 escrito?.Line(pregunta), banker);

            // And if some quest in progress asked precisely to come and see this one, that is done.
            await Managers.Quests.OnTalkingToAsync(stream, npc.NpcId);

            string origen = arbolEscrito ? $" (authored, {escrito!.Lines.Count} lines)"
                : escrito != null ? " (accept/refuse fallback)"
                : " (template)";
            Console.WriteLine($"[NPC] Dialogue for {npc.NpcId}: question {pregunta}, " +
                              $"{Math.Max(respuestas.Length, 1)} replies{origen}.");
        }

        /// <summary>
        /// A luminomachine's window: what it has left to light and what it costs.
        /// </summary>
        /// <remarks>
        /// The machine has no conversation written anywhere and does not need one: each of its seventy-six
        /// answers says its own thing -- «Dejar 4 sales de las profundidades para iluminar la segunda
        /// franja» -- and the only thing to decide is which ones to show. That is settled by
        /// <see cref="Luminomachine.RepliesFor"/> with two numbers: how much light the floor has and how
        /// much salt whoever asks carries.
        ///
        /// Outside a raid the machine is dead. It is not that it hides: it is on the map and can be talked
        /// to, but there is no instance at all to light, so the only thing it offers is not touching it.
        /// Promising light that cannot be switched on would be worse than keeping quiet.
        /// </remarks>
        private static async Task OpenMachineAsync(NetworkStream stream, Npcs.Spawn npc, long mapId)
        {
            int floor = Luminomachines.FloorOn(mapId);
            int light = floor == 0 ? -1 : Luminomachines.LightOn(GameState.CharacterId, floor);
            int salt = Managers.Equipment.HowMany(Luminomachine.SaltItem);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, npc.ContextualId)));

            long pregunta;
            long[] respuestas;

            if (light < 0)
            {
                pregunta = Luminomachine.LitMessage;
                respuestas = new[] { Luminomachine.DontTouchReply };
            }
            else
            {
                pregunta = Luminomachine.MessageAt(floor, light);
                respuestas = Lista(Luminomachine.RepliesFor(floor, light, salt));
            }

            SessionContext.State.OpenDialogueNpcId = npc.NpcId;
            SessionContext.State.OpenDialogueMapId = mapId;
            SessionContext.State.OpenDialogueMessage = pregunta;

            await PreguntarAsync(stream, pregunta, respuestas);

            Console.WriteLine($"[Luminomáquinas] Planta {floor}, luz {light}, {salt} sales: " +
                              $"pregunta {pregunta}, {respuestas.Length} respuestas.");
        }

        /// <summary>
        /// What a luminomachine answer does: charge the salt and raise the light.
        /// </summary>
        /// <remarks>
        /// The salt is charged BEFORE touching the light, and if the light has changed in between it is
        /// given back. With eight people on the same floor that is not a theoretical oddity: two who talk
        /// to the same machine at once both see the same offer, and the second to answer would be paying
        /// for a band that is already lit.
        /// </remarks>
        private static async Task MachineReplyAsync(NetworkStream stream, long reply)
        {
            var elegida = Luminomachine.Read(reply);

            CerrarConversacion();
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                    ConnectionProtocol.NpcDialogCloseReason)));

            // Not touching it, or going off to look for more salt: both leave without paying anything.
            if (elegida == null || !elegida.Value.Buys) return;

            var compra = elegida.Value;
            if (!await Managers.Equipment.TakeAsync(stream, Luminomachine.SaltItem, compra.Cost))
            {
                await DecirleAsync(stream, CommandTexts.Get("light.nosalt", compra.Cost));
                return;
            }

            int luz = Luminomachines.Deposit(GameState.CharacterId, compra.Floor, compra.From, compra.To);
            if (luz < 0)
            {
                await Managers.Equipment.GiveAsync(stream, Luminomachine.SaltItem, compra.Cost);
                await DecirleAsync(stream, CommandTexts.Get("light.changed"));
                return;
            }

            await DecirleAsync(stream, CommandTexts.Get("light.lit", compra.Floor, luz, compra.Cost));
        }

        /// <summary>
        /// The raid chest: dropping the treasures, getting closer, or turning back.
        /// </summary>
        /// <remarks>
        /// Dropping the treasures only comes up when some are carried, for the same reason as at the
        /// machine. And outside a raid the chest belongs to nobody: there is no score to raise nor raid to
        /// finish, so the only thing it offers is stepping back.
        /// </remarks>
        private static async Task OpenChestAsync(NetworkStream stream, Npcs.Spawn npc, long mapId)
        {
            long who = GameState.CharacterId;
            long score = Managers.RaidChests.ScoreOf(who);
            var traidos = score < 0 ? new Dictionary<int, int>() : Managers.RaidTreasures.InTheBag();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, npc.ContextualId)));

            await PreguntaDelCofreAsync(stream, npc, mapId, score, traidos.Count > 0);

            Console.WriteLine($"[Raids] Cofre del mapa {mapId}: {score} puntos, " +
                              $"{traidos.Count} clases de tesoro encima.");
        }

        /// <summary>The chest's first screen, which is put up again after dropping.</summary>
        private static async Task PreguntaDelCofreAsync(NetworkStream stream, Npcs.Spawn npc, long mapId,
                                                        long score, bool carrying)
        {
            long[] respuestas = score < 0
                ? new[] { RaidChest.StepBack }
                : Lista(RaidChest.FirstReplies(carrying));

            SessionContext.State.OpenDialogueNpcId = npc.NpcId;
            SessionContext.State.OpenDialogueMapId = mapId;
            SessionContext.State.OpenDialogueMessage = RaidChest.Vibrating;

            await PreguntarAsync(stream, RaidChest.Vibrating, respuestas);
        }

        /// <summary>
        /// What each answer of the chest does.
        /// </summary>
        /// <remarks>
        /// What REALLY leaves the bag is charged, not what was offered: between the window opening and the
        /// answer arriving, a stack may have gone somewhere else, and scoring what was not delivered would
        /// be scoring thin air.
        /// </remarks>
        private static async Task ChestReplyAsync(NetworkStream stream, long reply)
        {
            long who = GameState.CharacterId;
            long mapa = SessionContext.State.OpenDialogueMapId;
            var npc = CofreDelMapa(mapa);

            if (reply == RaidChest.DropTreasures)
            {
                var traidos = Managers.RaidTreasures.InTheBag();
                var entregados = new Dictionary<int, int>();
                foreach (var kv in traidos)
                {
                    if (await Managers.Equipment.TakeAsync(stream, kv.Key, kv.Value))
                    {
                        entregados[kv.Key] = kv.Value;
                    }
                }

                long ahora = Managers.RaidChests.Drop(who, entregados);
                if (entregados.Count == 0 || ahora < 0)
                {
                    await DecirleAsync(stream, CommandTexts.Get("chest.gone"));
                }
                else
                {
                    long cuantos = 0;
                    foreach (var kv in entregados) cuantos += kv.Value;
                    await DecirleAsync(stream, CommandTexts.Get("chest.dropped", cuantos,
                                                                Managers.RaidTreasures.Worth(entregados), ahora));
                }

                // And the window stays up, now without the drop option: the normal thing after emptying
                // the bag is getting closer to the chest, not having to talk to it again.
                if (npc != null)
                {
                    await PreguntaDelCofreAsync(stream, npc, mapa, Managers.RaidChests.ScoreOf(who), false);
                    return;
                }
            }
            else if (reply == RaidChest.Approach)
            {
                SessionContext.State.OpenDialogueMessage = RaidChest.Warning;
                await PreguntarAsync(stream, RaidChest.Warning, Lista(RaidChest.WarningReplies()));
                return;
            }
            else if (reply == RaidChest.TakeAndRun)
            {
                var raid = Managers.GuildRaidManager.RaidOf(who);
                int cual = raid?.RaidId ?? 0;

                CerrarConversacion();
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                        ConnectionProtocol.NpcDialogCloseReason)));

                long guild = raid?.GuildId ?? 0;
                long puntos = await Managers.RaidChests.TakeAsync(who);
                if (puntos <= 0)
                {
                    await DecirleAsync(stream, CommandTexts.Get("chest.taken.none"));
                    return;
                }

                int puesto = Managers.GuildStore.PlaceOf(guild, cual, DateTimeOffset.UtcNow);
                await DecirleAsync(stream, CommandTexts.Get("chest.taken", puntos, puesto,
                                                            Jondo.Unity.World.Content.Raids.Of(cual)?.Name ?? ""));
                return;
            }

            CerrarConversacion();
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                    ConnectionProtocol.NpcDialogCloseReason)));
        }

        /// <summary>
        /// The master puch: the first screen, with the six levels.
        /// </summary>
        /// <remarks>
        /// Measured in the Hipermago capture on the Amakna kanojedo: ioc, and an ios with 54965 and the six
        /// level answers, from 200 to 1, in that order.
        /// </remarks>
        private static async Task OpenMasterAsync(NetworkStream stream, Npcs.Spawn npc, long mapId)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, npc.ContextualId)));

            SessionContext.State.OpenDialogueNpcId = npc.NpcId;
            SessionContext.State.OpenDialogueMapId = mapId;
            SessionContext.State.OpenDialogueMessage = Kanojedo.FirstMessage;

            await PreguntarAsync(stream, Kanojedo.FirstMessage, Lista(Kanojedo.LevelReplies));
            Console.WriteLine($"[Kanojedo] El maestro del mapa {mapId} ofrece sus seis niveles.");
        }

        /// <summary>
        /// The Dispensador de favores: his offer while the favour of the room is to be chosen --
        /// 59655 "La suerte te sonríe...", with "Acepto el favor." and "No, gracias." -- and once
        /// it is chosen 59657, "La suerte ya te ha sonreído. No puedo hacerte otro favor por ahora.",
        /// with no reply but the client's own way out. The lines and their replies are his
        /// template's; which goes with which is the tree in content/npcs/dialogues.json.
        /// </summary>
        private static async Task OpenFavorAsync(NetworkStream stream, Npcs.Spawn npc, long mapId)
        {
            var escrito = NpcDialogues.For(npc.NpcId, mapId);
            long pregunta = DreamHandler.FavorPending() ? Managers.Dreams.FavorOfferMessage : Managers.Dreams.FavorGivenMessage;
            var linea = escrito?.Line(pregunta);
            long[] respuestas = linea != null ? LasQueTocan(linea) : Array.Empty<long>();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, npc.ContextualId)));

            SessionContext.State.OpenDialogueNpcId = npc.NpcId;
            SessionContext.State.OpenDialogueMapId = mapId;
            SessionContext.State.OpenDialogueMessage = pregunta;

            await PreguntarAsync(stream, pregunta, respuestas, null, linea);
            Console.WriteLine($"[Sueños] The Dispensador de favores says {pregunta}, {respuestas.Length} replies.");
        }

        /// <summary>
        /// What each answer of the master puch does.
        /// </summary>
        /// <remarks>
        /// A level leads to the second screen, whose four «Entrenarte con N» answers carry the parameter
        /// 905 they carry in the capture -- and the back one does not --. A count opens the fight at once:
        /// in the capture, after the ioy come the kld and the same burst as when stepping on a group, with
        /// a group id that was not on the map. And that is how it is done: the group is composed and not
        /// put on the map.
        /// </remarks>
        private static async Task MasterReplyAsync(NetworkStream stream, long reply)
        {
            long mapa = SessionContext.State.OpenDialogueMapId;
            if (mapa == 0) mapa = SessionContext.State.MapId;

            int nivel = Kanojedo.LevelIndexOf(reply);
            if (nivel >= 0)
            {
                var parametros = new Dictionary<long, IReadOnlyList<long>>();
                var cuentas = Kanojedo.CountReplies(nivel);
                for (int i = 0; i < Kanojedo.MostPuchs; i++)
                {
                    parametros[cuentas[i]] = new[] { Kanojedo.ReplyParameter };
                }

                SessionContext.State.OpenDialogueMessage = Kanojedo.MessageFor(nivel);
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ios,
                        ConnectionProtocol.BuildNpcQuestion(Kanojedo.MessageFor(nivel), Lista(cuentas), parametros)));
                return;
            }

            if (Kanojedo.IsBack(reply))
            {
                SessionContext.State.OpenDialogueMessage = Kanojedo.FirstMessage;
                await PreguntarAsync(stream, Kanojedo.FirstMessage, Lista(Kanojedo.LevelReplies));
                return;
            }

            CerrarConversacion();
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                    ConnectionProtocol.NpcDialogCloseReason)));

            var pedido = Kanojedo.ReadCount(reply);
            if (pedido == null) return;

            int level = Kanojedo.LevelAt(pedido.Value.LevelIndex);
            var elegidos = Kanojedo.Pick(level, pedido.Value.Count);
            var grupo = Managers.MobSpawnManager.ComposeOffMap(elegidos);
            if (grupo == null)
            {
                Console.WriteLine($"[Kanojedo] No hay puchs con grado al nivel {level}.");
                return;
            }

            Console.WriteLine($"[Kanojedo] Sesión al nivel {level} con {elegidos.Count} puch(s): " +
                              string.Join(", ", elegidos.Select(e => e.Monster)) + ".");
            await FightHandler.InitiateFightFromMobCollision(stream, grupo, mapa);
        }

        /// <summary>The chest placed on a map, to ask it again.</summary>
        private static Npcs.Spawn CofreDelMapa(long mapId)
        {
            foreach (var puesto in Npcs.Of(mapId))
            {
                if (puesto.NpcId == RaidChest.NpcId) return puesto;
            }

            return null;
        }

        /// <summary>A list of answers as the array the frame expects.</summary>
        private static long[] Lista(IReadOnlyList<long> respuestas)
        {
            var fuera = new long[respuestas.Count];
            for (int i = 0; i < respuestas.Count; i++) fuera[i] = respuestas[i];
            return fuera;
        }

        /// <summary>An information line, which is where what has no window of its own is told.</summary>
        private static Task DecirleAsync(NetworkStream stream, string text)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text)));

        /// <summary>
        /// Sends a question with its answers, and makes sure there is at least one.
        /// </summary>
        /// <remarks>
        /// A DIALOGUE WITH NO ANSWERS CANNOT BE CLOSED. When the list goes empty, the client draws a
        /// «Marcharte.» by itself, and that button does NOT send the ioy: the window stays up and there is
        /// no way out but reconnecting. It shows with the angry Bontarian, who has a message and zero
        /// answers in his template.
        ///
        /// So at least one real answer always goes, because a real answer does send the ioy and then we
        /// answer with the kld that closes. It is here and not in the two places that ask because now
        /// there are two: the first sentence and each of the ones that follow.
        /// </remarks>
        private static async Task PreguntarAsync(NetworkStream stream, long pregunta, long[] respuestas,
                                                 Npcs.Template? plantilla = null,
                                                 Jondo.Unity.World.Content.DialogueLine? frase = null,
                                                 Bankers.Banker? banker = null)
        {
            if (respuestas.Length == 0)
            {
                // One of his own, if he has any. The client resolves an answer's text from the template
                // OF THE NPC it is talking to, so sending one that NPC does not declare draws a blank
                // button: it is what came out with the angry Brakmarian.
                //
                // And if he has none, nothing is made up: the empty list is sent and the client draws
                // its own «Marcharte.». Getting out of there is the X's job, which is now served.
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

            // The numbers some answers carry inside, if the written tree says so.
            Dictionary<long, IReadOnlyList<long>>? parametros = null;
            if (frase != null)
            {
                foreach (var opcion in frase.Choices)
                {
                    if (opcion.Parameters.Count == 0) continue;
                    parametros ??= new Dictionary<long, IReadOnlyList<long>>();
                    parametros[opcion.Reply] = opcion.Parameters;
                }
            }

            // A banker's greeting says the fee -- "te costará #1 kamas" -- and his bank reply
            // carries effect 196: ios f3 "1397" and f2 { f1: 63535, f3 { f1: 196 } }, frame 74.
            if (banker != null)
            {
                parametros ??= new Dictionary<long, IReadOnlyList<long>>();
                parametros[banker.Consult] = BankHandler.ConsultReplyEffects;

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ios, BankProtocol.BuildQuestion(pregunta, respuestas, parametros,
                        BankHandler.GreetingParameters(SessionContext.Current.AccountId))));
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ios,
                    ConnectionProtocol.BuildNpcQuestion(pregunta, respuestas, parametros)));
        }

        /// <summary>
        /// The player has closed the window with the X (kla).
        /// </summary>
        /// <remarks>
        /// Without this the X closed nothing. The opcode was not even declared, so the packet fell into the
        /// unknown branch and the server did not answer; the client keeps the window up until the kld
        /// arrives. With the NPCs that offer no answer -- the angry Brakmarian, the angry Bontarian -- that
        /// left the player locked in the conversation with no way out but reconnecting.
        ///
        /// It comes up 192 times in the 401 captures and always empty: it does not say which NPC it comes
        /// from, so whatever was open is closed, which is the only thing there can be.
        /// </remarks>
        public static Task CloseAsync(NetworkStream stream, byte[] payload) => CloseAsync(stream);

        /// <summary>
        /// Closes the open conversation, without needing a message from the client.
        /// </summary>
        /// <remarks>
        /// The client does not close the window by itself: in the capture of talking to an NPC it sends
        /// NOTHING at the end, it waits for the server's kld. That is why a conversation the server
        /// considered finished without sending it stayed stuck on screen and the cross did not remove it
        /// either -- it is not that the cross failed, it was also waiting for the kld.
        ///
        /// Any place that ends the conversation on its own needs it, like a dungeon guardian who accepts
        /// the key and puts you inside.
        /// </remarks>
        public static async Task CloseAsync(NetworkStream stream)
        {
            CerrarConversacion();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                    ConnectionProtocol.NpcDialogCloseReason)));
        }

        /// <summary>
        /// The answers of a sentence this character should see.
        /// </summary>
        /// <remarks>
        /// An answer that belongs to a quest is not offered to whoever does not carry it, and one that
        /// belongs to a step is not offered before being at that step. The written tree says so; here
        /// only the character's journal is asked.
        /// </remarks>
        private static long[] LasQueTocan(DialogueLine linea)
        {
            var diario = SessionContext.State.Quests;
            if (diario == null) return linea.Replies();

            return linea.RepliesFor(
                diario.Active,
                diario.Finished,
                (mision, paso) => diario.Run(mision)?.StepId == paso,
                (mision, objetivo) => diario.Run(mision)?.Done.Contains(objetivo) == true,
                elemento => SessionContext.State.ElementsUsed.Contains(elemento));
        }

        /// <summary>
        /// What to offer when there is no written conversation for this NPC.
        /// </summary>
        /// <remarks>
        /// <b>A single answer, not the ones the template declares.</b> An NPC declares ALL the answers of
        /// ALL its trees together -- Snori Nairb has thirty-nine -- and sending them at once shows the player
        /// answers of quests he has not started, of stages he has not reached and of three different
        /// conversations mixed together. And none of them leads anywhere either, because without a tree
        /// there is nowhere to lead: any of the thirty-nine closes the window the same.
        ///
        /// So one is offered to say goodbye and that is it. It is less than there was and it is the only
        /// thing that does not lie. What is really needed is the tree, and that is written in the editor.
        /// </remarks>
        private static long[] SinArbolEscrito(Npcs.Template plantilla)
            => plantilla.Replies.Length > 0
                ? new[] { plantilla.Replies[^1] }
                : Array.Empty<long>();

        /// <summary>There stops being an open conversation.</summary>
        /// <summary>
        /// What an answer buys: charges the kamas and gives the item.
        /// </summary>
        /// <remarks>
        /// The price is written in the answer's text -- «Ponme una limonada. Toma, 1 kama.» -- and until now
        /// that was all there was: a sentence that did nothing. Pressing it neither gave the item nor
        /// charged, and since the lemonade is what makes the rat come out, the quest was left with no way
        /// to advance.
        ///
        /// It is charged BEFORE giving. The other way round, a failure when subtracting would leave the
        /// item as a gift; this way, the worst that can happen is that it is charged and the delivery
        /// fails, and that is said on the console instead of kept quiet.
        /// </remarks>
        private static async Task ComprarAsync(NetworkStream stream, DialogueChoice choice)
        {
            if (GameState.Kamas < choice.BuysPrice)
            {
                Console.WriteLine($"[NPC] No llega para {choice.BuysItem}: cuesta " +
                                  $"{choice.BuysPrice} y tiene {GameState.Kamas}.");
                // There is no measured «you cannot afford it» message, so the generic one about something
                // missing is used. Making up an id with no capture behind it is worse than over-explaining.
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                        InfoMessages.Warning, InfoMessages.MissingItem)));
                return;
            }

            if (choice.BuysPrice > 0)
            {
                GameState.Kamas -= choice.BuysPrice;
                DatabaseManager.SaveCurrentCharacter();
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(GameState.Kamas)));
            }

            if (!await Managers.Equipment.GiveAsync(stream, choice.BuysItem, choice.BuysCount))
            {
                Console.WriteLine($"[NPC] Cobrados {choice.BuysPrice} kamas y no se ha podido " +
                                  $"entregar {choice.BuysCount}x{choice.BuysItem}.");
                return;
            }

            Console.WriteLine($"[NPC] Vendido {choice.BuysCount}x{choice.BuysItem} por " +
                              $"{choice.BuysPrice} kamas; quedan {GameState.Kamas}.");
        }

        private static void CerrarConversacion()
        {
            SessionContext.State.OpenDialogueNpcId = 0;
            SessionContext.State.OpenDialogueMapId = 0;
            SessionContext.State.OpenDialogueMessage = 0;
        }

        /// <summary>
        /// The player has chosen an answer (ioy).
        ///
        /// The dialogue always closes, accepted or refused: the kld goes out all four times in the capture
        /// and goes AHEAD of the kamas.
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

            // Did a luminomachine answer? Neither quests nor written tree: what each of its answers
            // does is said by the answer itself.
            if (SessionContext.State.OpenDialogueNpcId == Luminomachine.NpcId &&
                Luminomachine.Owns(reply))
            {
                await MachineReplyAsync(stream, reply);
                return;
            }

            // And the same for the raid chest.
            if (SessionContext.State.OpenDialogueNpcId == RaidChest.NpcId && RaidChest.Owns(reply))
            {
                await ChestReplyAsync(stream, reply);
                return;
            }

            // And the kanojedo's master puch.
            if (SessionContext.State.OpenDialogueNpcId == Kanojedo.MasterNpc && Kanojedo.Owns(reply))
            {
                await MasterReplyAsync(stream, reply);
                return;
            }

            // The bank's reply: the dialogue closes and the bank opens behind it, frames 79-84 of
            // the bank capture. See BankHandler.
            var banker = Bankers.Of(SessionContext.State.OpenDialogueNpcId);
            if (banker != null && reply == banker.Consult)
            {
                CerrarConversacion();
                await BankHandler.OpenAsync(stream);
                return;
            }

            // Does the current line hand a quest over? Checked BEFORE walking further, because
            // walking changes OpenDialogueMessage, and before CerrarConversacion, which clears it.
            //
            // After the reply, not on arriving at the line: the capture walks to line 50071, the
            // player picks 66788, and only then does ief {2432} go out.
            // Does this reply start a quest? The tree says so (authored or accept/refuse fallback).
            // With neither, the old rule applies: any reply on the line the step names.
            var frase = NpcDialogues.ForTalk(SessionContext.State.OpenDialogueNpcId,
                                             SessionContext.State.OpenDialogueMapId)
                                    ?.Line(SessionContext.State.OpenDialogueMessage);
            var elegidaAhora = frase?.Choice(reply);

            // Is it the answer that accepts an offer that was read? Then there is no NPC involved:
            // the question was asked by a notice, and accepting it is what records having read it. It
            // is checked here because the ioy arrives loose, without saying where it came from.
            var oferta = Readables.ByAcceptReply(reply);
            if (oferta != null)
            {
                if (SessionContext.State.ElementsUsed.Add(oferta.Value.Element))
                {
                    DatabaseManager.RememberElement(GameState.CharacterId, oferta.Value.Element);
                }

                Console.WriteLine($"[Lecturas] Aceptada la oferta del elemento {oferta.Value.Element}.");
                await CloseAsync(stream);
                return;
            }

            // Does the answer buy something? Before the quest, because a purchase that fails must not
            // leave the conversation halfway with the quest already started.
            if (elegidaAhora != null && elegidaAhora.BuysItem != 0)
            {
                await ComprarAsync(stream, elegidaAhora);
            }

            // Does the answer touch the dream points? It is the fountain's shop, which has no protocol
            // of its own: it is this same dialogue, and what it buys is written in the answer.
            if (elegidaAhora != null && elegidaAhora.DreamPointsPercent != 0)
            {
                var sueno = Managers.Dreams.De(GameState.CharacterId);
                var aqui = sueno?.SalaActual;
                if (sueno != null && aqui != null && aqui.FavorTaken)
                {
                    // Once per fountain: his favor was taken here already. It could be asked for
                    // again and again, and 25 points became as many as one had patience for.
                    Console.WriteLine($"[Sueños] The Rey Gob's favor was already taken in room {aqui.Id}.");
                }
                else if (sueno != null)
                {
                    if (aqui != null) aqui.FavorTaken = true;
                    // The dream points, the f11: 25 become 38 in the long capture, 25 x 1.5 rounded up.
                    int antes = sueno.DreamPoints;
                    sueno.DreamPoints = (int)Math.Round(sueno.DreamPoints * elegidaAhora.DreamPointsPercent / 100.0,
                                                        MidpointRounding.AwayFromZero);

                    Console.WriteLine($"[Sueños] La respuesta {reply} deja los puntos de " +
                                      $"{antes} en {sueno.DreamPoints} " +
                                      $"({elegidaAhora.DreamPointsPercent}%).");

                    DreamHandler.Persist(sueno);
                    await DreamHandler.RefrescarEstadoAsync(stream);
                }
                else
                {
                    Console.WriteLine($"[Sueños] La respuesta {reply} toca los puntos y " +
                                      "no hay sueño en curso.");
                }
            }

            // "Acepto el favor.": the conversation ends and the favour's three choices open, in
            // the dream's shop window. See DreamHandler.OfferFavorAsync.
            if (elegidaAhora != null && elegidaAhora.DreamFavor)
            {
                CerrarConversacion();
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                        ConnectionProtocol.NpcDialogCloseReason)));
                await DreamHandler.OfferFavorAsync(stream);
                return;
            }

            if (elegidaAhora != null && elegidaAhora.StartsQuest != 0)
            {
                await Managers.Quests.StartAsync(stream, elegidaAhora.StartsQuest);
            }
            else if (frase == null)
            {
                await Managers.Quests.OnReplyAsync(stream, SessionContext.State.OpenDialogueMessage);
            }

            // And does this NPC guard a dungeon's door? If he goes in, the conversation has ended in a
            // map change and there is nothing more to tell him.
            long dondeHabla = SessionContext.State.OpenDialogueMapId;
            if (dondeHabla == 0) dondeHabla = SessionContext.State.MapId;
            if (await DungeonHandler.AtTheDoorAsync(stream, dondeHabla, reply))
            {
                CerrarConversacion();
                return;
            }

            // Does this answer move the player? It goes BEFORE following the conversation because an
            // answer that teleports ends it: in the capture the server sends the kld and right after
            // the jru, without another sentence in between.
            if (elegidaAhora != null && (elegidaAhora.TeleportsTo != 0 || elegidaAhora.ReturnsHome))
            {
                long adonde = elegidaAhora.TeleportsTo;
                if (elegidaAhora.ReturnsHome)
                {
                    // Where he came from, which is what was noted on entering. If there is no note -- somebody
                    // who got there on his own, or a freshly started server -- he is left at the well's
                    // entrance instead of being left locked in, which is what used to happen.
                    adonde = Managers.Dreams.DeDondeViene(GameState.CharacterId).Mapa;
                    if (adonde == 0) adonde = Managers.Dreams.MapaDelPozo;
                }

                CerrarConversacion();

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                        ConnectionProtocol.NpcDialogCloseReason)));

                await WorldMoveHandler.TeleportAsync(stream, adonde);
                Console.WriteLine($"[NPC] Respuesta {reply}: lleva al mapa {adonde}.");
                return;
            }

            // Does this answer lead to another sentence? It is the only thing that makes this a
            // conversation instead of a loose question, and only the hand-written tree can say so: the
            // client carries the sentences and the answers but never which goes with which.
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
        /// If the chosen answer leads to another sentence, sends it and says yes.
        /// </summary>
        /// <remarks>
        /// Returns <c>false</c> when the conversation ends here, which is what happens with every NPC
        /// without a written tree: then the caller closes with the kld as always.
        ///
        /// The ioy does not say which NPC it comes from nor which sentence, so it is placed by the session
        /// state. If it does not fit -- the player has another window open, or none -- it is closed, which is
        /// the safe thing: leaving the window up is leaving it with no way out.
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
                // The editor checks this before saving, so getting here means the file was edited by
                // hand. It is said and closed instead of leaving the player looking at a window that does
                // not respond.
                Console.WriteLine($"[NPC] La respuesta {reply} del {estado.OpenDialogueNpcId} lleva " +
                                  $"a la frase {elegida.Next}, que no está escrita. Se cierra.");
                return false;
            }

            estado.OpenDialogueMessage = siguiente.Message;
            await PreguntarAsync(stream, siguiente.Message, LasQueTocan(siguiente),
                                 Npcs.TemplateOf(estado.OpenDialogueNpcId), siguiente);

            Console.WriteLine($"[NPC] La respuesta {reply} lleva a la frase {siguiente.Message}, " +
                              $"con {Math.Max(siguiente.Choices.Count, 1)} respuestas.");
            return true;
        }

        /// <summary>
        /// Buying (kea). The client sends only the item and the quantity: the price is set by the server,
        /// which is the one that sent the catalogue.
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

            // And that the vendor is still where the player is.
            //
            // The shop stayed open on changing map -- Forget() existed but nobody called it -- so one
            // could talk to a vendor, walk three maps away and keep buying from his catalogue from the
            // other side of the world. Looking only at «OpenShop != 0» is not enough: that says there
            // was a shop, not that there is one now.
            //
            // It is checked here and not only on changing map, on purpose. Remembering to call Forget()
            // in the seven places a map change comes from -- walking, zaap, zaapi, dungeon door, house,
            // anomaly, end of fight -- is remembering seven times; this is once and does not depend on
            // the way out.
            if (Managers.Npcs.Find(SessionContext.State.MapId, OpenShop) == null)
            {
                Console.WriteLine($"[NPC] El vendedor {OpenShopNpc} no está en el mapa " +
                                  $"{SessionContext.State.MapId}: la tienda se cierra.");
                Forget();
                return;
            }

            // That the vendor who is open really has it: the catalogue is sent by us, so asking for
            // something else is not a valid purchase.
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

            // What is paid with here. Without a token shop it is the usual: kamas.
            var tokenShop = TokenShops.Of(OpenShopNpc);
            long price = tokenShop == null
                ? NpcShops.PriceOf(gid) * quantity
                : TokenShops.PriceOf(tokenShop, gid) * quantity;

            // The player's stack of tokens, if he has one. It is looked up by template in the
            // inventory: a token is a resource and stacks, so there is only one.
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
                // Tokens are spent by removing them from the inventory, the same as when destroying part
                // of a stack. If the purchase takes the last one, DestroyCharacterItem deletes the row.
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

            // The order is the measured one, and the two rounds of ivf/iun too: the real server sends
            // them identical before and after the kdg. Since both carry the total and not an increment,
            // repeating them throws nothing off.
            long capacity = 1000 + 5L * GameState.TotalStrength;

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
                // The notice of the token purchase and the stack's new total. The ivj carries WHAT IS
                // LEFT, not what was spent: it can be seen in the capture's rune marketplace, where the
                // same stack goes 107 -> 117 -> 217 -> 1217.
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
        /// The shop's close button (empty kla).
        ///
        /// The client sends it TWICE in a row, less than a millisecond apart, and the real server answers a
        /// single khd. That is why it closes at the first and the second falls away by itself: with no shop
        /// open any more, GameNodeProxy takes it to the zaap and that one has nothing open either.
        /// </summary>
        public static async Task CloseShopAsync(NetworkStream stream)
        {
            OpenShop = 0;
            OpenShopNpc = 0;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Khd, ConnectionProtocol.BuildShopClosed()));
        }

        /// <summary>
        /// On changing map nothing stays open: neither the shop nor the conversation.
        /// </summary>
        /// <remarks>
        /// The conversation is cleared here since a dialogue's X is served BEFORE the zaap's. With stale
        /// state -- the player opens a conversation and leaves without closing it -- the next X, the one
        /// that was the zaap's, would be taken by the dialogue and the destinations list would not close.
        /// It is the same bug the dialogue's X had, the other way round.
        ///
        /// And it is called from where the actors list is sent, which is where the five ways of reaching a
        /// map go through. The previous comment said WorldMoveHandler and FightHandler called it; the
        /// FightHandler part was not true, there is no call of its in the whole repository.
        ///
        /// BuyAsync checks again that the vendor is on the map: this is for tidiness, not for security,
        /// and security is handled by whoever charges.
        /// </remarks>
        public static void Forget()
        {
            OpenShop = 0;
            OpenShopNpc = 0;
            CerrarConversacion();
        }

        /// <summary>
        /// The uid of the item just bought.
        ///
        /// This read MAX(Uid) from the database on every purchase, which handed them out fine but was not
        /// atomic: two purchases at once read the same maximum and return the same number, and the second
        /// overwrites the first's row. Now DatabaseManager hands them out for the whole server, in one go
        /// and with a counter.
        /// </summary>
        private static long NextUid() => DatabaseManager.NextItemUid();
    }
}
