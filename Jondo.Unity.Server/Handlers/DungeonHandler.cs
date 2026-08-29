using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Getting into a dungeon, and getting out of it again.
    /// </summary>
    /// <remarks>
    /// The whole shape of this came off <c>Mazmorras\mazmorra de los jalatós completa</c>, a real
    /// playthrough of the Corte del Jalató Real from the door to the way out. What it shows is:
    ///
    /// <code>
    ///   iov  -> the guardian NPC on the entrance map, action 3, "talk"
    ///   ios  -> line 646, the guardian complaining about his jalatós
    ///   ioy  -> the player answers
    ///   ios  -> line 17040, "¿Seguro que quieres utilizar el manojo de llaves para entrar?"
    ///   ioy  -> the player answers
    ///   kld  -> the conversation closes
    ///   jru  -> map 121373185, which is room 0 of dungeon 1
    /// </code>
    ///
    /// Then, five times over: arrive in the room, walk on, fight, and be moved to the next room.
    /// After the last one, back to map 120063489 — the entrance, which for this dungeon is also
    /// the exit, as it is for 152 of the 187.
    ///
    /// <b>Two things here are ours and not Ankama's, and they are worth naming.</b>
    ///
    /// Ankama's dungeon is a chain of eleven maps: five rooms with a corridor between each pair,
    /// walked through with ordinary doors. This emulator has none of those doors — not one of the
    /// 187 dungeons has a single one of its internal passages, in the extracted table or in
    /// Ankama's own world graph — so a player put in room 0 would be stuck in it. What happens
    /// instead is that winning a fight in a room moves you to the next one, which is the shape the
    /// rooms table can actually support.
    ///
    /// And the guardian's conversation is one reply, not two lines with a confirmation, because
    /// the authored dialogue tree for these NPCs does not exist yet. Line 17040 is a line the
    /// server supplies, exactly like the quest ones; writing it into the tree is a job for the
    /// editor, not for this class.
    /// </remarks>
    public static class DungeonHandler
    {
        /// <summary>The keyring. Opens 107 of the 187 dungeons without their own key.</summary>
        /// <remarks>
        /// Item 10207, "Manojo de llaves". Named here as a constant with its number in the open
        /// rather than read from the dungeon data, because the dungeon data says <em>whether</em> a
        /// dungeon accepts the keyring and never says which item the keyring is.
        /// </remarks>
        public const int Keyring = 10207;

        /// <summary>"No tienes el nivel requerido."</summary>
        /// <remarks>
        /// This used to be <c>InfoMessages.JobLevelTooLow</c>, which is a different sentence: "No
        /// tienes el nivel <b>de oficio</b> necesario." A player turned away from a level 10 door
        /// was being told to go and level a profession.
        /// </remarks>
        private const int NotHighEnough = InfoMessages.LevelTooLow;

        /// <summary>
        /// The player has answered the guardian of a dungeon. Try to let them in.
        /// </summary>
        /// <remarks>
        /// Returns true when the player went in, so the caller knows the conversation ended in a
        /// map change and there is nothing more to say.
        ///
        /// Called on <em>any</em> reply, which is the same simplification the quest handover makes
        /// and for the same reason: nothing in the captured data marks which reply is the yes. The
        /// guardian's own tree, once somebody writes one, is where that belongs.
        /// </remarks>
        /// <summary>
        /// What the guardian of this map should offer, on top of whatever else it says.
        /// </summary>
        /// <remarks>
        /// The keyring ALWAYS when the dungeon takes it, because the free entry it gives is the
        /// player's to spend and hiding the option would hide the reason they cannot get in. The
        /// loose key ONLY when it is in the bag: an option that can never work is worse than no
        /// option, since the player has no way to tell it apart from a broken door.
        ///
        /// Empty when this map is not a dungeon entrance, or when the guardian declares neither
        /// reply -- see <see cref="DungeonDoor"/>, which finds them by wording rather than by id.
        /// </remarks>
        public static long[] DoorReplies(int npcId, long mapId)
        {
            var dungeon = DungeonManager.AtEntrance(mapId);
            if (dungeon == null || dungeon.FirstRoom == 0) return Array.Empty<long>();

            var options = DungeonDoor.For(npcId);
            var offer = new List<long>(2);

            if (dungeon.OnKeyring && options.Keyring != 0) offer.Add(options.Keyring);
            if (options.Key != 0 && HasAKeyFor(dungeon)) offer.Add(options.Key);

            return offer.ToArray();
        }

        /// <summary>Whether one of the keys this dungeon asks for is in the bag right now.</summary>
        private static bool HasAKeyFor(DungeonManager.Dungeon dungeon)
        {
            foreach (var (wanted, count) in dungeon.Required)
            {
                if (FindInBag(wanted, count) != 0) return true;
            }

            return false;
        }

        public static async Task<bool> AtTheDoorAsync(NetworkStream stream, long mapId, long reply = 0)
        {
            var dungeon = DungeonManager.AtEntrance(mapId);
            if (dungeon == null || dungeon.FirstRoom == 0) return false;

            var state = SessionContext.State;

            // Which of the two the player actually picked, when the guardian is one we recognise.
            //
            // When it is not -- and 60 of the 126 entrances have no guardian placed at all, while
            // some of the placed ones declare neither reply -- the old rule stands: ANY answer
            // opens the door. That rule is wrong and it is deliberate. Replacing it with "only the
            // right reply opens" would shut those dungeons completely, and a door that opens on
            // the wrong answer is a smaller lie than one that never opens.
            var options = DungeonDoor.For(state.OpenDialogueNpcId);
            bool recognised = options.Keyring != 0 || options.Key != 0;

            if (recognised && reply != 0 && reply != options.Keyring && reply != options.Key)
            {
                return false;
            }

            if (dungeon.MinLevel > 0 && state.CharacterLevel < dungeon.MinLevel)
            {
                Console.WriteLine($"[Mazmorra] {dungeon.Name}: hace falta nivel {dungeon.MinLevel} y " +
                                  $"se tiene {state.CharacterLevel}.");
                await WarnAsync(stream, NotHighEnough);
                return false;
            }

            // The keyring FIRST when its free entry is still there this week, and the loose key
            // only after that. Backwards from how it was, and it matters to the player: the key is
            // a craftable item somebody paid for or made, and the keyring entry expires unused
            // every Tuesday whether or not it is spent. Burning the key while a free entry was
            // sitting there is throwing away the one that cannot be got back.
            bool freeEntry = dungeon.OnKeyring
                             && DungeonKeyring.FreeEntryLeft(state.CharacterId, dungeon.Id, DateTime.Now);

            // And when the player said WHICH, that decides it rather than the order above. Picking
            // "Darle la llave y entrar" and having the keyring spent instead would be the server
            // overruling a choice it had just offered.
            if (recognised && reply == options.Key && reply != 0) freeEntry = false;

            // And when it is the keyring that is spent rather than missing, say which of the two it
            // is. "No tienes el objeto necesario" would be a lie: the keyring is right there in the
            // bag, it is this week's entry that is gone, and the two send the player to do
            // completely different things about it.
            bool keyringSpent = dungeon.OnKeyring && !freeEntry && FindInBag(Keyring, 1) != 0;

            if (!TryTakeTheKey(dungeon, freeEntry, out long uid, out int item))
            {
                // Told to the PLAYER, not only to the console. This branch used to return in
                // silence: the guardian's dialogue closed, nothing happened, and nothing on screen
                // said why. From the player's chair that is indistinguishable from the dungeon not
                // being implemented at all -- and it is what "no veo opcion de usar el llavero"
                // looks like from the inside, with the server log saying, correctly and only to
                // itself, "falta la llave (8143 x1, o el manojo 10207) y no hay manojo".
                if (keyringSpent)
                {
                    Console.WriteLine($"[Mazmorra] {dungeon.Name}: el manojo ya se usó esta semana " +
                                      $"y no hay llave suelta.");
                    await TellAsync(stream,
                        $"Ya has usado el manojo de llaves en {dungeon.Name} esta semana. " +
                        $"Vuelve el martes {DungeonKeyring.NextReset(DateTime.Now):d/M}. " +
                        "Con una llave puedes entrar las veces que quieras.");
                    return false;
                }

                Console.WriteLine($"[Mazmorra] {dungeon.Name}: falta la llave " +
                                  $"({Wanted(dungeon)}) y no hay manojo.");
                await WarnAsync(stream, InfoMessages.MissingItem);
                return false;
            }

            if (uid != 0)
            {
                // Spent, and the client told, exactly the way destroying an item by hand does it.
                // A key taken only from the database would come back the moment the bag is redrawn.
                if (DatabaseManager.DestroyCharacterItem(state.CharacterId, uid, 1))
                {
                    Equipment.Remove(uid, 1);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(uid)));
                    Console.WriteLine($"[Mazmorra] {dungeon.Name}: se gasta el objeto {item}.");
                }
            }

            if (item == Keyring)
            {
                // One free entry per dungeon per week, and the week turns over on Tuesday. See
                // DungeonKeyring: this is the client's own help text, not a house rule.
                DungeonKeyring.SpendFreeEntry(state.CharacterId, dungeon.Id, DateTime.Now);
                Console.WriteLine($"[Mazmorra] {dungeon.Name}: entra con el manojo. La entrada " +
                                  $"gratis vuelve el {DungeonKeyring.NextReset(DateTime.Now):dd/MM}.");
            }

            Console.WriteLine($"[Mazmorra] Entra en {dungeon.Name}, sala 1 de {dungeon.Rooms.Count}.");
            await TeleportHandler.ToMapAsync(stream, dungeon.FirstRoom);
            return true;
        }

        /// <summary>
        /// Where a fight in a dungeon room should leave the winner: the next room, or the way out.
        /// </summary>
        /// <remarks>
        /// Answered rather than acted on, because the one safe place to move a player at the end
        /// of a fight is inside <c>EndFightAsync</c>, before it sends the map it was going to send.
        /// A teleport issued after that returns is undone: the client answers the fight's own map
        /// message and the handler for that answer writes the old map back over this one.
        ///
        /// Zero means "not in a dungeon, or not somewhere this should interfere with", and the
        /// fight goes on doing what it did before.
        /// </remarks>
        public static long AfterAWinIn(long roomMapId)
        {
            var dungeon = DungeonManager.OfRoom(roomMapId);
            if (dungeon == null) return 0;

            long next = DungeonManager.NextRoom(dungeon, roomMapId);
            if (next != 0) return next;

            // The last room. Out through the exit, which for most of them is the door they came
            // in by.
            return DungeonManager.WayOut(dungeon);
        }

        /// <summary>Whether this map is the last room of a dungeon, where the boss stands.</summary>
        public static bool IsBossRoom(long mapId)
        {
            var dungeon = DungeonManager.OfRoom(mapId);
            return dungeon != null && dungeon.LastRoom == mapId;
        }

        /// <summary>
        /// Finds the key in the bag. Returns false when there is nothing that opens the door.
        /// </summary>
        /// <remarks>
        /// The dungeon's own key first, the keyring second, and a dungeon that asks for nothing
        /// opens for anybody — 61 of the 187 do. <paramref name="uid"/> comes back zero when
        /// nothing has to be spent, which is not the same as failing and must not be read as it.
        ///
        /// Two kinds of key and they behave differently, which is the whole reason this is not one
        /// lookup. The dungeon's OWN key is an ordinary craftable item (handyman) and is SPENT:
        /// handed to the guardian and gone from the bag. The keyring is a quest item, given once
        /// for finishing the tutorial, and is NEVER spent — 107 of the 187 dungeons take it, and
        /// it has to still be there for the next one.
        /// </remarks>
        private static bool TryTakeTheKey(DungeonManager.Dungeon dungeon, bool freeEntry,
                                          out long uid, out int item)
        {
            uid = 0;
            item = 0;

            if (dungeon.Required.Count == 0) return true;

            if (freeEntry && TryTheKeyring(out item)) return true;

            foreach (var (wanted, count) in dungeon.Required)
            {
                long found = FindInBag(wanted, count);
                if (found != 0)
                {
                    uid = found;
                    item = wanted;
                    return true;
                }
            }

            return false;
        }

        /// <summary>The keyring, if it is in the bag. Never spent, so there is no uid to give back.</summary>
        private static bool TryTheKeyring(out int item)
        {
            item = 0;

            {
                long found = FindInBag(Keyring, 1);
                if (found != 0)
                {
                    // Opens the door and IS NOT SPENT, which is why uid stays zero: it is handed
                    // out once, when the tutorial is finished, and it opens every dungeon that
                    // accepts it for the rest of the character's life. Returning its uid here --
                    // which this did -- meant the caller destroyed it on first use, and the player
                    // silently lost the one item that opens 107 of the 187 doors.
                    //
                    // The 80 that refuse it are not a special case anybody has to write down:
                    // availableOnKeyring is in the game's own data and says so. They are the
                    // Expedicion / Expedicion de Audacia / Expedicion de Bravura runs and the
                    // dimensional ones -- La Gelexta Dimension, Memoria de Orukam, Recuerdo de
                    // Imagiro -- and every one of them wants its own loose key.
                    item = Keyring;
                    return true;
                }
            }

            return false;
        }

        /// <summary>The uid of a stack of that item holding at least that many, or zero.</summary>
        private static long FindInBag(int template, int count)
        {
            foreach (var item in Equipment.All)
            {
                if (item.Template == template && item.Quantity >= Math.Max(1, count)) return item.Uid;
            }

            return 0;
        }

        /// <summary>What the door is asking for, for the log.</summary>
        private static string Wanted(DungeonManager.Dungeon dungeon)
        {
            var parts = new List<string>();
            foreach (var (item, count) in dungeon.Required) parts.Add($"{item} x{count}");
            if (dungeon.OnKeyring) parts.Add($"o el manojo {Keyring}");
            return parts.Count == 0 ? "nada" : string.Join(", ", parts);
        }

        /// <summary>
        /// Una frase cualquiera al jugador, por el canal de información y sólo para él.
        /// </summary>
        /// <remarks>
        /// Por la plantilla vacía del cliente — <see cref="InfoMessages.FreeText"/>, cuyo texto es
        /// <c>{0}</c> — porque lo que hay que decir aquí, cuándo vuelve la entrada gratis, no lo
        /// dice ninguna de las frases que el cliente trae escritas.
        /// </remarks>
        private static Task TellAsync(NetworkStream stream, string text)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning,
                                                        InfoMessages.FreeText, text)));

        private static Task WarnAsync(NetworkStream stream, int messageId)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, messageId)));
    }
}
