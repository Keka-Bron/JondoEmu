using System;
using System.IO;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Handles item inventory operations:
    /// - Equip / unequip / move items (isi)
    /// - Character visual appearance updates
    ///
    /// Stats computation (kri, isf, bonuses) lives in StatsHandler.
    /// </summary>
    public static class InventoryHandler
    {
        private static readonly Dictionary<int, int> ItemGidToSkinId = new Dictionary<int, int>
        {
            { 10801, 53375140 },  // Paper Hat
            { 10800, 68293394 },  // Paper Cape
            { 10798, 84411912 },  // Paper Shield
            { 10797, 110287861 }  // Paper Sword
        };

        public static async Task HandleItemMovementRequest(NetworkStream stream, byte[] payload)
        {
            LogDebug("[Inventory] Received Item Movement Request (isi) [3.6]");
            byte[]? inner = NetworkEnvelope.ExtractMessagePayload(payload, Op.Uri(Op.Isi));
            if (inner != null)
            {
                long itemUid    = 0;
                int  newPosition = 0; // Protobuf default: 0 = amulet slot (client omits field when value is 0)
                int  quantity    = 1;
                try
                {
                    int pos = 0;
                    while (pos < inner.Length)
                    {
                        uint tag      = NetworkEnvelope.ReadVarInt(inner, ref pos);
                        int  wireType = (int)(tag & 7);
                        int  fieldNum = (int)(tag >> 3);
                        if (fieldNum == 1 && wireType == 0)
                            itemUid     = (long)NetworkEnvelope.ReadVarInt64(inner, ref pos);
                        else if (fieldNum == 2 && wireType == 0)
                            quantity    = (int)NetworkEnvelope.ReadVarInt(inner, ref pos);
                        else if (fieldNum == 3 && wireType == 0)
                            newPosition = (int)NetworkEnvelope.ReadVarInt(inner, ref pos);
                        else
                            NetworkEnvelope.SkipField(inner, wireType, ref pos);
                    }
                }
                catch { }

                LogDebug($"[Inventory] Client requested to equip/move Item UID {itemUid} (qty {quantity}) to position {newPosition}");

                // 1. Process equipment change in memory (updates equipped cache)
                ProcessEquipmentChange(itemUid, newPosition);

                // 2. Confirm move with iry (ObjectMovementMessage)
                byte[] iryPacket = BuildIryPacket(itemUid, newPosition);
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, iryPacket);
                LogDebug($"[Inventory] Sent iry for Item UID {itemUid} → position {newPosition}");

                // 3. Commit transaction in client UI (luy)
                byte[] luyPacket = NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Luy), Array.Empty<byte>());
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, luyPacket);
                LogDebug("[Inventory] Sent luy (InventoryTransactionFinished)");

                // 4. Shortcut bar content packets (hhf / hhh)
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, BuildHhfPacket());
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, BuildHhhPacket());
                LogDebug("[Inventory] Sent hhf and hhh (ShortcutBar)");

                // 5. Update character appearance
                byte[]? lookBytes = UpdateCharacterLook();

                if (lookBytes != null)
                {
                    // 6. luq (UpdateSelfLookMessage) — updates local avatar in inventory panel
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, BuildLuqPacket(lookBytes));
                    LogDebug("[Inventory] Sent luq (UpdateSelfLookMessage)");
                }

                // 7. isf (InventoryWeightMessage)
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, StatsHandler.BuildIsfPacket());
                LogDebug("[Inventory] Sent isf (InventoryWeightMessage)");

                if (lookBytes != null)
                {
                    // 8. kku (ActorLookMessage) — updates character look in the world
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, BuildKkuPacket(lookBytes, Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId));
                    LogDebug("[Inventory] Sent kku (ActorLookMessage)");
                }

                // 9. kri (CharacterStatsListMessage) – reflects equipment stat bonuses
                byte[]? updatedKri = StatsHandler.BuildUpdatedKriPacket();
                if (updatedKri != null)
                {
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, updatedKri);
                    LogDebug("[Inventory] Sent kri (updated stats)");

                    // 9.5. krd (StatsUpgradeResultMessage) – force UI redraw on characteristics panel
                    var emptyKrd = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/krd", new byte[0]);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, emptyKrd);
                    LogDebug("[Inventory] Sent empty krd (UI redraw trigger)");
                }

                // 10. kns (InventoryTransactionCompletion) – finalizes the redraw cycle
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, BuildKnsPacket());
                LogDebug("[Inventory] Sent kns (InventoryTransactionCompletion)");
            }
        }

        // ─── Equipment ──────────────────────────────────────────────────────────────

        private static void ProcessEquipmentChange(long itemUid, int newPosition)
        {
            var item = Jondo.Unity.Launcher.Network.SessionContext.State.GetInventoryItem(itemUid);
            if (item != null)
            {
                item.Position = newPosition;
                DatabaseManager.SaveItemPosition(itemUid, newPosition);

                // Update equipped cache
                if (newPosition >= 0 && newPosition < 63)
                {
                    var equipped = new EquippedItemInfo { Slot = newPosition };
                    foreach (var kvp in item.Effects)
                        equipped.Stats[kvp.Key] = kvp.Value;
                    Jondo.Unity.Launcher.Network.SessionContext.State.SetEquippedItem(itemUid, equipped);
                }
                else
                {
                    Jondo.Unity.Launcher.Network.SessionContext.State.RemoveEquippedItem(itemUid);
                }
            }
        }

        // ─── Appearance ─────────────────────────────────────────────────────────────

        private static byte[]? UpdateCharacterLook()
        {
            if (Jondo.Unity.Launcher.Network.SessionContext.State.LookBytes == null || Jondo.Unity.Launcher.Network.SessionContext.State.LookBytes.Length == 0)
                Jondo.Unity.Launcher.Network.SessionContext.State.LookBytes = NetworkEnvelope.ConvertHexStringToByteArray("08-01-18-03-22-18-A2-8B-9B-0F-CB-E5-F6-15-A4-E1-B9-19-92-A6-C8-20-88-8C-A0-28-F5-B7-CB-34-2A-03-5B-E4-10-42-01-34-32-02-20-01-38-09");

            try
            {
                var lookMsg = ProtoMessage.Parse(Jondo.Unity.Launcher.Network.SessionContext.State.LookBytes);

                // 1. Extract current skins (Field 2 of EntityLook)
                var allSkins = new List<int>();
                foreach (var field in lookMsg.Fields)
                {
                    if (field.FieldNumber == 2)
                    {
                        if (field.WireType == 2) // packed
                        {
                            int subPos = 0;
                            while (subPos < field.BytesValue.Length)
                                allSkins.Add((int)ReadVarIntFromBytes(field.BytesValue, ref subPos));
                        }
                        else if (field.WireType == 0) // unpacked
                        {
                            allSkins.Add((int)field.VarIntValue);
                        }
                    }
                }

                // 2. Remove old equipment skins
                var equipmentSkins = new HashSet<int>(ItemGidToSkinId.Values);
                var updatedSkins   = allSkins.Where(s => !equipmentSkins.Contains(s)).ToList();

                // 3. Add skins of currently equipped items
                foreach (var equippedItem in Jondo.Unity.Launcher.Network.SessionContext.State.GetEquippedItemsCopy())
                {
                    var playerItem = Jondo.Unity.Launcher.Network.SessionContext.State.GetInventoryItem(equippedItem.Key);
                    if (playerItem != null && ItemGidToSkinId.TryGetValue(playerItem.ItemId, out int skinId))
                    {
                        if (!updatedSkins.Contains(skinId))
                            updatedSkins.Add(skinId);
                    }
                }

                // 4. Rebuild skins field in EntityLook
                lookMsg.Fields.RemoveAll(f => f.FieldNumber == 2);
                using (var msSkins = new MemoryStream())
                {
                    foreach (int skin in updatedSkins)
                        ProtoMessage.WriteVarInt(msSkins, (ulong)skin);
                    lookMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = msSkins.ToArray() });
                }

                byte[] entityLookBytes = lookMsg.ToByteArray();
                Jondo.Unity.Launcher.Network.SessionContext.State.LookBytes = entityLookBytes;

                // 5. Reconstruct actor details cleanly
                Jondo.Unity.Launcher.Network.SessionContext.State.PlayerActorDetails = DatabaseManager.ReconstructActorDetails(Jondo.Unity.Launcher.Network.SessionContext.State.LookBytes, Jondo.Unity.Launcher.Network.SessionContext.State.CharacterName);

                LogDebug($"[Appearance] Updated character look. Total skins: {updatedSkins.Count}");
                DatabaseManager.SaveCharacterLook(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId, entityLookBytes);
                LogDebug("[Appearance] Saved updated look to database.");

                return entityLookBytes;
            }
            catch (Exception ex)
            {
                LogDebug($"[-] Error in UpdateCharacterLook: {ex.Message}");
                return null;
            }
        }

        // ─── Packet builders ────────────────────────────────────────────────────────

        private static byte[] BuildIryPacket(long itemUid, int position)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 0)); output.WriteInt64(itemUid);  // Field 1: UID
            output.WriteTag((uint)((2 << 3) | 0)); output.WriteInt32(position); // Field 2: position
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Iry), ms.ToArray());
        }

        private static byte[] BuildLuqPacket(byte[] lookBytes)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((2 << 3) | 2)); output.WriteBytes(ByteString.CopyFrom(lookBytes));     // Field 2: EntityLook
            output.WriteTag((uint)((3 << 3) | 2)); output.WriteString(Guid.NewGuid().ToString());          // Field 3: session UUID
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Luq), ms.ToArray());
        }

        private static byte[] BuildKkuPacket(byte[] lookBytes, long characterId)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 2)); output.WriteBytes(ByteString.CopyFrom(lookBytes)); // Field 1: EntityLook
            output.WriteTag((uint)((2 << 3) | 0)); output.WriteInt64(characterId);                    // Field 2: Character ID
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Kku), ms.ToArray());
        }

        private static byte[] BuildHhfPacket()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 0)); output.WriteInt32(2); // HGZ_EBYW shortcut bar state
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Hhf), ms.ToArray());
        }

        private static byte[] BuildHhhPacket()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 0)); output.WriteInt32(2); // HGZ_EBYW shortcut bar state
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Hhh), ms.ToArray());
        }

        private static byte[] BuildKnsPacket()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 0)); output.WriteBool(true);
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Kns), ms.ToArray());
        }

        // ─── Utilities ──────────────────────────────────────────────────────────────

        private static ulong ReadVarIntFromBytes(byte[] data, ref int pos)
        {
            ulong value = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return value;
        }

        private static void LogDebug(string msg) => Program.LogDebug(msg);
    }
}
