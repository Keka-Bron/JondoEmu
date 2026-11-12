using System;
using System.IO;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Handlers
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

                // 5. Update character appearance
                var looks = UpdateCharacterLook();

                if (looks.HasValue)
                {
                    // 6. luq (UpdateSelfLookMessage) — updates local avatar in inventory panel
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, BuildLuqPacket(looks.Value.Preview));
                    LogDebug("[Inventory] Sent luq (UpdateSelfLookMessage)");
                }

                // 7. isf (InventoryWeightMessage)
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, StatsHandler.BuildIsfPacket());
                LogDebug("[Inventory] Sent isf (InventoryWeightMessage)");

                if (looks.HasValue)
                {
                    // 8. kku (ActorLookMessage) — updates character look in the world
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, BuildKkuPacket(looks.Value.Map, Jondo.Unity.Server.Network.SessionContext.State.CharacterId));
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
            // Both lists and the base, whichever of the two knows the item: one the login's list
            // did not have was never written, and the live one never moved.
            var item = Jondo.Unity.Server.Network.SessionContext.State.GetInventoryItem(itemUid);
            bool live = Managers.Equipment.Move(itemUid, newPosition);
            if (item == null && !live) return;
            DatabaseManager.SaveItemPosition(itemUid, newPosition,
                Jondo.Unity.Server.Network.SessionContext.State.CharacterId);

            // The cache of what is worn, through the only place that writes it.
            if (item != null) Managers.Equipment.RememberWorn(itemUid, newPosition, item.RawEffects);
            else Managers.Equipment.RememberWorn(itemUid, newPosition, Managers.Equipment.ByUid(itemUid)?.Effects);
        }

        // ─── Appearance ─────────────────────────────────────────────────────────────

        private static (EntityLook Preview, EntityLook Map)? UpdateCharacterLook()
        {
            try
            {
                return BuildLooks(SessionContext.Current, DatabaseManager.GetCharacterById,
                    (character, preview) => preview ? CharacterLookBuilder.SemanticLook(character, paraLaVentana: true)
                        : CharacterLookBuilder.SemanticMapLook(character));
            }
            catch (Exception ex)
            {
                LogDebug($"[-] Error in UpdateCharacterLook: {ex.Message}");
                return null;
            }
        }

        internal static (EntityLook Preview, EntityLook Map) BuildLooks(GameSession session,
            Func<long, DbCharacter?> find, Func<DbCharacter, bool, EntityLook> build)
        {
            using var context = SessionContext.Push(session);
            var character = find(session.CharacterId)
                ?? throw new InvalidOperationException($"Character {session.CharacterId} no longer exists.");
            return (build(character, true), build(character, false));
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

        internal static byte[] BuildLuqPacket(EntityLook look)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((2 << 3) | 2)); output.WriteBytes(ByteString.CopyFrom(CharacterLookBuilder.WriteLook(look).Build()));
            output.WriteTag((uint)((3 << 3) | 2)); output.WriteString(Guid.NewGuid().ToString());          // Field 3: session UUID
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Luq), ms.ToArray());
        }

        internal static byte[] BuildKkuPacket(EntityLook look, long characterId)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 2)); output.WriteBytes(ByteString.CopyFrom(CharacterLookBuilder.WriteLook(look).Build()));
            output.WriteTag((uint)((2 << 3) | 0)); output.WriteInt64(characterId);                    // Field 2: Character ID
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Kku), ms.ToArray());
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

        private static void LogDebug(string msg) => Program.LogDebug(msg);
    }
}
