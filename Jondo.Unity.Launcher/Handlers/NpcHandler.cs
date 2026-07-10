using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Handles all NPC interaction logic:
    /// - Generic NPC action requests (ilr)
    /// - NPC dialog choice responses
    /// - Leave dialog requests (lxh)
    /// - NPC dialog reply/close (kjl)
    /// </summary>
    public static class NpcHandler
    {
        public static async Task HandleNpcGenericActionRequest(NetworkStream stream, byte[] payload)
        {
            LogDebug("[Game Node] Received NPC Generic Action Request (ilr)");
            byte[]? inner = NetworkEnvelope.ExtractMessagePayload(payload, "type.ankama.com/ilr");
            if (inner == null) return;

            // Distinguish between initial NPC interaction and dialog choice reply
            int posTemp = 0;
            if (inner.Length > 0)
            {
                uint firstTag  = NetworkEnvelope.ReadVarInt(inner, ref posTemp);
                uint firstWire = firstTag & 7;
                if (firstWire == 2)
                {
                    // Dialogue choice ilr — user picked an option in a dialog
                    await HandleNpcDialogChoice(stream);
                    return;
                }
            }

            long mapId           = 0;
            int  actionId        = 0;
            long npcContextualId = 0;

            try
            {
                int pos = 0;
                while (pos < inner.Length)
                {
                    uint tag      = NetworkEnvelope.ReadVarInt(inner, ref pos);
                    uint fieldNum = tag >> 3;
                    uint wireType = tag & 7;

                    if (fieldNum == 1 && wireType == 0)
                        mapId = (long)NetworkEnvelope.ReadVarInt64(inner, ref pos);
                    else if (fieldNum == 2 && wireType == 0)
                        actionId = (int)NetworkEnvelope.ReadVarInt(inner, ref pos);
                    else if (fieldNum == 3 && wireType == 0)
                        npcContextualId = (long)NetworkEnvelope.ReadVarInt64(inner, ref pos);
                    else
                        NetworkEnvelope.SkipField(inner, (int)wireType, ref pos);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"[NPC] Error parsing ilr: {ex.Message}");
                return;
            }

            LogDebug($"[NPC] Interaction: NPC={npcContextualId}, Action={actionId}, Map={mapId}");

            // 1. NpcDialogCreationMessage (ilu) — Field 2: npcContextualId, Field 3: mapId
            var iluMsg = new ProtoMessage();
            iluMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = npcContextualId });
            iluMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = mapId });

            byte[] iluPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/ilu", iluMsg.ToByteArray());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, iluPacket);
            LogDebug("[NPC] Sent NpcDialogCreationMessage (ilu)");

            // 2. NpcDialogQuestionMessage (ilq)
            // Field 1 (repeated choice): replyId=24897 → "Hasta luego." (close option)
            // Field 2: questionId=20776
            var choiceMsg = new ProtoMessage();
            choiceMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 24897 });

            var ilqMsg = new ProtoMessage();
            ilqMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = choiceMsg.ToByteArray() });
            ilqMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 20776 });

            byte[] ilqPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/ilq", ilqMsg.ToByteArray());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ilqPacket);
            LogDebug("[NPC] Sent NpcDialogQuestionMessage (ilq)");
        }

        public static async Task HandleNpcDialogChoice(NetworkStream stream)
        {
            LogDebug("[Game Node] Received NPC Dialog Choice (ilr)");

            // 1. NpcDialogCloseMessage (kjn) — Field 2: 1
            var kjnMsg = new ProtoMessage();
            kjnMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 1 });
            byte[] kjnPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/kjn", kjnMsg.ToByteArray());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, kjnPacket);
            LogDebug("[NPC] Sent NpcDialogCloseMessage (kjn)");

            // 2. kns — Field 1: true (restores interactivity context)
            var knsMsg = new ProtoMessage();
            knsMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 1 });
            byte[] knsPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/kns", knsMsg.ToByteArray());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, knsPacket);
            LogDebug("[NPC] Sent kns to restore context");
        }

        public static async Task HandleLeaveDialogRequest(NetworkStream stream)
        {
            LogDebug("[Game Node] Received Leave Dialog Request (lxh)");

            // LeaveDialogMessage (lxj) — Field 1: 5 (closing dialog)
            byte[] lxjPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/lxj", new byte[] { 0x08, 0x05 });
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, lxjPacket);
            LogDebug("[NPC] Sent LeaveDialogMessage (lxj)");
        }

        public static async Task HandleNpcDialogReply(NetworkStream stream, byte[] payload)
        {
            LogDebug("[Game Node] Received NPC Dialog Reply / Close (kjl)");
            byte[]? inner  = NetworkEnvelope.ExtractMessagePayload(payload, "type.ankama.com/kjl");
            long   replyId = 1; // default: close

            if (inner != null)
            {
                try
                {
                    // Parse kjl → Field 1 is kff bytes → Field 2 of kff is replyId
                    int pos = 0;
                    byte[]? kffBytes = null;
                    while (pos < inner.Length)
                    {
                        uint tag      = NetworkEnvelope.ReadVarInt(inner, ref pos);
                        int  wireType = (int)(tag & 7);
                        int  fieldNum = (int)(tag >> 3);
                        if (fieldNum == 1 && wireType == 2)
                        {
                            uint length = NetworkEnvelope.ReadVarInt(inner, ref pos);
                            kffBytes = new byte[length];
                            Array.Copy(inner, pos, kffBytes, 0, (int)length);
                            break;
                        }
                        else NetworkEnvelope.SkipField(inner, wireType, ref pos);
                    }

                    if (kffBytes != null)
                    {
                        int pos2 = 0;
                        while (pos2 < kffBytes.Length)
                        {
                            uint tag      = NetworkEnvelope.ReadVarInt(kffBytes, ref pos2);
                            int  wireType = (int)(tag & 7);
                            int  fieldNum = (int)(tag >> 3);
                            if (fieldNum == 2 && wireType == 0)
                            {
                                replyId = (long)NetworkEnvelope.ReadVarInt64(kffBytes, ref pos2);
                                break;
                            }
                            else NetworkEnvelope.SkipField(kffBytes, wireType, ref pos2);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"[NPC] Error parsing kjl: {ex.Message}");
                }
            }

            LogDebug($"[NPC] Parsed replyId from kjl: {replyId}");

            // NpcDialogCloseMessage (kjn) — empty payload to finalize dialogue visual state
            byte[] kjnPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/kjn", Array.Empty<byte>());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, kjnPacket);
            LogDebug("[NPC] Sent NpcDialogCloseMessage (kjn) to close dialog");
        }

        private static void LogDebug(string msg) => Program.LogDebug(msg);
    }
}
