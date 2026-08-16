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

        // ─── Characteristic cost model ─────────────────────────────────────────────
        //
        // The cost is NOT flat: in Dofus it goes up in tiers. Verified against the client UI with
        // a real level-50 character: Chance 120 costs 140 (the first 100 at 1 each and the next 20
        // at 2 each), Wisdom 25 costs 75 (3 each) and everything else goes at 1.
        // With the previous flat model the server computed 300 where the client computed 320, and
        // since it also derived the capital from the stats instead of from the level, the panel
        // ended up showing "-75 / 245" and the reset button could never add up.

        /// <summary>Total point capital for a level: 5 per level starting from level 1.</summary>
        public static int TotalCapitalForLevel(int level) => Math.Max(0, (level - 1) * 5);

        /// <summary>Tier thresholds for the four elemental characteristics.</summary>
        private static readonly (int Upto, int Cost)[] ElementalTiers =
        {
            (100, 1), (200, 2), (300, 3), (400, 4), (int.MaxValue, 5)
        };

        /// <summary>Accumulated cost of raising an elemental characteristic up to 'points'.</summary>
        private static int ElementalCost(int points)
        {
            if (points <= 0) return 0;
            int cost = 0, done = 0;
            foreach (var (upto, unit) in ElementalTiers)
            {
                if (done >= points) break;
                int inThisTier = Math.Min(points, upto) - done;
                if (inThisTier <= 0) continue;
                cost += inThisTier * unit;
                done += inThisTier;
            }
            return cost;
        }

        /// <summary>Total cost of a complete distribution.</summary>
        public static int ComputeDistributionCost(int strength, int intelligence, int chance,
                                                  int agility, int vitality, int wisdom)
        {
            return ElementalCost(strength)
                 + ElementalCost(intelligence)
                 + ElementalCost(chance)
                 + ElementalCost(agility)
                 + Math.Max(0, vitality)               // vitality always at 1
                 + Math.Max(0, wisdom) * WisdomCost;   // wisdom always at 3
        }

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
            //   1=Agility(14) 2=Strength(10) 3=Intelligence(15) 4=Vitality(11) 5=Wisdom(12) 6=Chance(13)
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

            // The total capital comes from the LEVEL, not from the current stats: that way an
            // inconsistent earlier distribution cannot poison the maths (the source of "-75 / 245").
            int capitalPool = TotalCapitalForLevel(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel);

            // Wisdom arrives from the client already multiplied by its cost; normalize it to points.
            int wantWisdomPoints = wantWisdom / WisdomCost;

            int requestedCost = ComputeDistributionCost(wantStrength, wantIntelligence, wantChance,
                                                        wantAgility, wantVitality, wantWisdomPoints);

            Console.WriteLine($"[Stats] Requested — Str:{wantStrength} Int:{wantIntelligence} Cha:{wantChance} " +
                              $"Agi:{wantAgility} Vit:{wantVitality} Wis:{wantWisdomPoints} " +
                              $"(cost {requestedCost} / capital {capitalPool} level {Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel})");

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

            Jondo.Unity.Launcher.Network.SessionContext.State.StatStrength     = wantStrength;
            Jondo.Unity.Launcher.Network.SessionContext.State.StatIntelligence = wantIntelligence;
            Jondo.Unity.Launcher.Network.SessionContext.State.StatChance       = wantChance;
            Jondo.Unity.Launcher.Network.SessionContext.State.StatAgility      = wantAgility;
            Jondo.Unity.Launcher.Network.SessionContext.State.StatVitality     = wantVitality;
            Jondo.Unity.Launcher.Network.SessionContext.State.StatWisdom       = wantWisdomPoints;
            Jondo.Unity.Launcher.Network.SessionContext.State.CharacterRemainingPoints = capitalPool - requestedCost;

            Console.WriteLine($"[Stats] New — Vit:{Jondo.Unity.Launcher.Network.SessionContext.State.StatVitality} Wis:{Jondo.Unity.Launcher.Network.SessionContext.State.StatWisdom} Str:{Jondo.Unity.Launcher.Network.SessionContext.State.StatStrength} Int:{Jondo.Unity.Launcher.Network.SessionContext.State.StatIntelligence} Cha:{Jondo.Unity.Launcher.Network.SessionContext.State.StatChance} Agi:{Jondo.Unity.Launcher.Network.SessionContext.State.StatAgility}");
            Console.WriteLine($"[Stats] Remaining capital: {Jondo.Unity.Launcher.Network.SessionContext.State.CharacterRemainingPoints}");

            DatabaseManager.SaveCurrentCharacter();
            Console.WriteLine("[Stats] Saved updated stats to database.");

            // 1. Send updated pods weight (isf) — max pods depend on strength
            byte[] isfPacket = BuildIsfPacket();
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, isfPacket);
            Console.WriteLine("[Stats] Sent isf (pods weight).");

            // 2. Send the available-capital notification (krb). The official server sends this
            // whenever the stats-points pool changes; the client's characteristics panel binds
            // its "remaining points" counter to it and refreshes the open panel on receipt. Without
            // it, the panel only updated when closed and reopened.
            byte[] krbPacket = BuildKrbPacket(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterRemainingPoints);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, krbPacket);
            Console.WriteLine($"[Stats] Sent krb (available capital = {Jondo.Unity.Launcher.Network.SessionContext.State.CharacterRemainingPoints}).");

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
            foreach (var item in Jondo.Unity.Launcher.Network.SessionContext.State.GetInventoryCopy())
                currentWeight += DatabaseManager.GetItemRealWeight(item.ItemId) * item.Quantity;

            int maxWeight = 1000 + Jondo.Unity.Launcher.Network.SessionContext.State.StatStrength * 5;

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

        /// <summary>Stats whose values are computed from the database/session state;
        /// every other entry uses the official defaults below.</summary>
        private static readonly HashSet<int> DynamicStatIds = new HashSet<int> { 10, 11, 12, 13, 14, 15, 17, 18, 44 };

        /// <summary>
        /// Default stat entries reproduced verbatim from the official level-2 kri:
        /// (statId, subField, innerField, value). subField 3 = base value, 4 = innate
        /// value (AP/MP), 2 = limits/contextual. A value of 0 emits the empty sub-message,
        /// exactly like the official server does.
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

        // The experience thresholds come from the client's own table (character_xp.json).
        // They used to be hardcoded to 110 and 650, which are the level-2 ones: the experience bar
        // of a level-50 character showed a beginner's window.
        private static long XpLevelFloor => ExperienceTable.LevelFloor(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel);
        private static long XpNextLevel  => ExperienceTable.NextLevelFloor(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel);

        /// <summary>
        /// Builds the complete kri (CharacterStatsListMessage) frame from scratch:
        /// primary stats and capital come from the session state (loaded from SQLite), equipment
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
                levelMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel });
                levelMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel });
                levelMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 2, BytesValue = xpDetail.ToByteArray() });
                larMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = levelMsg.ToByteArray() });

                larMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterRemainingPoints });
                larMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = XpLevelFloor });  // XP at the start of the level
                larMsg.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterRemainingPoints });
                larMsg.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 0, VarIntValue = XpNextLevel });   // XP needed for the next level
                larMsg.Fields.Add(new ProtoField { FieldNumber = 11, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.Experience }); // Accumulated XP

                foreach (var e in DefaultKriEntries)
                {
                    if (DynamicStatIds.Contains(e.StatId)) continue;
                    long val = e.Value;
                    if (e.StatId == 3 || e.StatId == 40)
                    {
                        val = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterRemainingPoints;
                    }
                    larMsg.Fields.Add(CreateRawStatEntry(e.StatId, e.SubField, e.InnerField, val));
                }

                // Dynamic stats from database-backed session state + equipment bonuses
                int baseHp = 50 + (Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel * 5);
                larMsg.Fields.Add(CreateStatField(0, baseHp,                      GetEquipBonus(0)));  // Base HP
                larMsg.Fields.Add(CreateInnateStatField(1, 6,                     GetEquipBonus(1)));  // AP
                larMsg.Fields.Add(CreateInnateStatField(23, 3,                    GetEquipBonus(23))); // MP
                larMsg.Fields.Add(CreateStatField(11, Jondo.Unity.Launcher.Network.SessionContext.State.StatVitality,     GetEquipBonus(11))); // Vitality
                larMsg.Fields.Add(CreateStatField(12, Jondo.Unity.Launcher.Network.SessionContext.State.StatWisdom,       GetEquipBonus(12))); // Wisdom
                larMsg.Fields.Add(CreateStatField(10, Jondo.Unity.Launcher.Network.SessionContext.State.StatStrength,     GetEquipBonus(10))); // Strength
                larMsg.Fields.Add(CreateStatField(15, Jondo.Unity.Launcher.Network.SessionContext.State.StatIntelligence, GetEquipBonus(15))); // Intelligence
                larMsg.Fields.Add(CreateStatField(13, Jondo.Unity.Launcher.Network.SessionContext.State.StatChance,       GetEquipBonus(13))); // Chance
                larMsg.Fields.Add(CreateStatField(14, Jondo.Unity.Launcher.Network.SessionContext.State.StatAgility,      GetEquipBonus(14))); // Agility
                larMsg.Fields.Add(CreateStatField(25, 0,                          GetEquipBonus(25))); // Power
                larMsg.Fields.Add(CreateStatField(18, 0,                          GetEquipBonus(18))); // Critical

                // Initiative base = only elemental stats (Str+Int+Cha+Agi). Vitality and Wisdom do NOT count.
                int baseInitiative = Jondo.Unity.Launcher.Network.SessionContext.State.StatStrength + Jondo.Unity.Launcher.Network.SessionContext.State.StatIntelligence + Jondo.Unity.Launcher.Network.SessionContext.State.StatChance + Jondo.Unity.Launcher.Network.SessionContext.State.StatAgility;
                larMsg.Fields.Add(CreateStatField(44, baseInitiative, GetEquipBonus(44)));             // Initiative

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
            { 10, 118 }, // Strength
            { 11, 125 }, // Vitality
            { 12, 124 }, // Wisdom
            { 13, 123 }, // Chance
            { 14, 119 }, // Agility
            { 15, 126 }, // Intelligence
            { 16, 112 }, // Damage
            { 18, 115 }, // Critical
            // Power is characteristic 25, not 17. The client's own effect table says so: effect
            // 138 ("power (general damage)") points at 25. With 17, the +80 from the Purple Dofus
            // travelled in a characteristic the client does not draw, so the sheet showed Power 0.
            { 25, 138 }, // Power
            { 44, 174 }, // Initiative
        };

        /// <summary>Returns the total equipment bonus for a given stat ID, including set bonus.</summary>
        public static int GetEquipBonus(int statId)
        {
            int bonus = 0;
            // Map the generic statId to the specific effect ActionId used in the DB
            int effectId = EffectActionIdByStatId.TryGetValue(statId, out int mapped) ? mapped : statId;

            foreach (var equipped in Jondo.Unity.Launcher.Network.SessionContext.State.GetEquippedItemsCopy().Values)
            {
                if (equipped.Stats.TryGetValue(effectId, out int b))
                    bonus += b;
            }
            bonus += GetSetBonus(statId);
            return bonus;
        }

        /// <summary>
        /// Single authoritative source of truth for player's Max HP,
        /// including base level HP, allocated Vitality, and equipment/set bonuses.
        /// </summary>
        public static int GetPlayerMaxHp()
        {
            int baseHp = 50 + (Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel * 5) + Jondo.Unity.Launcher.Network.SessionContext.State.StatVitality;
            int equipHp = GetEquipBonus(11) + GetEquipBonus(0);
            return baseHp + equipHp;
        }

        /// <summary>
        /// Returns the set bonus for a given stat ID based on how many Intrepid set pieces are equipped.
        /// Set GIDs: 10801, 10800, 10798, 10797, 10784, 10785, 10799, 10794
        /// Bonuses:
        ///   Vitality (11): +1 per piece equipped (starting from 2 pieces)
        ///   Initiative (44): +2 with full set (8 pieces)
        /// </summary>
        private static int GetSetBonus(int statId)
        {
            var equippedItems  = Jondo.Unity.Launcher.Network.SessionContext.State.GetEquippedItemsCopy();
            var intrepidGids  = new HashSet<int> { 10801, 10800, 10798, 10797, 10784, 10785, 10799, 10794 };
            int count = 0;

            foreach (var uid in equippedItems.Keys)
            {
                var item = Jondo.Unity.Launcher.Network.SessionContext.State.GetInventoryItem(uid);
                if (item != null && intrepidGids.Contains(item.ItemId))
                    count++;
            }

            if (count >= 2)
            {
                if (statId == 11) return count;           // +1 Vitality per equipped piece
                if (statId == 44) return count == 8 ? 2 : 0; // +2 Initiative only with the full set
            }
            return 0;
        }

        private static void LogDebug(string msg) => Program.LogDebug(msg);
    }
}
