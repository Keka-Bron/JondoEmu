using System;
using System.IO;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol.Messages;

namespace Jondo.Unity.Launcher.Handlers
{
    public static class CharacterSelectionHandler
    {
        /// <summary>
        /// Effect action IDs used inside irm effect entries (f11), keyed by internal stat ID.
        /// Observed in the official capture: 125=Vitalidad, 174=Iniciativa, 138=Potencia.
        /// </summary>
        private static readonly Dictionary<int, int> EffectActionIdByStatId = new Dictionary<int, int>
        {
            { 1, 111 },  // AP
            { 2, 128 },  // MP
            { 10, 118 }, // Fuerza
            { 11, 125 }, // Vitalidad
            { 12, 124 }, // Sabiduría
            { 13, 123 }, // Suerte
            { 14, 119 }, // Agilidad
            { 15, 126 }, // Inteligencia
            { 16, 112 }, // Daños
            { 18, 115 }, // Crítico
            { 25, 138 }, // Potencia
            { 44, 174 }, // Iniciativa
        };

        private static readonly Dictionary<int, int> StatIdByEffectActionId = EffectActionIdByStatId.ToDictionary(k => k.Value, v => v.Key);

        private static readonly HashSet<int> IntrepidoSetGids = new HashSet<int> { 10784, 10785, 10794, 10797, 10798, 10799, 10800, 10801 };

        /// <summary>
        /// Builds the real inventory message (irm) payload from GameState.Inventory.
        /// Schema observed in the official world-entering capture (frame #11):
        ///   irm { repeated f3: { f2: position (63 = bolsa, 0-15 = ranura equipada, omitido si 0),
        ///                        f5: { f1: cantidad,
        ///                              repeated f2: efecto { f10: valor, f11: actionId },
        ///                              f4: uid, f5: gid } } }
        /// NOTE: the former target "icw" is NOT the inventory — the official icw payload carries
        /// guild territory data (coordinates + guild emblems) and must stream through untouched.
        /// </summary>
        public static byte[] BuildDynamicIrmPayload()
        {
            var irmMsg = new ProtoMessage();

            foreach (var item in GameState.GetInventoryCopy())
            {
                var detailMsg = new ProtoMessage();

                // f1: quantity
                detailMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = item.Quantity });

