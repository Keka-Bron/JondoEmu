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

        /// <summary>Duración del turno en décimas de segundo, tal y como viaja en jut.f1 y jyf.f2.</summary>
        public const int TurnDurationDeciseconds = 300;
        /// <summary>La misma duración en milisegundos, para el temporizador del servidor.</summary>
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
            GameState.IsInFight = true;
            GameState.CurrentFightMobId = mobGroup.MobId;

            long fightId = System.Threading.Interlocked.Increment(ref _nextFightId);
            long arenaMapId = MapManager.ResolveArenaMapId(mapId);
            var fight = new FightInstance(fightId, mapId, arenaMapId);

            // mobContextId is the roleplay mob group ID (e.g. -1030815 or -20003)
            fight.DefenderLeaderId = (mobContextId != 0) ? mobContextId : mobGroup.MobId;

            // Generate placement cells from arena map walkable cells
            var walkableCells = MobSpawnManager.GetInnerWalkableCells(arenaMapId);
            fight.GeneratePlacementCells(walkableCells);

            // Build Player Fighter from GameState (Fighter ID = player CharacterId)
            var playerFighter = new Fighter
            {
                Id = GameState.CharacterId,
                Name = GameState.CharacterName,
                TeamId = 0,
                CellId = fight.BluePlacementCells.FirstOrDefault(),
                Level = GameState.CharacterLevel > 0 ? GameState.CharacterLevel : 40,
                // Misma fuente que el jxx que se le manda al cliente. Antes había aquí una fórmula
                // propia que solo miraba la vitalidad BASE: el servidor creía que el personaje
                // tenía 305 PdV mientras el cliente mostraba 514, porque los objetos equipados
                // (el Dofus Esmeralda da +200) solo se sumaban en un lado. Resultado: el personaje
                // moría "de fondo" en 8 turnos con la barra de vida intacta en pantalla.
                MaxHP = StatsHandler.GetPlayerMaxHp(),
                MaxAP = 6,
                MaxMP = 3,
                // Misma iniciativa que enseña la ficha del personaje: características elementales
                // más lo que aporte el equipo. La fórmula que había aquí (100 + nivel + todas las
                // características) se inventaba el número y, sobre todo, ignoraba los objetos: con
                // el Dofus de pesadilla puesto (+1000 de iniciativa) el pío seguía jugando antes.
                Initiative = GameState.StatStrength + GameState.StatIntelligence + GameState.StatChance
                             + GameState.StatAgility + StatsHandler.GetEquipBonus(44),
                Strength = GameState.StatStrength + StatsHandler.GetEquipBonus(10),
                Intelligence = GameState.StatIntelligence + StatsHandler.GetEquipBonus(15),
                Chance = GameState.StatChance + StatsHandler.GetEquipBonus(13),
                Agility = GameState.StatAgility + StatsHandler.GetEquipBonus(14),
                // Potencia del equipo (característica 25). Entra directamente en el cálculo de daño.
                Power = StatsHandler.GetEquipBonus(25),
                // Crítico del equipo (característica 18): el Dofus Turquesa da +10.
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
                    // gradeXp de la ficha del monstruo: la experiencia que da al morir, la misma
                    // que el cliente enseña al pasar el ratón por encima del grupo.
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
            // RÁFAGA 1 (Disparada inmediatamente por colisión / jpp)
            // Sequence: joq, jpf, kkq, kkp, kkm, kri, joh, lor, krp, lsy, kkz
            // =========================================================================
            // 1. joq (Movement validation - empty opcode)
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/joq", Array.Empty<byte>()));

            // 2. jpf (GameContextDestroyMessage)
            byte[] jpfPacket = BuildJpfPacket(fight.DefenderLeaderId);
            await WriteFrameAsync(stream, jpfPacket);

            // 3. kkq: identifica el grupo de mobs contra el que se pelea.
            var kkqMsg = new ProtoMessage();
            kkqMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fight.DefenderLeaderId });
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/kkq", kkqMsg.ToByteArray()));

            // 4. kkp: destruye el contexto actual (mensaje vacío).
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/kkp", Array.Empty<byte>()));

            // 5. kkm: crea el contexto nuevo. 1 = combate; roleplay es 0 y por eso al terminar la
            // pelea este mismo mensaje va vacío.
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
            Program.LogDebug("[FightHandler] RÁFAGA 1 sent successfully.");

            // =========================================================================
            // RÁFAGA 2 (Inmediatamente después de la Ráfaga 1)
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
            Program.LogDebug("[FightHandler] RÁFAGA 2 sent successfully. Waiting for client kkr request...");

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
        /// Sends RÁFAGA 3 containing igs, jya, jyj, jxx, jyi, jyf, jyk, jxe, jwo, jox.
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
            Program.LogDebug("[FightHandler] Responding to fight map request (kkr) with RAFAGA 3...");

            // =========================================================================
            // RÁFAGA 3 (Disparada por el kkr del cliente)
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
            Program.LogDebug("[FightHandler] RÁFAGA 3 sent successfully. Client in placement phase (45s).");
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
                var mobs = MobSpawnManager.GetMobsForMap(GameState.MapId);
                var mobGroup = (mobContextId != 0) 
                    ? (mobs.FirstOrDefault(m => m.MobId == mobContextId) ?? MobSpawnManager.GetMobGroupById(mobContextId) ?? mobs.FirstOrDefault())
                    : mobs.FirstOrDefault();

                if (mobGroup != null)
                {
                    await InitiateFightFromMobCollision(stream, mobGroup, GameState.MapId, mobContextId);
                    return;
                }
            }
            else
            {
                // Re-sync combat context packets if requested
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/joq", Array.Empty<byte>()));
                await WriteFrameAsync(stream, BuildJpfPacket(fight.DefenderLeaderId));
                
                var johMsg = new ProtoMessage();
                johMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.MapId });
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

                        fight.ChangePlacementCell(GameState.CharacterId, newCell);
                        Program.LogDebug($"[FightHandler] Changed player placement cell to {newCell}.");

                        // Reply with kkz ack
                        byte[] ack = BuildKkzPacket(newCell, GameState.CharacterId, 3);
                        await WriteFrameAsync(stream, ack);
                    }
                }
                catch { }
            }
            await Task.CompletedTask;
        }

        // NOTA: aquí vivía HandleCombatMovementRequest, una versión antigua del movimiento de
        // combate que reenviaba el camino comprimido del cliente sin expandirlo y añadía un kkz
        // que forzaba la posición. Eso provocaba el teletransporte. Se ha eliminado para que no
        // vuelva a competir con HandleCombatMoveRequest (los nombres se diferenciaban en una
        // sola letra y el enrutado escogía el equivocado).

        private static async Task HandleTurnReady(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            Program.LogDebug("[FightHandler] Player clicked READY (jza / F1).");
            bool allReady = fight.SetFighterReady(GameState.CharacterId);

            if (allReady)
            {
                fight.StartFight();
                var current = fight.CurrentFighter ?? fight.Team0[0];

                // 1. jys (GameFightPreparationStartedMessage)
                var jysMsg = new ProtoMessage();
                jysMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 1 });
                jysMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.CharacterId });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jys", jysMsg.ToByteArray()));

                // 2. jwu (f3 = playerId)
                var jwu1 = new ProtoMessage();
                jwu1.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = GameState.CharacterId });
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
                jud1.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.CharacterId });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jud", jud1.ToByteArray()));

                // 10. jwm (FighterResyncMessage)
                await SendFighterResync(stream, fight);

                // 11. juc (sequence end)
                var juc1 = new ProtoMessage();
                juc1.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 8 });
                juc1.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.CharacterId });
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

            // Misma lista que la barra de accesos directos de roleplay.
            // TODO: cuando exista persistencia de "qué variante ha elegido el jugador", leerla en
            // GetPlayerAvailableSpells en vez de asumir siempre la base.
            var spellList = DatabaseManager.GetPlayerAvailableSpells(GameState.Breed, GameState.CharacterLevel);

            Program.LogDebug($"[FightHandler] jvn: {spellList.Count} hechizos disponibles a nivel " +
                             $"{GameState.CharacterLevel} para la raza {GameState.Breed}: " +
                             string.Join(", ", spellList));

            // Primera entrada: el ARMA. Va sin identificador de hechizo y con f3 = 2 (los hechizos
            // llevan f3 = 1). Faltaba, y por eso al entrar en combate desaparecía el icono de la
            // espada de la barra: el jvn rehace la barra y la dejaba sin la casilla del arma.
            var armaSub = new ProtoMessage();
            armaSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 2 });
            armaSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });
            jvnMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = armaSub.ToByteArray() });
            foreach (var spellId in spellList)
            {
                var sSub = new ProtoMessage();
                sSub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = spellId });
                sSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
                sSub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });
                jvnMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = sSub.ToByteArray() });
            }

            jvnMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = GameState.CharacterId });
            jvnMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = GameState.CharacterId });

            // Ranura 0 vacía, como en la captura oficial y en el itp: es la que ocupa el arma.
            var ranuraArma = new ProtoMessage();
            ranuraArma.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = Array.Empty<byte>() });
            jvnMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 2, BytesValue = ranuraArma.ToByteArray() });

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
            Program.LogDebug($"[FightHandler] Sent jvn (SpellListMessage) with {spellList.Count} spells for Breed {GameState.Breed}.");
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

            // Refresco de puntos al empezar el turno. Fighter.StartTurn() ya restaura PA/PM en el
            // servidor; esto se lo cuenta al cliente. Con delta 0 el bloque de valor queda reducido
            // al máximo, que es como la captura oficial expresa "puntos al máximo".
            //
            // Va envuelto en jud/juc: el motor de secuencias del cliente descarta los cambios de
            // característica que llegan sueltos, fuera de una secuencia abierta.
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
                              $"(PA {current.CurrentAP}/{current.MaxAP}, PM {current.CurrentMP}/{current.MaxMP}).");

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
        /// Arranca el temporizador de turno. jut.f1 = 300 solo le dice al cliente cuántas décimas
        /// de segundo dura el turno; hacer cumplir el plazo es responsabilidad del servidor. Sin
        /// esto, al agotarse el tiempo el contador del cliente seguía bajando en negativo y el
        /// turno no pasaba nunca.
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
                    return; // el jugador pasó turno a tiempo
                }

                // Solo forzamos el fin si seguimos exactamente en el mismo turno.
                var f = GetCurrentFight();
                if (f == null || f.FightId != fightId) return;
                if (f.State != FightState.Ongoing) return;
                if (f.CurrentFighter == null || f.CurrentFighter.Id != fighterId) return;
                if (f.RoundNumber != round) return;
                Program.LogDebug($"[FightHandler] ⏰ Se agotó el turno del luchador #{fighterId}. Pasando turno automáticamente.");

                try
                {
                    await EndTurnAsync(stream, f.CurrentFighter);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[FightHandler] Error al forzar el fin de turno: {ex.Message}");
                }
            });
        }

        public static async Task EndTurnAsync(NetworkStream stream, Fighter endingFighter)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;

            // El turno acaba: cancela el temporizador para que no fuerce un segundo fin de turno.
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
        /// Construye el joo (difusión de movimiento) tal y como lo emite el servidor oficial:
        ///   joo { f1 = fighterId, f2 = &lt;camino EMPAQUETADO&gt;, f5 = orientación final }
        /// El campo 2 es un packed repeated int32: los varints de celda van concatenados SIN
        /// etiqueta. Escribirlos como campos etiquetados (08 xx 08 xx ...) corrompe el camino,
        /// porque el cliente interpreta el 0x08 como un número de celda más.
        /// Verificado contra la captura: f2 = ac03 ab03 b803 c603 ... para [428,427,440,454,...].
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
        /// Variación de una característica de combate (PA = 1, PM = 23, vida = 19).
        ///
        /// Los tres campos del bloque de valor son OPCIONALES y el cliente distingue "presente con
        /// valor cero" de "ausente". La captura oficial lo deja claro: durante el turno manda
        /// {f2 = -pérdida acumulada, f4 = máximo, f8 = pérdida}, pero al restablecer los puntos
        /// manda ÚNICAMENTE {f4 = máximo}. Escribir "f2 = 0" no es lo mismo que omitirlo: el
        /// cliente lo lee como "aplica una variación de cero" y deja el contador donde estaba.
        /// Por eso los PA/PM seguían a cero al volver a tocarte el turno.
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
        /// Acción "lanzar hechizo" (f13 = 300). f5 identifica el hechizo con DOS ids: f1 es la fila
        /// de SpellLevels (el nivel concreto) y f4 el id del hechizo. Antes f1 iba fijo a 41870,
        /// que es el nivel 1 de Flecha Mágica: cualquier otro hechizo llegaba al cliente con un
        /// nivel que no le correspondía.
        /// </summary>
        public static byte[] BuildJtxSpellCastPacket(long casterId, int targetCell, long spellId, int spellLevelId, long targetId, int launchIndex)
        {
            var f5Sub = new ProtoMessage();
            f5Sub.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = spellLevelId > 0 ? spellLevelId : spellId });
            f5Sub.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = spellId });

            // f7 solo lleva lanzador e índice de lanzamiento. Aquí había un relleno de 13 bytes a
            // cero en un campo 1 que no existe en el mensaje real; bastaba con eso para que el
            // cliente descartara la acción entera y no se viera ninguna animación.
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
        /// Acción "pérdida de puntos de vida" (f13 = 99). El daño viaja en f25, NO en f6.
        ///
        /// Dentro de f25 el daño es el campo 5 y el elemento el campo 1 — no al revés. Estaban
        /// intercambiados, así que el cliente pintaba y aplicaba siempre el valor fijo que
        /// llevaba el campo 5 (un 7) mientras el servidor descontaba la vida de verdad: la barra
        /// del monstruo bajaba de 7 en 7 y el combate se acababa de golpe con el bicho aún lleno
        /// en pantalla. La captura oficial lo confirma dos veces: el hechizo 13425 hace 7-9 de
        /// daño de fuego y manda f1=2 (fuego) con f5=7 (la tirada).
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
        /// Acción "luchador abatido" (f13 = 103). Sin ella el cliente nunca da por muerto a nadie:
        /// el monstruo se quedaba de pie y la pantalla de fin de combate contaba cero enemigos
        /// derrotados. En la captura oficial va justo detrás del golpe que mata (trama 313).
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
        /// Acción "pérdida de puntos de acción" (f13 = 102) usada tras lanzar un hechizo. Es la que
        /// dibuja el "-N PA" flotante sobre el lanzador; el jvm solo actualiza el contador.
        /// </summary>
        /// <summary>
        /// Acción de pérdida de puntos: 102 para los PA y 129 para los PM. Es la que dibuja el
        /// "-N" flotante sobre el luchador y la que escribe la línea en el registro de combate;
        /// el jvm solo mueve el contador, sin avisar de nada.
        ///
        /// <paramref name="victimId"/> y <paramref name="casterId"/> coinciden cuando el gasto es
        /// propio (lanzar un hechizo) y difieren cuando alguien te lo retira.
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

        // Aquí había un jvm de "variación de vida" con la característica 19. No hacía nada: la
        // barra de vida la mueve el propio jtx de daño, y la 19 no es la vida. Se retira en vez
        // de dejar un mensaje inventado circulando.

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
        /// Lanzamientos de cada hechizo en el turno en curso, por hechizo y por objetivo. El
        /// cliente lee ese número del propio paquete de lanzamiento (f7.f5) y lo compara con el
        /// límite del hechizo para pintarlo gris.
        ///
        /// Antes había aquí un único contador global que no se reiniciaba nunca: al tercer o
        /// cuarto lanzamiento del combate el cliente ya creía haber agotado los 3 lanzamientos
        /// por turno de la Flecha Helada y la deshabilitaba, aunque fuera el primero del turno.
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
                    // Por NÚMERO de campo, no por posición: el golpe con arma llega como
                    // { f2 = casilla } sin campo 1, y leyendo por posición se tomaba la casilla
                    // como identificador de hechizo y se rechazaba la petición entera.
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
                Program.LogDebug("[FightHandler] Petición de lanzamiento sin casilla de destino; se descarta.");
                return;
            }

            // Sin identificador de hechizo = golpe con el ARMA equipada. El cliente lo manda así,
            // solo con la casilla, y hasta ahora se rechazaba por completo.
            bool esArma = spellId <= 0;
            var spellData = esArma
                ? DatabaseManager.GetEquippedWeaponAsSpell(GameState.CharacterId)
                : DatabaseManager.GetSpellCombatData((int)spellId, current.Level);

            if (spellData == null)
            {
                Program.LogDebug(esArma
                    ? "[FightHandler] Golpe con arma rechazado: no hay arma equipada con daño."
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
                Program.LogDebug($"[FightHandler] Hechizo {spellId} sin línea de visión de {current.CellId} a {targetCell}.");
                return;
            }

            var target = fight.Team1.FirstOrDefault(m => m.IsAlive && (m.CellId == targetCell || MapGeometry.Distance(m.CellId, targetCell) <= 1));
            long targetId = target != null ? target.Id : -1;

            // Límites de lanzamiento, tal como los declara el hechizo en la base de datos.
            _castsThisTurn.TryGetValue(spellId, out int castsDone);
            if (spellData.MaxCastPerTurn > 0 && castsDone >= spellData.MaxCastPerTurn)
            {
                Program.LogDebug($"[FightHandler] Hechizo {spellId} agotado este turno ({castsDone}/{spellData.MaxCastPerTurn}).");
                return;
            }

            var perTargetKey = (spellId, targetId);
            _castsPerTargetThisTurn.TryGetValue(perTargetKey, out int castsOnTarget);
            if (targetId != -1 && spellData.MaxCastPerTarget > 0 && castsOnTarget >= spellData.MaxCastPerTarget)
            {
                Program.LogDebug($"[FightHandler] Hechizo {spellId} agotado sobre ese objetivo ({castsOnTarget}/{spellData.MaxCastPerTarget}).");
                return;
            }

            current.AccumulatedApLoss += spellData.APCost;
            current.CurrentAP -= spellData.APCost;

            castsDone++;
            _castsThisTurn[spellId] = castsDone;
            if (targetId != -1) _castsPerTargetThisTurn[perTargetKey] = castsOnTarget + 1;

            Program.LogDebug($"[FightHandler] {(esArma ? "Golpe con arma" : $"Player cast spell {spellId}")} " +
                             $"en la casilla {targetCell} (gasta {spellData.APCost} PA, quedan {current.CurrentAP}, " +
                             $"lanzamiento {castsDone}" +
                             $"{(spellData.MaxCastPerTurn > 0 ? "/" + spellData.MaxCastPerTurn : "")} del turno).");

            // Secuencia calcada de la captura oficial (trama 254):
            //   jud(4) · jtx(300 lanzamiento) · jud(3) · jvm(PA) · juc(3) · jtx(102 pérdida de PA)
            //   · jtx(99 daño) · juc(4)
            //
            // Para el golpe con arma no se manda el jtx de lanzamiento: no tengo ninguna captura
            // de un ataque con arma y no sé cómo codifica el cliente esa acción. Se manda solo el
            // gasto de PA y el daño, que sí están comprobados. Faltará la animación del espadazo.
            await WriteFrameAsync(stream, BuildJud(4, current.Id));
            if (!esArma)
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
        /// Aplica TODOS los efectos de un hechizo sobre un objetivo y se los cuenta al cliente.
        /// La usan por igual los lanzamientos del jugador y los de los monstruos, para que un pío
        /// que quita alcance haga exactamente lo mismo que si lo hiciera el personaje.
        ///
        /// Cubre daño (por elemento), desplazamiento (empujar/atraer) y cualquier efecto que
        /// modifique una característica. Ese último grupo sale del catálogo de efectos importado
        /// del cliente: no hay ninguna lista de efectos escrita a mano.
        ///
        /// Lo que todavía NO hace: la tirada de esquiva. En Dofus quitar PA o PM se resuelve
        /// comparando la "retirada" del lanzador con la "esquiva" del objetivo, y la retirada del
        /// personaje no se está calculando desde el equipo, así que de momento el efecto se
        /// aplica entero. La estructura ya está preparada para meter la tirada cuando se lea ese
        /// dato del equipo.
        /// </summary>
        private static async Task<int> ApplySpellEffectsAsync(
            NetworkStream stream, FightInstance fight, Fighter caster, SpellCombatData spell, Fighter target)
        {
            int damageDealt = 0;

            if (spell.BaseDamageMin > 0 || spell.BaseDamageMax > 0)
            {
                var element = (ElementType)spell.Element;

                // Golpe crítico: probabilidad del hechizo más el crítico que aporte el equipo. En
                // crítico el daño NO se multiplica, se usa el rango crítico que trae el propio
                // hechizo (la Flecha Helada pasa de 12-14 a 15-17).
                int probCritica = spell.CriticalHitProbability + caster.CriticalBonus;
                bool esCritico = spell.HasCriticalDamage && probCritica > 0 && _lootRandom.Next(100) < probCritica;

                int minBase = esCritico ? spell.CriticalDamageMin : spell.BaseDamageMin;
                int maxBase = esCritico ? spell.CriticalDamageMax : spell.BaseDamageMax;

                // Bonificación de daño base que el propio hechizo se dejó puesta en un lanzamiento
                // anterior (efecto 293). Suma al daño BASE, antes de multiplicar por la
                // característica: la Flecha Helada pasa de 12-14 a 16-18 en el segundo lanzamiento.
                int bonoBase = caster.GetSpellDamageBonus((int)spell.SpellId, fight.RoundNumber);
                int danoBase = ((minBase + maxBase) / 2) + bonoBase;

                damageDealt = DamageCalculator.CalculateDamage(
                    baseDamage: danoBase,
                    element: element,
                    statValue: caster.GetStatForElement(element),
                    power: caster.Power,
                    flatElementDamage: 0,
                    flatDamage: 0,
                    targetResPct: target.GetResPctForElement(element),
                    targetFlatRes: 0);

                target.TakeDamage(damageDealt);
                Program.LogDebug($"[FightHandler] {caster.Name} hace {damageDealt} de daño a {target.Name} " +
                                 $"(elemento {spell.Element}, base {danoBase}" +
                                 $"{(bonoBase != 0 ? $" incluyendo +{bonoBase} del efecto" : "")}" +
                                 $"{(esCritico ? $", CRÍTICO al {probCritica} %" : "")}). " +
                                 $"Vida: {target.CurrentHP}/{target.MaxHP}");

                await WriteFrameAsync(stream, BuildJtxDamagePacket(caster.Id, target.Id, damageDealt, spell.Element));

                if (!target.IsAlive)
                {
                    await WriteFrameAsync(stream, BuildJtxDeathPacket(caster.Id, target.Id));
                    Program.LogDebug($"[FightHandler] {target.Name} ha caído.");
                }
            }

            // Bonificaciones que el hechizo deja puestas sobre el LANZADOR para próximos usos.
            // Volver a lanzarlo renueva el plazo en vez de sumar otra vez: la acumulación máxima
            // de la Flecha Helada es 1.
            foreach (var buff in spell.DamageBuffs)
            {
                caster.ApplySpellDamageBuff(buff.SpellId, buff.Bonus, buff.Duration, fight.RoundNumber);
                Program.LogDebug($"[FightHandler]   {caster.Name} gana +{buff.Bonus} de daño base en el hechizo " +
                                 $"{buff.SpellId} durante {buff.Duration} turno(s).");
            }

            // Efectos sobre características: quitar PA (efecto 1079 de la Flecha Helada),
            // quitar alcance (efecto 116, el que lleva el pío), etc.
            foreach (var se in spell.StatEffects)
            {
                if (se.Characteristic == 1)
                {
                    int perdidos = Math.Min(Math.Abs(se.Value), target.CurrentAP);
                    if (perdidos <= 0) continue;
                    target.CurrentAP -= perdidos;
                    target.AccumulatedApLoss += perdidos;
                    await WriteFrameAsync(stream, BuildJud(3, target.Id));
                    await WriteFrameAsync(stream, BuildJvmPacket(target.Id, 1, -target.AccumulatedApLoss, target.MaxAP));
                    await WriteFrameAsync(stream, BuildJuc(3, target.Id));
                    await WriteFrameAsync(stream, BuildJtxPointLossPacket(target.Id, caster.Id, perdidos));
                    Program.LogDebug($"[FightHandler]   efecto {se.EffectId}: -{perdidos} PA a {target.Name}.");
                }
                else if (se.Characteristic == 23)
                {
                    int perdidos = Math.Min(Math.Abs(se.Value), target.CurrentMP);
                    if (perdidos <= 0) continue;
                    target.CurrentMP -= perdidos;
                    target.AccumulatedMpLoss += perdidos;
                    await WriteFrameAsync(stream, BuildJud(3, target.Id));
                    await WriteFrameAsync(stream, BuildJvmPacket(target.Id, 23, -target.AccumulatedMpLoss, target.MaxMP));
                    await WriteFrameAsync(stream, BuildJuc(3, target.Id));
                    await WriteFrameAsync(stream, BuildJtxPointLossPacket(target.Id, caster.Id, perdidos, isMp: true));
                    Program.LogDebug($"[FightHandler]   efecto {se.EffectId}: -{perdidos} PM a {target.Name}.");
                }
                else
                {
                    // Resto de características (alcance, potencia, resistencias...): el cliente
                    // solo necesita la variación; el servidor todavía no las usa en sus cálculos.
                    await WriteFrameAsync(stream, BuildJud(3, target.Id));
                    await WriteFrameAsync(stream, BuildJvmPacket(target.Id, se.Characteristic, se.Value, 0));
                    await WriteFrameAsync(stream, BuildJuc(3, target.Id));
                    Program.LogDebug($"[FightHandler]   efecto {se.EffectId}: {se.Value} en la característica {se.Characteristic} de {target.Name}.");
                }
            }

            // Desplazamiento. Sin captura de un empuje real, se reutiliza el mismo joo que mueve
            // a un luchador por un camino: la animación no será la de un empujón, pero el
            // monstruo acaba en la casilla correcta y ya no se queda clavado en el sitio.
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
                    Program.LogDebug($"[FightHandler]   desplazamiento: {target.Name} va a la casilla {target.CellId} " +
                                     $"({pushPath.Count - 1} de {Math.Abs(spell.PushDistance)} casillas).");
                }
            }

            return damageDealt;
        }

        private static async Task HandleCombatMoveRequest(NetworkStream stream, byte[] payload)
        {
            var fight = GetCurrentFight();
            if (fight == null) return;
            var current = fight.CurrentFighter;
            if (current == null || current.Id != GameState.CharacterId) return;

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

            // Transitabilidad de COMBATE, no la de map_walkable_cells.json: aquella recorta los
            // bordes del mapa (se generó para colocar mobs en roleplay) y dejaba fuera el anillo
            // exterior de la arena, por el que sí se puede andar peleando.
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
                    // El grado del hechizo lo fija la ficha del monstruo (spellGrades), no su nivel.
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

            // El orden importa: si el monstruo atacó y DESPUÉS huyó, hay que mandarlo así. Al
            // revés, el cliente dibuja el disparo desde la casilla de huida y parece que ha
            // atacado desde mucho más lejos de lo que permite su hechizo.
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

            Program.LogDebug($"[FightHandler] El monstruo #{monster.Id} recorre {turnResult.PathCells.Count - 1} casilla(s) hasta la {monster.CellId}.");

            await WriteFrameAsync(stream, BuildJud(4, monster.Id));
            await WriteFrameAsync(stream, BuildJooMovementPacket(monster.Id, turnResult.PathCells));
            await WriteFrameAsync(stream, BuildJud(3, monster.Id));
            await WriteFrameAsync(stream, BuildJvmPacket(monster.Id, 23, -monster.AccumulatedMpLoss, monster.MaxMP));
            await WriteFrameAsync(stream, BuildJuc(3, monster.Id));
            await WriteFrameAsync(stream, BuildJuc(4, monster.Id));
        }

        /// <summary>Devuelve true si el combate ha terminado y ya se ha notificado.</summary>
        private static async Task<bool> SendMonsterCastAsync(
            NetworkStream stream, FightInstance fight, Fighter monster, MonsterTurnResult turnResult)
        {
            if (turnResult.SpellId == 0) return false;

            var target = fight.Team0.Concat(fight.Team1).FirstOrDefault(p => p.Id == turnResult.TargetFighterId);
            int grade = monster.SpellGrades.TryGetValue(turnResult.SpellId, out var mg) ? mg : 1;
            var monSpell = DatabaseManager.GetSpellCombatData(turnResult.SpellId, grade);
            if (target == null || monSpell == null) return false;

            // La casilla desde la que se lanzó, no la actual: si el monstruo atacó y luego huyó,
            // su CellId ya es el de destino.
            int desde = turnResult.CastFromCell >= 0 ? turnResult.CastFromCell : monster.CellId;
            int d = MapGeometry.Distance(desde, target.CellId);
            int veces = Math.Max(1, turnResult.CastCount);
            Program.LogDebug($"[FightHandler] El monstruo #{monster.Id} lanza el hechizo {turnResult.SpellId} " +
                             $"{veces} vez/veces sobre {target.Name} desde la casilla {desde} " +
                             $"(distancia {d}, alcance {monSpell.MinRange}-{monSpell.MaxRange}).");

            // Exactamente la misma secuencia que el lanzamiento del jugador, incluido el reparto
            // de efectos: así un monstruo que empuja o quita PA hace lo mismo que haría el
            // personaje con ese hechizo.
            for (int i = 1; i <= veces; i++)
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
            long mId = fight != null ? fight.RoleplayMapId : GameState.MapId;
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

            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(GameState.CharacterName);

            var lookBreedSub = new ProtoMessage();
            lookBreedSub.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = nameBytes });
            lookBreedSub.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = GameState.Breed });

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
            if (!fighter.IsMonster) AddBaseBonusVal(11, 0, GameState.StatVitality + StatsHandler.GetEquipBonus(11));
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
                // Bloque f9: la ficha del JUGADOR (nombre y nivel). Es el equivalente del f7 que
                // usan los monstruos, y sin él el cliente muestra "???" y "Niv. 0" al pasar el ratón.
                // Estructura decodificada de la captura (jugador Fortellon, nivel 2):
                //   f9 { f3 { f2 = 1 },
                //        f4 { f2 = <raza>, f3 = 3, f4 = 1, f5 { f2 = <nivel>, f4 = 3 } },
                //        f6 = -1,
                //        f7 = "<nombre>" }        <- 9 bytes = "Fortellon"
                var f9Level = new ProtoMessage();
                f9Level.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = fighter.Level });
                f9Level.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 3 });

                var f9Breed = new ProtoMessage();
                f9Breed.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.Breed });
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
                    BytesValue = System.Text.Encoding.UTF8.GetBytes(fighter.Name ?? GameState.CharacterName ?? "")
                });

                fighterSub3.Fields.Add(new ProtoField { FieldNumber = 9, WireType = 2, BytesValue = f9.ToByteArray() });
            }

            // 6. Entity details field 2:
            var entityDetails = new ProtoMessage();

            if (!fighter.IsMonster)
            {
                byte[] playerLookBytes = (GameState.LookBytes != null && GameState.LookBytes.Length > 0)
                    ? GameState.LookBytes
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
        /// Tira el botín de todos los monstruos derrotados y lo mete en el inventario.
        ///
        /// Cada monstruo tiene su propia tabla en MonsterTemplates.drops, con una probabilidad
        /// por grado. El Capiorico Rojo, por ejemplo, suelta pluma de pío rojo al 100 %, semillas
        /// de sésamo al 18 % y bolsita de limones al 3 %.
        ///
        /// Lo que NO se aplica todavía: la prospección. En el juego real la probabilidad se
        /// multiplica por la PP del personaje dividida entre 100, pero la prospección del equipo
        /// no se está calculando, así que se usa el porcentaje base (equivale a 100 de PP).
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
                DatabaseManager.AddItemToInventory(GameState.CharacterId, kv.Key, kv.Value);
                Program.LogDebug($"[FightHandler] Botín: objeto {kv.Key} x{kv.Value} al inventario.");
            }

            if (loot.Count > 0)
            {
                GameState.SetInventory(DatabaseManager.LoadInventory(GameState.CharacterId));
            }

            return loot;
        }

        private static async Task SendFightEnd(NetworkStream stream, FightInstance fight)
        {
            // Experiencia REAL de cada monstruo derrotado (gradeXp de su ficha), no una fórmula
            // inventada. Es la misma cifra que el cliente enseña al pasar el ratón por el grupo.
            //
            // Lo que NO se aplica: el ajuste por diferencia de nivel entre el grupo y el
            // personaje, ni el reparto entre varios miembros del equipo. Con un solo jugador y
            // sin fórmula contrastada, se entrega la experiencia base.
            long totalXP = (fight.WinnerTeamId == 0) ? fight.Team1.Sum(m => (long)m.XpReward) : 0;
            int totalKamas = fight.Team1.Sum(m => 10 + (m.Level * 5));

            int levelAntes = GameState.CharacterLevel;
            if (totalXP > 0)
            {
                GameState.Experience += totalXP;
                int levelNuevo = ExperienceTable.LevelForXp(GameState.Experience);
                if (levelNuevo > GameState.CharacterLevel)
                {
                    // 5 puntos de característica por nivel, igual que en TotalCapitalForLevel.
                    int niveles = levelNuevo - GameState.CharacterLevel;
                    GameState.CharacterRemainingPoints += niveles * 5;
                    GameState.CharacterLevel = levelNuevo;
                    Program.LogDebug($"[FightHandler] ¡Subida de nivel! {levelAntes} -> {levelNuevo} " +
                                     $"(+{niveles * 5} puntos de característica).");
                }
                DatabaseManager.SaveCurrentCharacter();
                Program.LogDebug($"[FightHandler] +{totalXP} de experiencia (total {GameState.Experience}, " +
                                 $"nivel {GameState.CharacterLevel}: de {ExperienceTable.LevelFloor(GameState.CharacterLevel)} " +
                                 $"a {ExperienceTable.NextLevelFloor(GameState.CharacterLevel)}).");
            }

            // jwf = pantalla de fin de combate. El campo 1 es REPETIDO y de tipo mensaje: una
            // entrada por luchador. Antes se mandaba un simple varint con el equipo ganador, así
            // que el cliente no conseguía ni descodificar el mensaje y no aparecía ninguna pantalla.
            //
            // Estructura tomada de la captura oficial (trama 334):
            //   f1 { f1 { f1: 1, f2 { f1{f3=idObjeto, f4=cantidad}…, f3 = kamas } }   <- botín
            //        f3 { f4: 1, f5 = idLuchador }
            //        f4: 2 }                                                          <- ganador
            //   f1 { f1: {}, f3 { f4: 1, f5 = idLuchador } }                          <- perdedor
            //   f2: -1
            //
            // Queda fuera el bloque f3.f9 de progreso de experiencia: la captura solo da una
            // muestra y no permite saber qué es cada número, así que se omite en vez de rellenarlo
            // a ojo. Es un campo opcional; la pantalla sale igual, sin la barra de experiencia.
            var loot = (fight.WinnerTeamId == 0) ? RollFightLoot(fight) : new Dictionary<int, int>();

            var lootMsg = new ProtoMessage();
            foreach (var kv in loot)
            {
                var itemEntry = new ProtoMessage();
                itemEntry.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = kv.Key });
                itemEntry.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = kv.Value });
                lootMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = itemEntry.ToByteArray() });
            }

            // Las kamas van en el campo 1 del envoltorio del botín, no en el 3 del bloque interior.
            // La prueba es directa: ahí iba un 1 fijo copiado de la captura y la pantalla de fin de
            // combate mostraba "kamas 1" tras un combate que dio 65. El campo 3 (3273 en la captura
            // oficial) es el valor estimado del botín, que además el cliente calcula por su cuenta.
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

                // Bloque de progreso de experiencia, solo para el personaje del jugador.
                //
                // Estructura deducida de tres capturas con el personaje a niveles 1, 2 y 3, y
                // contrastada con la tabla de experiencia del cliente:
                //   f4 = experiencia con la que empieza el nivel actual (se omite si es 0)
                //   f6 = experiencia a la que se sube al nivel siguiente
                //   f7 = experiencia acumulada ahora mismo (se omite si es 0)
                //   f9 = experiencia ganada en este combate (se omite si es 0)
                //   f1, f2, f3, f5, f8 = 1 en las tres capturas
                // y, un nivel más arriba, f2 = el nivel del personaje.
                // Comprobación: a nivel 3 la captura manda f4=650 y f6=1500, que son exactamente
                // los umbrales de los niveles 3 y 4 de la tabla del cliente.
                if (!f.IsMonster)
                {
                    long suelo = ExperienceTable.LevelFloor(GameState.CharacterLevel);
                    long siguiente = ExperienceTable.NextLevelFloor(GameState.CharacterLevel);

                    var xpDetalle = new ProtoMessage();
                    xpDetalle.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 1 });
                    xpDetalle.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = 1 });
                    xpDetalle.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 1 });
                    if (suelo > 0)
                        xpDetalle.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = suelo });
                    xpDetalle.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 1 });
                    xpDetalle.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = siguiente });
                    if (GameState.Experience > 0)
                        xpDetalle.Fields.Add(new ProtoField { FieldNumber = 7, WireType = 0, VarIntValue = GameState.Experience });
                    if (totalXP > 0)
                    {
                        xpDetalle.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 0, VarIntValue = 1 });
                        xpDetalle.Fields.Add(new ProtoField { FieldNumber = 9, WireType = 0, VarIntValue = totalXP });
                    }

                    var xpWrap = new ProtoMessage();
                    xpWrap.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = xpDetalle.ToByteArray() });

                    var xpBloque = new ProtoMessage();
                    xpBloque.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = xpWrap.ToByteArray() });
                    xpBloque.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.CharacterLevel });

                    fighterResult.Fields.Add(new ProtoField { FieldNumber = 9, WireType = 2, BytesValue = xpBloque.ToByteArray() });
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

            // krh = experiencia ganada. En las dos capturas va justo delante del jwf.
            var krhMsg = new ProtoMessage();
            if (totalXP > 0)
            {
                krhMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = totalXP });
            }
            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/krh", krhMsg.ToByteArray()));

            await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/jwf", jwfMsg.ToByteArray()));

            // El juo que se enviaba aquí ({f1 = xp, f2 = kamas}) tampoco se parecía al real, cuyo
            // campo 1 es un submensaje. Se elimina en lugar de sustituirlo por otra invención: un
            // mensaje mal formado es peor que ninguno.

            if (fight.WinnerTeamId == 0)
            {
                MobSpawnManager.RemoveMobGroup(fight.RoleplayMapId, GameState.CurrentFightMobId);
                Program.LogDebug($"[FightHandler] Mob group #{GameState.CurrentFightMobId} removed from map {fight.RoleplayMapId}.");

                // Se repone el grupo derrotado con otro generado al azar, sin tocar los que
                // quedan en el mapa. Va antes del kkr sintetizado de más abajo, para que el jpv
                // que se le manda al cliente ya lo incluya.
                var repuesto = MobSpawnManager.RespawnOneGroup(fight.RoleplayMapId);
                if (repuesto != null)
                {
                    Program.LogDebug($"[FightHandler] Repuesto el grupo #{repuesto.MobId} en la casilla " +
                                     $"{repuesto.CellId} con {repuesto.Members.Count} monstruo(s).");
                }
            }

            GameState.IsInFight = false;
            GameState.CurrentFightMobId = 0;
            _activeFights.TryRemove(fight.FightId, out _);

            // Vuelta a roleplay, calcada de la captura oficial (tramas 336-339):
            //   lxs · kkp · kkm · krb · joh · lor
            //
            // Lo que había aquí era jpf + kkq(0) + joh, y ninguno de los dos primeros sirve para
            // eso: kkq identifica al grupo de mobs y jpf abre el contexto de pelea. Los mensajes
            // que de verdad sacan al cliente del combate son kkp (destruir contexto) y kkm
            // (crear el nuevo; vacío = roleplay). Al no mandarlos, el cliente se quedaba dentro
            // del contexto de combate: de ahí que siguieran en pantalla el contador de turnos y
            // el temporizador después de cerrar la pantalla de victoria.
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

            // Repoblar el mapa de roleplay. Con joh a secas el cliente se quedaba en un mapa vacío,
            // sin jugador, NPCs ni grupos de mobs: falta el ciclo kkr -> jpv. Lo provocamos nosotros
            // sintetizando el kkr en vez de esperar a que el cliente lo pida.
            GameState.MapId = fight.RoleplayMapId;
            var kkrSynth = new ProtoMessage();
            kkrSynth.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = fight.RoleplayMapId });
            byte[] kkrPacket = BuildGameNodePacket("type.ankama.com/kkr", kkrSynth.ToByteArray());
            await MapLoadHandler.HandleMapLoadRequest(stream, kkrPacket);

            // Las kamas ganadas. Se guardan en el personaje y se le manda al cliente el bvr
            // (KamasUpdateMessage); sin él la bolsa seguía marcando lo de antes del combate.
            if (fight.WinnerTeamId == 0 && totalKamas > 0)
            {
                GameState.Kamas += totalKamas;
                DatabaseManager.SaveCurrentCharacter();

                var bvrMsg = new ProtoMessage();
                bvrMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = GameState.Kamas });
                await WriteFrameAsync(stream, BuildGameNodePacket("type.ankama.com/bvr", bvrMsg.ToByteArray()));
                Program.LogDebug($"[FightHandler] +{totalKamas} kamas (total {GameState.Kamas}).");
            }

            // Con botín nuevo hay que reenviar el inventario entero: si no, los objetos están en
            // la base de datos pero el cliente sigue mostrando la bolsa de antes del combate.
            if (loot.Count > 0)
            {
                await WriteFrameAsync(stream, BuildGameNodePacket(
                    "type.ankama.com/irm", CharacterSelectionHandlerOld.BuildDynamicIrmPayload()));
                Program.LogDebug($"[FightHandler] Inventario reenviado con {loot.Count} objeto(s) de botín.");
            }

            // Ficha de características. El cliente sale del combate con los contadores que tenía
            // el luchador al morir el último enemigo: por eso se veía 0 de vida, 3 PA y 0 PM ya en
            // roleplay. La captura oficial también reenvía el kri al terminar la pelea.
            byte[]? kriEnd = StatsHandler.BuildUpdatedKriPacket();
            if (kriEnd != null)
            {
                await WriteFrameAsync(stream, kriEnd);
                Program.LogDebug("[FightHandler] Reenviada la ficha de características (kri) al volver a roleplay.");
            }

            // Al subir de nivel se rehace la barra de hechizos: puede haber alguno nuevo que ya
            // cumpla el nivel mínimo. El cliente saca la pantalla de subida de nivel él solo, al
            // ver en el kri un nivel más alto que el que tenía.
            if (GameState.CharacterLevel > levelAntes)
            {
                await WriteFrameAsync(stream, TransitionPacketsBuilder.BuildHmdMessage());
                foreach (var itp in TransitionPacketsBuilder.BuildItpList())
                {
                    await WriteFrameAsync(stream, itp);
                }
                Program.LogDebug($"[FightHandler] Libro y barra de hechizos rehechos tras subir al nivel {GameState.CharacterLevel}.");
            }

            Program.LogDebug($"[FightHandler] Fight #{fight.FightId} ended! Restored Roleplay Map {fight.RoleplayMapId}. Winner: Team {fight.WinnerTeamId}. Rewards: {totalXP} XP, {totalKamas} Kamas, {loot.Count} objeto(s).");
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
