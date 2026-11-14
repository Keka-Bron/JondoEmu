using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// A storage window open at a house chest, a bin or a guild chest: items in and out, and
    /// closing. The bank and the haven bag chest have their own handlers; the haven bag's moves go
    /// through <see cref="MoveAsync(NetworkStream?, StorageStacks.Place, int, long, bool)"/> too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every storage answers kcr the same way, measured on four captures (frame numbers are
    /// positions in <c>hilo.tramas</c>):
    ///
    ///   C kcr { f1: -1, f2: uid }       house chest 10-13, bin 21-24, guild chest 61-64
    ///   S iua  the stack arrives in the bag, under a new uid (or ivj on the bag's stack it joins)
    ///   S itc  the storage's stack goes (or itd with what is left of it)
    ///   S iun  the pods
    ///
    ///   C kcr { f1: 1, f2: uid }        house chest 16-19, guild chest 48-51
    ///   S itd  it arrives in the storage, under a new uid
    ///   S ium  the bag's stack goes (or ivj with what is left of it, by symmetry: no partial
    ///          deposit was captured)
    ///   S iun
    ///
    /// The haven bag chest sends the same frames the other way round -- what goes first, then what
    /// arrives -- and that is measured too ("abrir cofre de mi merkasako", frames 12-29).
    /// </para>
    /// <para>
    /// A bin and a guild chest are shared, so somebody else may have the same one open. What they
    /// see change is not captured; they get the storage's side of the move, itd for a stack that
    /// is there with its new total and itc for one that went -- the frames the mover gets for the
    /// same thing. An inference.
    /// </para>
    /// </remarks>
    public static class StorageHandler
    {
        public enum Kind { HouseChest, Bin, GuildChest }

        /// <summary>What a session has open.</summary>
        public sealed class Window
        {
            public Kind Kind { get; init; }
            public StorageStacks.Place Place { get; set; }

            /// <summary>Where it was opened: a window left open by a jump elsewhere stops taking items.</summary>
            public long MapId { get; init; }
            public int ElementId { get; init; }

            /// <summary>The guild chest's guild and tab.</summary>
            public long GuildId { get; init; }
            public int Tab { get; set; }
        }

        /// <summary>The window this session has open, if it is still on its map.</summary>
        public static Window? Current
        {
            get
            {
                var window = SessionContext.State.Storage;
                return window != null && window.MapId == SessionContext.State.MapId ? window : null;
            }
        }

        public static bool IsOpen => Current != null;

        /// <summary>Opens the window: kci (or what the kind opens with, already sent) and the content.</summary>
        public static async Task OpenAsync(NetworkStream? stream, Window window, byte[]? opened)
        {
            SessionContext.State.Storage = window;
            if (opened != null) await SendAsync(stream, Op.Kci, opened);
            await SendAsync(stream, Op.Iwb, ConnectionProtocol.BuildStorageContent(StorageStacks.ItemsOf(window.Place)));
        }

        /// <summary>
        /// kla with a window open: khd { f3: 11 }, what the house chest (frames 20-21), the bin
        /// (83-84) and the guild chest (68-69) all answer. False when none is open.
        /// </summary>
        public static async Task<bool> CloseAsync(NetworkStream? stream)
        {
            var window = SessionContext.State.Storage;
            if (window == null) return false;

            SessionContext.State.Storage = null;
            await SendAsync(stream, Op.Khd, ConnectionProtocol.BuildStorageClosed());
            if (window.Kind == Kind.GuildChest) await GuildChestHandler.ViewersChangedAsync(window, SessionContext.Current.Id);
            return true;
        }

        /// <summary>kcr with a window open. False when none is, so the next window in line gets it.</summary>
        public static async Task<bool> MoveAsync(NetworkStream? stream, byte[] payload)
        {
            var window = Current;
            if (window == null) return false;

            if (!ReadMove(payload, out int quantity, out long uid)) return true;

            if (window.Kind == Kind.GuildChest && !await GuildChestHandler.MayMoveAsync(stream, window, quantity > 0, uid))
                return true;

            var logged = window.Kind == Kind.GuildChest ? GuildChestHandler.ItemOfMove(window, quantity, uid) : null;
            if (await MoveAsync(stream, window.Place, quantity, uid, goneFirst: false) && logged != null)
                GuildChestHandler.LogMove(window, quantity, uid, logged.Value);
            return true;
        }

        /// <summary>
        /// The windows' transfer buttons at the haven bag chest, a house chest, a bin or the guild
        /// chest, the same messages as at the bank (BankHandler.TransferAsync): kez everything into
        /// the storage, kgx what it has already, kdd { 1 the stacks, 2 how many } a selection, kcg
        /// everything out, kck out what the bag has already and kcz { 2 the stacks } a selection
        /// out -- «Transférer les objets visibles» from the storage's side. They reached the bank alone, which
        /// was not open, and nothing moved. Each stack moves as a kcr would move it, the guild
        /// chest's rights asked for each. False when no storage is open.
        /// </summary>
        public static async Task<bool> TransferAsync(NetworkStream? stream, byte[] payload, string op)
        {
            var window = Current;
            bool havenBag = window == null && ChestHandler.IsOpen;
            if (window == null && !havenBag) return false;
            var place = window?.Place ?? StorageStacks.HavenBag(SessionContext.State.CharacterId);

            var bag = Equipment.All.Where(i => i.Position == Equipment.Bag).ToList();
            var stored = StorageStacks.ItemsOf(place);
            List<(long Uid, int Quantity)> moves;
            switch (op)
            {
                case "kez":
                    moves = bag.Select(i => (i.Uid, i.Quantity)).ToList();
                    break;
                case "kgx":
                {
                    var there = stored.Select(i => i.Gid).ToHashSet();
                    moves = bag.Where(i => there.Contains(i.Template)).Select(i => (i.Uid, i.Quantity)).ToList();
                    break;
                }
                case "kcg":
                    moves = stored.Select(i => (i.Uid, -i.Quantity)).ToList();
                    break;
                case "kck":
                {
                    var inBag = bag.Select(i => i.Template).ToHashSet();
                    moves = stored.Where(i => inBag.Contains(i.Gid)).Select(i => (i.Uid, -i.Quantity)).ToList();
                    break;
                }
                case "kdd":
                {
                    byte[]? kdd = ConnectionProtocol.ReadPayload(payload, "kdd");
                    if (kdd == null) return true;
                    var fields = ProtoMessage.Parse(kdd).Fields;
                    var uids = BankHandler.PackedOrSingle(fields, 1);
                    var quantities = BankHandler.PackedOrSingle(fields, 2);
                    var byUid = bag.ToDictionary(i => i.Uid);
                    moves = new List<(long, int)>();
                    for (int k = 0; k < uids.Count; k++)
                    {
                        if (!byUid.TryGetValue(uids[k], out var item)) continue;
                        moves.Add((item.Uid, k < quantities.Count && quantities[k] > 0 ? (int)Math.Min(quantities[k], item.Quantity) : item.Quantity));
                    }
                    break;
                }
                case "kcz":
                {
                    byte[]? kcz = ConnectionProtocol.ReadPayload(payload, "kcz");
                    if (kcz == null) return true;
                    var uids = BankHandler.PackedOrSingle(ProtoMessage.Parse(kcz).Fields, 2).ToHashSet();
                    moves = stored.Where(i => uids.Contains(i.Uid)).Select(i => (i.Uid, -i.Quantity)).ToList();
                    break;
                }
                default:
                    return false;
            }

            int moved = 0;
            foreach (var (uid, quantity) in moves)
            {
                if (window?.Kind == Kind.GuildChest && !await GuildChestHandler.MayMoveAsync(stream, window, quantity > 0, uid))
                    continue;
                var logged = window?.Kind == Kind.GuildChest ? GuildChestHandler.ItemOfMove(window, quantity, uid) : null;
                if (!await MoveAsync(stream, place, quantity, uid, goneFirst: havenBag)) continue;
                moved++;
                if (logged != null) GuildChestHandler.LogMove(window!, quantity, uid, logged.Value);
            }
            Console.WriteLine($"[Storage] {SessionContext.State.CharacterName}: {op}, {moved} of {moves.Count} stack(s) moved in {place}.");
            return true;
        }

        /// <summary>
        /// kcr's two fields: f1 the signed count -- positive in, negative out; an int32, so -1
        /// travels as ten bytes of ones -- and f2 the uid. False when there is nothing to move.
        /// </summary>
        public static bool ReadMove(byte[] payload, out int quantity, out long uid)
        {
            quantity = 0;
            uid = 0;
            byte[]? kcr = ConnectionProtocol.ReadPayload(payload, Op.Kcr);
            if (kcr == null) return false;

            foreach (var field in ProtoMessage.Parse(kcr).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) quantity = unchecked((int)field.VarIntValue);
                else if (field.FieldNumber == 2) uid = field.VarIntValue;
            }
            return uid != 0 && quantity != 0;
        }

        /// <summary>
        /// Moves <paramref name="quantity"/> units -- positive into the storage, negative out of
        /// it -- and tells the client, in the order its storage uses. False when nothing moved.
        /// </summary>
        internal static async Task<bool> MoveAsync(NetworkStream? stream, StorageStacks.Place place,
                                                   int quantity, long uid, bool goneFirst)
        {
            long character = SessionContext.State.CharacterId;

            if (quantity > 0)
            {
                var deposit = StorageStacks.Put(place, character, uid, quantity);
                if (deposit == null)
                {
                    Console.WriteLine($"[Storage] {uid} x{quantity} could not go into {place}.");
                    return false;
                }

                Equipment.Remove(deposit.FromUid, deposit.Moved);
                RefreshLegacyInventory(character);

                byte[] arrived = ConnectionProtocol.BuildItemArrived(1, deposit.InStorage);
                (string opcode, byte[] body) left = deposit.LeftInBag == 0
                    ? (Op.Ium, ConnectionProtocol.BuildItemGone(deposit.FromUid))
                    : (Op.Ivj, ConnectionProtocol.BuildItemQuantity(deposit.FromUid, deposit.LeftInBag));

                if (!goneFirst) await SendAsync(stream, Op.Itd, arrived);
                await SendAsync(stream, left.opcode, left.body);
                if (goneFirst) await SendAsync(stream, Op.Itd, arrived);

                await TellOthersAsync(place, Op.Itd, arrived);
            }
            else
            {
                var withdrawal = StorageStacks.Take(place, character, uid, -quantity);
                if (withdrawal == null)
                {
                    Console.WriteLine($"[Storage] {uid} x{-quantity} could not come out of {place}.");
                    return false;
                }

                Equipment.Add(withdrawal.InBag.Uid, withdrawal.InBag.Gid, withdrawal.Moved, Equipment.Bag,
                              withdrawal.InBag.Effects);
                RefreshLegacyInventory(character);

                (string opcode, byte[] body) arrived = withdrawal.Merged
                    ? (Op.Ivj, ConnectionProtocol.BuildItemQuantity(withdrawal.InBag.Uid, withdrawal.InBag.Quantity))
                    : (Op.Iua, ConnectionProtocol.BuildItemArrived(3, withdrawal.InBag));
                (string opcode, byte[] body) left = withdrawal.LeftInStorage == null
                    ? (Op.Itc, ConnectionProtocol.BuildItemGone(withdrawal.FromUid))
                    : (Op.Itd, ConnectionProtocol.BuildItemArrived(1, withdrawal.LeftInStorage));

                if (!goneFirst) await SendAsync(stream, arrived.opcode, arrived.body);
                await SendAsync(stream, left.opcode, left.body);
                if (goneFirst) await SendAsync(stream, arrived.opcode, arrived.body);

                await TellOthersAsync(place, left.opcode, left.body);
            }

            await SendAsync(stream, Op.Iun, ConnectionProtocol.BuildPods(ConnectionProtocol.Carried(Jondo.Unity.Server.Network.SessionContext.State), ConnectionProtocol.Capacity(Jondo.Unity.Server.Network.SessionContext.State)));
            return true;
        }

        /// <summary>The storage's side of a move, to everybody else who has that storage open.</summary>
        private static async Task TellOthersAsync(StorageStacks.Place place, string opcode, byte[] body)
        {
            byte[]? frame = null;
            foreach (var session in SessionRegistry.InWorld())
            {
                if (session.Id == SessionContext.Current.Id) continue;
                var window = session.State.Storage;
                if (window == null || window.MapId != session.State.MapId || window.Place.Key != place.Key) continue;

                frame ??= ConnectionProtocol.Push(opcode, body);
                try
                {
                    await session.SendAsync(frame).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Storage] {session.CharacterId} could not be told of a move in {place}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// The legacy inventory list, read again from the database: what the bank does after a
        /// move, for the readers that still use it.
        /// </summary>
        private static void RefreshLegacyInventory(long characterId)
        {
            try
            {
                GameState.SetInventory(DatabaseManager.LoadInventory(characterId));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Storage] The inventory list could not be read again: {ex.Message}");
            }
        }

        /// <summary>
        /// To this session's client. With no stream -- a test, or anything run outside a
        /// connection -- it goes through the session, which drops it when there is no socket.
        /// </summary>
        internal static Task SendAsync(NetworkStream? stream, string opcode, byte[] body)
            => stream != null
                ? Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(opcode, body))
                : SessionContext.Current.SendAsync(ConnectionProtocol.Push(opcode, body));
    }
}
