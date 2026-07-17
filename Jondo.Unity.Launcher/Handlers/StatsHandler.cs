using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Handles all character stat/characteristic logic:
    /// - Stat upgrade requests (krc) from the client
    /// - Building the updated stats packet (kri)
    /// - Computing equipment and set bonuses
    /// </summary>
    public static class StatsHandler
    {
        // Capital cost per allocated point. Wisdom costs 3, every other primary costs 1.
        private const int WisdomCost = 3;

        public static async Task HandleStatsUpgradeRequest(NetworkStream stream, byte[] payload)
        {
            Console.WriteLine("[Game Node] Received Stats Upgrade Request (krc)");
            byte[]? inner = NetworkEnvelope.ExtractMessagePayload(payload, "type.ankama.com/krc");

            // The client sends the FULL desired allocation, not a delta: each field is the
            // absolute number of points the player wants placed in that characteristic
            // (a field omitted from the message means 0 points). We therefore SET each stat
            // to the requested value and recompute the capital from the total pool — this is
            // self-correcting (re-sending the same distribution is a no-op) and lets the reset
            // button work (all-zero request restores every point).
            // The definitive empirical mapping (Dofus 3.6+):
            //   1=Agilidad(14) 2=Fuerza(10) 3=Inteligencia(15) 4=Vitalidad(11) 5=Sabiduria(12) 6=Suerte(13)
            int wantAgility = 0, wantChance = 0, wantIntelligence = 0, wantStrength = 0, wantVitality = 0, wantWisdom = 0;

            if (inner != null)
            {
                try
                {
                    var krcMsg = ProtoMessage.Parse(inner);
                    Console.WriteLine($"[KRC-RAW] inner bytes ({inner.Length}): {BitConverter.ToString(inner)}");
                    foreach (var field in krcMsg.Fields)
                    {
                        if (field.WireType != 0) continue;
                        int val = (int)field.VarIntValue;
                        switch (field.FieldNumber)
                        {
                            case 1: wantAgility      = val; break;
                            case 2: wantStrength     = val; break;
                            case 3: wantIntelligence = val; break;
                            case 4: wantVitality     = val; break;
                            case 5: wantWisdom       = val; break;
                            case 6: wantChance       = val; break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[-] Error parsing krc: {ex.Message}");
                }
            }

            // Total capital pool = points still available + points already spent on the current allocation.
            // (Breed base for primaries is 0 at these levels, so the stored stat value equals the allocation.)
            int alreadySpent = GameState.StatStrength + GameState.StatIntelligence + GameState.StatChance
                             + GameState.StatAgility + GameState.StatVitality + GameState.StatWisdom * WisdomCost;
            int capitalPool  = GameState.CharacterRemainingPoints + alreadySpent;

            int requestedCost = wantStrength + wantIntelligence + wantChance + wantAgility + wantVitality + wantWisdom;

            Console.WriteLine($"[Stats] Requested — Str:{wantStrength} Int:{wantIntelligence} Cha:{wantChance} Agi:{wantAgility} Vit:{wantVitality} Wis:{wantWisdom / WisdomCost} (cost {requestedCost} / pool {capitalPool})");

            if (requestedCost < 0 || requestedCost > capitalPool)
            {
                // Not enough capital — reject the change and re-send the current authoritative state
                // so the client reverts its optimistic UI to what the server actually holds.
                Console.WriteLine($"[Stats] Rejected: requested cost {requestedCost} exceeds capital pool {capitalPool}. Re-syncing client.");
                byte[]? syncKri = BuildUpdatedKriPacket();
                if (syncKri != null)
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, syncKri);
                return;
            }

            GameState.StatStrength     = wantStrength;
            GameState.StatIntelligence = wantIntelligence;
            GameState.StatChance       = wantChance;
            GameState.StatAgility      = wantAgility;
            GameState.StatVitality     = wantVitality;
            GameState.StatWisdom       = wantWisdom / WisdomCost;
            GameState.CharacterRemainingPoints = capitalPool - requestedCost;

            Console.WriteLine($"[Stats] New — Vit:{GameState.StatVitality} Wis:{GameState.StatWisdom} Str:{GameState.StatStrength} Int:{GameState.StatIntelligence} Cha:{GameState.StatChance} Agi:{GameState.StatAgility}");
            Console.WriteLine($"[Stats] Remaining capital: {GameState.CharacterRemainingPoints}");

            DatabaseManager.SaveCurrentCharacter();
            Console.WriteLine("[Stats] Saved updated stats to database.");

            // 1. Send updated pods weight (isf) — max pods depend on strength
            byte[] isfPacket = BuildIsfPacket();
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, isfPacket);
            Console.WriteLine("[Stats] Sent isf (pods weight).");

            // 2. Send the available-capital notification (krb). The official server sends this
            // whenever the stats-points pool changes; the client's characteristics panel binds
            // its "puntos restantes" counter to it and refreshes the open panel on receipt. Without
            // it, the panel only updated when closed and reopened.
            byte[] krbPacket = BuildKrbPacket(GameState.CharacterRemainingPoints);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, krbPacket);
            Console.WriteLine($"[Stats] Sent krb (available capital = {GameState.CharacterRemainingPoints}).");

            // 3. Send the StatsUpgradeResultMessage (krd) to confirm the operation and trigger UI refresh
            byte[] krdPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/krd", Array.Empty<byte>());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, krdPacket);
            Console.WriteLine("[Stats] Sent krd (StatsUpgradeResult).");

            // 4. Send updated kri (CharacterStatsListMessage) to visually refresh the characteristics panel
            byte[]? updatedKri = BuildUpdatedKriPacket();
            if (updatedKri != null)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, updatedKri);
                Console.WriteLine("[Stats] Sent kri (character stats list).");
            }
        }

        /// <summary>
        /// Builds the krb (available stats-points / capital notification) packet.
        /// Observed in the official login stream as { f1: capitalPoints }.
        /// </summary>
        public static byte[] BuildKrbPacket(int availableCapital)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 0));
            output.WriteInt32(availableCapital);
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/krb", ms.ToArray());
        }

        /// <summary>
        /// Builds the isf (InventoryWeightMessage) packet from the database: current weight
        /// is the sum of each item's realWeight (ItemTemplates) times its quantity, and max
        /// pods follow the Dofus rule of 1000 base + 5 per strength point.
        /// Also used by InventoryHandler after equipment changes.
        /// </summary>
        public static byte[] BuildIsfPacket()
        {
            int currentWeight = 0;
            foreach (var item in GameState.GetInventoryCopy())
                currentWeight += DatabaseManager.GetItemRealWeight(item.ItemId) * item.Quantity;

            int maxWeight = 1000 + GameState.StatStrength * 5;

            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            // Field 1: Current weight
            output.WriteTag((uint)((1 << 3) | 0));
            output.WriteInt32(currentWeight);

            // Field 2: Max weight
            output.WriteTag((uint)((2 << 3) | 0));
            output.WriteInt32(maxWeight);

            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/isf", ms.ToArray());
        }

        /// <summary>Stats whose values are computed from the database/GameState;
        /// every other entry uses the official defaults below.</summary>
        private static readonly HashSet<int> DynamicStatIds = new HashSet<int> { 10, 11, 12, 13, 14, 15, 17, 18, 44 };

        /// <summary>
        /// Default stat entries reproduced verbatim from the official level-2 kri:
        /// (statId, subField, innerField, value). subField 3 = valor base, 4 = valor
        /// innato (PA/PM), 2 = límites/contextual. Un value 0 emite el submensaje vacío,
        /// igual que hace el servidor oficial.
        /// </summary>
        private static readonly (int StatId, int SubField, int InnerField, long Value)[] DefaultKriEntries = new (int, int, int, long)[]
        {
            (3, 3, 2, 5),
            (5, 3, 2, 0), (16, 3, 2, 0), (17, 3, 2, 0), (19, 3, 2, 0), (20, 3, 2, 0),
            (21, 3, 2, 0), (22, 3, 2, 0),
            (24, 3, 2, 0), (26, 3, 2, 0), (27, 3, 2, 0), (28, 3, 2, 0),
            (29, 2, 2, 0),
            (30, 3, 2, 0), (31, 3, 2, 0), (32, 3, 2, 0), (33, 3, 2, 0), (34, 3, 2, 0),
            (35, 3, 2, 0), (36, 3, 2, 0), (37, 3, 2, 0), (39, 3, 2, 0),
            (40, 3, 2, 5),
            (41, 3, 2, 0), (42, 3, 2, 0), (43, 3, 2, 0), (45, 3, 2, 0), (46, 3, 2, 0),
            (47, 2, 2, 10000),
            (48, 3, 2, 100),
            (49, 3, 2, 0), (50, 3, 2, 0), (51, 3, 2, 0), (52, 3, 2, 0), (53, 3, 2, 0),
            (54, 3, 2, 0), (55, 3, 2, 0), (56, 3, 2, 0), (57, 3, 2, 0), (58, 3, 2, 0),
            (69, 3, 2, 0), (70, 3, 2, 0), (71, 3, 2, 0), (72, 3, 2, 0), (74, 3, 2, 0),
            (75, 3, 2, 10),
            (76, 3, 2, 0), (77, 3, 2, 0), (78, 3, 2, 0), (79, 3, 2, 0), (80, 3, 2, 0),
            (81, 3, 2, 0), (82, 3, 2, 0), (83, 3, 2, 0), (84, 3, 2, 0), (85, 3, 2, 0),
            (86, 3, 2, 0), (87, 3, 2, 0), (88, 3, 2, 0), (89, 3, 2, 0), (90, 3, 2, 0),
            (91, 3, 2, 0), (92, 3, 2, 0), (93, 3, 2, 0), (94, 3, 2, 0), (95, 3, 2, 0),
            (96, 2, 2, 0),
            (97, 3, 2, -60),
            (98, 3, 2, 0), (99, 3, 2, 0), (100, 3, 2, 0), (101, 3, 2, 0), (102, 3, 2, 0),
            (103, 3, 2, 0), (104, 3, 2, 0), (105, 3, 2, 0), (106, 3, 2, 0),
            (107, 3, 2, 100),
            (108, 3, 2, 0), (109, 3, 2, 0), (110, 3, 2, 0),
            (120, 3, 2, 100), (121, 3, 2, 100), (122, 3, 2, 100), (123, 3, 2, 100),
            (124, 3, 2, 100), (125, 3, 2, 100),
            (126, 3, 2, 0), (127, 3, 2, 0), (128, 3, 2, 0), (129, 3, 2, 0), (130, 3, 2, 0),
            (131, 3, 2, 0), (132, 3, 2, 0), (133, 3, 2, 0), (134, 3, 2, 0), (135, 3, 2, 0),
            (136, 3, 2, 0), (137, 3, 2, 0), (138, 3, 2, 0), (139, 3, 2, 0), (140, 3, 2, 0),
            (141, 3, 2, 100), (142, 3, 2, 100), (143, 3, 2, 100), (150, 3, 2, 100),
            (158, 3, 2, 0), (200, 3, 2, 0),
        };

        // XP thresholds of the official Dofus curve for the only supported level for now (2).
        private const long XpLevelFloor = 110;
        private const long XpNextLevel  = 650;

        /// <summary>
        /// Builds the complete kri (CharacterStatsListMessage) frame from scratch:
        /// primary stats and capital come from GameState (loaded from SQLite), equipment
        /// bonuses from the equipped-items cache, and the remaining entries reproduce
        /// the official defaults. No captured payload is reused.
        /// Also used by InventoryHandler and GameNodeProxy after equipment changes/login.
        /// </summary>
        public static byte[]? BuildUpdatedKriPacket()
        {
            try
            {
                var larMsg = new ProtoMessage();

                // Experience block (lar.f4): level and XP window of the current level.
                var xpDetail = new ProtoMessage();
                xpDetail.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = 500 });
                var levelMsg = new ProtoMessage();
                levelMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.CharacterLevel });
                levelMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = GameState.CharacterLevel });
                levelMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 2, BytesValue = xpDetail.ToByteArray() });
                larMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = levelMsg.ToByteArray() });

                larMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = GameState.CharacterRemainingPoints });
                larMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = XpLevelFloor });  // XP inicio del nivel
                larMsg.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = GameState.CharacterRemainingPoints });
                larMsg.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 0, VarIntValue = XpNextLevel });   // XP del siguiente nivel
                larMsg.Fields.Add(new ProtoField { FieldNumber = 11, WireType = 0, VarIntValue = XpLevelFloor }); // XP actual (sin tracking de XP)

                foreach (var e in DefaultKriEntries)
                {
                    if (DynamicStatIds.Contains(e.StatId)) continue;
                    larMsg.Fields.Add(CreateRawStatEntry(e.StatId, e.SubField, e.InnerField, e.Value));
                }

                // Dynamic stats from database-backed GameState + equipment bonuses
                int baseHp = 50 + (GameState.CharacterLevel * 5);
                larMsg.Fields.Add(CreateStatField(0, baseHp,                      GetEquipBonus(0)));  // Base HP
                larMsg.Fields.Add(CreateInnateStatField(1, 6,                     GetEquipBonus(1)));  // AP
                larMsg.Fields.Add(CreateInnateStatField(23, 3,                    GetEquipBonus(23))); // MP
                larMsg.Fields.Add(CreateStatField(11, GameState.StatVitality,     GetEquipBonus(11))); // Vitalidad
                larMsg.Fields.Add(CreateStatField(12, GameState.StatWisdom,       GetEquipBonus(12))); // Sabiduría
                larMsg.Fields.Add(CreateStatField(10, GameState.StatStrength,     GetEquipBonus(10))); // Fuerza
                larMsg.Fields.Add(CreateStatField(15, GameState.StatIntelligence, GetEquipBonus(15))); // Inteligencia
                larMsg.Fields.Add(CreateStatField(13, GameState.StatChance,       GetEquipBonus(13))); // Suerte
                larMsg.Fields.Add(CreateStatField(14, GameState.StatAgility,      GetEquipBonus(14))); // Agilidad
                larMsg.Fields.Add(CreateStatField(17, 0,                          GetEquipBonus(17))); // Potencia
                larMsg.Fields.Add(CreateStatField(18, 0,                          GetEquipBonus(18))); // Crítico

                // Initiative base = only elemental stats (Str+Int+Cha+Agi). Vitality and Wisdom do NOT count.
                int baseInitiative = GameState.StatStrength + GameState.StatIntelligence + GameState.StatChance + GameState.StatAgility;
                larMsg.Fields.Add(CreateStatField(44, baseInitiative, GetEquipBonus(44)));             // Iniciativa

                var kriMsg = new ProtoMessage();
                kriMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = larMsg.ToByteArray() });

                return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/kri", kriMsg.ToByteArray());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error building updated kri: {ex.Message}");
                return null;
            }
        }

        /// <summary>Serializes one default stat entry: { [f5: statId], subField: { [innerField: value] } }.</summary>
        private static ProtoField CreateRawStatEntry(int statId, int subFieldNumber, int innerFieldNumber, long value)
        {
            var sub = new ProtoMessage();
            if (value != 0)
                sub.Fields.Add(new ProtoField { FieldNumber = innerFieldNumber, WireType = 0, VarIntValue = value });

            var entry = new ProtoMessage();
            if (statId != 0)
                entry.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = statId });
            entry.Fields.Add(new ProtoField { FieldNumber = subFieldNumber, WireType = 2, BytesValue = sub.ToByteArray() });

            return new ProtoField { FieldNumber = 10, WireType = 2, BytesValue = entry.ToByteArray() };
        }

        // ─── Stat helpers ───────────────────────────────────────────────────────────

        /// <summary>Serializes a single stat entry (las wrapper) as a ProtoField for the kri message.</summary>
        public static ProtoField CreateStatField(int statId, int baseValue, int equipValue)
        {
            var statMsg = new ProtoMessage();

            // Field 5: Stat ID
            statMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = statId });

            // Field 3: las sub-message (Field 2 = base value, Field 7 = equip bonus)
            var lasMsg = new ProtoMessage();
            if (baseValue != 0)
                lasMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = baseValue });
            if (equipValue != 0)
                lasMsg.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = equipValue });

            statMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = lasMsg.ToByteArray() });

            return new ProtoField { FieldNumber = 10, WireType = 2, BytesValue = statMsg.ToByteArray() };
        }

        public static ProtoField CreateInnateStatField(int statId, int innateValue, int equipValue)
        {
            var statMsg = new ProtoMessage();

            statMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = statId });

            // AP/MP use sub-message 4 instead of 3. Innate value is field 4, equip bonus is field 7.
            var innateMsg = new ProtoMessage();
            if (innateValue != 0)
                innateMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = innateValue });
            if (equipValue != 0)
                innateMsg.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = equipValue });

            statMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = innateMsg.ToByteArray() });

            return new ProtoField { FieldNumber = 10, WireType = 2, BytesValue = statMsg.ToByteArray() };
        }

        private static readonly Dictionary<int, int> EffectActionIdByStatId = new Dictionary<int, int>
        {
            { 1, 111 },  // AP
            { 23, 128 }, // MP
            { 10, 118 }, // Fuerza
            { 11, 125 }, // Vitalidad
            { 12, 124 }, // Sabiduría
            { 13, 123 }, // Suerte
            { 14, 119 }, // Agilidad
            { 15, 126 }, // Inteligencia
            { 16, 112 }, // Daños
            { 18, 115 }, // Crítico
            { 17, 138 }, // Potencia
            { 44, 174 }, // Iniciativa
        };

        /// <summary>Returns the total equipment bonus for a given stat ID, including set bonus.</summary>
        public static int GetEquipBonus(int statId)
        {
            int bonus = 0;
            // Map the generic statId to the specific effect ActionId used in the DB
            int effectId = EffectActionIdByStatId.TryGetValue(statId, out int mapped) ? mapped : statId;

            foreach (var equipped in GameState.GetEquippedItemsCopy().Values)
            {
                if (equipped.Stats.TryGetValue(effectId, out int b))
                    bonus += b;
            }
            bonus += GetSetBonus(statId);
            return bonus;
        }

        /// <summary>
        /// Returns the set bonus for a given stat ID based on how many Intrépido set pieces are equipped.
        /// Set GIDs: 10801, 10800, 10798, 10797, 10784, 10785, 10799, 10794
        /// Bonuses:
        ///   Vitality (11): +1 per piece equipped (starting from 2 pieces)
        ///   Initiative (44): +2 with full set (8 pieces)
        /// </summary>
        private static int GetSetBonus(int statId)
        {
            var equippedItems  = GameState.GetEquippedItemsCopy();
            var intrepidoGids  = new HashSet<int> { 10801, 10800, 10798, 10797, 10784, 10785, 10799, 10794 };
            int count = 0;

            foreach (var uid in equippedItems.Keys)
            {
                var item = GameState.GetInventoryItem(uid);
                if (item != null && intrepidoGids.Contains(item.ItemId))
                    count++;
            }

            if (count >= 2)
            {
                if (statId == 11) return count;           // +1 Vitalidad por pieza
                if (statId == 44) return count == 8 ? 2 : 0; // +2 Iniciativa solo con set completo
            }
            return 0;
        }

        private static void LogDebug(string msg) => Program.LogDebug(msg);
    }
}
