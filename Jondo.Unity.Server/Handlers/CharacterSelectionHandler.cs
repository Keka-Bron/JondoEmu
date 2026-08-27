using System;
using System.IO;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol.Messages;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    public static class CharacterSelectionHandler
    {
        /// <summary>
        /// Effect action IDs used inside irm effect entries (f11), keyed by internal stat ID.
        /// Observed in the official capture: 125=Vitality, 174=Initiative, 138=Power.
        /// </summary>
        private static readonly Dictionary<int, int> EffectActionIdByStatId = new Dictionary<int, int>
        {
            { 1, 111 },  // AP
            { 2, 128 },  // MP
            { 10, 118 }, // Strength
            { 11, 125 }, // Vitality
            { 12, 124 }, // Wisdom
            { 13, 123 }, // Chance
            { 14, 119 }, // Agility
            { 15, 126 }, // Intelligence
            { 16, 112 }, // Damage
            { 18, 115 }, // Critical
            { 25, 138 }, // Power
            { 44, 174 }, // Initiative
        };

        private static readonly Dictionary<int, int> StatIdByEffectActionId = EffectActionIdByStatId.ToDictionary(k => k.Value, v => v.Key);

        private static readonly HashSet<int> IntrepidSetGids = new HashSet<int> { 10784, 10785, 10794, 10797, 10798, 10799, 10800, 10801 };

        /// <summary>
        /// Builds the real inventory message (irm) payload from Jondo.Unity.Server.Network.SessionContext.State.Inventory.
        /// Schema observed in the official world-entering capture (frame #11):
        ///   irm { repeated f3: { f2: position (63 = bag, 0-15 = equipped slot, omitted when 0),
        ///                        f5: { f1: quantity,
        ///                              repeated f2: effect { f10: value, f11: actionId },
        ///                              f4: uid, f5: gid } } }
        /// NOTE: the former target "icw" is NOT the inventory — the official icw payload carries
        /// guild territory data (coordinates + guild emblems) and must stream through untouched.
        /// </summary>
        public static byte[] BuildDynamicIrmPayload()
        {
            var irmMsg = new ProtoMessage();

            foreach (var item in Jondo.Unity.Server.Network.SessionContext.State.GetInventoryCopy())
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

                // Marker effect present on every Intrepid set piece in the official capture (f11=981, no value)
                if (IntrepidSetGids.Contains(item.ItemId))
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
            irmMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = Jondo.Unity.Server.Network.SessionContext.State.Kamas });

            return irmMsg.ToByteArray();
        }

        public static async Task HandleAuthRequest(NetworkStream stream, byte[] payload, string payloadStr)
        {
            if (payloadStr.Contains("type.ankama.com/jtk"))
            {
                Console.WriteLine("[Game Node] Received New Auth Request (jtk)");
                byte[] jtmFrame = NetworkEnvelope.ConvertHexStringToByteArray("33-0A-31-12-2F-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6A-74-6D-12-18-08-01-12-14-32-30-33-35-2D-30-31-2D-30-31-54-30-30-3A-30-30-3A-30-30-5A");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, jtmFrame);
                Console.WriteLine("[Game Node] Sent New Auth Accepted (jtm)");
            }
            else if (payloadStr.Contains("type.ankama.com/knx"))
            {
                Console.WriteLine("[Game Node] Received New Auth Request (knx) [3.6]");
                byte[] frame557 = NetworkEnvelope.ConvertHexStringToByteArray("19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6F-66-24-1A-22-0A-20-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6C-6F-72-12-09-08-78-10-DC-BC-D5-D9-05-19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-68-6E-70-19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6E-72-19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6D-66-61-19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6D-65-7A-19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-68-6E-76");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, frame557);
                Console.WriteLine("[Game Node] Sent Auth Accepted and Handshake Packets (frame557)");

                byte[] klpFrame = NetworkEnvelope.ConvertHexStringToByteArray("1D-1A-1B-0A-19-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6C-70-12-02-10-00");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, klpFrame);
                Console.WriteLine("[Game Node] Sent Character List (klp) - Empty [New Build]");
            }
            else
            {
                Console.WriteLine("[Game Node] Received Auth Request (ise)");
                byte[] iuaFrame = NetworkEnvelope.ConvertHexStringToByteArray("28-0A-26-12-24-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-69-75-61-12-0D-0A-02-14-23-10-06-18-A2-82-D8-B0-AF-1A");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, iuaFrame);
                Console.WriteLine("[Game Node] Sent Auth Accepted (iua) [Old Build]");
                
                byte[] isjFrame = NetworkEnvelope.ConvertHexStringToByteArray("19-0A-17-12-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-69-75-6A");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, isjFrame);
                Console.WriteLine("[Game Node] Sent Character List (isj) - Empty [Old Build]");
            }
        }

        /// <summary>
        /// Character list requests from older client builds.
        ///
        /// In 3.6.10.10 the list is no longer requested this way: it arrives inside the welcome
        /// burst as soon as the client presents its ticket. This is kept for the old opcodes.
        /// </summary>
        public static async Task HandleCharacterListRequest(NetworkStream stream, byte[] payload, string payloadStr, long accountId, int serverId)
        {
            if (payloadStr.Contains("type.ankama.com/kpc"))
            {
                Console.WriteLine("[Game Node] Received Ticket/Ping Request (kpc) [3.6]");
                byte[] frame558 = NetworkEnvelope.ConvertHexStringToByteArray("19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6F-73");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, frame558);
                Console.WriteLine("[Game Node] Sent Server Selection Status (frame558) in response to kpc");
            }
            else if (payloadStr.Contains(Op.Uri(Op.Ksx)))
            {
                Console.WriteLine("[Game Node] Received Character List Request (ksx) [3.6] - Waiting for kpa");
            }
            else if (payloadStr.Contains(Op.Uri(Op.Kpa)))
            {
                Console.WriteLine("[Game Node] Received Character List Request (kpa) [3.6] - Sending Character List");
                
                var dbChars = DatabaseManager.GetCharactersByAccountId(accountId, serverId);
                string activeCharName = "";
                long activeCharId = 0;
                int level = 1;
                string lookHex = "";

                if (dbChars.Count > 0)
                {
                    activeCharName = dbChars[0].Name;
                    activeCharId = dbChars[0].Id;
                    level = dbChars[0].Level;
                    lookHex = dbChars[0].LookHex;
                }

                byte[] frame562 = NetworkEnvelope.ConvertHexStringToByteArray("19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6D-65-73");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, frame562);
                
                byte[] frame563 = NetworkEnvelope.ConvertHexStringToByteArray("1F-1A-1D-0A-1B-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6E-76-12-04-08-01-10-01");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, frame563);
                
                byte[] frame564 = NetworkEnvelope.ConvertHexStringToByteArray("1D-1A-1B-0A-19-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6E-76-12-02-08-01");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, frame564);
                
                byte[] frame565 = NetworkEnvelope.ConvertHexStringToByteArray("19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6E-76");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, frame565);
                
                // Send ksq (character list containing active character name/ID)
                byte[] frame566 = BuildKsqPacket(activeCharName, activeCharId, level);
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame566);
                
                byte[] frame568 = NetworkEnvelope.ConvertHexStringToByteArray("19-1A-17-0A-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6A-72-66");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, frame568);
                
                Console.WriteLine("[Game Node] Sent Character List (ksq) and World Ready (jrf)");
            }
            else
            {
                Console.WriteLine("[Game Node] Received Character List Request (jto)");
                byte[] ldtFrame = NetworkEnvelope.ConvertHexStringToByteArray("1B-0A-19-12-17-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6C-64-74-12-00");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, ldtFrame);
                Console.WriteLine("[Game Node] Sent Character List (ldt) - Empty");
            }
        }

        /// <summary>
        /// Character selection. The id comes in field 1 of the message, inside the wrapper
        /// (kvw in 3.6.10.10, ksl in older builds). The kvl sent immediately after character
        /// creation is different: field 1 is the success boolean and field 2 is the character id.
        ///
        /// Returns false if the character does not exist or does not belong to the session's
        /// account. There used to be a default id: when the message carried none, the same
        /// character was always loaded, so any account ended up playing with it.
        /// </summary>
        public static bool HandleCharacterSelectionRequest(byte[] framePayload, long accountId)
        {
            long characterIdToLoad = 0;
            try
            {
                characterIdToLoad = ReadSelectedCharacterId(framePayload);
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[-] Error reading the character id: {ex.Message}");
            }

            if (characterIdToLoad <= 0)
            {
                Console.WriteLine("[Game Node] The character selection carried no character id.");
                return false;
            }

            // Sin cuenta no se selecciona nada. Esto estaba escrito como «accountId > 0 && ...»,
            // o sea que la comprobación se apagaba sola justo en el caso que tenía que cazar: un
            // socket que manda el kvw ANTES del kqz llega aquí con cuenta cero, se salta la
            // comprobación entera y carga la ficha de quien quiera. Y como después se escribe
            // encima al guardar, no era sólo mirar.
            //
            // La cuenta se resuelve al canjear el ticket en kqz, y si el ticket no vale la sesión
            // se cierra ahí mismo, así que en el camino bueno esto nunca es cero.
            if (accountId <= 0)
            {
                Console.WriteLine($"[Game Node] Character selection without a resolved account " +
                                  $"(character {characterIdToLoad}). The ticket has not been presented.");
                return false;
            }

            if (!DatabaseManager.CharacterBelongsToAccount(characterIdToLoad, accountId))
            {
                Console.WriteLine($"[Game Node] Character {characterIdToLoad} does not belong to " +
                                  $"account {accountId}.");
                return false;
            }

            Console.WriteLine($"[Game Node] Selected character {characterIdToLoad}.");
            bool dbCharacterLoaded = DatabaseManager.LoadCharacter(characterIdToLoad);
            if (!dbCharacterLoaded)
            {
                Console.WriteLine($"[Game Node] Could not load character {characterIdToLoad}.");
                return false;
            }
            // Primero se lee la visita anterior y sólo después se pisa: al revés, lo que se le
            // enseñaría al jugador es la conexión de ahora mismo, que no le dice nada.
            SessionContext.State.PreviousVisit = DatabaseManager.ReadLastVisit(characterIdToLoad);
            DatabaseManager.TouchLastConnection(characterIdToLoad, SessionContext.State.ClientIp);

            try
            {
                var statsMsg = new ProtoMessage();
                
                var breedSexMsg = new ProtoMessage();
                breedSexMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = Jondo.Unity.Server.Network.SessionContext.State.Breed });
                breedSexMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = Jondo.Unity.Server.Network.SessionContext.State.Sex });
                statsMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = breedSexMsg.ToByteArray() });
                
                statsMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel });
                
                statsMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = accountId });

                var alignMsg = new ProtoMessage();
                alignMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = 1 });
                statsMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 2, BytesValue = alignMsg.ToByteArray() });

                statsMsg.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = 1 });

                var lgkMsg = new ProtoMessage();
                lgkMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = statsMsg.ToByteArray() });
                lgkMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = System.Text.Encoding.UTF8.GetBytes(Jondo.Unity.Server.Network.SessionContext.State.CharacterName) });

                var humanoidInfo = new ProtoMessage();
                humanoidInfo.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = lgkMsg.ToByteArray() });

                var detailsMsg = new ProtoMessage();
                if (Jondo.Unity.Server.Network.SessionContext.State.LookBytes != null && Jondo.Unity.Server.Network.SessionContext.State.LookBytes.Length > 0)
                {
                    detailsMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = Jondo.Unity.Server.Network.SessionContext.State.LookBytes });
                }
                detailsMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = humanoidInfo.ToByteArray() });

                Jondo.Unity.Server.Network.SessionContext.State.PlayerActorDetails = detailsMsg.ToByteArray();
                Program.LogDebug($"[Game Node] Dynamically built Jondo.Unity.Server.Network.SessionContext.State.PlayerActorDetails from DB (name: {Jondo.Unity.Server.Network.SessionContext.State.CharacterName}, breed: {Jondo.Unity.Server.Network.SessionContext.State.Breed}, level: {Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel}, length: {Jondo.Unity.Server.Network.SessionContext.State.PlayerActorDetails.Length} bytes).");
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[-] Error dynamically building player actor details: {ex.Message}");
            }
            
            if (dbCharacterLoaded)
            {
                Program.LogDebug($"[Stats Init] Stats loaded from database for {Jondo.Unity.Server.Network.SessionContext.State.CharacterName} (capital: {Jondo.Unity.Server.Network.SessionContext.State.CharacterRemainingPoints}).");
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

                Jondo.Unity.Server.Network.SessionContext.State.SetInventory(defaultItems);
                DatabaseManager.SeedInventory(characterIdToLoad, defaultItems);
            }
            else
            {
                Program.LogDebug($"[Inventory Load] Loaded {dbInventory.Count} items from database. Setting as active inventory.");
                Jondo.Unity.Server.Network.SessionContext.State.SetInventory(dbInventory);
            }

            Jondo.Unity.Server.Network.SessionContext.State.ClearEquippedItems();
            foreach (var item in Jondo.Unity.Server.Network.SessionContext.State.GetInventoryCopy())
            {
                if (item.Position >= 0 && item.Position < 63)
                {
                    var equipped = new EquippedItemInfo { Slot = item.Position };
                    foreach (var kvp in item.Effects)
                    {
                        equipped.Stats[kvp.Key] = kvp.Value;
                    }
                    Jondo.Unity.Server.Network.SessionContext.State.SetEquippedItem(item.Uid, equipped);
                }
            }

            return true;
        }

        /// <summary>Reads the two selection layouts without mistaking kvl's success flag for id 1.</summary>
        internal static long ReadSelectedCharacterId(byte[] framePayload)
        {
            byte[]? selection = ConnectionProtocol.ReadPayload(framePayload, Op.Kvw)
                                ?? ConnectionProtocol.ReadPayload(framePayload, Op.Ksl);
            int idField = 1;

            if (selection == null)
            {
                selection = ConnectionProtocol.ReadPayload(framePayload, Op.Kvl);
                idField = 2;
            }

            if (selection == null || selection.Length == 0) return 0;
            var message = ProtoMessage.Parse(selection);
            return message.Fields.FirstOrDefault(
                field => field.FieldNumber == idField && field.WireType == 0)?.VarIntValue ?? 0;
        }

        private static byte[] BuildKsqPacket(string characterName, long characterId, int level)
        {
            // 1. Build character details (lgz.lgy.lgx)
            using var detailsMs = new MemoryStream();
            {
                var output = new CodedOutputStream(detailsMs);
                byte[] lookBytes = NetworkEnvelope.ConvertHexStringToByteArray("12-26-08-01-18-03-22-18-A2-8B-9B-0F-CB-E5-F6-15-A4-E1-B9-19-92-A6-C8-20-88-8C-A0-28-F5-B7-CB-34-2A-03-5B-E4-10-42-01-34-32-02-20-01-38-09");
                
                if (Jondo.Unity.Server.Network.SessionContext.State.PlayerActorDetails != null)
                {
                    try
                    {
                        var detailsMsg = ProtoMessage.Parse(Jondo.Unity.Server.Network.SessionContext.State.PlayerActorDetails);
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

        // The character list (kvi) is now built by ConnectionProtocol.BuildCharactersList.
        // The version that used to live here could not work: it put the character id where the
        // level goes, the level inside the look block and the account id where the character id
        // goes, on top of wrapping the message in the wrong root field.
    }
}
