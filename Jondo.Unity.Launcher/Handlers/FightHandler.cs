using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using static Jondo.Unity.Launcher.Network.NetworkEnvelope;
using static Jondo.Protocol.NetworkMessage;

namespace Jondo.Unity.Launcher.Handlers
{
    public static class FightHandler
    {
        private static ConcurrentDictionary<long, FightInstance> _activeFights = new ConcurrentDictionary<long, FightInstance>();
        private static long _nextFightId = 1000;

        /// <summary>Turn duration in tenths of a second, exactly as it travels in jut.f1 and jyf.f2.</summary>
        public const int TurnDurationDeciseconds = 300;
        /// <summary>The same duration in milliseconds, for the server-side timer.</summary>
        private const int TurnDurationMs = TurnDurationDeciseconds * 100;

        public static void RegisterHandlers()
        {
            Program.LogDebug("[FightHandler] Combat handlers registered for jxx, jyk, jyz, jza, jwb, hoy.");
        }

        /// <summary>
        /// Called by MapChangeHandler when the player's movement path terminates on a mob's cell.
        /// Builds the FightInstance from real mob data and sends placement bursts 1 and 2.
        /// </summary>
        public static async Task InitiateFightFromMobCollision(NetworkStream stream, MobSpawnManager.MobGroup mobGroup, long mapId, long mobContextId = 0)
        {
            _activeFights.Clear();
            Jondo.Unity.Launcher.Network.SessionContext.State.IsInFight = true;
            Jondo.Unity.Launcher.Network.SessionContext.State.CurrentFightMobId = mobGroup.MobId;

            long fightId = System.Threading.Interlocked.Increment(ref _nextFightId);
            long arenaMapId = MapManager.ResolveArenaMapId(mapId);
            var fight = new FightInstance(fightId, mapId, arenaMapId);

            // mobContextId is the roleplay mob group ID (e.g. -1030815 or -20003)
            fight.DefenderLeaderId = (mobContextId != 0) ? mobContextId : mobGroup.MobId;

            // Generate placement cells from arena map walkable cells
            var walkableCells = MobSpawnManager.GetInnerWalkableCells(arenaMapId);
            fight.GeneratePlacementCells(walkableCells);

            // Build Player Fighter from the current session (Fighter ID = player CharacterId)
            var playerFighter = new Fighter
            {
                Id = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId,
                Name = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterName,
                TeamId = 0,
                CellId = fight.BluePlacementCells.FirstOrDefault(),
                Level = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel > 0 ? Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel : 40,
                // Same source as the jxx we send to the client. There used to be a custom formula
                // here that only looked at BASE vitality: the server believed the character had
                // 305 HP while the client displayed 514, because equipped items (the Emerald
                // Dofus gives +200) were only added on one side. The result: the character died
                // "in the background" after 8 turns with a full health bar on screen.
                MaxHP = StatsHandler.GetPlayerMaxHp(),
                MaxAP = 6,
                MaxMP = 3,
                // Same initiative the character sheet shows: elemental characteristics plus
                // whatever the gear contributes. The formula that used to be here (100 + level +
                // every characteristic) made the number up and, above all, ignored items: with the
                // Nightmare Dofus equipped (+1000 initiative) the piwi still played first.
                Initiative = Jondo.Unity.Launcher.Network.SessionContext.State.StatStrength + Jondo.Unity.Launcher.Network.SessionContext.State.StatIntelligence + Jondo.Unity.Launcher.Network.SessionContext.State.StatChance
                             + Jondo.Unity.Launcher.Network.SessionContext.State.StatAgility + StatsHandler.GetEquipBonus(44),
                Strength = Jondo.Unity.Launcher.Network.SessionContext.State.StatStrength + StatsHandler.GetEquipBonus(10),
                Intelligence = Jondo.Unity.Launcher.Network.SessionContext.State.StatIntelligence + StatsHandler.GetEquipBonus(15),
                Chance = Jondo.Unity.Launcher.Network.SessionContext.State.StatChance + StatsHandler.GetEquipBonus(13),
                Agility = Jondo.Unity.Launcher.Network.SessionContext.State.StatAgility + StatsHandler.GetEquipBonus(14),
                // Power from the gear (characteristic 25). It feeds straight into damage.
                Power = StatsHandler.GetEquipBonus(25),
                // Critical hit from the gear (characteristic 18): the Turquoise Dofus gives +10.
                CriticalBonus = StatsHandler.GetEquipBonus(18),
                LookBoneId = 744,
                IsMonster = false
            };
            playerFighter.CurrentHP = playerFighter.MaxHP;
            playerFighter.CurrentAP = playerFighter.MaxAP;
            playerFighter.CurrentMP = playerFighter.MaxMP;

            fight.AddPlayer(playerFighter);

            // Build Monster Fighters from real MobGroup data.
            // Fighter IDs for monsters MUST be sequential negative numbers per fight (-1, -2, -3...)
            long monsterSeqId = -1;
            int redIdx = 0;
            foreach (var member in mobGroup.Members)
            {
                long monFighterId = monsterSeqId--;
                int monCellId = (fight.RedPlacementCells.Count > redIdx)
                    ? fight.RedPlacementCells[redIdx++]
                    : fight.RedPlacementCells.FirstOrDefault();

                int boneId = 1;
                string look = member.Monster?.Look ?? "";
                if (!string.IsNullOrEmpty(look))
                {
                    string stripped = look.Trim('{', '}');
                    string[] parts = stripped.Split('|');
                    if (parts.Length > 0 && int.TryParse(parts[0], out int parsedBone))
                    {
                        boneId = parsedBone;
                    }
                }

                int monLevel = member.Level > 0 ? member.Level : 1;
                int monsterId = member.Monster?.Id ?? 0;
                int gradeIdx = member.GradeIndex;

                var dbStats = DatabaseManager.GetMonsterGradeStats(monsterId, gradeIdx);

                var monsterFighter = new Fighter
                {
                    Id = monFighterId,
                    Name = $"Monster_{monsterId}",
                    TeamId = 1,
                    CellId = monCellId,
                    IsMonster = true,
                    MonsterId = monsterId,
                    GradeIndex = gradeIdx,
                    Level = dbStats?.Level ?? monLevel,
                    MaxHP = dbStats?.LifePoints ?? (40 + (monLevel * 8)),
                    MaxAP = dbStats?.ActionPoints ?? 6,
                    MaxMP = dbStats?.MovementPoints ?? 3,
                    Initiative = dbStats != null ? (dbStats.Agility + dbStats.Strength + dbStats.Intelligence + dbStats.Chance + dbStats.Wisdom) : (50 + monLevel),
                    Strength = dbStats?.Strength ?? (5 + monLevel),
                    Intelligence = dbStats?.Intelligence ?? (5 + monLevel / 2),
                    Chance = dbStats?.Chance ?? (5 + monLevel / 2),
                    Agility = dbStats?.Agility ?? (5 + monLevel / 2),
                    NeutralResPct = dbStats?.NeutralResistance ?? Math.Min(50, monLevel / 3),
                    EarthResPct = dbStats?.EarthResistance ?? Math.Min(50, monLevel / 4),
                    FireResPct = dbStats?.FireResistance ?? Math.Min(50, monLevel / 4),
                    WaterResPct = dbStats?.WaterResistance ?? Math.Min(50, monLevel / 4),
                    AirResPct = dbStats?.AirResistance ?? Math.Min(50, monLevel / 4),
                    LookBoneId = boneId,
                    SpellIds = dbStats?.SpellIds ?? new List<int>(),
                    SpellGrades = dbStats?.SpellGrades ?? new Dictionary<int, int>(),
                    // gradeXp from the monster template: the experience it awards on death, the
                    // same figure the client shows when hovering over the group.
                    XpReward = dbStats?.GradeXp ?? 0
                };
                monsterFighter.CurrentHP = monsterFighter.MaxHP;
                monsterFighter.CurrentAP = monsterFighter.MaxAP;
                monsterFighter.CurrentMP = monsterFighter.MaxMP;

                fight.AddMonster(monsterFighter);
            }

            _activeFights[fightId] = fight;
            Program.LogDebug($"[FightHandler] Fight #{fightId} created on map {mapId}:");
            Program.LogDebug($"  Team 0 (Players): {fight.Team0.Count} fighters (Leader ID: {fight.ChallengerLeaderId})");
            Program.LogDebug($"  Team 1 (Monsters): {fight.Team1.Count} fighters (Context ID: {fight.DefenderLeaderId})");
            foreach (var m in fight.Team1)
            {
                Program.LogDebug($"    - Monster ID {m.MonsterId} (Fighter ID {m.Id}, Level {m.Level}, HP {m.MaxHP}, BoneId {m.LookBoneId})");
            }

            // =========================================================================
            // BURST 1 (Fired immediately on collision / jpp)
            // Sequence: joq, jpf, kkq, kkp, kkm, kri, joh, lor, krp, lsy, kkz
            // =========================================================================
            // 1. joq (Movement validation - empty opcode)
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/joq", Array.Empty<byte>()));

            // 2. jpf (GameContextDestroyMessage)
            byte[] jpfPacket = BuildJpfPacket(fight.DefenderLeaderId);
            await WriteFrameAsync(stream, jpfPacket);

            // 3. kkq: identifies the mob group being fought.
            var kkqMsg = new ProtoMessage();
            kkqMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fight.DefenderLeaderId });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/kkq", kkqMsg.ToByteArray()));

            // 4. kkp: destroys the current context (empty message).
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/kkp", Array.Empty<byte>()));