                // repeated f2: stat effects { f10: value, f11: actionId }
                    foreach (var kvp in item.Effects)
                    {
                        var effectMsg = new ProtoMessage();
                        if (kvp.Value != 0)
                            effectMsg.Fields.Add(new ProtoField { FieldNumber = 10, WireType = 0, VarIntValue = kvp.Value });
                        effectMsg.Fields.Add(new ProtoField { FieldNumber = 11, WireType = 0, VarIntValue = kvp.Key });
                        detailMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = effectMsg.ToByteArray() });
                    }

                // Weapon damage roll of the starter sword, as seen in the capture: { f4: dice{f1:6,f2:5}, f11:95 }
                if (item.ItemId == 10797)
                {
                    var diceMsg = new ProtoMessage();
                    diceMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 6 });
                    diceMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 5 });
                    var dmgMsg = new ProtoMessage();
                    dmgMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = diceMsg.ToByteArray() });
                    dmgMsg.Fields.Add(new ProtoField { FieldNumber = 11, WireType = 0, VarIntValue = 95 });
                    detailMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = dmgMsg.ToByteArray() });
                }

                // Marker effect present on every intrépido set piece in the official capture (f11=981, no value)
                if (IntrepidoSetGids.Contains(item.ItemId))
                {
                    var markerMsg = new ProtoMessage();
                    markerMsg.Fields.Add(new ProtoField { FieldNumber = 11, WireType = 0, VarIntValue = 981 });
                    detailMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = markerMsg.ToByteArray() });
                }

                // f4: uid, f5: gid
                detailMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = item.Uid });
                detailMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = item.ItemId });

                var itemMsg = new ProtoMessage();
                if (item.Position != 0)
                    itemMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = item.Position });
                itemMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 2, BytesValue = detailMsg.ToByteArray() });

                irmMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = itemMsg.ToByteArray() });
            }

            // Field 4 is kamas
            irmMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = GameState.Kamas });

            return irmMsg.ToByteArray();
        }

        public static async Task HandleAuthRequest(NetworkStream stream, byte[] payload, string payloadStr)
        {
            if (payloadStr.Contains("type.ankama.com/jtk"))
            {
                Console.WriteLine("[Game Node] Received New Auth Request (jtk)");
                byte[] jtmFrame = NetworkEnvelope.ConvertHexStringToByteArray("33-0A-31-12-2F-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6A-74-6D-12-18-08-01-12-14-32-30-33-35-2D-30-31-2D-30-31-54-30-30-3A-30-30-3A-30-30-5A");
                await stream.WriteAsync(jtmFrame, 0, jtmFrame.Length);
                Console.WriteLine("[Game Node] Sent New Auth Accepted (jtm)");
            }
            else if (payloadStr.Contains("type.ankama.com/knx"))
            {
                Console.WriteLine("[Game Node] Received New Auth Request (knx) [3.6]");
                byte[] frame557 = NetworkEnvelope.ConvertHexStringToByteArray("19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6F-66-24-1A-22-0A-20-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6C-6F-72-12-09-08-78-10-DC-BC-D5-D9-05-19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-68-6E-70-19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6E-72-19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6D-66-61-19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6D-65-7A-19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-68-6E-76");
                await stream.WriteAsync(frame557, 0, frame557.Length);
                Console.WriteLine("[Game Node] Sent Auth Accepted and Handshake Packets (frame557)");

                byte[] klpFrame = NetworkEnvelope.ConvertHexStringToByteArray("1D-1A-1B-0A-19-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6C-70-12-02-10-00");
                await stream.WriteAsync(klpFrame, 0, klpFrame.Length);
                Console.WriteLine("[Game Node] Sent Character List (klp) - Empty [New Build]");
            }
            else
            {
                Console.WriteLine("[Game Node] Received Auth Request (ise)");
                byte[] iuaFrame = NetworkEnvelope.ConvertHexStringToByteArray("28-0A-26-12-24-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-69-75-61-12-0D-0A-02-14-23-10-06-18-A2-82-D8-B0-AF-1A");
                await stream.WriteAsync(iuaFrame, 0, iuaFrame.Length);
                Console.WriteLine("[Game Node] Sent Auth Accepted (iua) [Old Build]");
                
                byte[] isjFrame = NetworkEnvelope.ConvertHexStringToByteArray("19-0A-17-12-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-69-75-6A");
                await stream.WriteAsync(isjFrame, 0, isjFrame.Length);
                Console.WriteLine("[Game Node] Sent Character List (isj) - Empty [Old Build]");
            }
        }

        public static async Task HandleCharacterListRequest(NetworkStream stream, byte[] payload, string payloadStr)
        {
            if (payloadStr.Contains("type.ankama.com/kpc"))
            {
                Console.WriteLine("[Game Node] Received Ticket/Ping Request (kpc) [3.6]");
                byte[] frame558 = NetworkEnvelope.ConvertHexStringToByteArray("19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6F-73");
                await stream.WriteAsync(frame558, 0, frame558.Length);
                Console.WriteLine("[Game Node] Sent Server Selection Status (frame558) in response to kpc");
            }
            else if (payloadStr.Contains("type.ankama.com/ksx"))
            {
                Console.WriteLine("[Game Node] Received Character List Request (ksx) [3.6] - Waiting for kpa");
            }
            else if (payloadStr.Contains("type.ankama.com/kpa"))
            {
                Console.WriteLine("[Game Node] Received Character List Request (kpa) [3.6] - Sending Character List");
                
                var dbChars = DatabaseManager.GetCharactersByAccountId(188940901);
                string activeCharName = "CADERNIS";
                long activeCharId = 13825558L;
                int level = 2;
                string lookHex = "080118032218A28B9B0FCBE5F615A4E1B91992A6C820888CA028F5B7CB342A035BE410420134320220013809";
                
                if (dbChars.Count > 0)
                {
                    activeCharName = dbChars[0].Name;
                    activeCharId = dbChars[0].Id;
                    level = dbChars[0].Level;
                    lookHex = dbChars[0].LookHex;
                }

                byte[] frame562 = NetworkEnvelope.ConvertHexStringToByteArray("19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6D-65-73");
                await stream.WriteAsync(frame562, 0, frame562.Length);
                
                byte[] frame563 = NetworkEnvelope.ConvertHexStringToByteArray("1F-1A-1D-0A-1B-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6E-76-12-04-08-01-10-01");
                await stream.WriteAsync(frame563, 0, frame563.Length);
                
                byte[] frame564 = NetworkEnvelope.ConvertHexStringToByteArray("1D-1A-1B-0A-19-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6E-76-12-02-08-01");
                await stream.WriteAsync(frame564, 0, frame564.Length);
                
                byte[] frame565 = NetworkEnvelope.ConvertHexStringToByteArray("19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6E-76");
                await stream.WriteAsync(frame565, 0, frame565.Length);
                
                // Send ksq (character list containing active character name/ID)
                byte[] frame566 = BuildKsqPacket(activeCharName, activeCharId, level);
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame566);
                
                byte[] frame568 = NetworkEnvelope.ConvertHexStringToByteArray("19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6A-72-66");
                await stream.WriteAsync(frame568, 0, frame568.Length);
                
                Console.WriteLine("[Game Node] Sent Character List (ksq) and World Ready (jrf)");
            }
            else
            {
                Console.WriteLine("[Game Node] Received Character List Request (jto)");
                byte[] ldtFrame = NetworkEnvelope.ConvertHexStringToByteArray("1B-0A-19-12-17-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6C-64-74-12-00");
                await stream.WriteAsync(ldtFrame, 0, ldtFrame.Length);
                Console.WriteLine("[Game Node] Sent Character List (ldt) - Empty");
            }
        }

        public static void HandleCharacterSelectionRequest(byte[] kslPayload)
        {
            Console.WriteLine("[Game Node] Received Character Selection Request (ksl) [3.6]");
            
            long characterIdToLoad = 13825558L;
            try
            {
                if (kslPayload != null && kslPayload.Length > 0)
                {
                    var kslMsg = ProtoMessage.Parse(kslPayload);
                    var charIdField = kslMsg.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 0);
                    if (charIdField != null)
                    {
                        characterIdToLoad = charIdField.VarIntValue;
                        Program.LogDebug($"[Game Node] Selected character ID parsed from ksl: {characterIdToLoad}");
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[-] Error parsing character ID from ksl packet: {ex.Message}");
            }
            
            bool dbCharacterLoaded = DatabaseManager.LoadCharacter(characterIdToLoad);

            try
            {
                var statsMsg = new ProtoMessage();
                
                var breedSexMsg = new ProtoMessage();
                breedSexMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.Breed });
                breedSexMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = GameState.Sex });
                statsMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = breedSexMsg.ToByteArray() });
                
                statsMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.CharacterLevel });
                
                statsMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 188940901L });

                var alignMsg = new ProtoMessage();
                alignMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = 1 });
                statsMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 2, BytesValue = alignMsg.ToByteArray() });

                statsMsg.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = 1 });

                var lgkMsg = new ProtoMessage();
                lgkMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = statsMsg.ToByteArray() });
                lgkMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = System.Text.Encoding.UTF8.GetBytes(GameState.CharacterName) });

                var humanoidInfo = new ProtoMessage();
                humanoidInfo.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = lgkMsg.ToByteArray() });

                var detailsMsg = new ProtoMessage();
                if (GameState.LookBytes != null && GameState.LookBytes.Length > 0)
                {
                    detailsMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = GameState.LookBytes });
                }
                detailsMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = humanoidInfo.ToByteArray() });

                GameState.PlayerActorDetails = detailsMsg.ToByteArray();
                Program.LogDebug($"[Game Node] Dynamically built GameState.PlayerActorDetails from DB (name: {GameState.CharacterName}, breed: {GameState.Breed}, level: {GameState.CharacterLevel}, length: {GameState.PlayerActorDetails.Length} bytes).");
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[-] Error dynamically building player actor details: {ex.Message}");
            }
            
            if (dbCharacterLoaded)
            {
                Program.LogDebug($"[Stats Init] Stats loaded from database for {GameState.CharacterName} (capital: {GameState.CharacterRemainingPoints}).");
            }
            else
            {
                Program.LogDebug($"[Stats Init] Character {characterIdToLoad} not found in database — stats keep in-memory defaults.");
            }

            var dbInventory = DatabaseManager.LoadInventory(characterIdToLoad);
            if (dbInventory.Count == 0)
            {
                Program.LogDebug("[Inventory Seed] Database inventory is empty! Seeding starting inventory...");
                var defaultItemsGids = new[] { 10784, 10785, 10794, 10799, 10800, 10801, 10798, 10797, 19622, 23, 43, 44, 45, 52, 69, 73, 74 };
                var defaultItems = new List<PlayerItem>();
                long baseUid = 10699035;
                
                var rnd = new Random();
                foreach (var gid in defaultItemsGids)
                {
                    var item = new PlayerItem { Uid = baseUid++, ItemId = gid, Quantity = 1, Position = 63, Effects = new Dictionary<int, int>() };
                    var rids = DatabaseManager.GetItemTemplatePossibleEffects(gid);
                    var effectsData = DatabaseManager.GetItemEffectsData(rids);
                    
                    foreach (var effect in effectsData)
                    {
                        int statId = StatIdByEffectActionId.TryGetValue(effect.EffectId, out int mapped) ? mapped : effect.EffectId;
                        int val = effect.DiceSide > effect.DiceNum ? rnd.Next(effect.DiceNum, effect.DiceSide + 1) : effect.DiceNum;
                        item.Effects[statId] = val;
                    }
                    defaultItems.Add(item);
                }

                GameState.SetInventory(defaultItems);
                DatabaseManager.SeedInventory(characterIdToLoad, defaultItems);
            }
            else
            {
                Program.LogDebug($"[Inventory Load] Loaded {dbInventory.Count} items from database. Setting as active inventory.");
                GameState.SetInventory(dbInventory);
            }

            GameState.ClearEquippedItems();
            foreach (var item in GameState.GetInventoryCopy())
            {
                if (item.Position >= 0 && item.Position < 63)
                {
                    var equipped = new EquippedItemInfo { Slot = item.Position };
                    foreach (var kvp in item.Effects)
                    {
                        equipped.Stats[kvp.Key] = kvp.Value;
                    }
                    GameState.SetEquippedItem(item.Uid, equipped);
                }
            }
        }

        private static byte[] BuildKsqPacket(string characterName, long characterId, int level)
        {
            // 1. Build character details (lgz.lgy.lgx)
            using var detailsMs = new MemoryStream();
            {
                var output = new CodedOutputStream(detailsMs);
                byte[] lookBytes = NetworkEnvelope.ConvertHexStringToByteArray("12-26-08-01-18-03-22-18-A2-8B-9B-0F-CB-E5-F6-15-A4-E1-B9-19-92-A6-C8-20-88-8C-A0-28-F5-B7-CB-34-2A-03-5B-E4-10-42-01-34-32-02-20-01-38-09");
                
                if (GameState.PlayerActorDetails != null)
                {
                    try
                    {
                        var detailsMsg = ProtoMessage.Parse(GameState.PlayerActorDetails);
                        var gbfoField = detailsMsg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
                        if (gbfoField != null)
                        {
                            var gbfoMsg = ProtoMessage.Parse(gbfoField.BytesValue);
                            var gbewField = gbfoMsg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
                            if (gbewField != null)
                            {
                                byte[] lookRawBytes = gbewField.BytesValue;
                                using var wrapMs = new MemoryStream();
                                var wrapOut = new CodedOutputStream(wrapMs);
                                wrapOut.WriteTag((uint)((2 << 3) | 2));
                                wrapOut.WriteBytes(ByteString.CopyFrom(lookRawBytes));
                                wrapOut.Flush();
                                lookBytes = wrapMs.ToArray();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.LogDebug($"[BuildKsqPacket] Error extracting look schema-freely: {ex.Message}");
                    }
                }
                
                output.WriteTag((uint)((2 << 3) | 2));
                output.WriteBytes(ByteString.CopyFrom(lookBytes));

                // Tag 3 (wire type 2): Name
                output.WriteTag((uint)((3 << 3) | 2));
                output.WriteString(characterName);

                // Tag 6 (wire type 0): Level
                output.WriteTag((uint)((6 << 3) | 0));
                output.WriteInt32(level);

                output.Flush();
            }
            byte[] detailsBytes = detailsMs.ToArray();

            // 2. Build character (lgz)
            using var characterMs = new MemoryStream();
            {
                var output = new CodedOutputStream(characterMs);
                // Tag 1 (wire type 2): details
                output.WriteTag((uint)((1 << 3) | 2));
                output.WriteBytes(ByteString.CopyFrom(detailsBytes));

                // Tag 2 (wire type 0): character ID
                output.WriteTag((uint)((2 << 3) | 0));
                output.WriteInt64(characterId);

                output.Flush();
            }
            byte[] characterBytes = characterMs.ToArray();

            // 3. Build ksq
            using var ksqMs = new MemoryStream();
            {
                var output = new CodedOutputStream(ksqMs);
                // Tag 1 (wire type 2): repeated character
                output.WriteTag((uint)((1 << 3) | 2));
                output.WriteBytes(ByteString.CopyFrom(characterBytes));
                output.Flush();
            }
            byte[] ksqBytes = ksqMs.ToArray();

            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/ksq", ksqBytes);
        }
    }
}
