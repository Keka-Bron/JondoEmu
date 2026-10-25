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

        /// <summary>"No tienes el nivel requerido." (You do not have the required level.)</summary>
        /// <remarks>
        /// This used to be <c>InfoMessages.JobLevelTooLow</c>, which is a different sentence: "No
        /// tienes el nivel <b>de oficio</b> necesario." A player turned away from a level 10 door
        /// was being told to go and level a profession.
        /// </remarks>
        private const int NotHighEnough = InfoMessages.LevelTooLow;

        /// <summary>
        /// What the guardian of this map should offer, on top of whatever else it says.
        /// </summary>
        /// <remarks>
        /// Offer the two independent entry methods declared by the guardian. Availability is
        /// checked when the player answers, with a chat warning if the selected method cannot work.
        ///
        /// Empty when this map is not a dungeon entrance, or when the guardian declares neither
        /// reply -- see <see cref="DungeonDoor"/>, which finds them by wording rather than by id.
        /// Such a guardian lets the player in on any answer; see <see cref="Chosen"/>.
        /// </remarks>
        public static long[] DoorReplies(int npcId, long mapId)
        {
            var dungeon = DungeonManager.AtEntrance(mapId);
            if (dungeon == null || dungeon.FirstRoom == 0) return Array.Empty<long>();

            var options = DungeonDoor.For(npcId);
            var offer = new List<long>(2);

            if (dungeon.OnKeyring && options.Keyring != 0) offer.Add(options.Keyring);
            if (options.Key != 0) offer.Add(options.Key);

            return offer.ToArray();
        }

        /// <summary>
        /// The player has answered the guardian of a dungeon. Try to let them in.
        /// </summary>
        /// <remarks>
        /// Returns true when the player went in, so the caller knows the conversation ended in a
        /// map change and there is nothing more to say.
        ///
        /// When the guardian offers a key or keyring reply, only those replies select an entry
        /// method and other dialogue answers do not open the dungeon. When it offers neither, any
        /// answer opens, as it always did; see <see cref="Chosen"/>.
        /// </remarks>
        public static async Task<bool> AtTheDoorAsync(NetworkStream stream, long mapId, long reply = 0)
        {
            var dungeon = DungeonManager.AtEntrance(mapId);
            if (dungeon == null || dungeon.FirstRoom == 0) return false;
            // Before the key is spent.
            if (await Managers.Jail.KeepsInAsync(stream)) return false;

            var state = SessionContext.State;

            var options = DungeonDoor.For(state.OpenDialogueNpcId);
            bool freeEntryLeft = dungeon.OnKeyring
                                 && DungeonKeyring.FreeEntryLeft(state.CharacterId, dungeon.Id, DateTime.Now);
            if (Chosen(dungeon, options, reply, freeEntryLeft) is not EntryMethod method) return false;

            if (dungeon.MinLevel > 0 && state.CharacterLevel < dungeon.MinLevel)
            {
                Console.WriteLine($"[Dungeon] {dungeon.Name}: needs level {dungeon.MinLevel} and " +
                                  $"has {state.CharacterLevel}.");
                await WarnAsync(stream, NotHighEnough);
                return false;
            }

            bool freeEntry = method == EntryMethod.Keyring && freeEntryLeft;

            if (!TryTakeTheKey(dungeon, method, freeEntry, out long uid, out int item))
            {
                if (method == EntryMethod.Keyring)
                {
                    Console.WriteLine($"[Dungeon] {dungeon.Name}: keyring entry unavailable " +
                                      $"(accepted: {dungeon.OnKeyring}, free entry: {freeEntry}, " +
                                      $"owned: {FindInBag(Keyring, 1) != 0}). No loose key is used.");
                }
                else
                {
                    Console.WriteLine($"[Dungeon] {dungeon.Name}: the key is missing " +
                                      $"({Wanted(dungeon)}). The keyring is not used.");
                }
                await TellAsync(stream, Refusal(dungeon, method, options.Asks, freeEntryLeft, DateTime.Now));
                return false;
            }

            if (uid != 0)
            {
                // Spend one key and announce the remaining stack, or the removal of its last unit.
                if (!await ItemUseHandler.SpendOneAsync(stream, uid, weigh: false))
                {
                    await TellAsync(stream, CommandTexts.Get("dungeon.missing_item"));
                    return false;
                }
                Console.WriteLine($"[Dungeon] {dungeon.Name}: one unit of item {item} is spent.");
            }

            if (method == EntryMethod.Keyring)
            {
                // One free entry per dungeon per week, and the week turns over on Tuesday. See
                // DungeonKeyring: this is the client's own help text, not a house rule.
                DungeonKeyring.SpendFreeEntry(state.CharacterId, dungeon.Id, DateTime.Now);
                Console.WriteLine($"[Dungeon] {dungeon.Name}: goes in with the keyring. The free entry " +
                                  $"comes back on {DungeonKeyring.NextReset(DateTime.Now):dd/MM}.");
            }

            // The guardian's window is closed before anybody is moved. The client does not close it
            // by itself -- it sends nothing when a conversation ends, it waits for the kld --, so
            // without this it stayed open over the dungeon and not even the cross removed it. Before
            // the teleport and not after, which is the capture's order: the kld goes out ahead of
            // what changes the map.
            await NpcHandler.CloseAsync(stream);

            Console.WriteLine($"[Dungeon] Enters {dungeon.Name}, room 1 of {dungeon.Rooms.Count}.");
            await TeleportHandler.ToMapAsync(stream, dungeon.FirstRoom);
            return true;
        }

        /// <summary>
        /// Whether the player can go in by the automatic search: high enough, and with the way in
        /// a door that asks nothing would take -- the keyring while this week's free entry is
        /// left, or the dungeon's own key.
        /// </summary>
        internal static bool CanEnterByMatch(DungeonManager.Dungeon dungeon, int level, long characterId)
        {
            if (dungeon == null || dungeon.FirstRoom == 0) return false;
            if (dungeon.MinLevel > 0 && level < dungeon.MinLevel) return false;
            bool freeEntryLeft = dungeon.OnKeyring && DungeonKeyring.FreeEntryLeft(characterId, dungeon.Id, DateTime.Now);
            var method = Chosen(dungeon, new DungeonDoor.Options(), 0, freeEntryLeft) ?? EntryMethod.Key;
            return TryTakeTheKey(dungeon, method, method == EntryMethod.Keyring && freeEntryLeft, out _, out _);
        }

        /// <summary>
        /// The automatic search's way in: the key spent as at the door, then straight into the
        /// first room -- the capture's iun and jru, room 1 of dungeon 141 --, or into
        /// <paramref name="room"/>, where the group one comes to complete is. False when the key
        /// is gone by now.
        /// </summary>
        internal static async Task<bool> EnterByMatchAsync(NetworkStream stream, DungeonManager.Dungeon dungeon, long room = 0)
        {
            var state = SessionContext.State;
            bool freeEntryLeft = dungeon.OnKeyring && DungeonKeyring.FreeEntryLeft(state.CharacterId, dungeon.Id, DateTime.Now);
            var method = Chosen(dungeon, new DungeonDoor.Options(), 0, freeEntryLeft) ?? EntryMethod.Key;
            bool freeEntry = method == EntryMethod.Keyring && freeEntryLeft;
            if (!TryTakeTheKey(dungeon, method, freeEntry, out long uid, out int item))
            {
                await TellAsync(stream, Refusal(dungeon, method, false, freeEntryLeft, DateTime.Now));
                return false;
            }
            if (uid != 0)
            {
                if (!await ItemUseHandler.SpendOneAsync(stream, uid)) return false;
                Console.WriteLine($"[Dungeon] {dungeon.Name}: one unit of item {item} is spent by the search.");
            }
            if (method == EntryMethod.Keyring) DungeonKeyring.SpendFreeEntry(state.CharacterId, dungeon.Id, DateTime.Now);

            long to = room != 0 && dungeon.Rooms.Contains(room) ? room : dungeon.FirstRoom;
            Console.WriteLine($"[Dungeon] {state.CharacterName} enters {dungeon.Name} by the search, " +
                              $"room {dungeon.Rooms.IndexOf(to) + 1} of {dungeon.Rooms.Count}.");
            return await TeleportHandler.ToMapAsync(stream, to) >= 0;
        }

        /// <summary>
        /// Where a fight in a dungeon room should leave the winner, including its arrival cell.
        /// </summary>
        /// <remarks>
        /// Answered rather than acted on, because the one safe place to move a player at the end
        /// of a fight is inside <c>EndFightAsync</c>, before it sends the map it was going to send.
        /// A teleport issued after that returns is undone: the client answers the fight's own map
        /// message and the handler for that answer writes the old map back over this one.
        ///
        /// A zero map means "not in a dungeon, or not somewhere this should interfere with", and the
        /// fight goes on doing what it did before.
        /// </remarks>
        public static (long Map, int Cell) AfterAWinIn(long roomMapId)
        {
            var dungeon = DungeonManager.OfRoom(roomMapId);
            if (dungeon == null) return (0, TeleportHandler.MapCentre);

            // The catalogue's exit is outside. Some dungeons -- Kardorim's -- first leave their
            // winners in a room of their own where an NPC offers the way out (DungeonExitRooms).
            if (DungeonExitRooms.AfterBoss(roomMapId) is { } after && after.Dungeon == dungeon.Id)
                return (after.MapId, after.Cell);

            long next = DungeonManager.NextRoom(dungeon, roomMapId);
            if (next != 0) return (next, TeleportHandler.MapCentre);

            // The last room. Out through the exit, which for most of them is the door they came
            // in by.
            return (DungeonManager.WayOut(dungeon), TeleportHandler.MapCentre);
        }

        /// <summary>
        /// The way out a post-boss room's NPC offers with one of its exit replies -- Kardorim's
        /// four «Quitter la crypte de Kardorim» --; zero for any other NPC, room or reply, whose
        /// own behaviour stays.
        /// </summary>
        internal static long ExitForReply(int npcId, long mapId, long reply)
        {
            var room = DungeonExitRooms.At(mapId);
            if (room == null || room.Npc != npcId || !room.Replies.Contains(reply)) return 0;

            var dungeon = DungeonManager.Get(room.Dungeon);
            return dungeon == null ? 0 : DungeonManager.WayOut(dungeon);
        }

        /// <summary>
        /// «Salir de la mazmorra», the map panel's option inside a dungeon (kjw, empty:
        /// MapInfoUI.ExitDungeon): out through its exterior exit. False, and nothing
        /// done, when the map has no dungeon exit or the player is fighting.
        /// </summary>
        public static async Task<bool> LeaveAsync(NetworkStream stream)
        {
            if (SessionContext.State.IsInFight) return false;
            var dungeon = DungeonManager.OfRoom(SessionContext.State.MapId);
            if (dungeon == null && DungeonExitRooms.At(SessionContext.State.MapId) is { } room)
                dungeon = DungeonManager.Get(room.Dungeon);
            long exit = dungeon == null ? 0 : DungeonManager.WayOut(dungeon);
            if (exit == 0) return false;
            return await TeleportHandler.ToMapAsync(stream, exit) >= 0;
        }

        /// <summary>Whether this map is the last room of a dungeon, where the boss stands.</summary>
        public static bool IsBossRoom(long mapId)
        {
            var dungeon = DungeonManager.OfRoom(mapId);
            return dungeon != null && dungeon.LastRoom == mapId;
        }

        internal enum EntryMethod { Key, Keyring }

        /// <summary>
        /// The entry method this reply picks, or null when it is not one of the door's replies.
        /// </summary>
        /// <remarks>
        /// A guardian that declares neither reply asks nothing, and then any answer opens, as it
        /// always did: nine dungeons have such a guardian at the door (the Larvas, the Minotoror's
        /// two rooms, the Araknas temple…), and waiting for the right reply would shut them for
        /// good. There the keyring goes first while this week's free entry is left, because the
        /// key is a crafted item and the free entry is lost every Tuesday whether used or not; a
        /// door that asks for nothing takes neither.
        /// </remarks>
        internal static EntryMethod? Chosen(DungeonManager.Dungeon dungeon, DungeonDoor.Options options,
                                            long reply, bool freeEntryLeft)
        {
            if (!options.Asks)
            {
                return dungeon.Required.Count > 0 && dungeon.OnKeyring && freeEntryLeft
                       && FindInBag(Keyring, 1) != 0
                    ? EntryMethod.Keyring
                    : EntryMethod.Key;
            }

            if (reply != 0 && reply == options.Keyring) return EntryMethod.Keyring;
            if (reply != 0 && reply == options.Key) return EntryMethod.Key;
            return null;
        }

        /// <summary>What the player is told when the door stays shut.</summary>
        /// <remarks>
        /// When the keyring is in the bag and the dungeon takes it, it is this week's entry that is
        /// gone, not the item, and "you do not have the item" would send the player looking for
        /// something they already own. That applies when they picked the keyring, or when the
        /// guardian asked nothing and the keyring was the one that could have opened.
        /// </remarks>
        internal static string Refusal(DungeonManager.Dungeon dungeon, EntryMethod method, bool guardianAsks,
                                       bool freeEntryLeft, DateTime now)
        {
            bool keyringUsedUp = dungeon.OnKeyring && !freeEntryLeft && FindInBag(Keyring, 1) != 0
                                 && (method == EntryMethod.Keyring || !guardianAsks);

            return keyringUsedUp
                ? CommandTexts.Get("dungeon.keyring_used", (object)dungeon.Name, DungeonKeyring.NextReset(now))
                : CommandTexts.Get("dungeon.missing_item");
        }

        /// <summary>
        /// Finds only the item selected by the player's entry method.
        /// </summary>
        /// <remarks>
        /// There is no fallback between a key and the keyring. <paramref name="uid"/> is zero
        /// for the keyring because it is never consumed; its weekly use is recorded separately.
        ///
        /// Two kinds of key and they behave differently, which is the whole reason this is not one
        /// lookup. The dungeon's OWN key is an ordinary craftable item (handyman) and is SPENT:
        /// handed to the guardian and gone from the bag. The keyring is a quest item, given once
        /// for finishing the tutorial, and is NEVER spent — 107 of the 187 dungeons take it, and
        /// it has to still be there for the next one.
        /// </remarks>
        internal static bool TryTakeTheKey(DungeonManager.Dungeon dungeon, EntryMethod method, bool freeEntry,
                                          out long uid, out int item)
        {
            uid = 0;
            item = 0;

            if (method == EntryMethod.Keyring)
                return dungeon.OnKeyring && freeEntry && TryTheKeyring(out item);

            if (dungeon.Required.Count == 0) return true;

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
            return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
        }

        /// <summary>
        /// A refused entry is a private red, bold chat notice, not a connection error.
        /// </summary>
        /// <remarks>
        /// The free-text template exists at type 0, id 0. Its rich-text tags specify the
        /// requested colour and weight, and the sentence follows the session language.
        /// </remarks>
        private static Task TellAsync(NetworkStream stream, string text)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildNotice($"<color=#FF0000><b>{text}</b></color>")));

        private static Task WarnAsync(NetworkStream stream, int messageId)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, messageId)));
    }
}