            // 5. kkm: creates the new context. 1 = fight; roleplay is 0, which is why this very
            // same message goes out empty once the fight is over.
            var kkmMsg = new ProtoMessage();
            kkmMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 1 });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/kkm", kkmMsg.ToByteArray()));

            // 6. kri (CharacterStatsListMessage for fight context)
            byte[]? kriPacket = StatsHandler.BuildUpdatedKriPacket();
            if (kriPacket != null)
            {
                await WriteFrameAsync(stream, kriPacket);
            }

            // 7. joh (CurrentMapMessage for ArenaMapId)
            var johMsg = new ProtoMessage();
            johMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fight.ArenaMapId });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/joh", johMsg.ToByteArray()));

            // 8. lor (TimeMessage)
            var lorMsg = new ProtoMessage();
            lorMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 120 });
            lorMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/lor", lorMsg.ToByteArray()));

            // 9. krp (f1=278, f2=77, f3=77)
            var krpMsg = new ProtoMessage();
            krpMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 278 });
            krpMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 77 });
            krpMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 77 });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/krp", krpMsg.ToByteArray()));

            // 10. lsy (SubArea alignment info - empty)
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/lsy", Array.Empty<byte>()));

            // 11. kkz (Player placement position)
            byte[] kkzPlayer = BuildKkzPacket(playerFighter.CellId, playerFighter.Id, 3);
            await WriteFrameAsync(stream, kkzPlayer);
            Program.LogDebug("[FightHandler] BURST 1 sent successfully.");

            // =========================================================================
            // BURST 2 (Immediately after Burst 1)
            // Sequence: jyf (player team), jyf (monster team), kkz (player), kkz (monsters)
            // =========================================================================
            // 1. jyf #1 & #2
            foreach (var packet in BuildPlacementPossiblePositionsPackets(fight))
            {
                await WriteFrameAsync(stream, packet);
            }

            // 2. kkz player
            await WriteFrameAsync(stream, kkzPlayer);

            // 3. kkz for each monster
            foreach (var m in fight.Team1)
            {
                await WriteFrameAsync(stream, BuildKkzPacket(m.CellId, m.Id, 7));
            }
            Program.LogDebug("[FightHandler] BURST 2 sent successfully. Waiting for client kkr request...");

            // 4. Schedule 45s preparation timeout to auto-start Turn 1
            long currentFightId = fight.FightId;
            _ = Task.Run(async () =>
            {
                await Task.Delay(45000);
                var f = GetCurrentFight();
                if (f != null && f.FightId == currentFightId && f.State == Jondo.Unity.World.Fights.FightState.Placement)
                {
                    Program.LogDebug($"[FightHandler] ⏰ 45s preparation timeout reached. Auto-starting Turn 1 for fight #{currentFightId}!");
                    await HandleTurnReady(stream, Array.Empty<byte>());
                }
            });
        }

        public static byte[] BuildIgsPacket(FightInstance fight)
        {
            return BuildGameNodePacket("type.ankama.com/igs", Array.Empty<byte>());
        }

        /// <summary>
        /// Responds to map load request (kkr / jqf) from the client during fight setup.
        /// Sends BURST 3 containing igs, jya, jyj, jxx, jyi, jyf, jyk, jxe, jwo, jox.
        /// </summary>
        public static async Task HandleFightMapLoad(NetworkStream stream)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            if (fight.HasLoadedMap)
            {
                // Re-send burst 3 on subsequent map load requests (jqf/kkr)
                await ResendFightMapBurst3(stream, fight);
                return;
            }
            fight.HasLoadedMap = true;
            Program.LogDebug("[FightHandler] Responding to fight map request (kkr) with BURST 3...");

            // =========================================================================
            // BURST 3 (Fired by the client's kkr)
            // Sequence: igs, jya, jyj, jxx (all), jyi, jyf, jykjxe, jwo, jox
            // =========================================================================
            // 1. igs (GameFightComplementaryInformationsDataMessage with subarea & placement positions)
            await WriteFrameAsync(stream, BuildIgsPacket(fight));

            // 1b. jyg (GameFightJoinMessage - switches soundtrack to combat music and hides roleplay entities)
            var jygMsg = new ProtoMessage();
            jygMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 0 });
            jygMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 1 });
            jygMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 0 });
            jygMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 450 });
            jygMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 4 });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jyg", jygMsg.ToByteArray()));

            // 2. jya (FightStarting)
            await SendFightStarting(stream, fight);

            // 3. jyj (GameFightOptionStateUpdateMessage)
            var jyjMsg = new ProtoMessage();
            jyjMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 1 });
            jyjMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 4 });
            jyjMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 443 });
            jyjMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = 1 });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jyj", jyjMsg.ToByteArray()));

            // 4. jxx (GameFightShowFighterMessage) for each fighter
            foreach (var f in fight.Team0.Concat(fight.Team1))
            {
                await SendFighterShow(stream, f);
            }

            // 5. jyi (GameFightPlacementPossiblePositionsMessage)
            await SendPlacementPositionsList(stream, fight);

            // 6. jyf (placement options update - single packet)
            var jyfPackets = BuildPlacementPossiblePositionsPackets(fight);
            if (jyfPackets.Count > 0)
            {
                await WriteFrameAsync(stream, jyfPackets[0]);
            }

            // 7. jyk options (0, 1, 2, 3)
            int[] optionTypes = new int[] { 2, 1, 3, 0 };
            foreach (int opt in optionTypes)
            {
                var jykMsg = new ProtoMessage();
                jykMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = opt });
                jykMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 300 });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jyk", jykMsg.ToByteArray()));
            }

            // 8. jxe (GameFightTurnListMessage)
            await SendTurnList(stream, fight);

            // 9. jwo (GameFightTurnStartPlayingMessage header - empty 3-letter opcode)
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwo", Array.Empty<byte>()));

            // 10. jox (GameFightTurnStartMessage for placement phase - f1=450, f2.f1=-3, f2.f2=-2)
            await SendPlacementTurnStart(stream, fight);
            Program.LogDebug("[FightHandler] BURST 3 sent successfully. Client in placement phase (45s).");
        }

        private static async Task ResendFightMapBurst3(NetworkStream stream, FightInstance fight)
        {
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/igs", Array.Empty<byte>()));
            foreach (var f in fight.Team0.Concat(fight.Team1))
            {
                await SendFighterShow(stream, f);
            }
            await SendPlacementPositionsList(stream, fight);
        }

        public static byte[] BuildTurnListBytes(FightInstance fight)
        {
            var jxeMsg = new ProtoMessage();
            var fighters = (fight.TurnOrder.Count > 0) 
                ? fight.TurnOrder 
                : fight.Team0.Concat(fight.Team1).OrderByDescending(f => f.Initiative).ToList();

            foreach (var fighter in fighters)
            {
                var fSubInner = new ProtoMessage();
                fSubInner.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fighter.Id });

                var fSubOuter = new ProtoMessage();
                fSubOuter.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = fSubInner.ToByteArray() });

                jxeMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = fSubOuter.ToByteArray() });
            }

            return BuildGameNodePacket("type.ankama.com/jxe", jxeMsg.ToByteArray());
        }

        public static async Task SendTurnList(NetworkStream stream, FightInstance fight)
        {
            byte[] jxePacket = BuildTurnListBytes(fight);
            await WriteFrameAsync(stream, jxePacket);
            Program.LogDebug($"[FightHandler] Sent jxe (GameFightTurnListMessage) with {fight.TurnOrder.Count} fighters in turn order.");
        }

        public static async Task SendPlacementTurnStart(NetworkStream stream, FightInstance fight)
        {
            var joxSub = new ProtoMessage();
            joxSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = -3 });
            joxSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = -2 });

            var joxMsg = new ProtoMessage();
            joxMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 450 }); // 45s Placement Phase Timer
            joxMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = joxSub.ToByteArray() });
            if (fight != null)
            {
                joxMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = fight.MapId });
            }

            byte[] joxPacket = BuildGameNodePacket("type.ankama.com/jox", joxMsg.ToByteArray());
            await WriteFrameAsync(stream, joxPacket);
            Program.LogDebug($"[FightHandler] Sent jox Placement Phase Countdown (45s) for map {fight?.MapId}.");
        }

        public static async Task SendTurnStart(NetworkStream stream, Fighter fighter)
        {
            var fight = GetCurrentFight();

            // Send jwo header first (GameFightTurnStartPlayingMessage - empty 3-letter opcode)
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwo", Array.Empty<byte>()));

            // Send jox (GameFightTurnStartMessage) for turn 1
            var joxSub = new ProtoMessage();
            joxSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fighter.Id });
            joxSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 0 });

            var joxMsg = new ProtoMessage();
            joxMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 450 }); // Turn time limit (45s)
            joxMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = joxSub.ToByteArray() });
            if (fight != null)
            {
                joxMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = fight.MapId });
            }

            byte[] joxPacket = BuildGameNodePacket("type.ankama.com/jox", joxMsg.ToByteArray());
            await WriteFrameAsync(stream, joxPacket);
            Program.LogDebug($"[FightHandler] Sent jwo & jox (GameFightTurnStartMessage) for Fighter ID {fighter.Id} on map {fight?.MapId}.");
        }

        public static byte[] BuildPlacementPositionsListBytes(FightInstance fight)
        {
            var jyiMsg = new ProtoMessage();

            using var msRed = new MemoryStream();
            var codedRed = new CodedOutputStream(msRed);
            foreach (var c in fight.RedPlacementCells)
            {
                codedRed.WriteUInt32((uint)c);
            }
            codedRed.Flush();

            using var msBlue = new MemoryStream();
            var codedBlue = new CodedOutputStream(msBlue);
            foreach (var c in fight.BluePlacementCells)
            {
                codedBlue.WriteUInt32((uint)c);
            }
            codedBlue.Flush();

            var innerSub = new ProtoMessage();
            innerSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = msRed.ToArray() });
            innerSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = msBlue.ToArray() });

            jyiMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = innerSub.ToByteArray() });

            return BuildGameNodePacket("type.ankama.com/jyi", jyiMsg.ToByteArray());
        }

        public static async Task SendPlacementPositionsList(NetworkStream stream, FightInstance fight)
        {
            byte[] jyiPacket = BuildPlacementPositionsListBytes(fight);
            await WriteFrameAsync(stream, jyiPacket);
            Program.LogDebug("[FightHandler] Sent dynamic jyi (GameFightPlacementPossiblePositionsMessage).");
        }

        public static async Task HandleFightMessageAsync(NetworkStream stream, byte[] payload, string payloadStr)
        {
            Program.LogDebug($"\n[FIGHT PACKET RECEIVED] Length: {payload.Length} bytes");
            try
            {
                var parsed = ProtoMessage.Parse(payload);
                Program.LogDebug(parsed.DumpFieldsToString("  "));
            }
            catch
            {
                string hex = BitConverter.ToString(payload).Replace("-", " ");
                if (hex.Length > 80) hex = hex.Substring(0, 80) + "...";
                Program.LogDebug($"  Hex: {hex}");
            }

            var fight = GetCurrentFight();
            if (payloadStr.Contains("type.ankama.com/jyz"))
            {
                if (fight != null && fight.State == Jondo.Unity.World.Fights.FightState.Ongoing)
                {
                    Program.LogDebug("  -> Type: jyz (Combat Move)");
                    await HandleCombatMoveRequest(stream, payload);
                }
                else
                {
                    Program.LogDebug("  -> Type: jyz (Placement Position Request)");
                    await HandlePlacementCellChangeRequest(stream, payload);
                }
            }
            else if (payloadStr.Contains("type.ankama.com/joi"))
            {
                Program.LogDebug("  -> Type: joi (Combat Move Request)");
                await HandleCombatMoveRequest(stream, payload);
            }
            else if (payloadStr.Contains("type.ankama.com/jza"))
            {
                Program.LogDebug("  -> Type: jza (GameFightReadyMessage / Player Clicked Ready)");
                await HandleTurnReady(stream, payload);
            }
            else if (payloadStr.Contains("type.ankama.com/jwe"))
            {
                Program.LogDebug("  -> Type: jwe (GameFightTurnReadyAckMessage / Turn Handshake Ack)");
                await HandleTurnReadyAck(stream);
            }
            else if (payloadStr.Contains("type.ankama.com/jxw"))
            {
                Program.LogDebug("  -> Type: jxw (GameFightTurnFinishMessage / Pass Turn)");
                await HandlePassTurnRequest(stream, payload);
            }
            else if (payloadStr.Contains("type.ankama.com/jub"))
            {
                Program.LogDebug("  -> Type: jub (GameFightSpellCastRequestMessage / Cast Spell)");
                await HandleSpellCastRequest(stream, payload);
            }
            else if (payloadStr.Contains("type.ankama.com/hoy"))
            {
                Program.LogDebug("  -> Type: hoy (GameFightOptionToggleMessage)");
                await HandleFightOptionToggleRequest(stream, payload);
            }
        }

        private static async Task HandleFightOptionToggleRequest(NetworkStream stream, byte[] payload)
        {
            long mobContextId = 0;
            try
            {
                var msg = ProtoMessage.Parse(payload);
                if (msg.Fields.Count > 0 && msg.Fields[0].WireType == 2)
                {
                    var inner = ProtoMessage.Parse(msg.Fields[0].BytesValue);
                    if (inner.Fields.Count > 1 && inner.Fields[1].WireType == 2)
                    {
                        var inner2 = ProtoMessage.Parse(inner.Fields[1].BytesValue);
                        if (inner2.Fields.Count > 1 && inner2.Fields[1].WireType == 2)
                        {
                            var inner3 = ProtoMessage.Parse(inner2.Fields[1].BytesValue);
                            if (inner3.Fields.Count > 0 && inner3.Fields[0].WireType == 0)
                            {
                                mobContextId = inner3.Fields[0].VarIntValue;
                            }
                        }
                    }
                }
            }
            catch { }
            Program.LogDebug($"[FightHandler] Client requested Fight Interaction (hoy) for Mob Context ID {mobContextId}.");

            var fight = GetCurrentFight();
            if (fight == null)
            {
                var mobs = MobSpawnManager.GetMobsForMap(Jondo.Unity.Launcher.Network.SessionContext.State.MapId);
                var mobGroup = (mobContextId != 0) 
                    ? (mobs.FirstOrDefault(m => m.MobId == mobContextId) ?? MobSpawnManager.GetMobGroupById(mobContextId) ?? mobs.FirstOrDefault())
                    : mobs.FirstOrDefault();

                if (mobGroup != null)
                {
                    await InitiateFightFromMobCollision(stream, mobGroup, Jondo.Unity.Launcher.Network.SessionContext.State.MapId, mobContextId);
                    return;
                }
            }
            else
            {
                // Re-sync combat context packets if requested
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/joq", Array.Empty<byte>()));
                await WriteFrameAsync(stream, BuildJpfPacket(fight.DefenderLeaderId));
                
                var johMsg = new ProtoMessage();
                johMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.MapId });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/joh", johMsg.ToByteArray()));

                foreach (var p in BuildPlacementPossiblePositionsPackets(fight))
                {
                    await WriteFrameAsync(stream, p);
                }
                foreach (var f in fight.Team0.Concat(fight.Team1))
                {
                    await SendFighterShow(stream, f);
                }
                await SendFightStarting(stream, fight);
            }
        }

        private static FightInstance? GetCurrentFight()
        {
            return _activeFights.Values.FirstOrDefault();
        }

        private static async Task HandlePlacementCellChangeRequest(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            var inner = ExtractMessagePayload(payload, "type.ankama.com/jyz");
            if (inner != null)
            {
                try
                {
                    var msg = ProtoMessage.Parse(inner);
                    if (msg.Fields.Count > 0 && msg.Fields[0].WireType == 0)
                    {
                        int newCell = (int)msg.Fields[0].VarIntValue;
                        if (!fight.BluePlacementCells.Contains(newCell))
                        {
                            Program.LogDebug($"[FightHandler] Rejected invalid placement cell {newCell} for player (cell not in blue placement cells).");
                            return;
                        }

                        fight.ChangePlacementCell(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId, newCell);
                        Program.LogDebug($"[FightHandler] Changed player placement cell to {newCell}.");

                        // Reply with kkz ack
                        byte[] ack = BuildKkzPacket(newCell, Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId, 3);
                        await WriteFrameAsync(stream, ack);
                    }
                }
                catch { }
            }
            await Task.CompletedTask;
        }

        // NOTE: HandleCombatMovementRequest used to live here, an old version of combat movement
        // that echoed the client's compressed path back without expanding it and tacked on a kkz
        // that forced the position. That is what caused the teleporting. It was removed so it can
        // no longer compete with HandleCombatMoveRequest (the two names differed by a single
        // letter and the routing kept picking the wrong one).

        private static async Task HandleTurnReady(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            Program.LogDebug("[FightHandler] Player clicked READY (jza / F1).");
            bool allReady = fight.SetFighterReady(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId);

            if (allReady)
            {
                fight.StartFight();
                var current = fight.CurrentFighter ?? fight.Team0[0];

                // 1. jys (GameFightPreparationStartedMessage)
                var jysMsg = new ProtoMessage();
                jysMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 1 });
                jysMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jys", jysMsg.ToByteArray()));

                // 2. jwu (f3 = playerId)
                var jwu1 = new ProtoMessage();
                jwu1.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwu", jwu1.ToByteArray()));

                // 3. lsy (empty)
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/lsy", Array.Empty<byte>()));

                // 4. kkz (ALL fighters)
                await WriteFrameAsync(stream, BuildKkzAllPacket(fight));

                // 5. jyn (empty)
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jyn", Array.Empty<byte>()));

                // 6. jvn (player combat spells list)
                await SendSpellList(stream);

                // 7. jwb (f1 = 1)
                var jwbMsg = new ProtoMessage();
                jwbMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 1 });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwb", jwbMsg.ToByteArray()));

                // 8. jwu (f3 = playerId)
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwu", jwu1.ToByteArray()));

                // 9. jud (sequence start)
                var jud1 = new ProtoMessage();
                jud1.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 8 });
                jud1.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jud", jud1.ToByteArray()));

                // 10. jwm (FighterResyncMessage)
                await SendFighterResync(stream, fight);

                // 11. juc (sequence end)
                var juc1 = new ProtoMessage();
                juc1.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 8 });
                juc1.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId });
                juc1.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3 });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/juc", juc1.ToByteArray()));

                // 12. juu (Wait turn ack from client)
                var juuMsg = new ProtoMessage();
                juuMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = current.Id });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/juu", juuMsg.ToByteArray()));
                Program.LogDebug($"[FightHandler] Turn 1 preparation complete. Sent juu for Fighter #{current.Id} ({current.Name}).");
            }
        }

        public static async Task SendSpellList(NetworkStream stream)
        {
            var jvnMsg = new ProtoMessage();

            // Same list as the roleplay shortcut bar.
            // TODO: once "which variant the player picked" is persisted, read it in
            // GetPlayerAvailableSpells instead of always assuming the base one.
            var spellList = DatabaseManager.GetPlayerAvailableSpells(Jondo.Unity.Launcher.Network.SessionContext.State.Breed, Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel);

            Program.LogDebug($"[FightHandler] jvn: {spellList.Count} spells available at level " +
                             $"{Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel} for breed {Jondo.Unity.Launcher.Network.SessionContext.State.Breed}: " +
                             string.Join(", ", spellList));

            // First entry: the WEAPON. It carries no spell id and uses f3 = 2 (spells use f3 = 1).
            // It was missing, and that is why the sword icon vanished from the bar on entering a
            // fight: jvn rebuilds the bar and was leaving it without the weapon slot.
            var weaponSub = new ProtoMessage();
            weaponSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 2 });
            weaponSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });
            jvnMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = weaponSub.ToByteArray() });
            foreach (var spellId in spellList)
            {
                var sSub = new ProtoMessage();
                sSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = spellId });
                sSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
                sSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });
                jvnMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = sSub.ToByteArray() });
            }

            jvnMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId });
            jvnMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId });

            // Slot 0 left empty, just like the official capture and the itp: that is the weapon slot.
            var weaponSlot = new ProtoMessage();
            weaponSlot.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = Array.Empty<byte>() });
            jvnMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 2, BytesValue = weaponSlot.ToByteArray() });

            int slot = 1;
            foreach (var spellId in spellList)
            {
                var spSub = new ProtoMessage();
                spSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = spellId });

                var slotSub = new ProtoMessage();
                slotSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = slot++ });
                slotSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = spSub.ToByteArray() });

                jvnMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 2, BytesValue = slotSub.ToByteArray() });
            }

            byte[] env = BuildGameNodePacket("type.ankama.com/jvn", jvnMsg.ToByteArray());
            await WriteFrameAsync(stream, env);
            Program.LogDebug($"[FightHandler] Sent jvn (SpellListMessage) with {spellList.Count} spells for Breed {Jondo.Unity.Launcher.Network.SessionContext.State.Breed}.");
        }

        public static async Task SendFighterResync(NetworkStream stream, FightInstance fight)
        {
            var jwmMsg = new ProtoMessage();
            foreach (var f in fight.Team0.Concat(fight.Team1))
            {
                byte[] fShowBytes = BuildFighterShowBytes(f);
                byte[]? payload = NetworkEnvelope.ExtractGameNodePayload(fShowBytes);
                if (payload != null)
                {
                    var innerMsg = ProtoMessage.Parse(payload);
                    if (innerMsg.Fields.Count > 1)
                    {
                        jwmMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = innerMsg.Fields[1].BytesValue });
                    }
                }
            }
            byte[] env = BuildGameNodePacket("type.ankama.com/jwm", jwmMsg.ToByteArray());
            await WriteFrameAsync(stream, env);
            Program.LogDebug("[FightHandler] Sent jwm (FighterResyncMessage).");
        }

        public static async Task HandleTurnReadyAck(NetworkStream stream)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            var current = fight.CurrentFighter;
            if (current == null) return;

            Program.LogDebug($"[FightHandler] Turn Ready Ack (jwe) received for Fighter #{current.Id} ({current.Name}).");

            // Send jut & jwl
            var jutMsg = new ProtoMessage();
            jutMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = TurnDurationDeciseconds });
            jutMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = fight.RoundNumber });
            jutMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = current.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jut", jutMsg.ToByteArray()));

            ResetTurnCastCounters();

            // Point refresh at the start of the turn. Fighter.StartTurn() already restores AP/MP on
            // the server side; this tells the client about it. With a delta of 0 the value block
            // collapses down to just the maximum, which is how the official capture expresses
            // "points back to full".
            //
            // It goes wrapped in jud/juc: the client's sequence engine discards characteristic
            // changes that arrive loose, outside an open sequence.
            await WriteFrameAsync(stream, BuildJud(4, current.Id));
            await WriteFrameAsync(stream, BuildJud(3, current.Id));
            await WriteFrameAsync(stream, BuildJvmPacket(current.Id, 1, 0, current.MaxAP));
            await WriteFrameAsync(stream, BuildJuc(3, current.Id));
            await WriteFrameAsync(stream, BuildJud(3, current.Id));
            await WriteFrameAsync(stream, BuildJvmPacket(current.Id, 23, 0, current.MaxMP));
            await WriteFrameAsync(stream, BuildJuc(3, current.Id));
            await WriteFrameAsync(stream, BuildJuc(4, current.Id));

            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwl", Array.Empty<byte>()));
            Program.LogDebug($"[FightHandler] Sent jut & jwl (Turn Started & Playable) for Fighter #{current.Id} " +
                              $"(AP {current.CurrentAP}/{current.MaxAP}, MP {current.CurrentMP}/{current.MaxMP}).");

            if (current.IsMonster)
            {
                await RunMonsterTurnAsync(stream, current);
            }
            else
            {
                StartTurnTimer(stream, fight, current);
            }
        }

        /// <summary>
        /// Starts the turn timer. jut.f1 = 300 only tells the client how many tenths of a second
        /// the turn lasts; enforcing the deadline is the server's job. Without this, once the time
        /// ran out the client's counter just kept ticking down into negatives and the turn never
        /// passed.
        /// </summary>
        private static void StartTurnTimer(NetworkStream stream, FightInstance fight, Fighter fighter)
        {
            fight.CancelTurnTimer();

            var cts = new CancellationTokenSource();
            fight.TurnTimerCts = cts;

            long fightId = fight.FightId;
            long fighterId = fighter.Id;
            int round = fight.RoundNumber;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TurnDurationMs, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return; // the player passed the turn in time
                }

                // Only force the end if we are still on exactly the same turn.
                var f = GetCurrentFight();
                if (f == null || f.FightId != fightId) return;
                if (f.State != FightState.Ongoing) return;
                if (f.CurrentFighter == null || f.CurrentFighter.Id != fighterId) return;
                if (f.RoundNumber != round) return;
                Program.LogDebug($"[FightHandler] ⏰ Fighter #{fighterId} ran out of time. Passing the turn automatically.");

                try
                {
                    await EndTurnAsync(stream, f.CurrentFighter);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[FightHandler] Error while forcing the end of the turn: {ex.Message}");
                }
            });
        }

        public static async Task EndTurnAsync(NetworkStream stream, Fighter endingFighter)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            // The turn is over: cancel the timer so it cannot force a second end of turn.
            fight.CancelTurnTimer();

            var jwkMsg = new ProtoMessage();
            jwkMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = endingFighter.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwk", jwkMsg.ToByteArray()));

            var nextFighter = fight.NextTurn();
            if (nextFighter == null || fight.State == FightState.Ended)
            {
                await SendFightEnd(stream, fight);
                return;
            }

            if (fight.StartsNewRound)
            {
                var jwbMsg = new ProtoMessage();
                jwbMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fight.RoundNumber });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwb", jwbMsg.ToByteArray()));
            }

            var jwuMsg = new ProtoMessage();
            jwuMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = nextFighter.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwu", jwuMsg.ToByteArray()));

            var juuMsg = new ProtoMessage();
            juuMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = nextFighter.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/juu", juuMsg.ToByteArray()));
            Program.LogDebug($"[FightHandler] Sent juu (Wait Turn Ack) for Fighter #{nextFighter.Id} ({nextFighter.Name}).");
        }

        private static async Task HandlePassTurnRequest(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            var current = fight.CurrentFighter;
            if (current != null)
            {
                Program.LogDebug($"[FightHandler] Player requested Pass Turn (jxw) for Fighter #{current.Id}.");
                await EndTurnAsync(stream, current);
            }
        }

        public static List<int> GenerateSimplePath(int startCell, int targetCell)
        {
            var path = new List<int> { startCell };
            int startX = startCell % 14;
            int startY = startCell / 14;
            int targetX = targetCell % 14;
            int targetY = targetCell / 14;

            int currX = startX;
            int currY = startY;
            while (currX != targetX || currY != targetY)
            {
                if (currX < targetX) currX++;
                else if (currX > targetX) currX--;

                if (currY < targetY) currY++;
                else if (currY > targetY) currY--;

                int cell = currY * 14 + currX;
                path.Add(cell);
                if (path.Count > 20) break;
            }
            return path;
        }

        /// <summary>
        /// Builds the joo (movement broadcast) exactly as the official server emits it:
        ///   joo { f1 = fighterId, f2 = &lt;PACKED path&gt;, f5 = final orientation }
        /// Field 2 is a packed repeated int32: the cell varints are concatenated WITHOUT tags.
        /// Writing them as tagged fields (08 xx 08 xx ...) corrupts the path, because the client
        /// reads the 0x08 as just another cell number.
        /// Verified against the capture: f2 = ac03 ab03 b803 c603 ... for [428,427,440,454,...].
        /// </summary>
        public static byte[] BuildJooMovementPacket(long fighterId, List<int> pathCells, int orientation = 3)
        {
            using var packed = new MemoryStream();
            foreach (var c in pathCells)
            {
                ProtoMessage.WriteVarInt(packed, (ulong)c);
            }

            var jooMsg = new ProtoMessage();
            jooMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fighterId });
            jooMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = packed.ToArray() });
            jooMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = orientation });

            return BuildGameNodePacket("type.ankama.com/joo", jooMsg.ToByteArray());
        }

        /// <summary>
        /// Variation of a combat characteristic (AP = 1, MP = 23, health = 19).
        ///
        /// The three fields of the value block are OPTIONAL, and the client tells "present with a
        /// value of zero" apart from "absent". The official capture makes it plain: during the turn
        /// it sends {f2 = -accumulated loss, f4 = maximum, f8 = loss}, but when the points are
        /// restored it sends ONLY {f4 = maximum}. Writing "f2 = 0" is not the same as leaving it
        /// out: the client reads it as "apply a variation of zero" and leaves the counter where it
        /// was. That is why AP/MP stayed at zero when your turn came round again.
        /// </summary>
        public static byte[] BuildJvmPacket(long fighterId, int statId, int accumulatedDelta, int maxStatValue)
        {
            var f8Sub = new ProtoMessage();
            if (accumulatedDelta != 0)
            {
                f8Sub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = accumulatedDelta });
            }
            f8Sub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = maxStatValue });
            if (accumulatedDelta != 0)
            {
                f8Sub.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 0, VarIntValue = Math.Abs(accumulatedDelta) });
            }

            var f4Inner = new ProtoMessage();
            f4Inner.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = f8Sub.ToByteArray() });
            f4Inner.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = statId });

            var f3Sub = new ProtoMessage();
            f3Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 2 });
            f3Sub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = f4Inner.ToByteArray() });

            var jvmMsg = new ProtoMessage();
            jvmMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighterId });
            jvmMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = f3Sub.ToByteArray() });

            return BuildGameNodePacket("type.ankama.com/jvm", jvmMsg.ToByteArray());
        }

        /// <summary>
        /// "Cast spell" action (f13 = 300). f5 identifies the spell with TWO ids: f1 is the
        /// SpellLevels row (the specific level) and f4 the spell id. f1 used to be hardcoded to
        /// 41870, which is level 1 of Magic Arrow: every other spell reached the client carrying a
        /// level that did not belong to it.
        /// </summary>
        public static byte[] BuildJtxSpellCastPacket(long casterId, int targetCell, long spellId, int spellLevelId, long targetId, int launchIndex)
        {
            var f5Sub = new ProtoMessage();
            f5Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = spellLevelId > 0 ? spellLevelId : spellId });
            f5Sub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = spellId });

            // f7 only carries the caster and the cast index. There used to be 13 bytes of zero
            // padding here, in a field 1 that does not exist in the real message; that alone was
            // enough for the client to drop the whole action and show no animation at all.
            var f7Sub = new ProtoMessage();
            f7Sub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = casterId });
            f7Sub.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = launchIndex });

            var f34Msg = new ProtoMessage();
            f34Msg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 1 });
            f34Msg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = targetCell });
            f34Msg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 2, BytesValue = f5Sub.ToByteArray() });
            f34Msg.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 2, BytesValue = f7Sub.ToByteArray() });
            f34Msg.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 0, VarIntValue = targetId });

            var jtxMsg = new ProtoMessage();
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 13, WireType = 0, VarIntValue = 300 });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 29, WireType = 0, VarIntValue = casterId });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 34, WireType = 2, BytesValue = f34Msg.ToByteArray() });

            return BuildGameNodePacket("type.ankama.com/jtx", jtxMsg.ToByteArray());
        }

        /// <summary>
        /// "Life point loss" action (f13 = 99). The damage travels in f25, NOT in f6.
        ///
        /// Inside f25 the damage is field 5 and the element is field 1 — not the other way round.
        /// They were swapped, so the client always drew and applied the fixed value that field 5
        /// happened to carry (a 7) while the server subtracted the real health: the monster's bar
        /// went down 7 at a time and the fight ended all of a sudden with the creature still at
        /// full health on screen. The official capture confirms it twice: spell 13425 deals 7-9
        /// fire damage and sends f1=2 (fire) with f5=7 (the roll).
        /// </summary>
        public static byte[] BuildJtxDamagePacket(long casterId, long targetId, int damageDealt, int elementId)
        {
            var f25Sub = new ProtoMessage();
            f25Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = elementId });
            f25Sub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = targetId });
            f25Sub.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = damageDealt });

            var jtxMsg = new ProtoMessage();
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 13, WireType = 0, VarIntValue = 99 });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 25, WireType = 2, BytesValue = f25Sub.ToByteArray() });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 29, WireType = 0, VarIntValue = casterId });

            return BuildGameNodePacket("type.ankama.com/jtx", jtxMsg.ToByteArray());
        }

        /// <summary>
        /// "Fighter killed" action (f13 = 103). Without it the client never considers anyone dead:
        /// the monster stayed on its feet and the end-of-fight screen counted zero enemies
        /// defeated. In the official capture it comes right after the killing blow (frame 313).
        /// </summary>
        public static byte[] BuildJtxDeathPacket(long killerId, long deadFighterId)
        {
            var f2Sub = new ProtoMessage();
            f2Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = deadFighterId });

            var jtxMsg = new ProtoMessage();
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = f2Sub.ToByteArray() });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 13, WireType = 0, VarIntValue = 103 });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 29, WireType = 0, VarIntValue = killerId });

            return BuildGameNodePacket("type.ankama.com/jtx", jtxMsg.ToByteArray());
        }

        /// <summary>
        /// "Action point loss" action (f13 = 102) used after casting a spell. This is the one that
        /// draws the floating "-N AP" over the caster; jvm only updates the counter.
        /// </summary>
        /// <summary>
        /// Point loss action: 102 for AP and 129 for MP. This is the one that draws the floating
        /// "-N" over the fighter and writes the line into the combat log; jvm only moves the
        /// counter, without announcing anything.
        ///
        /// <paramref name="victimId"/> and <paramref name="casterId"/> match when the cost is
        /// self-inflicted (casting a spell) and differ when someone else strips the points off you.
        /// </summary>
        public static byte[] BuildJtxPointLossPacket(long victimId, long casterId, int amount, bool isMp = false)
        {
            var f6Sub = new ProtoMessage();
            f6Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = victimId });
            f6Sub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = -amount });

            var jtxMsg = new ProtoMessage();
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 2, BytesValue = f6Sub.ToByteArray() });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 13, WireType = 0, VarIntValue = isMp ? 129 : 102 });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 29, WireType = 0, VarIntValue = casterId });

            return BuildGameNodePacket("type.ankama.com/jtx", jtxMsg.ToByteArray());
        }

        public static byte[] BuildJtxApLossPacket(long casterId, int apLost)
            => BuildJtxPointLossPacket(casterId, casterId, apLost);

        // There used to be a "life variation" jvm here on characteristic 19. It did nothing: the
        // health bar is moved by the damage jtx itself, and 19 is not health. It is removed rather
        // than leaving a made-up message in circulation.

        private static byte[] BuildJud(int kind, long fighterId)
        {
            var m = new ProtoMessage();
            m.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = kind });
            m.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighterId });
            return BuildGameNodePacket("type.ankama.com/jud", m.ToByteArray());
        }

        private static byte[] BuildJuc(int kind, long fighterId)
        {
            var m = new ProtoMessage();
            m.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = kind });
            m.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighterId });
            m.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
            return BuildGameNodePacket("type.ankama.com/juc", m.ToByteArray());
        }

        /// <summary>
        /// Casts of each spell during the current turn, per spell and per target. The client reads
        /// that number straight from the cast packet (f7.f5) and compares it against the spell's
        /// limit to grey the icon out.
        ///
        /// There used to be a single global counter here that was never reset: by the third or
        /// fourth cast of the fight the client already believed Frozen Arrow's 3 casts per turn
        /// were spent and disabled it, even when it was the first cast of that turn.
        /// </summary>
        private static readonly Dictionary<long, int> _castsThisTurn = new Dictionary<long, int>();
        private static readonly Dictionary<(long Spell, long Target), int> _castsPerTargetThisTurn
            = new Dictionary<(long, long), int>();

        public static void ResetTurnCastCounters()
        {
            _castsThisTurn.Clear();
            _castsPerTargetThisTurn.Clear();
        }

        private static async Task HandleSpellCastRequest(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            var current = fight.CurrentFighter;
            if (current == null || current.IsMonster) return;

            long spellId = 0;
            int targetCell = -1;
            try
            {
                var inner = ExtractMessagePayload(payload, "type.ankama.com/jub");
                if (inner != null)
                {
                    // By field NUMBER, not by position: a weapon hit arrives as { f2 = cell }
                    // with no field 1, and reading by position took the cell as the spell id and
                    // rejected the whole request.
                    var jubMsg = ProtoMessage.Parse(inner);
                    foreach (var f in jubMsg.Fields)
                    {
                        if (f.WireType != 0) continue;
                        if (f.FieldNumber == 1) spellId = f.VarIntValue;
                        else if (f.FieldNumber == 2) targetCell = (int)f.VarIntValue;
                    }
                }
            }
            catch { }

            if (targetCell < 0)
            {
                Program.LogDebug("[FightHandler] Cast request with no target cell; discarding it.");
                return;
            }

            // No spell id = a hit with the equipped WEAPON. That is exactly how the client sends
            // it, with the cell alone, and until now it was rejected outright.
            bool isWeapon = spellId <= 0;
            var spellData = isWeapon
                ? DatabaseManager.GetEquippedWeaponAsSpell(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId)
                : DatabaseManager.GetSpellCombatData((int)spellId, current.Level);

            if (spellData == null)
            {
                Program.LogDebug(isWeapon
                    ? "[FightHandler] Weapon hit rejected: no equipped weapon deals damage."
                    : $"[FightHandler] Rejected spell cast: spell {spellId} data not found in DB.");
                return;
            }

            if (current.CurrentAP < spellData.APCost)
            {
                Program.LogDebug($"[FightHandler] Player has insufficient AP ({current.CurrentAP}/{spellData.APCost}) for spell {spellId}.");
                return;
            }

            int distToTarget = MapGeometry.Distance(current.CellId, targetCell);
            if (distToTarget < spellData.MinRange || distToTarget > spellData.MaxRange)
            {
                Program.LogDebug($"[FightHandler] Spell {spellId} out of range ({distToTarget} cells, range {spellData.MinRange}-{spellData.MaxRange}).");
                return;
            }

            if (spellData.NeedsLineOfSight &&
                !MapGeometry.HasLineOfSight(current.CellId, targetCell, MapManager.GetLosBlockers(fight.ArenaMapId)))
            {
                Program.LogDebug($"[FightHandler] Spell {spellId} has no line of sight from {current.CellId} to {targetCell}.");
                return;
            }

            var target = fight.Team1.FirstOrDefault(m => m.IsAlive && (m.CellId == targetCell || MapGeometry.Distance(m.CellId, targetCell) <= 1));
            long targetId = target != null ? target.Id : -1;

            // Cast limits, exactly as the spell declares them in the database.
            _castsThisTurn.TryGetValue(spellId, out int castsDone);
            if (spellData.MaxCastPerTurn > 0 && castsDone >= spellData.MaxCastPerTurn)
            {
                Program.LogDebug($"[FightHandler] Spell {spellId} already spent this turn ({castsDone}/{spellData.MaxCastPerTurn}).");
                return;
            }

            var perTargetKey = (spellId, targetId);
            _castsPerTargetThisTurn.TryGetValue(perTargetKey, out int castsOnTarget);
            if (targetId != -1 && spellData.MaxCastPerTarget > 0 && castsOnTarget >= spellData.MaxCastPerTarget)
            {
                Program.LogDebug($"[FightHandler] Spell {spellId} already spent on that target ({castsOnTarget}/{spellData.MaxCastPerTarget}).");
                return;
            }

            current.AccumulatedApLoss += spellData.APCost;
            current.CurrentAP -= spellData.APCost;

            castsDone++;
            _castsThisTurn[spellId] = castsDone;
            if (targetId != -1) _castsPerTargetThisTurn[perTargetKey] = castsOnTarget + 1;

            Program.LogDebug($"[FightHandler] {(isWeapon ? "Weapon hit" : $"Player cast spell {spellId}")} " +
                             $"on cell {targetCell} (costs {spellData.APCost} AP, {current.CurrentAP} left, " +
                             $"cast {castsDone}" +
                             $"{(spellData.MaxCastPerTurn > 0 ? "/" + spellData.MaxCastPerTurn : "")} of the turn).");

            // Sequence traced from the official capture (frame 254):
            //   jud(4) -> jtx(300 cast) -> jud(3) -> jvm(AP) -> juc(3) -> jtx(102 AP loss)
            //   -> jtx(99 damage) -> juc(4)
            //
            // The cast jtx is not sent for a weapon hit: there is no capture of a weapon attack
            // and it is unknown how the client encodes that action. Only the AP cost and the
            // damage go out, which are both verified. The sword swing animation will be missing.
            await WriteFrameAsync(stream, BuildJud(4, current.Id));
            if (!isWeapon)
            {
                await WriteFrameAsync(stream, BuildJtxSpellCastPacket(current.Id, targetCell, spellId, spellData.SpellLevelId, targetId, castsDone));
            }

            await WriteFrameAsync(stream, BuildJud(3, current.Id));
            await WriteFrameAsync(stream, BuildJvmPacket(current.Id, 1, -current.AccumulatedApLoss, current.MaxAP));
            await WriteFrameAsync(stream, BuildJuc(3, current.Id));
            await WriteFrameAsync(stream, BuildJtxApLossPacket(current.Id, spellData.APCost));

            if (target != null)
            {
                await ApplySpellEffectsAsync(stream, fight, current, spellData, target);
            }

            await WriteFrameAsync(stream, BuildJuc(4, current.Id));

            fight.CheckFightEnd();
            if (fight.State == FightState.Ended)
            {
                await SendFightEnd(stream, fight);
            }
        }

        /// <summary>
        /// Applies EVERY effect of a spell to a target and reports them to the client. Player
        /// casts and monster casts both go through it, so that a piwi stripping range does exactly
        /// what the character would do with the same spell.
        ///
        /// Covers damage (per element), displacement (push/pull) and any effect that modifies a
        /// characteristic. That last group comes from the effect catalogue imported from the
        /// client: there is no hand-written list of effects anywhere.
        ///
        /// What it still does NOT do: the dodge roll. In Dofus, stripping AP or MP is resolved by
        /// pitting the caster's "withdrawal" against the target's "dodge", and the character's
        /// withdrawal is not being computed from the gear, so for now the effect is applied in
        /// full. The structure is already in place to add the roll once that value is read from
        /// the gear.
        /// </summary>
        private static async Task<int> ApplySpellEffectsAsync(
            NetworkStream stream, FightInstance fight, Fighter caster, SpellCombatData spell, Fighter target)
        {
            int damageDealt = 0;

            if (spell.BaseDamageMin > 0 || spell.BaseDamageMax > 0)
            {
                var element = (ElementType)spell.Element;

                // Critical hit: the spell's own probability plus whatever critical the gear adds.
                // On a critical the damage is NOT multiplied; the critical range carried by the
                // spell itself is used instead (Frozen Arrow goes from 12-14 to 15-17).
                int criticalChance = spell.CriticalHitProbability + caster.CriticalBonus;
                bool isCritical = spell.HasCriticalDamage && criticalChance > 0 && _lootRandom.Next(100) < criticalChance;

                int minBase = isCritical ? spell.CriticalDamageMin : spell.BaseDamageMin;
                int maxBase = isCritical ? spell.CriticalDamageMax : spell.BaseDamageMax;

                // Base damage bonus the spell left on itself during an earlier cast (effect 293).
                // It adds to the BASE damage, before multiplying by the characteristic: Frozen
                // Arrow goes from 12-14 to 16-18 on the second cast.
                int baseBonus = caster.GetSpellDamageBonus((int)spell.SpellId, fight.RoundNumber);
                int baseDamageRoll = ((minBase + maxBase) / 2) + baseBonus;

                damageDealt = DamageCalculator.CalculateDamage(
                    baseDamage: baseDamageRoll,
                    element: element,
                    statValue: caster.GetStatForElement(element),
                    power: caster.Power,
                    flatElementDamage: 0,
                    flatDamage: 0,
                    targetResPct: target.GetResPctForElement(element),
                    targetFlatRes: 0);

                target.TakeDamage(damageDealt);
                Program.LogDebug($"[FightHandler] {caster.Name} deals {damageDealt} damage to {target.Name} " +
                                 $"(element {spell.Element}, base {baseDamageRoll}" +
                                 $"{(baseBonus != 0 ? $" including +{baseBonus} from the effect" : "")}" +
                                 $"{(isCritical ? $", CRITICAL at {criticalChance} %" : "")}). " +
                                 $"HP: {target.CurrentHP}/{target.MaxHP}");

                await WriteFrameAsync(stream, BuildJtxDamagePacket(caster.Id, target.Id, damageDealt, spell.Element));

                if (!target.IsAlive)
                {
                    await WriteFrameAsync(stream, BuildJtxDeathPacket(caster.Id, target.Id));
                    Program.LogDebug($"[FightHandler] {target.Name} has fallen.");
                }
            }

            // Bonuses the spell leaves on the CASTER for later uses. Casting it again refreshes
            // the duration instead of stacking a second time: Frozen Arrow's maximum stack is 1.
            foreach (var buff in spell.DamageBuffs)
            {
                caster.ApplySpellDamageBuff(buff.SpellId, buff.Bonus, buff.Duration, fight.RoundNumber);
                Program.LogDebug($"[FightHandler]   {caster.Name} gains +{buff.Bonus} base damage on spell " +
                                 $"{buff.SpellId} for {buff.Duration} turn(s).");
            }

            // Characteristic effects: AP removal (effect 1079 of Frozen Arrow), range removal
            // (effect 116, the one the piwi carries), and so on.
            foreach (var se in spell.StatEffects)
            {
                if (se.Characteristic == 1)
                {
                    int lost = Math.Min(Math.Abs(se.Value), target.CurrentAP);
                    if (lost <= 0) continue;
                    target.CurrentAP -= lost;
                    target.AccumulatedApLoss += lost;
                    await WriteFrameAsync(stream, BuildJud(3, target.Id));
                    await WriteFrameAsync(stream, BuildJvmPacket(target.Id, 1, -target.AccumulatedApLoss, target.MaxAP));
                    await WriteFrameAsync(stream, BuildJuc(3, target.Id));
                    await WriteFrameAsync(stream, BuildJtxPointLossPacket(target.Id, caster.Id, lost));
                    Program.LogDebug($"[FightHandler]   effect {se.EffectId}: -{lost} AP on {target.Name}.");
                }
                else if (se.Characteristic == 23)
                {
                    int lost = Math.Min(Math.Abs(se.Value), target.CurrentMP);
                    if (lost <= 0) continue;
                    target.CurrentMP -= lost;
                    target.AccumulatedMpLoss += lost;
                    await WriteFrameAsync(stream, BuildJud(3, target.Id));
                    await WriteFrameAsync(stream, BuildJvmPacket(target.Id, 23, -target.AccumulatedMpLoss, target.MaxMP));
                    await WriteFrameAsync(stream, BuildJuc(3, target.Id));
                    await WriteFrameAsync(stream, BuildJtxPointLossPacket(target.Id, caster.Id, lost, isMp: true));
                    Program.LogDebug($"[FightHandler]   effect {se.EffectId}: -{lost} MP on {target.Name}.");
                }
                else
                {
                    // Every other characteristic (range, power, resistances...): the client only
                    // needs the variation; the server does not use them in its own maths yet.
                    await WriteFrameAsync(stream, BuildJud(3, target.Id));
                    await WriteFrameAsync(stream, BuildJvmPacket(target.Id, se.Characteristic, se.Value, 0));
                    await WriteFrameAsync(stream, BuildJuc(3, target.Id));
                    Program.LogDebug($"[FightHandler]   effect {se.EffectId}: {se.Value} on characteristic {se.Characteristic} of {target.Name}.");
                }
            }

            // Displacement. With no capture of a real push, the same joo that walks a fighter
            // along a path is reused: the animation will not be a shove, but the monster ends up
            // on the right cell instead of staying nailed to the spot.
            if (spell.PushDistance != 0 && target.IsAlive)
            {
                var walkable = MapManager.GetFightWalkable(fight.ArenaMapId);
                var occupied = new HashSet<int>(fight.Team0.Concat(fight.Team1)
                    .Where(f => f.IsAlive && f.Id != target.Id).Select(f => f.CellId));

                var pushPath = MapGeometry.ComputePush(caster.CellId, target.CellId, spell.PushDistance, walkable, occupied);
                if (pushPath.Count > 1)
                {
                    target.CellId = pushPath[pushPath.Count - 1];
                    await WriteFrameAsync(stream, BuildJud(3, target.Id));
                    await WriteFrameAsync(stream, BuildJooMovementPacket(target.Id, pushPath));
                    await WriteFrameAsync(stream, BuildJuc(3, target.Id));
                    Program.LogDebug($"[FightHandler]   displacement: {target.Name} moves to cell {target.CellId} " +
                                     $"({pushPath.Count - 1} of {Math.Abs(spell.PushDistance)} cells).");
                }
            }

            return damageDealt;
        }

        private static async Task HandleCombatMoveRequest(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;
            var current = fight.CurrentFighter;
            if (current == null || current.Id != Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId) return;

            var inner = ExtractMessagePayload(payload, "type.ankama.com/jyz");
            if (inner == null) inner = ExtractMessagePayload(payload, "type.ankama.com/joi");

            var vertices = new List<int>();
            if (inner != null)
            {
                try
                {
                    var msg = ProtoMessage.Parse(inner);
                    foreach (var f in msg.Fields)
                    {
                        if (f.FieldNumber == 3)
                        {
                            if (f.WireType == 0)
                            {
                                int val = (int)f.VarIntValue;
                                vertices.Add(val % 4096);
                            }
                            else if (f.WireType == 2)
                            {
                                int pos = 0;
                                while (pos < f.BytesValue.Length)
                                {
                                    int val = (int)ReadVarInt(f.BytesValue, ref pos);
                                    vertices.Add(val % 4096);
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            if (vertices.Count == 0) return;

            // COMBAT walkability, not the one in map_walkable_cells.json: that one trims the map
            // borders (it was generated to place mobs in roleplay) and left out the arena's outer
            // ring, which you can perfectly well walk on during a fight.
            var arenaWalkable = MapManager.GetFightWalkable(fight.ArenaMapId);
            var expandedPath = MapGeometry.ExpandPath(vertices, arenaWalkable);

            if (expandedPath.Count <= 1) return;

            int steps = Math.Min(expandedPath.Count - 1, current.CurrentMP);
            var actualPath = expandedPath.Take(steps + 1).ToList();

            current.AccumulatedMpLoss += steps;
            current.CurrentMP -= steps;
            current.CellId = actualPath.Last();

            Program.LogDebug($"[FightHandler] Combat move for Player #{current.Id}: {actualPath.Count} cells to cell {current.CellId} (used {steps} MP, {current.CurrentMP} MP left).");

            var jud4Start = new ProtoMessage();
            jud4Start.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 4 });
            jud4Start.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jud", jud4Start.ToByteArray()));

            await WriteFrameAsync(stream, BuildJooMovementPacket(current.Id, actualPath));

            var jud3 = new ProtoMessage();
            jud3.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 3 });
            jud3.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jud", jud3.ToByteArray()));

            await WriteFrameAsync(stream, BuildJvmPacket(current.Id, 23, -current.AccumulatedMpLoss, current.MaxMP));

            var juc3 = new ProtoMessage();
            juc3.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 3 });
            juc3.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current.Id });
            juc3.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/juc", juc3.ToByteArray()));

            var jtxMsg = new ProtoMessage();
            var f6Sub = new ProtoMessage();
            f6Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = current.Id });
            f6Sub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = -steps });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 2, BytesValue = f6Sub.ToByteArray() });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 13, WireType = 0, VarIntValue = 129 });
            jtxMsg.Fields.Add(new ProtoField { FieldNumber = 29, WireType = 0, VarIntValue = current.Id });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jtx", jtxMsg.ToByteArray()));

            var juc4End = new ProtoMessage();
            juc4End.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 4 });
            juc4End.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current.Id });
            juc4End.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/juc", juc4End.ToByteArray()));
        }

        private static async Task RunMonsterTurnAsync(NetworkStream stream, Fighter monster)
        {
            var fight = GetCurrentFight();
            if (fight == null || !monster.IsMonster || !monster.IsAlive) return;
            Program.LogDebug($"[FightHandler] Running AI turn for Monster #{monster.Id} ({monster.Name})...");

            var arenaWalkable = MapManager.GetFightWalkable(fight.ArenaMapId);
            var losBlockers = MapManager.GetLosBlockers(fight.ArenaMapId);

            var turnResult = MonsterAI.ExecuteTurn(
                monster,
                fight.Team0.Concat(fight.Team1).ToList(),
                arenaWalkable,
                (spellId) =>
                {
                    // The spell grade comes from the monster's own sheet (spellGrades), not from its level.
                    int grade = monster.SpellGrades.TryGetValue(spellId, out var g) ? g : 1;
                    var sData = DatabaseManager.GetSpellCombatData(spellId, grade);
                    if (sData == null) return null;
                    return new MonsterAI.AISpellData
                    {
                        SpellId = (int)spellId,
                        APCost = sData.APCost,
                        MinRange = sData.MinRange,
                        MaxRange = sData.MaxRange,
                        BaseDamageMin = sData.BaseDamageMin,
                        BaseDamageMax = sData.BaseDamageMax,
                        Element = sData.Element,
                        NeedsLineOfSight = sData.NeedsLineOfSight,
                        MaxCastPerTurn = sData.MaxCastPerTurn
                    };
                },
                losBlockers
            );

            // Order matters: if the monster attacked and THEN fled, that is the order it has to be
            // sent in. The other way round, the client draws the shot from the escape cell and it
            // looks as if the monster attacked from much farther away than its spell allows.
            if (turnResult.CastBeforeMove)
            {
                if (await SendMonsterCastAsync(stream, fight, monster, turnResult)) return;
                await SendMonsterMoveAsync(stream, monster, turnResult);
            }
            else
            {
                await SendMonsterMoveAsync(stream, monster, turnResult);
                if (await SendMonsterCastAsync(stream, fight, monster, turnResult)) return;
            }

            await EndTurnAsync(stream, monster);
        }

        private static async Task SendMonsterMoveAsync(NetworkStream stream, Fighter monster, MonsterTurnResult turnResult)
        {
            if (turnResult.PathCells.Count <= 1) return;

            Program.LogDebug($"[FightHandler] Monster #{monster.Id} walks {turnResult.PathCells.Count - 1} cell(s) to cell {monster.CellId}.");

            await WriteFrameAsync(stream, BuildJud(4, monster.Id));
            await WriteFrameAsync(stream, BuildJooMovementPacket(monster.Id, turnResult.PathCells));
            await WriteFrameAsync(stream, BuildJud(3, monster.Id));
            await WriteFrameAsync(stream, BuildJvmPacket(monster.Id, 23, -monster.AccumulatedMpLoss, monster.MaxMP));
            await WriteFrameAsync(stream, BuildJuc(3, monster.Id));
            await WriteFrameAsync(stream, BuildJuc(4, monster.Id));
        }

        /// <summary>Returns true if the fight is over and has already been reported.</summary>
        private static async Task<bool> SendMonsterCastAsync(
            NetworkStream stream, FightInstance fight, Fighter monster, MonsterTurnResult turnResult)
        {
            if (turnResult.SpellId == 0) return false;

            var target = fight.Team0.Concat(fight.Team1).FirstOrDefault(p => p.Id == turnResult.TargetFighterId);
            int grade = monster.SpellGrades.TryGetValue(turnResult.SpellId, out var mg) ? mg : 1;
            var monSpell = DatabaseManager.GetSpellCombatData(turnResult.SpellId, grade);
            if (target == null || monSpell == null) return false;

            // The cell the spell was cast FROM, not the current one: if the monster attacked and
            // then fled, its CellId is already the destination.
            int fromCell = turnResult.CastFromCell >= 0 ? turnResult.CastFromCell : monster.CellId;
            int d = MapGeometry.Distance(fromCell, target.CellId);
            int castCount = Math.Max(1, turnResult.CastCount);
            Program.LogDebug($"[FightHandler] Monster #{monster.Id} casts spell {turnResult.SpellId} " +
                             $"{castCount} time(s) on {target.Name} from cell {fromCell} " +
                             $"(distance {d}, range {monSpell.MinRange}-{monSpell.MaxRange}).");

            // Exactly the same sequence as a player cast, effect application included: that way a
            // monster that pushes or strips AP does the same thing the character would do with
            // that spell.
            for (int i = 1; i <= castCount; i++)
            {
                await WriteFrameAsync(stream, BuildJud(4, monster.Id));
                await WriteFrameAsync(stream, BuildJtxSpellCastPacket(monster.Id, turnResult.TargetCellId, turnResult.SpellId, monSpell.SpellLevelId, target.Id, i));

                await WriteFrameAsync(stream, BuildJud(3, monster.Id));
                await WriteFrameAsync(stream, BuildJvmPacket(monster.Id, 1, -monster.AccumulatedApLoss, monster.MaxAP));
                await WriteFrameAsync(stream, BuildJuc(3, monster.Id));
                if (monSpell.APCost > 0)
                {
                    await WriteFrameAsync(stream, BuildJtxApLossPacket(monster.Id, monSpell.APCost));
                }

                await ApplySpellEffectsAsync(stream, fight, monster, monSpell, target);

                await WriteFrameAsync(stream, BuildJuc(4, monster.Id));

                fight.CheckFightEnd();
                if (fight.State == FightState.Ended)
                {
                    await SendFightEnd(stream, fight);
                    return true;
                }
                if (!target.IsAlive) break;
            }
            return false;
        }

        // =========================================================================
        // PACKET BUILDERS AND SENDERS (100% Organic Protobuf Construction)
        // =========================================================================

        public static byte[] BuildJpfPacket(long mobContextId)
        {
            int subAreaId = 450;
            var fight = GetCurrentFight();
            long mId = fight != null ? fight.RoleplayMapId : Jondo.Unity.Launcher.Network.SessionContext.State.MapId;
            if (MapManager.Maps.TryGetValue(mId, out var mInfo) && mInfo.SubAreaId != 0)
            {
                subAreaId = mInfo.SubAreaId;
            }

            var jpfSub = new ProtoMessage();

            var f1Sub = new ProtoMessage();
            f1Sub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = subAreaId });
            f1Sub.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 5 });
            jpfSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = f1Sub.ToByteArray() });

            var boneSub = new ProtoMessage();
            boneSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3273 });
            boneSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 3 });
            boneSub.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = 3 });

            var f3Sub = new ProtoMessage();
            f3Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = boneSub.ToByteArray() });

            var actorSub = new ProtoMessage();
            actorSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = -1 });
            actorSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = f3Sub.ToByteArray() });
            actorSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });

            var lookSub = new ProtoMessage();
            lookSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 3256 });
            lookSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3 });

            var f2Sub = new ProtoMessage();
            f2Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = lookSub.ToByteArray() });
            f2Sub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = actorSub.ToByteArray() });

            jpfSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = f2Sub.ToByteArray() });
            jpfSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = mobContextId });

            var jpfMsg = new ProtoMessage();
            jpfMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = jpfSub.ToByteArray() });

            return BuildGameNodePacket("type.ankama.com/jpf", jpfMsg.ToByteArray());
        }

        public static byte[] BuildKkzPacket(int cellId, long fighterId, int direction)
        {
            var kkzSub = new ProtoMessage();
            kkzSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = cellId });
            kkzSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = fighterId });
            kkzSub.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = direction });

            var kkzMsg = new ProtoMessage();
            kkzMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = kkzSub.ToByteArray() });

            return BuildGameNodePacket("type.ankama.com/kkz", kkzMsg.ToByteArray());
        }

        public static byte[] BuildKkzAllPacket(FightInstance fight)
        {
            var kkzMsg = new ProtoMessage();

            foreach (var f in fight.Team0.Concat(fight.Team1))
            {
                int dir = f.TeamId == 0 ? 3 : 7;
                var kkzSub = new ProtoMessage();
                kkzSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = f.CellId });
                kkzSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = f.Id });
                kkzSub.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = dir });

                kkzMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = kkzSub.ToByteArray() });
            }

            return BuildGameNodePacket("type.ankama.com/kkz", kkzMsg.ToByteArray());
        }

        public static List<byte[]> BuildPlacementPossiblePositionsPackets(FightInstance fight)
        {
            var list = new List<byte[]>();

            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterName);

            var lookBreedSub = new ProtoMessage();
            lookBreedSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = nameBytes });
            lookBreedSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.Breed });

            var memberSub = new ProtoMessage();
            memberSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fight.ChallengerLeaderId });
            memberSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = lookBreedSub.ToByteArray() });

            var memberOuter = new ProtoMessage();
            memberOuter.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = memberSub.ToByteArray() });

            // Send jyf #1 (Team 0: Player Team)
            var msg1 = new ProtoMessage();
            var team0Wrapper = new ProtoMessage();
            team0Wrapper.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fight.ChallengerLeaderId });
            team0Wrapper.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = 1 });
            team0Wrapper.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 2, BytesValue = memberOuter.ToByteArray() });

            msg1.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = team0Wrapper.ToByteArray() });
            msg1.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 300 });
            list.Add(BuildGameNodePacket("type.ankama.com/jyf", msg1.ToByteArray()));

            // Send jyf #2 (Team 1: Monster Team)
            var msg2 = new ProtoMessage();
            var team1Wrapper = new ProtoMessage();
            team1Wrapper.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fight.DefenderLeaderId });
            team1Wrapper.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });
            team1Wrapper.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = 1 });
            team1Wrapper.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = 1 });

            msg2.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = team1Wrapper.ToByteArray() });
            msg2.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 300 });
            list.Add(BuildGameNodePacket("type.ankama.com/jyf", msg2.ToByteArray()));

            return list;
        }

        private static async Task SendFightStarting(NetworkStream stream, FightInstance fight)
        {
            var msg = new ProtoMessage();
            msg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 300 });
            msg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fight.ChallengerLeaderId });
            msg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 4 });
            msg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = fight.DefenderLeaderId });

            byte[] env = BuildGameNodePacket("type.ankama.com/jya", msg.ToByteArray());
            await WriteFrameAsync(stream, env);
            Program.LogDebug($"[FightHandler] Sent jya (FightStarting) for Challenger={fight.ChallengerLeaderId}, Defender={fight.DefenderLeaderId}.");
        }

        public static byte[] BuildFighterShowBytes(Fighter fighter)
        {
            int cellId = fighter.CellId;
            int dir = fighter.TeamId == 0 ? 3 : 7;
            long fighterId = fighter.Id; // -1, -2 for monsters, CharacterId for player

            // 1. Position submessage: f1=0, f2=cellId, f5=dir
            var posMsg = new ProtoMessage();
            posMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 0 });
            posMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = cellId });
            posMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = dir });
            byte[] posBytes = posMsg.ToByteArray();

            // 2. Fighter inner location: f4 = { f1 = posBytes, f3 = fighterId }
            var fighterInnerLoc = new ProtoMessage();
            fighterInnerLoc.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = posBytes });
            fighterInnerLoc.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = fighterId });

            // 3. Team submessage: f2 = teamId, f3 = 1, f4 = fighterInnerLoc
            var teamMsg = new ProtoMessage();
            teamMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighter.TeamId });
            teamMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
            teamMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = fighterInnerLoc.ToByteArray() });

            // 4. Stats submessage (lgk): 36 canonical entries matching official PCAP
            var statsMsg = new ProtoMessage();
            statsMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 2 });

            void AddStatEntry(int? statId, ProtoMessage valMsg)
            {
                var entry = new ProtoMessage();
                entry.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = valMsg.ToByteArray() });
                if (statId.HasValue)
                {
                    entry.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = statId.Value });
                }
                statsMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = entry.ToByteArray() });
            }

            void AddSimpleVal(int? statId, int val)
            {
                var vSub = new ProtoMessage();
                vSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = val });
                AddStatEntry(statId, vSub);
            }

            void AddBaseBonusVal(int? statId, int baseVal, int bonusVal)
            {
                var vSub = new ProtoMessage();
                if (baseVal != 0) vSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = baseVal });
                if (bonusVal != 0) vSub.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = bonusVal });
                AddStatEntry(statId, vSub);
            }

            // 1. AP (statId 1)
            if (!fighter.IsMonster) AddBaseBonusVal(1, fighter.MaxAP, 0);
            else AddSimpleVal(1, fighter.MaxAP);

            // 2. MP (statId 23)
            if (!fighter.IsMonster) AddBaseBonusVal(23, fighter.MaxMP, 0);
            else AddSimpleVal(23, fighter.MaxMP);

            // 3-6. 37, 33, 35, 36 (empty)
            AddStatEntry(37, new ProtoMessage());
            AddStatEntry(33, new ProtoMessage());
            AddStatEntry(35, new ProtoMessage());
            AddStatEntry(36, new ProtoMessage());

            // 7. 34 (Total HP - 12 for monster, empty for player)
            if (fighter.IsMonster) AddSimpleVal(34, 12);
            else AddStatEntry(34, new ProtoMessage());

            // 8-15. 58, 54, 56, 57, 55, 85, 87, 101 (empty)
            AddStatEntry(58, new ProtoMessage());
            AddStatEntry(54, new ProtoMessage());
            AddStatEntry(56, new ProtoMessage());
            AddStatEntry(57, new ProtoMessage());
            AddStatEntry(55, new ProtoMessage());
            AddStatEntry(85, new ProtoMessage());
            AddStatEntry(87, new ProtoMessage());
            AddStatEntry(101, new ProtoMessage());

            // 16-17. 27, 28 (1 for monster, empty for player)
            if (fighter.IsMonster) { AddSimpleVal(27, 1); AddSimpleVal(28, 1); }
            else { AddStatEntry(27, new ProtoMessage()); AddStatEntry(28, new ProtoMessage()); }

            // 18. 93 (val 3)
            AddSimpleVal(93, 3);

            // 19-20. 79, 78 (empty)
            AddStatEntry(79, new ProtoMessage());
            AddStatEntry(78, new ProtoMessage());

            // 21. 44 (Initiative: player base 5 bonus 12; monster empty)
            if (!fighter.IsMonster) AddBaseBonusVal(44, 5, 12);
            else AddStatEntry(44, new ProtoMessage());

            // 22. STATID 0 = LIFE POINTS / MAX HP! (statId = null -> omitted f5)
            if (!fighter.IsMonster) AddBaseBonusVal(null, StatsHandler.GetPlayerMaxHp(), 0);
            else AddSimpleVal(null, fighter.MaxHP);

            // 23. 11 (Vitality: player bonus; monster empty)
            if (!fighter.IsMonster) AddBaseBonusVal(11, 0, Jondo.Unity.Launcher.Network.SessionContext.State.StatVitality + StatsHandler.GetEquipBonus(11));
            else AddStatEntry(11, new ProtoMessage());

            // 25. 97 (empty)
            AddStatEntry(97, new ProtoMessage());

            // 26-36. 107, 150, 120..125, 141..143 = 100
            AddSimpleVal(107, 100);
            AddSimpleVal(150, 100);
            for (int s = 120; s <= 125; s++) AddSimpleVal(s, 100);
            for (int s = 141; s <= 143; s++) AddSimpleVal(s, 100);

            // 5. Fighter sub-field 3: f1 = teamMsg, f2 = (player ? playerId : 0), f4 = statsMsg, f7 = (monster ? f7Sub : null)
            var fighterSub3 = new ProtoMessage();
            fighterSub3.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = teamMsg.ToByteArray() });
            fighterSub3.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighter.IsMonster ? 0 : fighterId });
            fighterSub3.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = statsMsg.ToByteArray() });

            if (fighter.IsMonster)
            {
                int mId = fighter.MonsterId > 0 ? fighter.MonsterId : 3273;
                int gr = fighter.GradeIndex + 1;
                var f7Inner = new ProtoMessage();
                f7Inner.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = mId });
                f7Inner.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = gr });
                f7Inner.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 3 });

                var f7Outer = new ProtoMessage();
                f7Outer.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = f7Inner.ToByteArray() });
                fighterSub3.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 2, BytesValue = f7Outer.ToByteArray() });
            }
            else
            {
                // Block f9: the PLAYER's sheet (name and level). It is the counterpart of the f7
                // monsters use, and without it the client shows "???" and "Lv. 0" on mouse over.
                // Structure decoded from the capture (a level 2 character):
                //   f9 { f3 { f2 = 1 },
                //        f4 { f2 = <breed>, f3 = 3, f4 = 1, f5 { f2 = <level>, f4 = 3 } },
                //        f6 = -1,
                //        f7 = "<name>" }          <- the character name as raw UTF-8 bytes
                var f9Level = new ProtoMessage();
                f9Level.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighter.Level });
                f9Level.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 3 });

                var f9Breed = new ProtoMessage();
                f9Breed.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.Breed });
                f9Breed.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3 });
                f9Breed.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });
                f9Breed.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 2, BytesValue = f9Level.ToByteArray() });

                var f9Flag = new ProtoMessage();
                f9Flag.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 1 });

                var f9 = new ProtoMessage();
                f9.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = f9Flag.ToByteArray() });
                f9.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = f9Breed.ToByteArray() });
                f9.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = -1 });
                f9.Fields.Add(new ProtoField
                {
                    FieldNumber = 7,
                    WireType = 2,
                    BytesValue = System.Text.Encoding.UTF8.GetBytes(fighter.Name ?? Jondo.Unity.Launcher.Network.SessionContext.State.CharacterName ?? "")
                });

                fighterSub3.Fields.Add(new ProtoField { FieldNumber = 9, WireType = 2, BytesValue = f9.ToByteArray() });
            }

            // 6. Entity details field 2:
            var entityDetails = new ProtoMessage();

            if (!fighter.IsMonster)
            {
                byte[] playerLookBytes = (Jondo.Unity.Launcher.Network.SessionContext.State.LookBytes != null && Jondo.Unity.Launcher.Network.SessionContext.State.LookBytes.Length > 0)
                    ? Jondo.Unity.Launcher.Network.SessionContext.State.LookBytes
                    : NetworkEnvelope.ConvertHexStringToByteArray("08-01-18-03-22-18-A2-8B-9B-0F-CB-E5-F6-15-A4-E1-B9-19-92-A6-C8-20-88-8C-A0-28-F5-B7-CB-34-2A-03-5B-E4-10-42-01-34-32-02-20-01-38-09");
                entityDetails.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = playerLookBytes });
            }
            else
            {
                int boneId = fighter.LookBoneId > 0 ? fighter.LookBoneId : 3256;
                var monsterLookMsg = new ProtoMessage();
                monsterLookMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = boneId });
                monsterLookMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3 });
                entityDetails.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = monsterLookMsg.ToByteArray() });

                var boneSub = new ProtoMessage();
                boneSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = boneId });
                boneSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 3 });
                boneSub.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 3 });

                var boneWrapper = new ProtoMessage();
                boneWrapper.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = boneSub.ToByteArray() });
                entityDetails.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 2, BytesValue = boneWrapper.ToByteArray() });
            }

            entityDetails.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = fighterSub3.ToByteArray() });

            // 7. Outer jxx payload: f2 = { f1 = posBytes, f2 = entityDetails, f3 = fighterId }
            var jxxInnerPayload = new ProtoMessage();
            jxxInnerPayload.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = posBytes });
            jxxInnerPayload.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = entityDetails.ToByteArray() });
            jxxInnerPayload.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = fighterId });

            var jxxOuterPayload = new ProtoMessage();
            jxxOuterPayload.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = jxxInnerPayload.ToByteArray() });

            return BuildGameNodePacket("type.ankama.com/jxx", jxxOuterPayload.ToByteArray());
        }

        private static async Task SendFighterShow(NetworkStream stream, Fighter fighter)
        {
            byte[] packet = BuildFighterShowBytes(fighter);
            await WriteFrameAsync(stream, packet);
            Program.LogDebug($"[FightHandler] Sent organic jxx for {(fighter.IsMonster ? $"Monster ID {fighter.MonsterId} (Fighter ID {fighter.Id}, BoneId {fighter.LookBoneId})" : $"Player ID {fighter.Id}")} at Cell {fighter.CellId}.");
        }

        private static async Task SendPointsVariation(NetworkStream stream, long fighterId, int current, int max, bool isMP)
        {
            // kkz for MP, jys for AP
            string typeUrl = isMP ? "type.ankama.com/kkz" : "type.ankama.com/jys";
            var msg = new ProtoMessage();
            msg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fighterId });
            msg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = current });
            msg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = max });
            byte[] env = BuildGameNodePacket(typeUrl, msg.ToByteArray());
            await WriteFrameAsync(stream, env);
        }

        private static async Task SendLifePointsVariation(NetworkStream stream, long fighterId, int currentHP, int maxHP, int damage)
        {
            var msg = new ProtoMessage();
            msg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fighterId });
            msg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = damage });
            msg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = currentHP });
            msg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = maxHP });
            byte[] env = BuildGameNodePacket("type.ankama.com/jwu", msg.ToByteArray());
            await WriteFrameAsync(stream, env);
        }

        private static readonly Random _lootRandom = new Random();

        /// <summary>
        /// Rolls the loot of every defeated monster and puts it into the inventory.
        ///
        /// Each monster has its own table in MonsterTemplates.drops, with one probability per
        /// grade. The red piwi chief, for instance, drops a red piwi feather at 100 %, sesame
        /// seeds at 18 % and a pouch of lemons at 3 %.
        ///
        /// What is NOT applied yet: prospecting. In the real game the probability is multiplied by
        /// the character's prospecting divided by 100, but prospecting from the gear is not being
        /// computed, so the base percentage is used (equivalent to 100 prospecting).
        /// </summary>
        private static Dictionary<int, int> RollFightLoot(FightInstance fight)
        {
            var loot = new Dictionary<int, int>();

            foreach (var monster in fight.Team1.Where(m => m.IsMonster))
            {
                var table = DatabaseManager.GetMonsterDrops(monster.MonsterId, monster.GradeIndex);
                foreach (var drop in table)
                {
                    if (_lootRandom.NextDouble() * 100.0 >= drop.PercentDrop) continue;
                    loot.TryGetValue(drop.ObjectId, out int q);
                    loot[drop.ObjectId] = q + 1;
                }
            }

            foreach (var kv in loot)
            {
                DatabaseManager.AddItemToInventory(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId, kv.Key, kv.Value);
                Program.LogDebug($"[FightHandler] Loot: item {kv.Key} x{kv.Value} added to the inventory.");
            }

            if (loot.Count > 0)
            {
                Jondo.Unity.Launcher.Network.SessionContext.State.SetInventory(DatabaseManager.LoadInventory(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId));
            }

            return loot;
        }

        private static async Task SendFightEnd(NetworkStream stream, FightInstance fight)
        {
            // The REAL experience of each defeated monster (gradeXp from its sheet), not a made-up
            // formula. It is the same figure the client shows when hovering over the group.
            //
            // What is NOT applied: the adjustment for the level gap between the group and the
            // character, nor the split across several team members. With a single player and no
            // verified formula, the base experience is handed out as is.
            long totalXP = (fight.WinnerTeamId == 0) ? fight.Team1.Sum(m => (long)m.XpReward) : 0;
            int totalKamas = fight.Team1.Sum(m => 10 + (m.Level * 5));

            int previousLevel = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel;
            if (totalXP > 0)
            {
                Jondo.Unity.Launcher.Network.SessionContext.State.Experience += totalXP;
                int newLevel = ExperienceTable.LevelForXp(Jondo.Unity.Launcher.Network.SessionContext.State.Experience);
                if (newLevel > Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel)
                {
                    // 5 characteristic points per level, same as in TotalCapitalForLevel.
                    int levelsGained = newLevel - Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel;
                    Jondo.Unity.Launcher.Network.SessionContext.State.CharacterRemainingPoints += levelsGained * 5;
                    Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel = newLevel;
                    Program.LogDebug($"[FightHandler] Level up! {previousLevel} -> {newLevel} " +
                                     $"(+{levelsGained * 5} characteristic points).");
                }
                DatabaseManager.SaveCurrentCharacter();
                Program.LogDebug($"[FightHandler] +{totalXP} experience (total {Jondo.Unity.Launcher.Network.SessionContext.State.Experience}, " +
                                 $"level {Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel}: from {ExperienceTable.LevelFloor(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel)} " +
                                 $"to {ExperienceTable.NextLevelFloor(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel)}).");
            }

            // jwf = the end-of-fight screen. Field 1 is REPEATED and of message type: one entry
            // per fighter. It used to be sent as a plain varint holding the winning team, so the
            // client could not even decode the message and no screen showed up at all.
            //
            // Structure taken from the official capture (frame 334):
            //   f1 { f1 { f1: 1, f2 { f1{f3=itemId, f4=quantity}..., f3 = kamas } }   <- loot
            //        f3 { f4: 1, f5 = fighterId }
            //        f4: 2 }                                                          <- winner
            //   f1 { f1: {}, f3 { f4: 1, f5 = fighterId } }                           <- loser
            //   f2: -1
            //
            // The f3.f9 experience progress block is left out: the capture gives a single sample
            // and does not let us tell what each number means, so it is omitted instead of being
            // filled in by eye. It is optional; the screen still shows up, minus the XP bar.
            var loot = (fight.WinnerTeamId == 0) ? RollFightLoot(fight) : new Dictionary<int, int>();

            var lootMsg = new ProtoMessage();
            foreach (var kv in loot)
            {
                var itemEntry = new ProtoMessage();
                itemEntry.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = kv.Key });
                itemEntry.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = kv.Value });
                lootMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = itemEntry.ToByteArray() });
            }

            // The kamas go in field 1 of the loot wrapper, not in field 3 of the inner block. The
            // proof is direct: a fixed 1 copied from the capture used to sit there and the
            // end-of-fight screen showed "kamas 1" after a fight that paid 65. Field 3 (3273 in
            // the official capture) is the estimated value of the loot, which the client works
            // out on its own anyway.
            var lootWrap = new ProtoMessage();
            lootWrap.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = totalKamas });
            lootWrap.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = lootMsg.ToByteArray() });

            var jwfMsg = new ProtoMessage();
            foreach (var f in fight.Team0.Concat(fight.Team1))
            {
                bool isWinner = f.TeamId == fight.WinnerTeamId;

                var fighterResult = new ProtoMessage();
                fighterResult.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });
                fighterResult.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = f.Id });

                // Experience progress block, for the player's character only.
                //
                // Structure deduced from three captures with the character at levels 1, 2 and 3,
                // and cross-checked against the client's experience table:
                //   f4 = experience the current level starts at (omitted when 0)
                //   f6 = experience at which the next level is reached
                //   f7 = experience accumulated right now (omitted when 0)
                //   f9 = experience gained in this fight (omitted when 0)
                //   f1, f2, f3, f5, f8 = 1 in all three captures
                // and, one level up, f2 = the character's level.
                // Check: at level 3 the capture sends f4=650 and f6=1500, which are exactly the
                // thresholds of levels 3 and 4 in the client's table.
                if (!f.IsMonster)
                {
                    long levelFloor = ExperienceTable.LevelFloor(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel);
                    long nextLevelFloor = ExperienceTable.NextLevelFloor(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel);

                    var xpDetail = new ProtoMessage();
                    xpDetail.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 1 });
                    xpDetail.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 1 });
                    xpDetail.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
                    if (levelFloor > 0)
                        xpDetail.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = levelFloor });
                    xpDetail.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 1 });
                    xpDetail.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = nextLevelFloor });
                    if (Jondo.Unity.Launcher.Network.SessionContext.State.Experience > 0)
                        xpDetail.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.Experience });
                    if (totalXP > 0)
                    {
                        xpDetail.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 0, VarIntValue = 1 });
                        xpDetail.Fields.Add(new ProtoField { FieldNumber = 9, WireType = 0, VarIntValue = totalXP });
                    }

                    var xpWrap = new ProtoMessage();
                    xpWrap.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = xpDetail.ToByteArray() });

                    var xpBlock = new ProtoMessage();
                    xpBlock.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = xpWrap.ToByteArray() });
                    xpBlock.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel });

                    fighterResult.Fields.Add(new ProtoField { FieldNumber = 9, WireType = 2, BytesValue = xpBlock.ToByteArray() });
                }

                var entry = new ProtoMessage();
                entry.Fields.Add(new ProtoField
                {
                    FieldNumber = 1,
                    WireType = 2,
                    BytesValue = (isWinner && !f.IsMonster) ? lootWrap.ToByteArray() : Array.Empty<byte>()
                });
                entry.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = fighterResult.ToByteArray() });
                if (isWinner)
                {
                    entry.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 2 });
                }

                jwfMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = entry.ToByteArray() });
            }
            jwfMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = -1 });

            // krh = experience gained. In both captures it comes right before the jwf.
            var krhMsg = new ProtoMessage();
            if (totalXP > 0)
            {
                krhMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = totalXP });
            }
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/krh", krhMsg.ToByteArray()));

            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwf", jwfMsg.ToByteArray()));

            // The juo that used to be sent here ({f1 = xp, f2 = kamas}) did not look like the real
            // one either, whose field 1 is a submessage. It is dropped instead of replaced by
            // another invention: a malformed message is worse than no message at all.

            if (fight.WinnerTeamId == 0)
            {
                MobSpawnManager.RemoveMobGroup(fight.RoleplayMapId, Jondo.Unity.Launcher.Network.SessionContext.State.CurrentFightMobId);
                Program.LogDebug($"[FightHandler] Mob group #{Jondo.Unity.Launcher.Network.SessionContext.State.CurrentFightMobId} removed from map {fight.RoleplayMapId}.");

                // The defeated group is replaced with a freshly randomized one, leaving the groups
                // still on the map alone. It runs before the synthesized kkr further down, so that
                // the jpv sent to the client already includes it.
                var respawned = MobSpawnManager.RespawnOneGroup(fight.RoleplayMapId);
                if (respawned != null)
                {
                    Program.LogDebug($"[FightHandler] Respawned group #{respawned.MobId} on cell " +
                                     $"{respawned.CellId} with {respawned.Members.Count} monster(s).");
                }
            }

            Jondo.Unity.Launcher.Network.SessionContext.State.IsInFight = false;
            Jondo.Unity.Launcher.Network.SessionContext.State.CurrentFightMobId = 0;
            _activeFights.TryRemove(fight.FightId, out _);

            // Back to roleplay, traced from the official capture (frames 336-339):
            //   lxs -> kkp -> kkm -> krb -> joh -> lor
            //
            // What used to be here was jpf + kkq(0) + joh, and neither of the first two does that
            // job: kkq identifies the mob group and jpf opens the fight context. The messages that
            // really pull the client out of the fight are kkp (destroy context) and kkm (create
            // the new one; empty = roleplay). Without them the client stayed inside the fight
            // context, which is why the turn counter and the timer were still on screen after
            // closing the victory panel.
            await WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLxsMessage());
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/kkp", Array.Empty<byte>()));
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/kkm", Array.Empty<byte>()));
            await WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKrbMessage());

            var johRp = new ProtoMessage();
            johRp.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fight.RoleplayMapId });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/joh", johRp.ToByteArray()));

            var lorRp = new ProtoMessage();
            lorRp.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 120 });
            lorRp.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/lor", lorRp.ToByteArray()));

            // Repopulate the roleplay map. With joh alone the client was left on an empty map, with
            // no player, no NPCs and no mob groups: the kkr -> jpv cycle is missing. We trigger it
            // ourselves by synthesizing the kkr instead of waiting for the client to ask for it.
            Jondo.Unity.Launcher.Network.SessionContext.State.MapId = fight.RoleplayMapId;
            var kkrSynth = new ProtoMessage();
            kkrSynth.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fight.RoleplayMapId });
            byte[] kkrPacket = BuildGameNodePacket("type.ankama.com/kkr", kkrSynth.ToByteArray());
            await MapLoadHandler.HandleMapLoadRequest(stream, kkrPacket);

            // The kamas won. They are saved on the character and a bvr (KamasUpdateMessage) is sent
            // to the client; without it the purse kept showing the pre-fight amount.
            if (fight.WinnerTeamId == 0 && totalKamas > 0)
            {
                Jondo.Unity.Launcher.Network.SessionContext.State.Kamas += totalKamas;
                DatabaseManager.SaveCurrentCharacter();

                var bvrMsg = new ProtoMessage();
                bvrMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.Kamas });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/bvr", bvrMsg.ToByteArray()));
                Program.LogDebug($"[FightHandler] +{totalKamas} kamas (total {Jondo.Unity.Launcher.Network.SessionContext.State.Kamas}).");
            }

            // With new loot the whole inventory has to be resent: otherwise the items are in the
            // database but the client keeps showing the bag it had before the fight.
            if (loot.Count > 0)
            {
                await WriteFrameAsync(stream, BuildGameNodePacket(
                    "type.ankama.com/irm", CharacterSelectionHandler.BuildDynamicIrmPayload()));
                Program.LogDebug($"[FightHandler] Inventory resent with {loot.Count} looted item(s).");
            }

            // Characteristics sheet. The client leaves the fight carrying the counters the fighter
            // had when the last enemy died: that is why it showed 0 HP, 3 AP and 0 MP once back in
            // roleplay. The official capture also resends the kri when the fight ends.
            byte[]? kriEnd = StatsHandler.BuildUpdatedKriPacket();
            if (kriEnd != null)
            {
                await WriteFrameAsync(stream, kriEnd);
                Program.LogDebug("[FightHandler] Characteristics sheet (kri) resent on the way back to roleplay.");
            }

            // On level up the spell bar is rebuilt: there may be a new spell that now meets the
            // minimum level. The client pops the level-up screen by itself as soon as it sees a
            // higher level in the kri than the one it had.
            if (Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel > previousLevel)
            {
                await WriteFrameAsync(stream, TransitionPacketsBuilder.BuildHmdMessage());
                foreach (var itp in TransitionPacketsBuilder.BuildItpList())
                {
                    await WriteFrameAsync(stream, itp);
                }
                Program.LogDebug($"[FightHandler] Spell book and spell bar rebuilt after reaching level {Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel}.");
            }

            Program.LogDebug($"[FightHandler] Fight #{fight.FightId} ended! Restored Roleplay Map {fight.RoleplayMapId}. Winner: Team {fight.WinnerTeamId}. Rewards: {totalXP} XP, {totalKamas} Kamas, {loot.Count} item(s).");
        }

        // =========================================================================
        // HELPERS
        // =========================================================================

        private static uint ReadVarInt(byte[] data, ref int pos)
        {
            uint value = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return value;
        }
    }
}
