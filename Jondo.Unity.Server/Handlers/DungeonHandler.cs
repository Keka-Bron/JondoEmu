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

        /// <summary>"No tienes el nivel necesario", roughly. Reused from the job-level warning.</summary>
        private const int NotHighEnough = InfoMessages.JobLevelTooLow;

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
        public static async Task<bool> AtTheDoorAsync(NetworkStream stream, long mapId)
        {
            var dungeon = DungeonManager.AtEntrance(mapId);
            if (dungeon == null || dungeon.FirstRoom == 0) return false;

            var state = SessionContext.State;

            if (dungeon.MinLevel > 0 && state.CharacterLevel < dungeon.MinLevel)
            {
                Console.WriteLine($"[Mazmorra] {dungeon.Name}: hace falta nivel {dungeon.MinLevel} y " +
                                  $"se tiene {state.CharacterLevel}.");
                await WarnAsync(stream, NotHighEnough);
                return false;
            }

            if (!TryTakeTheKey(dungeon, out long uid, out int item))
            {
                Console.WriteLine($"[Mazmorra] {dungeon.Name}: falta la llave " +
                                  $"({Wanted(dungeon)}) y no hay manojo.");
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
        /// </remarks>
        private static bool TryTakeTheKey(DungeonManager.Dungeon dungeon, out long uid, out int item)
        {
            uid = 0;
            item = 0;

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

            if (dungeon.OnKeyring)
            {
                long found = FindInBag(Keyring, 1);
                if (found != 0)
                {
                    uid = found;
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

        private static Task WarnAsync(NetworkStream stream, int messageId)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, messageId)));
    }
}
