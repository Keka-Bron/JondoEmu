using System;
using System.Linq;
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

            // An alliance's tax collector is talked to with the same iov (action 3, as in «atacar a
            // recaudador y perder») and answers with its own ion.
            if (action == Npcs.Talk && await TaxCollectorHandler.TalkAsync(stream, mapId, contextualId)) return;
            // And a prism, the same way, with its own question (iot).
            if (action == Npcs.Talk && await PrismHandler.TalkAsync(stream, mapId, contextualId)) return;
            // And a divine dimension's portal, which speaks as its NPC (DimensionPortalHandler).
            if (action == Npcs.Talk && await DimensionPortalHandler.TalkAsync(stream, mapId, contextualId)) return;

            var npc = Npcs.Find(mapId, contextualId);
            if (npc == null)
            {
                Console.WriteLine($"[NPC] The client clicks {contextualId} on map {mapId}, " +
                                  "which is nobody here.");
                return;
            }

            // «Intercambiar» on an NPC who swaps things: Gobiere Trokeur's barter, not a shop.
            if (action == Npcs.TradeCosmetics && NpcBarterHandler.Barters(npc.NpcId))
            {
                await NpcBarterHandler.OpenAsync(stream, npc.NpcId, npc.ContextualId);
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

            // An estate agent's «Consultar las casas».
            if (action == EstateAgencyHandler.Action)
            {
                await EstateAgencyHandler.OpenAsync(stream);
                return;
            }

            Console.WriteLine($"[NPC] Action {action} on NPC {npc.NpcId}, which is not done.");
        }

        /// <summary>The whole catalogue, in one go, which is how the real server sends it.</summary>
        private static async Task OpenShopAsync(NetworkStream stream, Npcs.Spawn npc)
        {
            var catalogue = NpcShops.CatalogueOf(npc.NpcId);
            if (catalogue.Count == 0)
            {
                Console.WriteLine($"[NPC] {npc.NpcId} has a shop action but sells nothing.");
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

            string moneda = tokenShop == null ? "kamas" : $"token {tokenShop.TokenGid}";
            Console.WriteLine($"[NPC] Shop of {npc.NpcId}: {catalogue.Count} items, " +
                              $"{kbd.Length} bytes, paid in {moneda}.");
        }

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

            // Nor Belladona: her chess, its rules or her spite once beaten (GuildRaidEfedra).
            if (npc.NpcId == Managers.GuildRaidEfedra.Npc)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, npc.ContextualId)));
                var (question, replies) = Managers.GuildRaidEfedra.DialogueFor(GameState.CharacterId);
                SessionContext.State.OpenDialogueNpcId = npc.NpcId;
                SessionContext.State.OpenDialogueMapId = mapId;
                SessionContext.State.OpenDialogueMessage = question;
                await PreguntarAsync(stream, question, replies);
                return;
            }

            // The kanojedo's master puch: the six levels, and then how many.
            if (npc.NpcId == Kanojedo.MasterNpc)
            {
                await OpenMasterAsync(stream, npc, mapId);
                return;
            }

            // Gargandias's altar and the titan himself (GargandiasHandler).
            if (GargandiasHandler.Owns(npc.NpcId))
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, npc.ContextualId)));
                var (opening, choices) = GargandiasHandler.Opening(npc.NpcId);
                SessionContext.State.OpenDialogueNpcId = npc.NpcId;
                SessionContext.State.OpenDialogueMapId = mapId;
                SessionContext.State.OpenDialogueMessage = opening;
                await PreguntarAsync(stream, opening, choices);
                return;
            }

            // Chantal Inea's oath and Morry Gann's way back to neutral: what they offer is the
            // character's side, fee and last change (AlignmentHandler).
            if (AlignmentHandler.Owns(npc.NpcId))
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, npc.ContextualId)));
                var (question, replies) = AlignmentHandler.Opening(npc.NpcId);
                SessionContext.State.OpenDialogueNpcId = npc.NpcId;
                SessionContext.State.OpenDialogueMapId = mapId;
                SessionContext.State.OpenDialogueMessage = question;
                await PreguntarAsync(stream, question, replies);
                return;
            }

            // Ontoral Zo: what he offers is the day's offering as the character has it
            // (AlmanaxOfferingHandler). Other quests that only ask to come and see him still close.
            if (AlmanaxOfferingHandler.Owns(npc.NpcId))
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, npc.ContextualId)));
                var (question, replies, texts) = AlmanaxOfferingHandler.Opening();
                SessionContext.State.OpenDialogueNpcId = npc.NpcId;
                SessionContext.State.OpenDialogueMapId = mapId;
                SessionContext.State.OpenDialogueMessage = question;
                await PreguntarAsync(stream, question, replies, lineParameters: texts);
                await Managers.Quests.OnTalkingToAsync(stream, npc.NpcId);
                Console.WriteLine($"[NPC] Dialog of {npc.NpcId}: question {question}, {replies.Length} replies (the Almanax).");
                return;
            }

            // The Dispensador de favores of a dream favour opens on what the favour is at.
            if (npc.NpcId == Managers.Dreams.FavorNpc)
            {
                await OpenFavorAsync(stream, npc, mapId);
                return;
            }

            // Dhain's treasure quests done on an earlier day come back before his replies are chosen.
            TreasureHuntHandler.BeforeTalkingTo(npc.NpcId);

            // What a quest in progress asked of this NPC by just coming to see him -- a «ve a ver»,
            // an item handed over on a hello -- is done BEFORE he chooses what to say: the old
            // quests' NPC answers the hand-over with the next step's line straight away (Guemado
            // Elwapo's «Gracias por las patatas» as the sacks change hands).
            await Managers.Quests.OnTalkingToAsync(stream, npc.NpcId);

            var template = Npcs.TemplateOf(npc.NpcId);
            var escrito = NpcDialogues.For(npc.NpcId, mapId);
            var primera = escrito == null ? null : OpeningFor(escrito);

            if (primera == null && (template == null || template.DialogMessageId == 0))
            {
                Console.WriteLine($"[NPC] {npc.NpcId} has no dialog in its template.");
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
            // and the only thing on screen was "No.". Offer the guardian's independent entry
            // choices; DungeonHandler checks the selected item and reports a refusal in chat.
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

            // And a jailer takes the criminals carried and sells the news of those still out.
            long[] wanted = WantedHandler.OpeningReplies(npc.NpcId, mapId);
            if (wanted.Length > 0) respuestas = wanted.Concat(respuestas.Where(r => !wanted.Contains(r))).ToArray();

            // And a banker offers the bank: his reply in front of the rest, the way the Bontarian
            // banker offers it in frame 74 of the bank capture. See BankHandler.
            var banker = Bankers.Of(npc.NpcId);
            if (banker != null && !Bankers.MayOpen(SessionContext.State.CharacterLevel))
            {
                // Under level 10 he says so, in his own words, and offers no chest.
                if (banker.Refusal != 0) pregunta = banker.Refusal;
                respuestas = respuestas.Where(r => r != banker.Consult).ToArray();
                banker = null;
            }
            else if (banker != null) respuestas = BankHandler.WithTheBankReply(banker, respuestas);

            // Where the conversation is going is noted. Without this the ioy that comes later cannot be
            // placed: it carries the answer's id and nothing else, not which NPC nor which sentence it
            // came from.
            SessionContext.State.OpenDialogueNpcId = npc.NpcId;
            SessionContext.State.OpenDialogueMapId = mapId;
            SessionContext.State.OpenDialogueMessage = pregunta;

            await PreguntarAsync(stream, pregunta, respuestas, template,
                                 escrito?.Line(pregunta), banker);

            Console.WriteLine($"[NPC] Dialog of {npc.NpcId}: question {pregunta}, " +
                              $"{Math.Max(respuestas.Length, 1)} replies" +
                              (escrito != null ? $" (written, {escrito.Lines.Count} lines)" : " (from the template)") + ".");
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
            // The salt is the raid's pool, not the bag (the guides): what the whole team gathered.
            int salt = (int)Math.Max(0, Managers.GuildRaidManager.SaltOf(GameState.CharacterId));

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

            Console.WriteLine($"[Luminomachines] Floor {floor}, light {light}, {salt} salts: " +
                              $"question {pregunta}, {respuestas.Length} replies.");
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
            if (!Managers.GuildRaidManager.SpendSalt(GameState.CharacterId, compra.Cost))
            {
                await DecirleAsync(stream, CommandTexts.Get("light.nosalt", compra.Cost));
                return;
            }

            int luz = Luminomachines.Deposit(GameState.CharacterId, compra.Floor, compra.From, compra.To);
            if (luz < 0)
            {
                Managers.GuildRaidManager.RefundSalt(GameState.CharacterId, compra.Cost);
                await DecirleAsync(stream, CommandTexts.Get("light.changed"));
                return;
            }

            await DecirleAsync(stream, CommandTexts.Get("light.lit", compra.Floor, luz, compra.Cost));
            await GuildRaidHandler.TellRunningStateAsync(GameState.CharacterId);
            await Managers.GuildRaidLights.MachineUsedAsync(GameState.CharacterId, compra.Floor);
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

            Console.WriteLine($"[Raids] Chest of map {mapId}: {score} points, " +
                              $"{traidos.Count} kinds of treasure on top.");
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
                if (ahora >= 0) await GuildRaidHandler.TellRunningStateAsync(who);
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

                // The Gigalodón comes out for whoever takes the chest (the guides); the raid ends
                // with its fight. Only where there is no Gigalodón to fight does taking it end the raid.
                if (await Managers.GuildRaidManager.StartGigalodonAsync(stream, who, mapa)) return;

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
            Console.WriteLine($"[Kanojedo] The master of map {mapId} offers his six levels.");
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
            Console.WriteLine($"[Dreams] The Dispensador de favores says {pregunta}, {respuestas.Length} replies.");
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
                Console.WriteLine($"[Kanojedo] There are no puchs with a grade at level {level}.");
                return;
            }

            Console.WriteLine($"[Kanojedo] Session at level {level} with {elegidos.Count} puch(s): " +
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
                                                 Bankers.Banker? banker = null,
                                                 IReadOnlyList<string>? lineParameters = null)
        {
            // The hours until the daily quests come back: the tired dopeuls' «vuelve dentro de #1 hora/s».
            // It goes first: that #1 is a number, never the character's name.
            if ((lineParameters == null || lineParameters.Count == 0) && frase?.HoursToDailyReset == true)
                lineParameters = new[] { HoursToDailyReset(DateTime.Now).ToString(System.Globalization.CultureInfo.InvariantCulture) };

            // A written line that names the character carries his name (Curro's 20681).
            if ((lineParameters == null || lineParameters.Count == 0) && frase?.NamesTheCharacter == true)
                lineParameters = new[] { SessionContext.State.CharacterName ?? "" };

            // A written line with nothing to offer this character -- none written, or none his -- gets
            // the client's own «Marcharte.»: his last reply would be somebody else's words, Otomai's
            // apprentice saying «Ir a ver al maestro cazador» when the beast is coming, Chronokwartz
            // «Aceptar el trato» to a Xelor with her cards still to find. Its #1 still goes with it.
            if (respuestas.Length == 0 && frase != null)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ios, lineParameters is { Count: > 0 }
                        ? ConnectionProtocol.BuildNpcQuestion(pregunta, Array.Empty<long>(), null, lineParameters)
                        : ConnectionProtocol.BuildNpcQuestion(pregunta, Array.Empty<long>())));
                return;
            }

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
            Dictionary<long, IReadOnlyList<IReadOnlyList<long>>>? arguments = null;
            if (frase != null)
            {
                foreach (var opcion in frase.Choices)
                {
                    if (opcion.Parameters.Count == 0) continue;
                    parametros ??= new Dictionary<long, IReadOnlyList<long>>();
                    parametros[opcion.Reply] = opcion.Parameters;
                    if (opcion.ParameterArgs.Count == 0) continue;
                    arguments ??= new Dictionary<long, IReadOnlyList<IReadOnlyList<long>>>();
                    arguments[opcion.Reply] = opcion.ParameterArgs;
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

            // A line that names somebody -- «Haré esta ofrenda en tu nombre, #1» -- carries the name.
            byte[] ios = lineParameters is { Count: > 0 }
                ? ConnectionProtocol.BuildNpcQuestion(pregunta, respuestas, parametros, lineParameters)
                : ConnectionProtocol.BuildNpcQuestion(pregunta, respuestas, parametros, arguments);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ios, ios));
        }

        /// <summary>The whole hours, rounded up, until the next daily turn (QuestResets), at least one.</summary>
        public static int HoursToDailyReset(DateTime now)
        {
            var next = Jondo.Unity.World.Quests.QuestResets.Daily(now).AddDays(1);
            return Math.Max(1, (int)Math.Ceiling((next - now).TotalHours));
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
        /// <summary>
        /// The line a written conversation opens on for this character: by where his quests stand
        /// and by his class (see <see cref="Jondo.Unity.World.Content.DialogueOpening"/>).
        /// </summary>
        private static DialogueLine? OpeningFor(Jondo.Unity.World.Content.NpcDialogue escrito)
        {
            var diario = SessionContext.State.Quests;
            return LineFor(escrito, (breed, active, finished, onStep, objectiveDone) =>
                escrito.OpeningFor(breed, active, finished, onStep, objectiveDone,
                                   quest => diario != null && diario.CanStart(quest, out _),
                                   item => Managers.Equipment.HowMany(item) > 0,
                                   criterion => Managers.PlayerCriteria.Met(criterion, SessionContext.State.MapId)));
        }

        /// <summary>The line of an opening or a push, for this character, with his journal asked.</summary>
        private static DialogueLine? LineFor(Jondo.Unity.World.Content.NpcDialogue escrito,
            Func<int, Func<int, bool>, Func<int, bool>, Func<int, int, bool>, Func<int, int, bool>, DialogueLine?> pick)
        {
            var diario = SessionContext.State.Quests;
            if (diario == null) return pick(SessionContext.State.Breed, _ => false, _ => false, (_, _) => false, (_, _) => false);
            return pick(SessionContext.State.Breed,
                diario.Active,
                diario.Finished,
                (mision, paso) => diario.Run(mision)?.StepId == paso,
                (mision, objetivo) => diario.ObjectiveDone(objetivo));
        }

        /// <summary>
        /// What an NPC on the character's map says on his own once a step of a quest is done: the
        /// guided tutorial's voice presents the class's god as soon as the first steps are walked.
        /// The capture has idz 3226 and then, with no iov between, ioc and ios 60333.
        /// </summary>
        public static async Task PushAfterStepAsync(NetworkStream stream, int stepId)
        {
            long mapId = SessionContext.State.MapId;
            foreach (var puesto in Npcs.Of(mapId))
            {
                var escrito = NpcDialogues.For(puesto.NpcId, mapId);
                if (escrito == null || escrito.Pushes.Count == 0) continue;

                foreach (var push in escrito.Pushes)
                {
                    if (push.AfterStep != stepId) continue;
                    long message = push.Line.MessageFor(SessionContext.State.Breed);
                    if (message == 0) continue;
                    var line = escrito.Line(message) ?? new DialogueLine { Message = message };

                    // The stagings around the line: the tutorial's camera on the god's statue.
                    await Managers.MapStagings.PlayAsync(stream, push.StagingsBefore);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, puesto.ContextualId)));
                    SessionContext.State.OpenDialogueNpcId = puesto.NpcId;
                    SessionContext.State.OpenDialogueMapId = mapId;
                    SessionContext.State.OpenDialogueMessage = message;
                    await PreguntarAsync(stream, message, LasQueTocan(line), Npcs.TemplateOf(puesto.NpcId), line);
                    await Managers.MapStagings.PlayAsync(stream, push.Stagings);
                    Console.WriteLine($"[NPC] {puesto.NpcId} speaks after step {stepId}: question {message}.");
                    return;
                }
            }
        }

        /// <summary>
        /// An NPC of the map who walks in now (jsn) and opens his conversation (ioc, ios), as
        /// Ganymed does in the combat room once Master Darmas has been told the fight is won.
        /// </summary>
        /// <summary>Whoever a reply brings in: its «showsNpc» and the «alsoShows» with it.</summary>
        private static async Task ComeInAsync(NetworkStream stream, DialogueChoice choice)
        {
            await ComesInAsync(stream, choice.ShowsNpc);
            foreach (int npc in choice.AlsoShows) await ComesInAsync(stream, npc);
        }

        /// <summary>An NPC of this map comes in, for this character (jsn), without a word.</summary>
        public static async Task ComesInAsync(NetworkStream stream, int npcId)
        {
            long mapId = SessionContext.State.MapId;
            var spawn = Npcs.OnMap(mapId).FirstOrDefault(s => s.NpcId == npcId);
            if (spawn == null) return;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildNpcShown(spawn, mapId)));
        }

        private static async Task ComesInAndSpeaksAsync(NetworkStream stream, int npcId, long thenLine = 0)
        {
            long mapId = SessionContext.State.MapId;
            // Coming in is appearing: one who hides until then (Ramy in the conduits of the palace's
            // kitchen) stands on the map for nobody before his entrance.
            var spawn = Npcs.OnMap(mapId).FirstOrDefault(s => s.NpcId == npcId)
                        ?? (thenLine != 0 ? Npcs.Of(mapId).FirstOrDefault(s => s.NpcId == npcId) : null);
            if (spawn == null)
            {
                Console.WriteLine($"[NPC] {npcId} was to come in on {mapId} and is not there for this character.");
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildNpcShown(spawn, mapId)));

            var escrito = NpcDialogues.For(npcId, mapId);
            var line = escrito == null ? null : thenLine != 0 ? escrito.Line(thenLine) : OpeningFor(escrito);
            if (line == null) return;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, spawn.ContextualId)));
            SessionContext.State.OpenDialogueNpcId = npcId;
            SessionContext.State.OpenDialogueMapId = mapId;
            SessionContext.State.OpenDialogueMessage = line.Message;
            await PreguntarAsync(stream, line.Message, LasQueTocan(line), Npcs.TemplateOf(npcId), line);
            await Managers.Quests.OnTalkingToAsync(stream, npcId);
            Console.WriteLine($"[NPC] {npcId} comes in and speaks: question {line.Message}.");
        }

        /// <summary>
        /// Whether one choice is offered to the character of the session: the filter of
        /// <see cref="LasQueTocan"/> for a single choice, so that a reply a line offers twice --
        /// once for a quest, once for everybody -- is read as the one that was on screen.
        /// </summary>
        private static bool SeLeOfrece(Jondo.Unity.World.Content.DialogueChoice choice)
        {
            var diario = SessionContext.State.Quests;
            if (diario == null) return choice.Always;
            if (!Jondo.Unity.World.Content.DialogueLine.Shows(choice,
                    diario.Active,
                    diario.Finished,
                    (mision, paso) => diario.Run(mision)?.StepId == paso,
                    (mision, objetivo) => diario.ObjectiveDone(objetivo),
                    elemento => SessionContext.State.ElementsUsed.Contains(elemento)))
                return false;
            if (!choice.ForLevel(SessionContext.State.CharacterLevel)) return false;
            if (choice.Holds != 0 && Managers.Equipment.HowMany(choice.Holds) < choice.HoldsCount) return false;
            if (!Managers.Quests.HandsOverEnough(choice.Objectives(), choice.Partial)) return false;
            if (choice.Criterion.Length > 0 && !Managers.PlayerCriteria.Met(choice.Criterion, SessionContext.State.MapId)) return false;
            if (NotItsTurnYet(choice)) return false;
            if (!ItsQuestIsOpenToHim(choice)) return false;
            if (choice.RefusedNext == 0 && choice.TakesAll.Any(i => Managers.Equipment.HowMany(i) <= 0)) return false;
            return choice.Takes == 0 || Managers.Equipment.HowMany(choice.Takes) >= choice.TakesCount;
        }

        /// <summary>
        /// Whether the quest a reply hands over, or leads into («beforeQuest»), is one this character
        /// may take: its own start condition, the class of a class quest above all. Saketsú's «Informarte
        /// sobre el culto de Sacrógrito.» is for sacriers; a huppermage was offered the whole talk and
        /// refused the quest at its end.
        /// </summary>
        private static bool ItsQuestIsOpenToHim(Jondo.Unity.World.Content.DialogueChoice choice)
        {
            int quest = choice.StartsQuest != 0 ? choice.StartsQuest : choice.BeforeQuest;
            if (quest == 0) return true;
            var diario = SessionContext.State.Quests;
            return diario == null || diario.CanStart(quest, out _);
        }

        /// <summary>
        /// Whether a reply does an objective its quest has not come to yet: of the objectives it does,
        /// the first (the rest of a «completesAll» open one after another as it marks them).
        /// </summary>
        private static bool NotItsTurnYet(Jondo.Unity.World.Content.DialogueChoice choice)
        {
            int first = choice.Completes != 0 ? choice.Completes
                        : choice.CompletesAll.Count > 0 ? choice.CompletesAll[0] : 0;
            return Managers.Quests.NotYetRevealed(first);
        }

        private static long[] LasQueTocan(DialogueLine linea)
        {
            var diario = SessionContext.State.Quests;
            if (diario == null) return linea.Replies();

            var replies = linea.RepliesFor(
                diario.Active,
                diario.Finished,
                (mision, paso) => diario.Run(mision)?.StepId == paso,
                (mision, objetivo) => diario.ObjectiveDone(objetivo),
                elemento => SessionContext.State.ElementsUsed.Contains(elemento));

            // And the rest of what each choice asks (SeLeOfrece). A reply the line offers twice -- right
            // for one character, wrong for another, as the bridge guardian's answers -- is offered when
            // any of its choices is.
            return replies.Where(r =>
            {
                bool written = false;
                foreach (var choice in linea.Choices)
                {
                    if (choice.Reply != r) continue;
                    written = true;
                    if (SeLeOfrece(choice)) return true;
                }

                return !written;
            }).ToArray();
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
        private static async Task<bool> ComprarAsync(NetworkStream stream, DialogueChoice choice)
        {
            if (GameState.Kamas < choice.BuysPrice)
            {
                Console.WriteLine($"[NPC] Not enough for {choice.BuysItem}: it costs " +
                                  $"{choice.BuysPrice} and they have {GameState.Kamas}.");
                // There is no measured «you cannot afford it» message, so the generic one about something
                // missing is used. Making up an id with no capture behind it is worse than over-explaining.
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                        InfoMessages.Warning, InfoMessages.MissingItem)));
                return false;
            }

            if (choice.BuysPrice > 0)
            {
                GameState.Kamas -= choice.BuysPrice;
                DatabaseManager.SaveCurrentCharacter();
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(GameState.Kamas)));
            }

            // A price with nothing to hand over is a fee: Nacf Virgilio's hundred kamas to be
            // challenged in «Cien por cien natural».
            if (choice.BuysItem == 0)
            {
                if (choice.BuysPrice > 0)
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                            Managers.InfoMessages.KamasLost, choice.BuysPrice.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                Console.WriteLine($"[NPC] Charged a fee of {choice.BuysPrice} kamas; {GameState.Kamas} left.");
                return true;
            }

            if (!await Managers.Equipment.GiveAsync(stream, choice.BuysItem, choice.BuysCount))
            {
                Console.WriteLine($"[NPC] {choice.BuysPrice} kamas charged and " +
                                  $"{choice.BuysCount}x{choice.BuysItem} could not be handed over.");
                return false;
            }

            // What the chat says, as Grobid's lemonade in «Matarratas»' capture: after the kld,
            // lqn 46 with the kamas and lqn 21 with the item.
            if (choice.BuysPrice > 0)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                        Managers.InfoMessages.KamasLost, choice.BuysPrice.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            }
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                    Managers.InfoMessages.ItemGained, choice.BuysCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    choice.BuysItem.ToString(System.Globalization.CultureInfo.InvariantCulture))));

            Console.WriteLine($"[NPC] Sold {choice.BuysCount}x{choice.BuysItem} for " +
                              $"{choice.BuysPrice} kamas; {GameState.Kamas} left.");
            return true;
        }

        /// <summary>What a reply hands over (DialogueChoice.Gives), with the chat's line.</summary>
        private static async Task GiveFromReplyAsync(NetworkStream stream, DialogueChoice choice)
        {
            // The reply's item, then the others it hands over with it, one of each: Gobriel's two
            // stones for whoever wishes him a better life.
            var items = new List<(int Item, int Count)>();
            if (choice.Gives != 0) items.Add((choice.Gives, choice.GivesCount));
            items.AddRange(choice.AlsoGives.Select(i => (i, 1)));
            // And the kamas it pays: the mulou's 10,000 for a goblin.
            if (choice.GivesKamas > 0) await Managers.CharacterRewards.GiveKamasAsync(stream, choice.GivesKamas);
            foreach (var (item, count) in items)
            {
                if (!await Managers.QuestFollowers.GiveAsync(stream, item, count))
                {
                    Console.WriteLine($"[NPC] Reply {choice.Reply} could not hand over {count}x{item}.");
                    continue;
                }
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                        Managers.InfoMessages.ItemGained, count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        item.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            }
        }

        /// <summary>There stops being an open conversation.</summary>
        private static void CerrarConversacion()
        {
            SessionContext.State.OpenDialogueNpcId = 0;
            SessionContext.State.OpenDialogueMapId = 0;
            SessionContext.State.OpenDialogueMessage = 0;
            SessionContext.State.LastNpcQuestion = 0;
            SessionContext.State.LastNpcReplies = Array.Empty<long>();
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

            // A dimension's portal: its one reply goes through it.
            if (await DimensionPortalHandler.ReplyAsync(stream, reply)) return;

            // A reply that starts a fight (content/fights/npc_fights.json): the tutorial's chafers.
            long talkedOn = SessionContext.State.OpenDialogueMapId != 0 ? SessionContext.State.OpenDialogueMapId : SessionContext.State.MapId;
            var npcFight = NpcFights.For(SessionContext.State.OpenDialogueNpcId, talkedOn, reply);
            // The same reply offered as a step further for one character and a fight for another --
            // the guardian of the bridge of death's right and wrong answers --: the one offered here
            // goes on to a line, so it is the conversation and not the fight.
            if (npcFight != null)
            {
                var offered = NpcDialogues.For(SessionContext.State.OpenDialogueNpcId, SessionContext.State.OpenDialogueMapId)
                                          ?.Line(SessionContext.State.OpenDialogueMessage)?.Choice(reply, SeLeOfrece);
                if (offered != null && offered.Next != 0 && offered.Criterion.Length > 0) npcFight = null;
            }
            if (npcFight != null)
            {
                // Only a reply this character is offered: Klaus Konbah's fight of «Eterno descanso»
                // started from a reply sent by hand before the holy water had called him up.
                var fightLine = NpcDialogues.For(SessionContext.State.OpenDialogueNpcId,
                                                 SessionContext.State.OpenDialogueMapId)
                                            ?.Line(SessionContext.State.OpenDialogueMessage);
                var fightChoice = fightLine?.Choice(reply, SeLeOfrece);
                if (fightChoice != null && !SeLeOfrece(fightChoice))
                {
                    Console.WriteLine($"[NPC] Fight reply {reply} is not offered on line {fightLine!.Message} to this character; ignored.");
                    await CloseAsync(stream);
                    return;
                }
                // And what the reply does besides, before the fight takes the character away: «Ve a
                // ver a Klaus Konbah» is done by choosing to defend oneself.
                if (fightChoice != null && fightChoice.Completes != 0)
                    await Managers.Quests.CompleteByReplyAsync(stream, fightChoice.Quest, fightChoice.Completes);
                // And the quest it hands over, first: the prince of Wey's «À votre service» starts
                // «Le Pwince Monseigneuw» and the fight against the fat tofu at once, and the fight's
                // first objective has to be in hand when the fight ends.
                if (fightChoice != null && fightChoice.StartsQuest != 0)
                    await Managers.Quests.StartAsync(stream, fightChoice.StartsQuest);
                // Or the quest it ends there and then, with nothing paid: Gobatchef's goblins called
                // in for whoever sides with the bworks.
                if (fightChoice != null && fightChoice.Forfeits != 0)
                    await Managers.Quests.ForfeitAsync(stream, fightChoice.Forfeits);
                // And its stagings: the tutorial's camera on the Celestial Puch.
                if (fightChoice != null && fightChoice.Stagings.Count > 0)
                    await Managers.MapStagings.PlayAsync(stream, fightChoice.Stagings);
                CerrarConversacion();
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(ConnectionProtocol.NpcDialogCloseReason)));
                var monsters = npcFight.ByLevel
                    ? npcFight.Monsters.Select(m => (m.Monster, MobSpawnManager.GradeForLevel(m.Monster, SessionContext.State.CharacterLevel))).ToList()
                    : npcFight.Monsters;
                var group = MobSpawnManager.ComposeOffMap(monsters);
                if (group == null) return;
                await FightHandler.InitiateFightFromMobCollision(stream, group, SessionContext.State.MapId,
                    arenaMapId: npcFight.ArenaMapId, allies: MobSpawnManager.ComposeMembers(npcFight.Allies),
                    alliesMustLive: npcFight.AlliesMustLive,
                    winMapId: npcFight.WinMapId, winCellId: npcFight.WinCellId);
                return;
            }

            // Did a luminomachine answer? Neither quests nor written tree: what each of its answers
            // does is said by the answer itself.
            if (SessionContext.State.OpenDialogueNpcId == Luminomachine.NpcId &&
                Luminomachine.Owns(reply))
            {
                await MachineReplyAsync(stream, reply);
                return;
            }

            // Belladona: the rules bring her pieces out; the challenge, her game; the rest, goodbye.
            if (SessionContext.State.OpenDialogueNpcId == Managers.GuildRaidEfedra.Npc && Managers.GuildRaidEfedra.Owns(reply))
            {
                if (reply == Managers.GuildRaidEfedra.AskRules)
                {
                    var (question, replies) = await Managers.GuildRaidEfedra.RulesAsync(GameState.CharacterId);
                    SessionContext.State.OpenDialogueMessage = question;
                    await PreguntarAsync(stream, question, replies);
                    return;
                }
                CerrarConversacion();
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(ConnectionProtocol.NpcDialogCloseReason)));
                if (reply == Managers.GuildRaidEfedra.Challenge)
                    await Managers.GuildRaidEfedra.ChallengeAsync(stream, GameState.CharacterId);
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

            // Gargandias's altar and the titan (GargandiasHandler): a reply that moves the player
            // closes first, as every teleporting reply does.
            if (GargandiasHandler.Owns(SessionContext.State.OpenDialogueNpcId))
            {
                var (sentence, choices, then) = await GargandiasHandler.ReplyAsync(stream, SessionContext.State.OpenDialogueNpcId, reply);
                if (sentence == 0)
                {
                    CerrarConversacion();
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(ConnectionProtocol.NpcDialogCloseReason)));
                    if (then is { } where) await TeleportHandler.ToMapAsync(stream, where.Map, where.Cell);
                    return;
                }
                SessionContext.State.OpenDialogueMessage = sentence;
                await PreguntarAsync(stream, sentence, choices);
                return;
            }

            // The alignment's two NPCs (AlignmentHandler).
            if (AlignmentHandler.Owns(SessionContext.State.OpenDialogueNpcId))
            {
                var (question, replies) = await AlignmentHandler.ReplyAsync(stream, SessionContext.State.OpenDialogueNpcId, reply);
                if (question == 0)
                {
                    CerrarConversacion();
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(ConnectionProtocol.NpcDialogCloseReason)));
                    return;
                }
                SessionContext.State.OpenDialogueMessage = question;
                await PreguntarAsync(stream, question, replies);
                return;
            }

            // Ontoral Zo's (AlmanaxOfferingHandler).
            if (AlmanaxOfferingHandler.Owns(SessionContext.State.OpenDialogueNpcId))
            {
                var (question, replies, texts) = await AlmanaxOfferingHandler.ReplyAsync(stream, reply);
                if (question == 0)
                {
                    CerrarConversacion();
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(ConnectionProtocol.NpcDialogCloseReason)));
                    return;
                }
                SessionContext.State.OpenDialogueMessage = question;
                await PreguntarAsync(stream, question, replies, lineParameters: texts);
                return;
            }

            // A jailer's: a criminal handed over, or his news paid for.
            var wanted = await WantedHandler.ReplyAsync(stream, SessionContext.State.OpenDialogueNpcId,
                SessionContext.State.OpenDialogueMapId != 0 ? SessionContext.State.OpenDialogueMapId : SessionContext.State.MapId,
                reply);
            if (wanted is { } next)
            {
                if (next.Question == 0)
                {
                    CerrarConversacion();
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(ConnectionProtocol.NpcDialogCloseReason)));
                    return;
                }
                SessionContext.State.OpenDialogueMessage = next.Question;
                await PreguntarAsync(stream, next.Question, next.Replies);
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

            // A post-boss room's NPC (DungeonExitRooms): only his exit replies leave the dungeon.
            var dialogueState = SessionContext.State;
            long dungeonExit = !dialogueState.IsInFight && dialogueState.OpenDialogueMapId == dialogueState.MapId
                ? DungeonHandler.ExitForReply(dialogueState.OpenDialogueNpcId, dialogueState.MapId, reply)
                : 0;
            if (dungeonExit != 0)
            {
                CerrarConversacion();
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                        ConnectionProtocol.NpcDialogCloseReason)));
                await TeleportHandler.ToMapAsync(stream, dungeonExit);
                return;
            }

            // Does the sentence he is on hand out a quest? It is checked BEFORE going on, because
            // going on changes OpenDialogueMessage, and before CerrarConversacion, which sets it to zero.
            //
            // It goes after choosing and not on reaching the sentence because that is how it is in the
            // capture: the server takes the conversation down to 50071, the player chooses 66788, and
            // only then does the ief with quest 2432 come out.
            // Does this particular answer give a quest? The written tree says so. If there is no tree,
            // the old rule applies: any answer of the sentence the step names.
            var frase = NpcDialogues.For(SessionContext.State.OpenDialogueNpcId,
                                         SessionContext.State.OpenDialogueMapId)
                                    ?.Line(SessionContext.State.OpenDialogueMessage);
            var elegidaAhora = frase?.Choice(reply, SeLeOfrece);

            // A reply the line has but does not offer this character is not his to choose: the real
            // client only sends what it was shown, so this is somebody sending ids by hand.
            if (elegidaAhora != null && !SeLeOfrece(elegidaAhora))
            {
                Console.WriteLine($"[NPC] Reply {reply} is not offered on line {frase!.Message} to this character; ignored.");
                await CloseAsync(stream);
                return;
            }

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

                Console.WriteLine($"[Readables] Offer of element {oferta.Value.Element} accepted.");
                await CloseAsync(stream);
                return;
            }

            // What the reply hands over all at once (Nibe Lula's samples): all of them, or the line
            // that says one is missing and nothing else -- no objective, no quest.
            if (elegidaAhora != null && elegidaAhora.TakesAll.Count > 0)
            {
                if (elegidaAhora.TakesAll.Any(i => Managers.Equipment.HowMany(i) <= 0))
                {
                    var falta = elegidaAhora.RefusedNext == 0 ? null
                        : NpcDialogues.For(SessionContext.State.OpenDialogueNpcId, SessionContext.State.OpenDialogueMapId)
                                      ?.Line(elegidaAhora.RefusedNext);
                    if (falta == null)
                    {
                        await CloseAsync(stream);
                        return;
                    }
                    SessionContext.State.OpenDialogueMessage = falta.Message;
                    await PreguntarAsync(stream, falta.Message, LasQueTocan(falta),
                                         Npcs.TemplateOf(SessionContext.State.OpenDialogueNpcId), falta);
                    Console.WriteLine($"[NPC] Reply {reply}: something to hand over is missing; line {falta.Message}.");
                    return;
                }
                foreach (int item in elegidaAhora.TakesAll)
                {
                    await Managers.Equipment.TakeAsync(stream, item, 1);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                            Managers.InfoMessages.ItemLost, "1",
                            item.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                }
            }

            // Does the answer buy something? Before the quest, because a purchase that fails must not
            // leave the conversation halfway with the quest already started.
            bool sinPagar = false;
            if (elegidaAhora != null && (elegidaAhora.BuysItem != 0 || elegidaAhora.BuysPrice > 0))
            {
                sinPagar = !await ComprarAsync(stream, elegidaAhora);
                // Unpaid, the conversation stops here: its next line is the one that takes the money
                // («*se embolsa discretamente tu dinero y te indica que pases*»), and following it
                // would hand over for nothing what the reply was buying -- the old chapel's way in.
                // With a refusal line written, it is that line instead: the Turist-A promoter's «no
                // puedo venderte un billete por menos 1000 kamas», with its own replies.
                var unpaidLine = !sinPagar || elegidaAhora.RefusedNext == 0 ? null
                    : NpcDialogues.For(SessionContext.State.OpenDialogueNpcId, SessionContext.State.OpenDialogueMapId)
                                  ?.Line(elegidaAhora.RefusedNext);
                if (unpaidLine != null)
                {
                    SessionContext.State.OpenDialogueMessage = unpaidLine.Message;
                    await PreguntarAsync(stream, unpaidLine.Message, LasQueTocan(unpaidLine),
                                         Npcs.TemplateOf(SessionContext.State.OpenDialogueNpcId), unpaidLine);
                    Console.WriteLine($"[NPC] Reply {reply}: not enough kamas; line {unpaidLine.Message}.");
                    return;
                }
                if (sinPagar)
                {
                    CerrarConversacion();
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                            ConnectionProtocol.NpcDialogCloseReason)));
                    return;
                }
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
                    Console.WriteLine($"[Dreams] The Rey Gob's favor was already taken in room {aqui.Id}.");
                }
                else if (sueno != null)
                {
                    if (aqui != null) aqui.FavorTaken = true;
                    // The dream points, the f11: 25 become 38 in the long capture, 25 x 1.5 rounded up.
                    int antes = sueno.DreamPoints;
                    sueno.DreamPoints = (int)Math.Round(sueno.DreamPoints * elegidaAhora.DreamPointsPercent / 100.0,
                                                        MidpointRounding.AwayFromZero);

                    Console.WriteLine($"[Dreams] Reply {reply} leaves the points from " +
                                      $"{antes} at {sueno.DreamPoints} " +
                                      $"({elegidaAhora.DreamPointsPercent}%).");

                    DreamHandler.Persist(sueno);
                    await DreamHandler.RefrescarEstadoAsync(stream);
                }
                else
                {
                    Console.WriteLine($"[Dreams] Reply {reply} touches the points and " +
                                      "there is no dream in progress.");
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

            // A dream goblin's power: the client's actions his accepting reply carries -- "Dejar
            // que Gobserico extermine a los monstruos de la sala actual por ti", 4037 and 4033.
            // It ends the conversation and is done (DreamHandler.GoblinPowerAsync).
            if (elegidaAhora != null && DreamHandler.CarriesGoblinPower(elegidaAhora.Parameters))
            {
                CerrarConversacion();
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                        ConnectionProtocol.NpcDialogCloseReason)));
                await DreamHandler.GoblinPowerAsync(stream, elegidaAhora.Parameters);
                return;
            }

            // A reply that hands a quest over and ends the conversation closes it first: the
            // tutorial capture has kld, then lqn 54 and ief (60352's «Desplazarte.», 60411's
            // «Unirte al maestro Curro»). One that goes on starts it before the next line.
            // What the reply hands to the NPC: Acidrik's beer.
            if (elegidaAhora != null && elegidaAhora.Takes != 0)
            {
                if (Managers.Equipment.HowMany(elegidaAhora.Takes) < elegidaAhora.TakesCount) return;
                await Managers.QuestFollowers.TakeAsync(stream, elegidaAhora.Takes, elegidaAhora.TakesCount);
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                        Managers.InfoMessages.ItemLost, elegidaAhora.TakesCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        elegidaAhora.Takes.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            }

            // A reply that does a conversation objective: before the next line when it goes on,
            // after the window closes when it ends it -- «He neutralizado a la rata.» is kld, then
            // lqn 56 and ied 1633 in the capture.
            int completesAfterClosing = 0;
            if (elegidaAhora != null && elegidaAhora.Completes != 0 && elegidaAhora.StartsQuest == 0 && !sinPagar)
            {
                if (elegidaAhora.Ends && elegidaAhora.TeleportsTo == 0 && elegidaAhora.TeleportPassage == 0)
                    completesAfterClosing = elegidaAhora.Completes;
                else if (await Managers.Quests.CompleteByReplyAsync(stream, elegidaAhora.Quest, elegidaAhora.Completes))
                {
                    await GiveFromReplyAsync(stream, elegidaAhora);
                    // And whoever comes in as it is said (Abel Hoz at the bank).
                    if (elegidaAhora.ShowsNpc != 0) await ComeInAsync(stream, elegidaAhora);
                }
            }
            else if (elegidaAhora != null && (elegidaAhora.Gives != 0 || elegidaAhora.AlsoGives.Count > 0 || elegidaAhora.GivesKamas > 0))
            {
                await GiveFromReplyAsync(stream, elegidaAhora);
            }

            // The other objectives the reply does, all at once: Vey Gana's four slobbers.
            if (elegidaAhora != null && elegidaAhora.CompletesAll.Count > 0 && elegidaAhora.StartsQuest == 0 && !sinPagar)
            {
                foreach (int objective in elegidaAhora.CompletesAll)
                    await Managers.Quests.CompleteByReplyAsync(stream, elegidaAhora.Quest, objective);
            }

            // Somebody who comes in on a reply that does no objective: Vey Gana's «Darle los
            // ingredientes.» draws the country doll out of the barn.
            if (elegidaAhora != null && elegidaAhora.Completes == 0 && elegidaAhora.ShowsNpc != 0)
                await ComeInAsync(stream, elegidaAhora);

            int startsAfterClosing = 0;
            // A quest the reply ends there and then, with nothing paid: the goblin sold to the mulou of
            // «Titi Gobelait le magobelin».
            if (elegidaAhora != null && elegidaAhora.Forfeits != 0 && !sinPagar)
                await Managers.Quests.ForfeitAsync(stream, elegidaAhora.Forfeits);

            if (elegidaAhora != null && elegidaAhora.StartsQuest != 0)
            {
                bool closesHere = elegidaAhora.Ends && elegidaAhora.TeleportsTo == 0 && !elegidaAhora.ReturnsHome
                                  && elegidaAhora.TeleportPassage == 0;
                if (closesHere) startsAfterClosing = elegidaAhora.StartsQuest;
                else
                {
                    await Managers.Quests.StartAsync(stream, elegidaAhora.StartsQuest);
                    // And its first objective, when it is talking to whoever hands it over.
                    if (elegidaAhora.Completes != 0)
                        await Managers.Quests.CompleteByReplyAsync(stream, elegidaAhora.StartsQuest, elegidaAhora.Completes);
                }
            }
            else if (frase == null)
            {
                await Managers.Quests.OnReplyAsync(stream, SessionContext.State.OpenDialogueMessage);
            }

            // And an objective that is this very reply: Noken wishing the character luck.
            await Managers.Quests.OnReplyChosenAsync(stream, reply);

            // And does this NPC guard a dungeon's door? If he goes in, the conversation has ended in a
            // map change and there is nothing more to tell him.
            long dondeHabla = SessionContext.State.OpenDialogueMapId;
            if (dondeHabla == 0) dondeHabla = SessionContext.State.MapId;
            if (await DungeonHandler.AtTheDoorAsync(stream, dondeHabla, reply))
            {
                CerrarConversacion();
                // Going in can be what hands a quest over: Klasmor's «Criptología».
                if (startsAfterClosing != 0) await Managers.Quests.StartAsync(stream, startsAfterClosing);
                return;
            }

            // PortailIncarnam.pcapng: actor 4398, not Anemo, owns the crossing. Keep its destination
            // and exact landing cell in teleports.json, without declaring a fake map interactive.
            // The stagings the reply plays (MapStagings): the tutorial's path to the next island
            // on «Ver cómo se materializa un camino».
            if (elegidaAhora != null && elegidaAhora.Stagings.Count > 0)
                await Managers.MapStagings.PlayAsync(stream, elegidaAhora.Stagings);

            if (elegidaAhora != null && elegidaAhora.TeleportPassage != 0)
            {
                if (SessionContext.State.IsInFight || SessionContext.State.MapId != dondeHabla ||
                    !TeleportManager.TryGetNpcPassage(dondeHabla, elegidaAhora.TeleportPassage,
                        SessionContext.State.OpenDialogueNpcId, out var passage))
                {
                    Console.WriteLine($"[NPC] Passage {elegidaAhora.TeleportPassage} does not belong to this dialogue.");
                    return;
                }

                CerrarConversacion();
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kld, ConnectionProtocol.BuildDialogClosed(
                        ConnectionProtocol.NpcDialogCloseReason)));
                await TeleportHandler.ToMapAsync(stream, passage.DestinationMapId, passage.DestinationCellId);
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

                // Warko's brew: a soul before the map change, so the map he lands on draws him as one.
                if (elegidaAhora.PhantomPhoenix != 0)
                    PhantomHandler.Become(GameState.CharacterId, elegidaAhora.PhantomPhoenix);

                await WorldMoveHandler.TeleportAsync(stream, adonde);
                Console.WriteLine($"[NPC] Reply {reply}: leads to map {adonde}.");
                if (elegidaAhora.PhantomPhoenix != 0) await PhantomHandler.TellAsync(stream);

                // The orbalantir's, after the map change: the mutation taken away or given.
                if (elegidaAhora.EndsMutation) await MutationHandler.EndAsync(stream);
                if (elegidaAhora.GivesMutation != 0) await MutationHandler.GiveAsync(stream, elegidaAhora.GivesMutation);
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

            if (completesAfterClosing != 0
                && await Managers.Quests.CompleteByReplyAsync(stream, elegidaAhora!.Quest, completesAfterClosing))
            {
                await GiveFromReplyAsync(stream, elegidaAhora);
                // Whoever comes in once it is said and the window is shut: Selim Llenneb back in
                // the vault on «Respaldar a Becky y dirigirte hacia la puerta».
                if (elegidaAhora.ShowsNpc != 0) await ComeInAsync(stream, elegidaAhora);
            }

            if (startsAfterClosing != 0)
            {
                await Managers.Quests.StartAsync(stream, startsAfterClosing);
                if (elegidaAhora!.Completes != 0)
                    await Managers.Quests.CompleteByReplyAsync(stream, startsAfterClosing, elegidaAhora.Completes);
                return;
            }

            // And an NPC who comes in and speaks once this one is done (Darmas, then Ganymed).
            if (elegidaAhora != null && elegidaAhora.ThenNpc != 0)
            {
                await ComesInAndSpeaksAsync(stream, elegidaAhora.ThenNpc, elegidaAhora.ThenLine);
                return;
            }

            if (reply != KamasMountainReply)
            {
                Console.WriteLine($"[NPC] Reply {reply}: gives nothing.");
                return;
            }

            GameState.Kamas += KamasMountainReward;
            DatabaseManager.SaveCurrentCharacter();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(GameState.Kamas)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                    ConnectionProtocol.KamasReceivedMessage, KamasMountainReward.ToString())));

            Console.WriteLine($"[NPC] The mountain of kamas pays {KamasMountainReward}; " +
                              $"they now have {GameState.Kamas}.");
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
            var elegida = frase?.Choice(reply, SeLeOfrece);
            if (elegida == null || elegida.Ends) return false;

            // A conquest role taken (Hal Ylanza): its certificate, or the line that says not yet.
            long next = elegida.Next;
            if (elegida.KothRole != 0
                && !await Managers.KothRoles.BecomeAsync(stream, elegida.KothRole, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                next = elegida.RefusedNext;
                if (next == 0) return false;
            }

            // A trade of chapeuvepés (the Sacres): paid, then handed over; without enough, the line
            // that says so.
            if (elegida.Chapeuvepes > 0)
            {
                if (!Managers.HeadHunts.Pay(GameState.CharacterId, -elegida.Chapeuvepes))
                {
                    next = elegida.RefusedNext;
                    if (next == 0) return false;
                }
                else if (elegida.Trades != 0)
                {
                    if (!await Managers.Equipment.GiveAsync(stream, elegida.Trades, elegida.TradeCount))
                    {
                        Managers.HeadHunts.Pay(GameState.CharacterId, elegida.Chapeuvepes);
                        Console.WriteLine($"[NPC] {elegida.TradeCount}x{elegida.Trades} could not be handed over; " +
                                          $"the {elegida.Chapeuvepes} chapeuvepés are given back.");
                    }
                    else
                    {
                        Console.WriteLine($"[NPC] Traded {elegida.TradeCount}x{elegida.Trades} for {elegida.Chapeuvepes} chapeuvepés.");
                    }
                }
            }

            var siguiente = conversacion!.Line(next);
            if (siguiente == null)
            {
                // The editor checks this before saving, so getting here means the file was edited by
                // hand. It is said and closed instead of leaving the player looking at a window that does
                // not respond.
                Console.WriteLine($"[NPC] Reply {reply} of {estado.OpenDialogueNpcId} leads " +
                                  $"to line {next}, which is not written. It closes.");
                return false;
            }

            estado.OpenDialogueMessage = siguiente.Message;
            await PreguntarAsync(stream, siguiente.Message, LasQueTocan(siguiente),
                                 Npcs.TemplateOf(estado.OpenDialogueNpcId), siguiente);

            Console.WriteLine($"[NPC] Reply {reply} leads to line {siguiente.Message}, " +
                              $"with {Math.Max(siguiente.Choices.Count, 1)} replies.");
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
                Console.WriteLine($"[NPC] Purchase of item {gid} with no shop open.");
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
                Console.WriteLine($"[NPC] Vendor {OpenShopNpc} is not on map " +
                                  $"{SessionContext.State.MapId}: the shop closes.");
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
                Console.WriteLine($"[NPC] Vendor {OpenShopNpc} does not sell item {gid}.");
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
                // Out of the live inventory: tokens won after logging in were not in the login's list.
                foreach (var item in Equipment.All)
                {
                    if (item.Template != tokenShop.TokenGid || item.Position != Equipment.Bag) continue;
                    tokenUid = item.Uid;
                    tokenLeft = item.Quantity;
                    break;
                }

                if (tokenUid == 0 || tokenLeft < price)
                {
                    Console.WriteLine($"[NPC] Item {gid} costs {price} token(s) of " +
                                      $"{tokenShop.TokenGid} and there are only {tokenLeft}.");
                    return;
                }
            }
            else if (GameState.Kamas < price)
            {
                Console.WriteLine($"[NPC] Item {gid} costs {price} and there are only {GameState.Kamas}.");
                return;
            }

            long uid = NextUid();
            string effects = NpcShops.EffectsOf(gid);

            if (!DatabaseManager.InsertCharacterItem(uid, GameState.CharacterId, gid, (int)quantity,
                                                     Equipment.Bag, effects))
            {
                Console.WriteLine($"[NPC] Item {gid} could not be saved.");
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
                ? $"[NPC] Bought item {gid} x{quantity} (uid {uid}) for {price}; " +
                  $"{GameState.Kamas} kamas left."
                : $"[NPC] Bought item {gid} x{quantity} (uid {uid}) for {price} token(s) " +
                  $"of {tokenShop.TokenGid}; {tokenLeft} left.");
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
