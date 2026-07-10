using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Google.Protobuf;
using Jondo.Unity.Launcher.Handlers;

namespace Jondo.Unity.Launcher.Network
{
    public static class GameNodeProxy
    {
        private static TcpListener? _tcpListener;
        private static bool _isRunning;
        private static CancellationTokenSource? _cts;

        public static void Start(int port)
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();

            _tcpListener = new TcpListener(IPAddress.Any, port);
            _tcpListener.Start();

            Console.WriteLine($"[+] Emulating Game Node on TCP port {port} (Online)");

            _ = Task.Run(async () =>
            {
                while (_isRunning && _tcpListener != null)
                {
                    try
                    {
                        var client = await _tcpListener.AcceptTcpClientAsync(_cts.Token);
                        _ = HandleGameNodeConnection(client);
                    }
                    catch (Exception ex)
                    {
                        if (!_isRunning) break;
                        Console.WriteLine($"[Game Node Accept Error] {ex.Message}");
                    }
                }
            });
        }

        public static void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _cts?.Cancel();
            _tcpListener?.Stop();
            _tcpListener = null;
        }

        private static async Task HandleGameNodeConnection(TcpClient client)
        {
            using (client)
            {
                try
                {
                    Console.WriteLine($"[+] Client connected to Game Node! ({client.Client.RemoteEndPoint})");
                    var stream = client.GetStream();

                    byte[] payload = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream);
                    if (payload == null) return;

                    string payloadStr = Encoding.UTF8.GetString(payload);
                    await HandleGameNodeSessionAsync(stream, payload, payloadStr);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[-] Game Node Connection Closed: {e.Message}");
                }
            }
        }

        public static async Task HandleGameNodeSessionAsync(NetworkStream stream, byte[] firstPayload, string firstPayloadStr)
        {
            byte[] payload = firstPayload;
            string payloadStr = firstPayloadStr;
            bool isAuthenticated = false;
            bool hasSentIthBurst = false;

            while (_isRunning)
            {
                GameServerProxy.LogTraffic("GAME_C->S", payload, payload.Length);

                if (!isAuthenticated && (payloadStr.Contains("type.ankama.com/hmt") || payloadStr.Contains("type.ankama.com/ise") || payloadStr.Contains("type.ankama.com/jtk") || payloadStr.Contains("type.ankama.com/knx")))
                {
                    isAuthenticated = true;
                    await CharacterSelectionHandler.HandleAuthRequest(stream, payload, payloadStr);
                }
                else if (payloadStr.Contains("type.ankama.com/jto") || payloadStr.Contains("type.ankama.com/kpc") || payloadStr.Contains("type.ankama.com/ksx") || payloadStr.Contains("type.ankama.com/kpa"))
                {
                    await CharacterSelectionHandler.HandleCharacterListRequest(stream, payload, payloadStr);
                }
                else if (payloadStr.Contains("type.ankama.com/ksl"))
                {
                    // Character selection and database synchronization
                    CharacterSelectionHandler.HandleCharacterSelectionRequest(payload);

                    // Stream database-synchronized world entering packets
                    Console.WriteLine("[Game Node] Streaming database-synchronized world entering packets...");
                    using (var ms = new MemoryStream(BasePayloads.WorldEnteringPackets))
                    {
                        int packetCount = 0;
                        // Limit to first 17 entering packets, excluding the legacy transition packets
                        while (ms.Position < ms.Length && packetCount < 17)
                        {
                            int length = 0;
                            int shift = 0;
                            var lenBytes = new System.Collections.Generic.List<byte>();
                            while (true)
                            {
                                int b = ms.ReadByte();
                                if (b == -1) break;
                                lenBytes.Add((byte)b);
                                length |= (b & 0x7F) << shift;
                                if ((b & 0x80) == 0) break;
                                shift += 7;
                            }

                            if (length <= 0 || ms.Position + length > ms.Length) break;

                            byte[] packetPayload = new byte[length];
                            ms.Read(packetPayload, 0, length);

                            // A. Replace the real inventory frame (irm) with one built entirely
                            // from the database inventory — the captured frame is discarded.
                            // icw is NOT the inventory (its official payload carries guild territory data)
                            // and streams through untouched.
                            byte[] targetIrmBytes = Encoding.UTF8.GetBytes("type.ankama.com/irm");
                            if (NetworkEnvelope.ContainsSequence(packetPayload, targetIrmBytes))
                            {
                                packetPayload = NetworkEnvelope.BuildGameNodePacket(
                                    "type.ankama.com/irm", CharacterSelectionHandler.BuildDynamicIrmPayload());
                                lenBytes.Clear();
                                ulong ulen = (ulong)packetPayload.Length;
                                while (true)
                                {
                                    byte b = (byte)(ulen & 0x7F);
                                    ulen >>= 7;
                                    if (ulen > 0) lenBytes.Add((byte)(b | 0x80));
                                    else { lenBytes.Add(b); break; }
                                }
                                Program.LogDebug("[Game Node] Intercepted and streamed database-synchronized irm (inventory) packet.");
                            }

                            // B. Patch CurrentMapMessage (joh) dynamically
                            byte[] targetJohBytes = Encoding.UTF8.GetBytes("type.ankama.com/joh");
                            if (NetworkEnvelope.ContainsSequence(packetPayload, targetJohBytes))
                            {
                                packetPayload = PatchJohPacket(packetPayload, GameState.MapId);
                                lenBytes.Clear();
                                ulong ulen = (ulong)packetPayload.Length;
                                while (true)
                                {
                                    byte b = (byte)(ulen & 0x7F);
                                    ulen >>= 7;
                                    if (ulen > 0) lenBytes.Add((byte)(b | 0x80));
                                    else { lenBytes.Add(b); break; }
                                }
                                Program.LogDebug($"[Game Node] Intercepted and patched joh MapID to: {GameState.MapId}");
                            }

                            // C. Patch CharacterSelectedSuccessMessage (ktw) dynamically
                            byte[] targetKtwBytes = Encoding.UTF8.GetBytes("type.ankama.com/ktw");
                            if (NetworkEnvelope.ContainsSequence(packetPayload, targetKtwBytes))
                            {
                                packetPayload = PatchKtwPacket(packetPayload);
                                lenBytes.Clear();
                                ulong ulen = (ulong)packetPayload.Length;
                                while (true)
                                {
                                    byte b = (byte)(ulen & 0x7F);
                                    ulen >>= 7;
                                    if (ulen > 0) lenBytes.Add((byte)(b | 0x80));
                                    else { lenBytes.Add(b); break; }
                                }
                                Program.LogDebug("[Game Node] Intercepted and patched ktw packet.");
                            }

                            // D. Patch CharacterStatsListMessage (kri) dynamically
                            byte[] targetKriBytes = Encoding.UTF8.GetBytes("type.ankama.com/kri");
                            if (NetworkEnvelope.ContainsSequence(packetPayload, targetKriBytes))
                            {
                                byte[]? updatedKri = StatsHandler.BuildUpdatedKriPacket();
                                if (updatedKri != null)
                                {
                                    packetPayload = updatedKri;
                                    lenBytes.Clear();
                                    ulong ulen = (ulong)packetPayload.Length;
                                    while (true)
                                    {
                                        byte b = (byte)(ulen & 0x7F);
                                        ulen >>= 7;
                                        if (ulen > 0) lenBytes.Add((byte)(b | 0x80));
                                        else { lenBytes.Add(b); break; }
                                    }
                                    Program.LogDebug("[Game Node] Intercepted and patched kri packet.");
                                }
                            }
 
                            // E. Patch MapComplementaryInformationsDataMessage (jpv) dynamically
                            byte[] targetJpvBytes = Encoding.UTF8.GetBytes("type.ankama.com/jpv");
                            if (NetworkEnvelope.ContainsSequence(packetPayload, targetJpvBytes))
                            {
                                packetPayload = PatchJpvEnteringPacket(packetPayload);
                                lenBytes.Clear();
                                ulong ulen = (ulong)packetPayload.Length;
                                while (true)
                                {
                                    byte b = (byte)(ulen & 0x7F);
                                    ulen >>= 7;
                                    if (ulen > 0) lenBytes.Add((byte)(b | 0x80));
                                    else { lenBytes.Add(b); break; }
                                }
                                Program.LogDebug("[Game Node] Intercepted and patched jpv entering packet.");
                            }

                            // Write VarInt length prefix + payload
                            byte[] packet = new byte[lenBytes.Count + packetPayload.Length];
                            Array.Copy(lenBytes.ToArray(), 0, packet, 0, lenBytes.Count);
                            Array.Copy(packetPayload, 0, packet, lenBytes.Count, packetPayload.Length);

                            await stream.WriteAsync(packet, 0, packet.Length);
                            packetCount++;
                            await Task.Delay(20);
                        }
                        Console.WriteLine($"[Game Node] Streamed {packetCount} database-synchronized entering packets to client.");
                    }

                    // Immediately stream the complete, dynamic 33-packet transition burst
                    Console.WriteLine("[Game Node] Sending complete 33-packet dynamic transition burst...");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKqoMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildHhqMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildHmlMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIsfMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLolMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLok1Message());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLok2Message());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIcgMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIboMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildHmjMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildBvrMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLxsMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildHnqMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKsvMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLouMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIyaMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKdxMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIzhMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIznMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildItyMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKojMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKyjMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKtjMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLtkMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLvkMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLwbMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLuyMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildHhfMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildHhhMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLuqMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildHhiMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIdfMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildJrfMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIzuMessage());
                    Console.WriteLine("[Game Node] Sent complete 33-packet dynamic transition burst.");
                }
                else if (payloadStr.Contains("type.ankama.com/loy"))
                {
                    Console.WriteLine("[Game Node] Received loy (World Load Ack) from client. Map loaded successfully. Sending lok and jdj...");
                    
                    // Send lok (SelectedServerData / Game State configuration)
                    byte[] lokBytes = NetworkEnvelope.ConvertHexStringToByteArray("1A-1E-0A-1C-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6C-6F-6B-12-05-10-01-18-CD-01");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, lokBytes);
                    
                    // Send jdj (Server date / Maintenance synchronization)
                    byte[] jdjBytes = NetworkEnvelope.ConvertHexStringToByteArray("12-3A-12-2D-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6A-64-6A-12-16-12-14-32-30-32-36-2D-30-36-2D-33-30-54-30-35-3A-30-30-3A-30-30-5A-18-FF-FF-FF-FF-FF-FF-FF-FF-FF-01");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, jdjBytes);
                    
                    Console.WriteLine("[Game Node] Sent lok and jdj status packets successfully.");
                }
                else if (payloadStr.Contains("type.ankama.com/kkn"))
                {
                    Console.WriteLine("[Game Node] Received kkn from client. Sending initialization burst...");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKkpMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKkmMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKrbMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIlcMessage());
                    
                    // Patch joh dynamically with character's map ID
                    byte[] patchedJoh = PatchJohPacket(TransitionPacketsBuilder.BuildJohMessage(), GameState.MapId);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, patchedJoh);
                    
                    int subAreaId = 1;
                    try
                    {
                        var mapInfo = MapManager.GetMapInfo(GameState.MapId);
                        if (mapInfo != null)
                        {
                            subAreaId = mapInfo.SubAreaId;
                        }
                    }
                    catch { }
                    if (subAreaId == 444) subAreaId = 20663;

                    foreach (var lor in TransitionPacketsBuilder.BuildLorList())
                    {
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, lor);
                    }
                    
                    // Dynamically send character's real stats (kri)
                    byte[]? updatedKri = StatsHandler.BuildUpdatedKriPacket();
                    if (updatedKri != null)
                    {
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, updatedKri);
                    }
                    
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildHmdMessage());
                    
                    foreach (var itp in TransitionPacketsBuilder.BuildItpList())
                    {
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, itp);
                    }
                    Console.WriteLine("[Game Node] Initialization burst sent successfully.");
                }
                else if (payloadStr.Contains("type.ankama.com/lpj"))
                {
                    Console.WriteLine("[Game Node] Received lpj from client. Sending lpe response...");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLpeMessage());
                }
                else if (payloadStr.Contains("type.ankama.com/hmv"))
                {
                    Console.WriteLine("[Game Node] Received hmv from client. Sending official hnk and kqm chat channel lists...");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPayloads.hnk);
                    
                    int subAreaId = 1;
                    try
                    {
                        var mapInfo = MapManager.GetMapInfo(GameState.MapId);
                        if (mapInfo != null)
                        {
                            subAreaId = mapInfo.SubAreaId;
                        }
                    }
                    catch { }

                    if (subAreaId == 444)
                    {
                        subAreaId = 20663;
                    }

                    foreach (var kqm in TransitionPayloads.kqmList)
                    {
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, kqm);
                    }
                }
                else if (payloadStr.Contains("type.ankama.com/ibt"))
                {
                    if (!hasSentIthBurst)
                    {
                        hasSentIthBurst = true;
                        Console.WriteLine("[Game Node] Received ibt from client. Sending final initialization burst (ith, icg, klt, klp)...");
                        
                        int subAreaId = 1;
                        try
                        {
                            var mapInfo = MapManager.GetMapInfo(GameState.MapId);
                            if (mapInfo != null)
                            {
                                subAreaId = mapInfo.SubAreaId;
                            }
                        }
                        catch { }
                        if (subAreaId == 444) subAreaId = 20663;

                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIcgMessage());
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIcgMessage());
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIcgMessage());
                        
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIthMessage());
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKltMessage());
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKlpMessage());
                        Console.WriteLine("[Game Node] Final initialization burst sent successfully.");
                    }
                    else
                    {
                        Console.WriteLine("[Game Node] Received duplicate ibt from client. Ignored.");
                    }
                }
                else if (payloadStr.Contains("type.ankama.com/kkr"))
                {
                    await MapLoadHandler.HandleMapLoadRequest(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/joi"))
                {
                    await MapChangeHandler.HandleMovementRequest(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/jos"))
                {
                    await MapChangeHandler.HandleMapChangeRequest(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/jpp"))
                {
                    await MapChangeHandler.HandleMovementConfirm(stream);
                }
                else if (payloadStr.Contains("type.ankama.com/isi"))
                {
                    await InventoryHandler.HandleItemMovementRequest(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/ilr"))
                {
                    await NpcHandler.HandleNpcGenericActionRequest(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/lxh"))
                {
                    await NpcHandler.HandleLeaveDialogRequest(stream);
                }
                else if (payloadStr.Contains("type.ankama.com/kjl"))
                {
                    await NpcHandler.HandleNpcDialogReply(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/krc"))
                {
                    await StatsHandler.HandleStatsUpgradeRequest(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/kqn"))
                {
                    await ChatHandler.HandleChatMessage(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/itn"))
                {
                    byte[] rawItt = NetworkEnvelope.ConvertHexStringToByteArray("22-22-08-FF-FF-FF-FF-FF-FF-FF-FF-FF-01-12-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-69-74-77");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, rawItt);
                }
                else if (payloadStr.Contains("type.ankama.com/jte"))
                {
                    byte[] rawJtf = NetworkEnvelope.ConvertHexStringToByteArray("0A-1B-12-19-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6A-74-6F-12-02-10-01");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, rawJtf);
                    Console.WriteLine("[Game Node] Sent jtf response");
                }
                else if (payloadStr.Contains("type.ankama.com/kod"))
                {
                    Console.WriteLine("[Game Node] Received Heartbeat/Ping Request (kod) [3.6]");
                    byte[] rawKns = NetworkEnvelope.ConvertHexStringToByteArray("1A-1B-0A-19-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6E-73-12-02-08-01");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, rawKns);
                    Console.WriteLine("[Game Node] Sent Heartbeat/Pong Response (kns)");
                }
                else
                {
                    // Clean and silence known client-side notification payloads that don't require responses
                    // (e.g. UI logs, almanax requests, heartbeats, recipes) to prevent console flooding.
                    string cleanPayload = payloadStr.Replace("?", "").Trim();
                    if (cleanPayload.Contains("kmw") || cleanPayload.Contains("klw") || cleanPayload.Contains("knb") || 
                        cleanPayload.Contains("klo") || cleanPayload.Contains("kmt") || cleanPayload.Contains("jgv") || 
                        cleanPayload.Contains("jct") || cleanPayload.Contains("jfc") || cleanPayload.Contains("kqk") || 
                        cleanPayload.Contains("itr") || cleanPayload.Contains("knc") || cleanPayload.Contains("kna") || 
                        cleanPayload.Contains("hmt") || cleanPayload.Contains("lxi") || cleanPayload.Contains("jqf"))
                    {
                        // Ignored silently as they are secondary client events
                    }
                    else
                    {
                        Console.WriteLine($"[Game Node] Unknown payload received: {payloadStr}");
                    }
                }

                payload = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream);
                if (payload == null) break;
                payloadStr = Encoding.UTF8.GetString(payload);
            }
        }

        private static byte[] PatchJpvEnteringPacket(byte[] packetBytes)
        {
            try
            {
                byte[]? payload = NetworkEnvelope.ExtractGameNodePayload(packetBytes);
                if (payload == null) return packetBytes;

                var jpvMsg = ProtoMessage.Parse(payload);
                var actorFields = jpvMsg.Fields.Where(f => f.FieldNumber == 15 && f.WireType == 2).ToList();
                
                foreach (var actorField in actorFields)
                {
                    var actorMsg = ProtoMessage.Parse(actorField.BytesValue);
                    var contextualIdField = actorMsg.Fields.FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 0);
                    if (contextualIdField != null && (contextualIdField.VarIntValue == GameState.CharacterId || contextualIdField.VarIntValue == 13825558L || contextualIdField.VarIntValue == 906071769378L || contextualIdField.VarIntValue == 670668947750L))
                    {
                        // 1. Update ContextualId to player ID
                        contextualIdField.VarIntValue = GameState.CharacterId;

                        // 2. Overwrite Details with our patched PlayerActorDetails
                        var origDetailsField = actorMsg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
                        if (origDetailsField != null) actorMsg.Fields.Remove(origDetailsField);
                        
                        if (GameState.PlayerActorDetails != null)
                        {
                            actorMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = GameState.PlayerActorDetails });
                        }
                        
                        actorField.BytesValue = actorMsg.ToByteArray();
                        Program.LogDebug($"[Game Node] Patched player actor name and ID inside entering jpv: ID={GameState.CharacterId}");
                    }
                }
                
                return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/jpv", jpvMsg.ToByteArray());
            }
            catch (Exception ex)
            {
                Program.LogDebug("[-] Error patching entering jpv packet: " + ex.Message);
                return packetBytes;
            }
        }

        private static byte[] PatchJohPacket(byte[] packetPayload, long mapId)
        {
            try
            {
                var rootMsg = ProtoMessage.Parse(packetPayload);
                var rootField = rootMsg.Fields.FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 2);
                if (rootField == null) return packetPayload;

                var wrapperMsg = ProtoMessage.Parse(rootField.BytesValue);
                var wrapperField = wrapperMsg.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 2);
                if (wrapperField == null) return packetPayload;

                var anyMsg = ProtoMessage.Parse(wrapperField.BytesValue);
                var anyValueField = anyMsg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
                if (anyValueField == null) return packetPayload;

                var johMsg = ProtoMessage.Parse(anyValueField.BytesValue);
                var mapIdField = johMsg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0);
                if (mapIdField != null)
                {
                    mapIdField.VarIntValue = mapId;
                }
                else
                {
                    johMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = mapId });
                }

                anyValueField.BytesValue = johMsg.ToByteArray();
                wrapperField.BytesValue = anyMsg.ToByteArray();
                rootField.BytesValue = wrapperMsg.ToByteArray();

                return rootMsg.ToByteArray();
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[-] Error patching joh packet: {ex.Message}");
                return packetPayload;
            }
        }

        private static byte[] PatchKtwPacket(byte[] packetPayload)
        {
            try
            {
                var rootMsg = ProtoMessage.Parse(packetPayload);
                var rootField = rootMsg.Fields.FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 2);
                if (rootField == null) return packetPayload;

                var wrapperMsg = ProtoMessage.Parse(rootField.BytesValue);
                var wrapperField = wrapperMsg.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 2);
                if (wrapperField == null) return packetPayload;

                var anyMsg = ProtoMessage.Parse(wrapperField.BytesValue);
                var anyValueField = anyMsg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
                if (anyValueField == null) return packetPayload;

                var ktwMsg = ProtoMessage.Parse(anyValueField.BytesValue);
                
                // In Dofus 3.6, the real CharacterSelectedSuccessMessage is wrapped in Field 1 of the Any value.
                var field1 = ktwMsg.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 2);
                if (field1 != null)
                {
                    var successMsg = ProtoMessage.Parse(field1.BytesValue);
                    
                    // Inside successMsg, Field 3 = characterBaseInfoMsg (CharacterBaseInformations)
                    var field3 = successMsg.Fields.FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 2);
                    if (field3 != null)
                    {
                        var characterBaseInfoMsg = ProtoMessage.Parse(field3.BytesValue);
                        
                        // Inside characterBaseInfoMsg:
                        // Field 2 = characterId (VarInt)
                        // Field 1 = details (CharacterMinimalPlusLookInformations)
                        
                        // 1. Patch characterId (Field 2)
                        var idField = characterBaseInfoMsg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0);
                        if (idField != null)
                        {
                            idField.VarIntValue = GameState.CharacterId;
                            Program.LogDebug($"[KTW Patch] Patched character ID to: {GameState.CharacterId}");
                        }
                        else
                        {
                            characterBaseInfoMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.CharacterId });
                        }
                        
                        // 2. Patch details (Field 1)
                        var detailsField = characterBaseInfoMsg.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 2);
                        if (detailsField != null)
                        {
                            var detailsMsg = ProtoMessage.Parse(detailsField.BytesValue);
                            
                            // Inside detailsMsg:
                            // Field 3 = characterName (String)
                            // Field 6 = characterLevel (VarInt)
                            // Field 2 = entityLook (Message)
                            
                            // Patch name (Field 3)
                            var nameField = detailsMsg.Fields.FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 2);
                            if (nameField != null)
                            {
                                nameField.BytesValue = Encoding.UTF8.GetBytes(GameState.CharacterName);
                                Program.LogDebug($"[KTW Patch] Patched character name to: {GameState.CharacterName}");
                            }
                            else
                            {
                                detailsMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = Encoding.UTF8.GetBytes(GameState.CharacterName) });
                            }
                            
                            // Patch character level (Field 6)
                            var levelField = detailsMsg.Fields.FirstOrDefault(f => f.FieldNumber == 6 && f.WireType == 0);
                            if (levelField != null)
                            {
                                levelField.VarIntValue = GameState.CharacterLevel;
                                Program.LogDebug($"[KTW Patch] Patched character level to: {GameState.CharacterLevel}");
                            }
                            else
                            {
                                detailsMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = GameState.CharacterLevel });
                            }
                            
                            // Patch entityLook (Field 2)
                            var lookField = detailsMsg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
                            if (lookField != null)
                            {
                                try
                                {
                                    var lookWrapper = ProtoMessage.Parse(lookField.BytesValue);
                                    var entityLookField = lookWrapper.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
                                    
                                    byte[] defaultLookBytes = NetworkEnvelope.ConvertHexStringToByteArray("08-01-18-03-22-18-A2-8B-9B-0F-CB-E5-F6-15-A4-E1-B9-19-92-A6-C8-20-88-8C-A0-28-F5-B7-CB-34-2A-03-5B-E4-10-42-01-34-32-02-20-01-38-09");
                                    byte[] entityLookBytes = defaultLookBytes;
                                    if (GameState.LookBytes != null && GameState.LookBytes.Length > 0)
                                    {
                                        entityLookBytes = GameState.LookBytes;
                                    }

                                    if (entityLookField != null)
                                    {
                                        entityLookField.BytesValue = entityLookBytes;
                                    }
                                    else
                                    {
                                        lookWrapper.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = entityLookBytes });
                                    }
                                    
                                    lookField.BytesValue = lookWrapper.ToByteArray();
                                    Program.LogDebug("[KTW Patch] Patched EntityLook inside lookWrapper.");
                                }
                                catch (Exception lookEx)
                                {
                                    Program.LogDebug($"[-] Error patching EntityLook in KTW: {lookEx.Message}");
                                }
                            }
                            
                            detailsField.BytesValue = detailsMsg.ToByteArray();
                        }
                        
                        field3.BytesValue = characterBaseInfoMsg.ToByteArray();
                        field1.BytesValue = successMsg.ToByteArray();
                        anyValueField.BytesValue = ktwMsg.ToByteArray();
                        wrapperField.BytesValue = anyMsg.ToByteArray();
                        rootField.BytesValue = wrapperMsg.ToByteArray();
                        
                        Program.LogDebug("[KTW Patch] Successfully patched ktw packet.");
                        return rootMsg.ToByteArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[-] Error in PatchKtwPacket: {ex.Message}");
            }
            return packetPayload;
        }
    }
}
