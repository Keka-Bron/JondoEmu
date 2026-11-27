using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;

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
    public static partial class DungeonHandler
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
            var dungeon = DungeonManager.AtEntrance(mapId, npcId);
            if (dungeon == null || dungeon.FirstRoom == 0) return Array.Empty<long>();

            var options = DungeonDoor.For(npcId);
            var offer = new List<long>(2);

            if (dungeon.OnKeyring && options.Keyring != 0) offer.Add(options.Keyring);
            if (options.Key != 0) offer.Add(options.Key);

            return offer.ToArray();
        }

        /// <summary>
        /// Whether <paramref name="reply"/> is this guardian's keyring reply and the keyring cannot
        /// open his door for the character of the session: the dungeon is not on it, there is no
        /// keyring in the bag, or this week's free entry is spent. The real game does not offer it
        /// then; offered, it only led to the refusal in the chat.
        /// </summary>
        public static bool KeyringOfferedForNothing(int npcId, long mapId, long reply)
        {
            var options = DungeonDoor.For(npcId);
            if (reply == 0 || options.Keyring != reply) return false;
            return KeyringCannotOpen(mapId, npcId);
        }

        /// <summary>
        /// Whether the keyring cannot open the door of the dungeon whose entrance this map is, for the
        /// character of the session: the dungeon is not on it, there is no keyring in the bag, or this
        /// week's free entry is spent. False on a map that is no dungeon's entrance.
        /// </summary>
        public static bool KeyringCannotOpen(long mapId, int npcId = 0)
        {
            var dungeon = DungeonManager.AtEntrance(mapId, npcId);
            if (dungeon == null || dungeon.FirstRoom == 0) return false;

            return !dungeon.OnKeyring
                   || FindInBag(Keyring, 1) == 0
                   || !DungeonKeyring.FreeEntryLeft(SessionContext.State.CharacterId, dungeon.Id, DateTime.Now);
        }

        /// <summary>
        /// Whether <paramref name="reply"/> is this guardian's key reply and the key his dungeon asks
        /// for is not in the bag. The real game does not offer it then: Rotabla the shepherd offers
        /// «Donner la clef des bouftous» only to whoever carries one. Offered, it only led to
        /// «Vous ne possédez pas l'objet nécessaire» in the chat.
        /// </summary>
        public static bool KeyOfferedForNothing(int npcId, long mapId, long reply)
        {
            var options = DungeonDoor.For(npcId);
            if (reply == 0 || options.Key != reply) return false;
            var dungeon = DungeonManager.AtEntrance(mapId, npcId);
            if (dungeon == null || dungeon.FirstRoom == 0 || dungeon.Required.Count == 0) return false;

            return dungeon.Required.All(r => FindInBag(r.Item, r.Count) == 0);
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
        public static async Task<bool> AtTheDoorAsync(NetworkStream stream, long mapId, long reply = 0, string door = "")
        {
            var dungeon = DungeonManager.AtEntrance(mapId, SessionContext.State.OpenDialogueNpcId);
            if (dungeon == null || dungeon.FirstRoom == 0) return false;
            // Before the key is spent.
            if (await Managers.Jail.KeepsInAsync(stream)) return false;
            if (await PlayerPolicies.BlocksActivityAsync(stream, PlayerActivity.Dungeon)) return false;

            var state = SessionContext.State;

            var options = DungeonDoor.For(state.OpenDialogueNpcId);

            // Somebody else on the map keeps the door: this one only stands there. Megustam
            // Laspelas works in the peasants' workshop, where Mawy Ingals keeps the Hungry
            // Sunflower Barn, and asking her about the bread took the character into the barn.
            // A keeper is one whose replies the client words as the door's or whose written
            // conversation names them: Lorkos keeps the Minotot's door in the antechamber, and the
            // Minotot's «¿Qué relación tienes tú con el dofus Púrpura?» took the character in.
            bool Keeps(int npc) => DungeonDoor.For(npc).Asks || NamesItsDoors(npc, mapId);
            if (!Keeps(state.OpenDialogueNpcId) && Npcs.Of(mapId).Any(s => s.NpcId != state.OpenDialogueNpcId && Keeps(s.NpcId)))
                return false;

            bool freeEntryLeft = dungeon.OnKeyring
                                 && DungeonKeyring.FreeEntryLeft(state.CharacterId, dungeon.Id, DateTime.Now);
            // A written reply that names its door goes in by it (DialogueChoice.Door); any other
            // reply is one of the door's own or none.
            var written = WrittenDoor(door);
            if (written == null && NamesItsDoors(state.OpenDialogueNpcId, mapId)) return false;
            if ((written ?? Chosen(dungeon, options, reply, freeEntryLeft)) is not EntryMethod method) return false;

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

            var admission = await CommitAdmissionAsync(stream, dungeon, dungeon.FirstRoom, uid,
                method == EntryMethod.Keyring, guardian: state.OpenDialogueNpcId);
            if (admission == null) return false;

            if (uid != 0)
            {
                Console.WriteLine($"[Dungeon] {dungeon.Name}: one unit of item {item} is spent.");
                await Quests.OnKeyHandedOverAsync(SessionContext.Current, state.OpenDialogueNpcId, item);
            }

            if (method == EntryMethod.Keyring)
                Console.WriteLine($"[Dungeon] {dungeon.Name}: goes in with the keyring. The free entry " +
                                  $"comes back on {DungeonKeyring.NextReset(DateTime.Now):dd/MM}.");

            int keyItem = item != 0 ? item : dungeon.Required.Select(r => r.Item).FirstOrDefault();
            if (admission.KeyedFollowers.Count > 0 && keyItem != 0)
            {
                int guardian = state.OpenDialogueNpcId;
                await QuestSharing.ShareQuestActionAsync(companion => Managers.Quests.OnKeyHandedOverAsync(companion, guardian, keyItem),
                    participates: companion => admission.KeyedFollowers.Contains(companion.CharacterId));
            }

            // The guardian's window is closed before anybody is moved. The client does not close it
            // by itself -- it sends nothing when a conversation ends, it waits for the kld --, so
            // without this it stayed open over the dungeon and not even the cross removed it. Before
            // the teleport and not after, which is the capture's order: the kld goes out ahead of
            // what changes the map.
            await NpcHandler.CloseAsync(stream);

            Console.WriteLine($"[Dungeon] Enters {dungeon.Name}, room 1 of {dungeon.Rooms.Count}.");
            // A new run from the door: the rooms around a lobby are all to be won again.
            DungeonProgress.SetHubWin(state.CharacterId, dungeon, 0);
            foreach (long follower in admission.FollowerIds) DungeonProgress.SetHubWin(follower, dungeon, 0);
            return await EnterNewRunAsync(dungeon, dungeon.FirstRoom, admission);
        }

        private static async Task<bool> EnterNewRunAsync(DungeonManager.Dungeon dungeon, long room, Admission admission)
        {
            if (await admission.MoveAsync(room) < 0) return false;

            DungeonProgress.SetRoom(SessionContext.State.CharacterId, dungeon, room);
            foreach (long follower in admission.FollowerIds)
                DungeonProgress.SetRoom(follower, dungeon, room);
            return true;
        }

        internal static long ResumeRoom(DungeonManager.Dungeon dungeon, long characterId)
        {
            long room = DungeonProgress.SavedRoom(characterId, dungeon);
            return room != 0 && MapManager.GetMapInfo(room) != null ? room : 0;
        }

        internal static long ResumeReply(int npcId, long mapId, long characterId)
        {
            var dungeon = DungeonManager.AtEntrance(mapId, npcId);
            long reply = DungeonDoor.ResumeFor(npcId, dungeon?.Name);
            return reply != 0 && dungeon != null && ResumeRoom(dungeon, characterId) != 0 ? reply : 0;
        }

        // A resume reply is handled here even if the checkpoint disappeared after the window opened.
        internal static async Task<bool> TryResumeAsync(NetworkStream stream, long reply)
        {
            var state = SessionContext.State;
            if (reply == 0) return false;
            var dungeon = DungeonManager.AtEntrance(state.OpenDialogueMapId, state.OpenDialogueNpcId);
            if (reply != DungeonDoor.ResumeFor(state.OpenDialogueNpcId, dungeon?.Name)) return false;
            if (dungeon == null) return false;
            if (state.IsInFight || state.OpenDialogueMapId != state.MapId) return true;

            long room = ResumeRoom(dungeon, state.CharacterId);
            if (room == 0) return true;
            if (await Jail.KeepsInAsync(stream) || await PlayerPolicies.BlocksActivityAsync(stream, PlayerActivity.Dungeon))
                return true;
            if (dungeon.MinLevel > state.CharacterLevel)
            {
                await WarnAsync(stream, NotHighEnough);
                return true;
            }

            // TeleportHandler moves the admitted companions with the controller, so each needs this checkpoint.
            var gameplay = ServerModHooks.Get<IDungeonPartyPolicy>();
            var followers = gameplay?.DungeonFollowers(SessionContext.Current);
            if (followers != null && !gameplay!.CanResumeDungeon(followers, room,
                characterId => DungeonProgress.SavedRoom(characterId, dungeon)))
            {
                await TellAsync(stream, CommandTexts.Get("dungeon.party_resume_room"));
                return true;
            }

            await NpcHandler.CloseAsync(stream);
            await TeleportHandler.ToMapAsync(SessionContext.Current, room, TeleportHandler.MapCentre, followers);
            return true;
        }

        /// <summary>
        /// Whether this guardian takes that item at his door: the key of the dungeon he keeps.
        /// </summary>
        /// <remarks>
        /// A quest that asks for the key to be given to him has it given by going in, not by a
        /// hello: «¿Dónde se ha ido la 7.ª compañía?» wants the laboratory's key handed to Tassa,
        /// and on a hello the key went into the quest and the door then had none to open with.
        /// </remarks>
        internal static bool TakesAtTheDoor(int npcId, long mapId, int itemId)
        {
            var dungeon = DungeonManager.AtEntrance(mapId, npcId);
            if (dungeon == null || dungeon.FirstRoom == 0 || DungeonDoor.For(npcId).Key == 0) return false;
            return dungeon.Required.Any(r => r.Item == itemId);
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
            long to = room != 0 && dungeon.Rooms.Contains(room) ? room : dungeon.FirstRoom;
            var admission = await CommitAdmissionAsync(stream, dungeon, to, uid, method == EntryMethod.Keyring,
                weigh: true, warnOnFailure: false);
            if (admission == null) return false;
            if (uid != 0)
                Console.WriteLine($"[Dungeon] {dungeon.Name}: one unit of item {item} is spent by the search.");

            if (to == dungeon.FirstRoom)
            {
                DungeonProgress.SetHubWin(state.CharacterId, dungeon, 0);
                foreach (long follower in admission.FollowerIds) DungeonProgress.SetHubWin(follower, dungeon, 0);
            }
            Console.WriteLine($"[Dungeon] {state.CharacterName} enters {dungeon.Name} by the search, " +
                              $"room {dungeon.Rooms.IndexOf(to) + 1} of {dungeon.Rooms.Count}.");
            return await EnterNewRunAsync(dungeon, to, admission);
        }

        /// <summary>
        /// A dungeon whose key is a teleport item, used from anywhere: the Multygely, «il suffit de
        /// consommer un Multygely pour être téléporté dans le donjon» and «vous serez directement
        /// téléporté dans la salle 1 du donjon» (Dofus pour les noobs, «Gelaxième Dimension»). Into
        /// the first room on the item's own cell, the item spent there, and a new run started as the
        /// guardian's door starts one: the companions along on a key each, the lobby's and the colours'
        /// wins forgotten. False, nothing spent, when he cannot go in.
        /// </summary>
        internal static async Task<bool> EnterByItemAsync(NetworkStream stream, DungeonManager.Dungeon dungeon,
                                                          long itemUid, int cell)
        {
            var state = SessionContext.State;
            if (dungeon.FirstRoom == 0) return false;
            if (await PlayerPolicies.BlocksActivityAsync(stream, PlayerActivity.Dungeon)) return false;
            if (dungeon.MinLevel > 0 && state.CharacterLevel < dungeon.MinLevel)
            {
                Console.WriteLine($"[Dungeon] {dungeon.Name}: needs level {dungeon.MinLevel} and has {state.CharacterLevel}.");
                await WarnAsync(stream, NotHighEnough);
                return false;
            }
            var admission = await CommitAdmissionAsync(stream, dungeon, dungeon.FirstRoom, itemUid,
                masterKeyring: false, weigh: true, itemCell: cell);
            if (admission == null) return false;
            if (admission.Followers is { Count: > 0 })
                await admission.MoveAsync(dungeon.FirstRoom, cell);

            foreach (long who in admission.FollowerIds.Prepend(state.CharacterId))
            {
                DungeonProgress.SetHubWin(who, dungeon, 0);
                DungeonProgress.SetRoom(who, dungeon, dungeon.FirstRoom);
                DungeonPuddles.Forget(who, dungeon);
            }
            Console.WriteLine($"[Dungeon] {state.CharacterName} enters {dungeon.Name} by its item, " +
                              $"room 1 of {dungeon.Rooms.Count}.");
            return true;
        }

        // Called before the fight sends its return map. A win saves the next room, or clears the
        // checkpoint after the boss. A defeat saves the room the character fought in.
        internal static long RecordFightProgress(long characterId, long room, bool won)
        {
            var dungeon = DungeonManager.OfRoom(room);
            if (dungeon == null) return 0;

            // A boss room chosen in another dungeon's transition room belongs to that run: a win ends
            // it, a defeat keeps the choice to make again and sends the party back to its door.
            if (DungeonManager.ChosenFrom(dungeon) is { } run)
            {
                DungeonProgress.SetRoom(characterId, run, won ? 0 : dungeon.EntranceMapId);
                return won ? 0 : WayBackAfterADefeat(run, characterId);
            }
            if (dungeon.Authored && !dungeon.KeepsADoor) return 0;

            long next = DungeonManager.NextRoom(dungeon, room);
            if (won && next == 0) next = DungeonManager.ChoiceRoom(dungeon);
            if (won && dungeon.Hub?.AfterAWin(room, DungeonProgress.HubWins(characterId, dungeon)) is { } lobby)
            {
                DungeonProgress.SetHubWin(characterId, dungeon, room);
                next = lobby.Map;
            }
            DungeonProgress.SetRoom(characterId, dungeon, won ? next : room);
            if (won && next == 0) DungeonProgress.SetHubWin(characterId, dungeon, 0);
            if (won) return 0;
            return WayBackAfterADefeat(dungeon, characterId);
        }

        private static long WayBackAfterADefeat(DungeonManager.Dungeon dungeon, long characterId)
        {
            long entrance = Expeditions.IsExpedition(dungeon.Id) ? 0 : dungeon.EntranceMapId;
            if (entrance != 0 && !DungeonManager.IsRoom(entrance) && MapManager.GetMapInfo(entrance) != null)
                return entrance;
            long exit = Expeditions.WayOut(dungeon, characterId);
            return DungeonManager.IsRoom(exit) ? 0 : exit;
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

            // A room around a lobby leads back to it, and the last of them on (Kolosso's Caverns).
            // RecordFightProgress has already noted this win; the room is counted either way.
            if (dungeon.Hub?.AfterAWin(roomMapId, DungeonProgress.HubWins(SessionContext.State.CharacterId, dungeon)) is { } lobby)
                return lobby;

            long next = DungeonManager.NextRoom(dungeon, roomMapId);
            if (next != 0) return (next, TeleportHandler.MapCentre);

            // The last room. Out through the exit, which for most of them is the door they came
            // in by.
            return (Expeditions.WayOut(dungeon, SessionContext.State.CharacterId), TeleportHandler.MapCentre);
        }

        /// <summary>
        /// The exit replies a post-boss room's NPC offers up front; none for any other NPC or room.
        /// </summary>
        internal static long[] ExitReplies(int npcId, long mapId)
        {
            var room = DungeonExitRooms.At(mapId);
            return room == null || room.Npc != npcId ? Array.Empty<long>() : room.Replies.OrderBy(r => r).ToArray();
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
            dungeon ??= DungeonManager.LobbyAt(SessionContext.State.MapId);
            long exit = dungeon == null ? 0 : Expeditions.WayOut(dungeon, SessionContext.State.CharacterId);
            // A lobby that is its dungeon's own exit (Al Howin's Vegetable Patch) has no door
            // out: leaving it goes back through the portal that leads in.
            if (exit == SessionContext.State.MapId) exit = DungeonManager.OutsideOf(exit);
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
        /// Whether the guardian's written conversation on that map names its doors: then only
        /// those replies open, whatever the client's wording says.
        /// </summary>
        /// <remarks>
        /// Lorkos, at the centre of the Minotoror's labyrinth, words his door replies his own way
        /// («Aceptar y utilizar la llave del laberinto.»), so the door found none and took every
        /// answer as the way in -- «Informarte acerca del Minotauroro.» too. His written ones say
        /// which reply is the door.
        /// </remarks>
        internal static bool NamesItsDoors(int npcId, long mapId)
            => NpcDialogues.For(npcId, mapId)?.Lines.Any(line => line.Choices.Any(c => c.Door.Length > 0)) == true;

        /// <summary>The entry method a written reply's door names, or null when it names none.</summary>
        internal static EntryMethod? WrittenDoor(string door) => door switch
        {
            Jondo.Unity.World.Content.DialogueChoice.KeyDoor => EntryMethod.Key,
            Jondo.Unity.World.Content.DialogueChoice.KeyringDoor => EntryMethod.Keyring,
            _ => null,
        };

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

        internal static Task WarnAsync(NetworkStream stream, int messageId)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, messageId)));
    }
}
