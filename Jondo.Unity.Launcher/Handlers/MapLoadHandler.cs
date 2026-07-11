using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
{
    public static class MapLoadHandler
    {
        public static async Task HandleMapLoadRequest(NetworkStream stream, byte[] payload)
        {
            LogDebug("[Game Node] Received Map Complementary Info Request (kkr) [Initial Map Load]");
            byte[]? inner = NetworkEnvelope.ExtractMessagePayload(payload, "type.ankama.com/kkr");
            if (inner == null)
            {
                inner = NetworkEnvelope.ExtractMessagePayload(payload, "type.ankama.com/joi");
            }
            if (inner != null)
            {
                long requestedMapId = 0;
                try
                {
                    int pos = 0;
                    while (pos < inner.Length)
                    {
                        uint tag = NetworkEnvelope.ReadVarInt(inner, ref pos);
                        int wireType = (int)(tag & 7);
                        int fieldNum = (int)(tag >> 3);
                        if (fieldNum == 1 && wireType == 0)
                        {
                            requestedMapId = (long)NetworkEnvelope.ReadVarInt64(inner, ref pos);
                        }
                        else
                        {
                            NetworkEnvelope.SkipField(inner, wireType, ref pos);
                        }
                    }
                }
                catch { }

                long mapIdToLoad = requestedMapId > 0 ? requestedMapId : GameState.MapId;
                if (mapIdToLoad > 0)
                {
                    LogDebug($"[Game Node] Client requested map complementary info for Map ID: {mapIdToLoad} (extracted: {requestedMapId})");
                    GameState.MapId = mapIdToLoad;

                    int spawnCellId = GameState.CellId > 0 ? GameState.CellId : 344;
                    var mapInfo = MapManager.GetMapInfo(mapIdToLoad);
                    int subAreaId = mapInfo != null ? mapInfo.SubAreaId : 1;
                    if (subAreaId == 444)
                    {
                        subAreaId = 20663;
                    }
 
                    // 1. Send lxd (MapComplementaryInfo wrapper) - Dynamically instantiated empty lxd message
                    var emptyLxd = new Jondo.Unity.Protocol.Messages.lxd();
                    byte[] lxdPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/lxd", emptyLxd.ToByteArray());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, lxdPacket);
                    LogDebug($"[Game Node] Sent dynamic empty lxd for Map ID: {mapIdToLoad}");

                    // 2. Send jpv (MapComplementaryInformationsDataMessage) - Dynamically built from DB
                    try
                    {
                        var jpvMsg = new ProtoMessage();

                        // Field 1: subAreaId (VarInt)
                        jpvMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = subAreaId });

                        // Field 4: mapId (VarInt)
                        jpvMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = mapIdToLoad });

                        // Field 12: subArea message wrapper (lkt)
                        var lktMsg = new ProtoMessage();
                        lktMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = subAreaId });
                        jpvMsg.Fields.Add(new ProtoField { FieldNumber = 12, WireType = 2, BytesValue = lktMsg.ToByteArray() });

                        // Field 15: Actors (Repeated)
                        // A. Add Player Character Actor
                        var playerActor = new ProtoMessage();
                        
                        // Disposition (Field 1)
                        var playerDisp = new ProtoMessage();
                        playerDisp.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = spawnCellId });
                        playerDisp.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = GameState.Orientation });
                        playerActor.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = playerDisp.ToByteArray() });

                        // Details (Field 2)
                        if (GameState.PlayerActorDetails != null)
                        {
                            playerActor.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = GameState.PlayerActorDetails });
                        }

                        // Contextual ID (Field 3)
                        playerActor.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = GameState.CharacterId });

                        jpvMsg.Fields.Add(new ProtoField { FieldNumber = 15, WireType = 2, BytesValue = playerActor.ToByteArray() });

                        // B. Add Database NPC Spawns
                        var spawns = DatabaseManager.GetNpcSpawnsForMap(mapIdToLoad);
                        LogDebug($"[Game Node] Building dynamic jpv for Map ID: {mapIdToLoad} containing {spawns.Count} database NPCs.");

                        long npcContextId = -20000;
                        foreach (var spawn in spawns)
                        {
                            var npcActorMsg = BuildNpcActorMsg(spawn, npcContextId);
                            jpvMsg.Fields.Add(new ProtoField { FieldNumber = 15, WireType = 2, BytesValue = npcActorMsg.ToByteArray() });
                            LogDebug($"[Game Node] Spawned NPC {spawn.NpcId} at Cell {spawn.CellId} with contextual ID {npcContextId}.");
                            npcContextId--;
                        }

                        // C. Add Aggressive Mobs
                        var mobs = Managers.MobSpawnManager.GetMobsForMap(mapIdToLoad);
                        foreach (var mob in mobs)
                        {
                            if (mob.Members.Count > 0)
                            {
                                var mobActorMsg = BuildMobGroupActorMsg(mob);
                                jpvMsg.Fields.Add(new ProtoField { FieldNumber = 15, WireType = 2, BytesValue = mobActorMsg.ToByteArray() });
                            }
                        }

                        byte[] jpvBytes = jpvMsg.ToByteArray();
                        byte[] jpvPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/jpv", jpvBytes);
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, jpvPacket);
                        LogDebug($"[Game Node] Sent dynamic database-driven jpv for Map ID: {mapIdToLoad}, Cell: {spawnCellId}.");
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"[Game Node] Error building/sending dynamic jpv: {ex.Message}");
                    }

                    // 3. Send dynamic lsy containing the active subarea ID and status to match official capture
                    var lsyMsg = new ProtoMessage();
                    lsyMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = (long)subAreaId });
                    lsyMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 45L });
                    byte[] lsyPayload = lsyMsg.ToByteArray();
                    byte[] lsyPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/lsy", lsyPayload);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, lsyPacket);
                    LogDebug($"[Game Node] Sent dynamic lsy containing SubArea ID: {subAreaId} (matches official capture).");

                    // 4. Send dynamically instantiated kns (Fymx = true)
                    var knsMsg = new Jondo.Unity.Protocol.Messages.kns
                    {
                        Fymx = true
                    };
                    byte[] knsPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/kns", knsMsg.ToByteArray());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, knsPacket);
                    LogDebug("[Game Node] Sent dynamically instantiated kns (Fymx = true).");
                }
            }
        }

        public static async Task HandleLoy(NetworkStream stream)
        {
            // Server must ACK with kmw (empty packet)
            byte[] rawKmw = NetworkEnvelope.ConvertHexStringToByteArray("0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6D-77");
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, rawKmw);
            LogDebug("[Game Node] Received loy (world load ack) - Sent kmw response.");
        }

        public static async Task HandleLpj(NetworkStream stream)
        {
            // Server must ACK with jfc (empty packet)
            byte[] rawJfc = NetworkEnvelope.ConvertHexStringToByteArray("0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6A-66-63");
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, rawJfc);
            LogDebug("[Game Node] Received lpj (secondary ready signal) - Sent jfc response.");
        }

        private static ProtoMessage BuildNpcActorMsg(DatabaseManager.NpcSpawn spawn, long contextualId)
        {
            // 1. Build Disposition (LFJ)
            var lfjMsg = new ProtoMessage();
            lfjMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = spawn.CellId });
            lfjMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = spawn.Orientation });

            // 2. Build root EntityLook (lkr)
            var rootLook = new ProtoMessage();
            rootLook.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = spawn.BoneId }); // Field 1: bonesId = NPC Bone ID!
            rootLook.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3 }); // Field 3: constant 3

            int npcScale = 100;
            if (!string.IsNullOrEmpty(spawn.Look) && spawn.Look.Contains("|"))
            {
                var parts = spawn.Look.Trim('{', '}').Split('|');
                if (parts.Length > 3 && int.TryParse(parts[3], out int sc))
                {
                    npcScale = sc;
                }
            }
            rootLook.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 2, BytesValue = new byte[] { (byte)npcScale } }); // Field 8: scale (packed VarInt)

            // 3. Build npcMinimalInfo (matching PCAP: Field 4 = tooltipVisible, Field 6 = npcId)
            var npcMinimalInfo = new ProtoMessage();
            npcMinimalInfo.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 }); // tooltipVisible
            npcMinimalInfo.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = spawn.NpcId }); // npcId

            // 4. Build npcInfoWrapper -> Field 5: npcMinimalInfo
            var npcInfoWrapper = new ProtoMessage();
            npcInfoWrapper.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 2, BytesValue = npcMinimalInfo.ToByteArray() });

            // 5. Build detailsMsg (lni) -> Field 1: root EntityLook, Field 2: npcInfoWrapper
            var detailsMsg = new ProtoMessage();
            detailsMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = rootLook.ToByteArray() });
            detailsMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = npcInfoWrapper.ToByteArray() });

            // 6. Build root ActorMsg (lnk)
            var actorMsg = new ProtoMessage();
            actorMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = lfjMsg.ToByteArray() });
            actorMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = detailsMsg.ToByteArray() });
            actorMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = contextualId });

            return actorMsg;
        }

        private static ProtoMessage BuildMobGroupActorMsg(Managers.MobSpawnManager.MobGroup mob)
        {
            var mainMob = mob.Members[0].Monster;

            // 1. Build Disposition (LFJ)
            var lfjMsg = new ProtoMessage();
            lfjMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = mob.CellId });
            lfjMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 3 }); // Random orientation

            // 2. Build root EntityLook (lkr)
            var rootLook = new ProtoMessage();
            int defaultBone = 1; // Default bone
            int npcScale = 100;
            if (!string.IsNullOrEmpty(mainMob.Look) && mainMob.Look.Contains("|"))
            {
                var parts = mainMob.Look.Trim('{', '}').Split('|');
                if (parts.Length > 0 && int.TryParse(parts[0], out int b)) defaultBone = b;
                if (parts.Length > 3 && int.TryParse(parts[3], out int sc)) npcScale = sc;
            }
            
            rootLook.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = defaultBone });
            rootLook.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3 }); // Field 3: constant 3
            rootLook.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 2, BytesValue = new byte[] { (byte)npcScale } });

            // 3. Build npcMinimalInfo (matching PCAP: Field 4 = tooltipVisible, Field 6 = npcId)
            // Using NPC structure for now so they render correctly, using the monster's generic ID as NPC ID
            var npcMinimalInfo = new ProtoMessage();
            npcMinimalInfo.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 }); // tooltipVisible
            npcMinimalInfo.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = mainMob.Id }); 

            // 4. Build npcInfoWrapper -> Field 5: npcMinimalInfo
            var npcInfoWrapper = new ProtoMessage();
            npcInfoWrapper.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 2, BytesValue = npcMinimalInfo.ToByteArray() });

            // 5. Build detailsMsg (lni) -> Field 1: root EntityLook, Field 2: npcInfoWrapper
            var detailsMsg = new ProtoMessage();
            detailsMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = rootLook.ToByteArray() });
            detailsMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = npcInfoWrapper.ToByteArray() });

            // 6. Build root ActorMsg (lnk)
            var actorMsg = new ProtoMessage();
            actorMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = lfjMsg.ToByteArray() });
            actorMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = detailsMsg.ToByteArray() });
            actorMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = mob.MobId }); // contextual ID

            return actorMsg;
        }

        private static byte[] SerializePackedVarints(System.Collections.Generic.List<int> values)
        {
            using (var ms = new MemoryStream())
            {
                foreach (var val in values)
                {
                    ProtoMessage.WriteVarInt(ms, (ulong)val);
                }
                return ms.ToArray();
            }
        }

        private static void LogDebug(string msg)
        {
            Program.LogDebug(msg);
        }
    }
}
