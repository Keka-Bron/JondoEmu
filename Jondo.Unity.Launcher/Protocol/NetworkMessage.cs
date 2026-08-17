using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace Jondo.Protocol
{
    public static class NetworkMessage
    {
        private static readonly ConditionalWeakTable<Stream, SemaphoreSlim> WriteLocks
            = new ConditionalWeakTable<Stream, SemaphoreSlim>();

        public static async Task<byte[]> ReadFrameAsync(Stream stream)
        {
            // Read VarInt length
            int length = 0;
            int shift = 0;
            while (true)
            {
                byte[] buf = new byte[1];
                int read = await stream.ReadAsync(buf, 0, 1);
                if (read == 0) return null; // End of stream
                
                byte b = buf[0];
                length |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            
            // Read payload
            byte[] payload = new byte[length];
            int totalRead = 0;
            while (totalRead < length)
            {
                int read = await stream.ReadAsync(payload, totalRead, length - totalRead);
                if (read == 0) return null; // End of stream prematurely
                totalRead += read;
            }
            
            // Log packet
            try
            {
                string? typeUrl = Jondo.Unity.Launcher.Network.NetworkEnvelope.GetMessageTypeUrl(payload);
                if (typeUrl != null)
                {
                    LogTrafficEnriched("Client -> Server", typeUrl, payload);
                }
            }
            catch { }
            
            return payload;
        }

        public static async Task WriteFrameAsync(Stream stream, IMessage message)
        {
            int size = message.CalculateSize();
            
            using var ms = new MemoryStream();
            var codedStream = new CodedOutputStream(ms);
            
            // Write length as VarInt
            codedStream.WriteUInt32((uint)size);
            
            // Write message payload
            message.WriteTo(codedStream);
            codedStream.Flush();
            
            byte[] buf = ms.ToArray();

            // Log packet
            try
            {
                int pos = 0;
                uint len = Jondo.Unity.Launcher.Network.NetworkEnvelope.ReadVarInt(buf, ref pos);
                byte[] payload = new byte[len];
                Array.Copy(buf, pos, payload, 0, len);
                string? typeUrl = Jondo.Unity.Launcher.Network.NetworkEnvelope.GetMessageTypeUrl(payload);
                if (typeUrl != null)
                {
                    LogTrafficEnriched("Server -> Client", typeUrl, payload);
                }
                Jondo.Unity.Launcher.Network.GameServerProxy.LogTraffic("S->C", payload, payload.Length);
            }
            catch { }
            
            await WriteSerializedAsync(stream, buf);
        }

        public static async Task WriteFrameAsync(Stream stream, byte[] payload)
        {
            using var ms = new MemoryStream();
            var codedStream = new CodedOutputStream(ms);
            
            // Write length as VarInt
            codedStream.WriteUInt32((uint)payload.Length);
            codedStream.Flush();
            byte[] lenBytes = ms.ToArray();

            // Log packet
            try
            {
                string? typeUrl = Jondo.Unity.Launcher.Network.NetworkEnvelope.GetMessageTypeUrl(payload);
                if (typeUrl != null)
                {
                    LogTrafficEnriched("Server -> Client", typeUrl, payload);
                }
                Jondo.Unity.Launcher.Network.GameServerProxy.LogTraffic("S->C", payload, payload.Length);
            }
            catch { }
            
            byte[] frame = new byte[lenBytes.Length + payload.Length];
            Buffer.BlockCopy(lenBytes, 0, frame, 0, lenBytes.Length);
            Buffer.BlockCopy(payload, 0, frame, lenBytes.Length, payload.Length);
            await WriteSerializedAsync(stream, frame);
        }

        private static async Task WriteSerializedAsync(Stream stream, byte[] frame)
        {
            var gate = WriteLocks.GetValue(stream, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                await stream.WriteAsync(frame, 0, frame.Length);
            }
            finally
            {
                gate.Release();
            }
        }

        private static void LogTrafficEnriched(string direction, string typeUrl, byte[] payload)
        {
            int length = payload.Length;
            var meta = GetPacketMetadata(typeUrl);
            
            // Choose color for direction
            ConsoleColor dirColor = direction.Contains("Client") ? ConsoleColor.Cyan : ConsoleColor.Green;

            // Choose color for categories to make it visual
            ConsoleColor taskColor = ConsoleColor.Gray;
            if (meta.Task == "Character") taskColor = ConsoleColor.Yellow;
            else if (meta.Task == "Interfaces") taskColor = ConsoleColor.Magenta;
            else if (meta.Task == "Inventory") taskColor = ConsoleColor.DarkYellow;
            else if (meta.Task == "Map") taskColor = ConsoleColor.Blue;
            else if (meta.Task == "Chat") taskColor = ConsoleColor.Red;
            else if (meta.Task == "Connection") taskColor = ConsoleColor.DarkCyan;
            else if (meta.Task == "Sync") taskColor = ConsoleColor.DarkGreen;

            lock (Console.Out)
            {
                Console.ForegroundColor = dirColor;
                Console.Write($"[{direction}] ");
                
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"({length} B) ");
                
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"[{meta.Context}] ");
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"[{meta.Task}] ");
                
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"{typeUrl.Replace("type.ankama.com/", "")} -> ");
                
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(meta.Description);

                string shortType = typeUrl.Replace("type.ankama.com/", "").Trim();
                bool isPing = shortType == "kod" || shortType == "kns" || shortType == "kpc" || shortType == "jgv";

                if (!isPing)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    string hexStr = BitConverter.ToString(payload);
                    if (hexStr.Length > 160) hexStr = hexStr.Substring(0, 160) + "... [truncated]";
                    Console.WriteLine($"   📦 Hex Payload: {hexStr}");

                    try
                    {
                        var protoTree = Jondo.Unity.Launcher.Network.ProtoMessage.Parse(payload);
                        string treeDump = protoTree.DumpFieldsToString("      ", maxLines: 12);
                        if (!string.IsNullOrWhiteSpace(treeDump))
                        {
                            Console.ForegroundColor = ConsoleColor.DarkCyan;
                            Console.WriteLine("   🌳 Protobuf Payload Tree:");
                            Console.WriteLine(treeDump);
                        }
                    }
                    catch { }
                }
                
                Console.ResetColor();
            }
        }

        private static (string Context, string Task, string Description) GetPacketMetadata(string typeUrl)
        {
            string uri = typeUrl.Replace("type.ankama.com/", "").Trim();

            switch (uri)
            {
                // === Context: Server List ===
                case "knx": return ("Server List", "Connection", "AuthTokenRequest (Token authentication request)");
                case "kof": return ("Server List", "Connection", "ProtocolAccepted (Network protocol accepted)");
                case "lor": return ("Server List", "Sync", "TimeMessage (Server clock synchronization)");
                case "hnp": return ("Server List", "Sync", "SystemConfiguration (Game variable configuration)");
                case "knr": return ("Server List", "Sync", "BreedListMessage (List of enabled character breeds)");
                case "mfa": return ("Server List", "Connection", "FeatureStatus (Account feature status)");
                case "mez": return ("Server List", "Connection", "ServerSeasonInfo (Season of the active server)");
                case "hnv": return ("Server List", "Connection", "ServerOptionalFeatures (Optional features)");
                case "kpc": return ("Server List", "Connection", "PingRequest (Latency and session check)");
                case "kos": return ("Server List", "Connection", "ServerSelectionStatus (Server connection status)");

                // === Context: Character Selection ===
                case "klp": return ("Character Selection", "Interfaces", "CharacterListEmpty (Initial empty character list)");
                case "ksx": return ("Character Selection", "Interfaces", "CharacterListRequest (Character request - phase 1)");
                case "kpa": return ("Character Selection", "Interfaces", "CharacterListRequest (Character request - phase 2)");
                case "mes": return ("Character Selection", "Interfaces", "MessageWrapper (UI container wrapper)");
                case "knv": return ("Character Selection", "Interfaces", "UiLayoutMessage (Metadata of the selection UI)");
                case "ksq": return ("Character Selection", "Character", "CharacterListMessage (Detailed character list)");
                case "jrf": return ("Character Selection", "Sync", "WorldReady (Signal that the world is ready to simulate)");
                case "ksl": return ("Character Selection", "Character", "CharacterSelectionRequest (Request to enter the world)");

                // === Context: World Loading ===
                case "kri": return ("World Loading", "Character", "CharacterStatsListMessage (Full stats and characteristics)");
                case "itp": return ("World Loading", "Interfaces", "ShortcutBarContentMessage (Keyboard shortcuts and UI bars)");
                case "izn": return ("World Loading", "Interfaces", "ChatChannelsListMessage (Initialization of the local chat channels)");
                case "krh": return ("World Loading", "Sync", "ClientReadyForPackets (Ack that the client is ready)");
                case "imd": return ("World Loading", "Inventory", "InventoryWeightMessage (Basic initialization of the inventory weight)");
                case "ktw": return ("World Loading", "Character", "CharacterSelectedSuccessMessage (Character spawn on the pedestal)");
                case "mek": return ("World Loading", "Interfaces", "SpellListMessage (The character's spell book)");
                case "lry": return ("World Loading", "Interfaces", "QuestListMessage (Active quest list and journal)");
                case "icb": return ("World Loading", "Character", "CharacterStatsListMessage (Combat state and base stats)");
                case "irm": return ("World Loading", "Character", "MapActorsListMessage (Initial NPC and mob spawns of the map)");
                case "hke": return ("World Loading", "Interfaces", "ServerWelcomeMessage (Welcome message and news of the day)");
                case "kfr": return ("World Loading", "Character", "EmoteListMessage (Unlocked emotes and animations)");
                case "ipv": return ("World Loading", "Map", "MapComplementaryInformationsData (Cell interactives)");
                case "ipu": return ("World Loading", "Map", "MapInteractiveElements (Active doors and triggers)");
                case "ipw": return ("World Loading", "Map", "MapStatedElements (Visual state of the interactive elements)");
                case "icw": return ("World Loading", "Inventory", "InventoryContentMessage (Full inventory - 180 items)");
                case "loy": return ("World Loading", "Sync", "WorldLoadAck (Client ack of a successful map load)");
                case "lok": return ("World Loading", "Sync", "SelectedServerData (Server session metadata)");
                case "jdj": return ("World Loading", "Sync", "ServerDateMessage (Server date synchronization)");

                // === Context: World Loading - 33 Packets Transition Burst ===
                case "kqo": return ("World Loading", "Interfaces", "ChatChannelsReadMessage (Chat channels open for reading)");
                case "hhq": return ("World Loading", "Interfaces", "SocialGroupPackets (Guild and alliance information)");
                case "hml": return ("World Loading", "Interfaces", "SocialPreferences (The player's social settings)");
                case "isf": return ("World Loading", "Sync", "QuestListMessage (Notification of the active quests)");
                case "lol": return ("World Loading", "Interfaces", "NotificationListMessage (Quest notifications)");
                case "icg": return ("World Loading", "Inventory", "InventoryWeightMessage (Inventory carry pods)");
                case "ibo": return ("World Loading", "Interfaces", "ShortcutBarContentMessage (Quick spell bar)");
                case "hmj": return ("World Loading", "Interfaces", "SocialGroupStatus (The player's guild status)");
                case "lxs": return ("World Loading", "Sync", "AlignmentSubAreaUpdate (PvP and sub-area alignment)");
                case "hnq": return ("World Loading", "Sync", "SpouseStatusMessage (Marital status / marriage)");
                case "ksv": return ("World Loading", "Character", "CharacterCapabilitiesMessage (Stat caps and capabilities)");
                case "lou": return ("World Loading", "Connection", "ServerAccessStatus (Server accessibility status)");
                case "iya": return ("World Loading", "Sync", "FeatureStatusMessage (Active experimental features)");
                case "kdx": return ("World Loading", "Connection", "AccountCapabilitiesMessage (Global account rights)");
                case "izh": return ("World Loading", "Sync", "AlmanaxDateMessage (Almanax day and active bonus)");
                case "ity": return ("World Loading", "Sync", "ExpMultiplierMessage (Global experience multipliers)");
                case "koj": return ("World Loading", "Map", "HavenBagStatusMessage (Haven bag or player house data)");
                case "kyj": return ("World Loading", "Sync", "ArenaRankInfosMessage (Kolossium league information)");
                case "ktj": return ("World Loading", "Character", "ExpGainDetails (Details of the accumulated experience pool)");
                case "ltk": return ("World Loading", "Interfaces", "TitleListMessage (Available honorific titles)");
                case "lvk": return ("World Loading", "Interfaces", "OrnamentListMessage (Available graphical ornaments)");
                case "lwb": return ("World Loading", "Character", "EmoteListMessage (Unlocked emoticons)");
                case "luy": return ("World Loading", "Inventory", "JobDescriptionMessage (List of learned jobs)");
                case "hhf": return ("World Loading", "Interfaces", "SocialGroupRights (Rights granted within the guild)");
                case "hhh": return ("World Loading", "Interfaces", "SocialGroupDetails (Descriptive sheet of the guild)");
                case "luq": return ("World Loading", "Interfaces", "JobCrafterDirectorySettings (Job directory visibility settings)");
                case "hhi": return ("World Loading", "Interfaces", "SocialGroupAlliance (Sheet of the alliance)");
                case "idf": return ("World Loading", "Inventory", "InventoryPreview (Quick preview of the items)");
                case "izu": return ("World Loading", "Sync", "QuestStepProgress (Current quest progress)");

                // === Context: World Loading - kkn Burst ===
                case "kkn": return ("World Loading", "Sync", "MapLoadCompleted (Notification that the graphics load finished)");
                case "kkp": return ("World Loading", "Interfaces", "SocialStatusMessage (Configuration of the online social status)");
                case "kkm": return ("World Loading", "Interfaces", "SocialOptionsMessage (Friend notification settings)");
                case "krb": return ("World Loading", "Character", "CharacterRemainingPoints (Confirmed remaining stat points)");
                case "ilc": return ("World Loading", "Sync", "ServerSettingsMessage (Regional settings of the server)");
                case "joh": return ("World Loading", "Map", "CurrentMapMessage (Confirms the Map ID to load into the scene)");
                case "hmd": return ("World Loading", "Inventory", "InventoryWeightMessage (Inventory carry pods)");
                case "lpj": return ("World Loading", "Sync", "SecondaryReadySignal (Signal that the secondary threads are ready)");
                case "lpe": return ("World Loading", "Sync", "SecondaryReadyConfirm (Ack that the secondary threads are ready)");
                case "hmv": return ("World Loading", "Chat", "ChatChannelsListRequest (Request for the chat channels)");
                case "hnk": return ("World Loading", "Chat", "ChatChannelsListMessage (Available chat channels)");
                case "kqm": return ("World Loading", "Chat", "ChatChannelConfigMessage (Configuration and colour of the chat channel)");
                case "ibt": return ("World Loading", "Sync", "GameReadyTrigger (Request to hand over control of the game)");
                case "ith": return ("World Loading", "Character", "FullCharacterStatsMessage (Bulk stats sheet)");

                // === Context: In Game ===
                case "kkr": return ("In Game", "Map", "MapComplementaryInfoRequest (Request for actors and interactives)");
                case "lxd": return ("In Game", "Map", "MapComplementaryInfo (Wrapper for interactives, cells and weather)");
                case "jpv": return ("In Game", "Character", "MapActorsShowMessage (Character spawns on the current map)");
                case "kns": return ("In Game", "Sync", "KnockAck / Heartbeat (Sync heartbeat / Pong)");
                case "kod": return ("In Game", "Sync", "HeartbeatRequest (Sync heartbeat / Ping)");
                case "joi": return ("In Game", "Character", "PlayerMovementRequest (Cell-by-cell movement request)");
                case "jpp": return ("In Game", "Character", "PlayerMovementConfirm (Confirms the destination cell was reached)");
                case "jos": return ("In Game", "Map", "MapChangeRequest (Request to cross the map boundary)");
                case "isi": return ("In Game", "Inventory", "ItemMovementRequest (Request to equip/move an item)");
                case "iry": return ("In Game", "Inventory", "ItemMovementConfirm (Confirmation that the item was equipped)");
                case "krc": return ("In Game", "Character", "StatsUpgradeRequest (Request to assign points to stats)");
                case "kqn": return ("In Game", "Chat", "ChatSendRequest (Sending a chat message typed by the player)");
                case "kqp": return ("In Game", "Chat", "ChatBroadcastMessage (Broadcast of a chat message on the channel)");

                default: return ("In Game", "Unknown", $"Utility message ({uri})");
            }
        }
    }
}
