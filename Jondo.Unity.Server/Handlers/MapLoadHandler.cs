using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    public static class MapLoadHandler
    {
        public static async Task HandleMapLoadRequest(NetworkStream stream, byte[] payload)
        {
            if (GameState.IsInFight)
            {
                await FightHandler.HandleFightMapLoad(stream);
                return;
            }

            LogDebug("[Game Node] Received Map Complementary Info Request (kkr) [Initial Map Load]");
            byte[]? inner = NetworkEnvelope.ExtractMessagePayload(payload, Op.Uri(Op.Kkr));
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

                    int spawnCellId = MapManager.GetNearestWalkableCell(mapIdToLoad, GameState.CellId > 0 ? GameState.CellId : 344);
                    GameState.CellId = spawnCellId;
                    var mapInfo = MapManager.GetMapInfo(mapIdToLoad);
                    int subAreaId = mapInfo != null ? mapInfo.SubAreaId : 1;
                    if (subAreaId == 444)
                    {
                        subAreaId = 20663;
                    }
 
                    // 1. Send lxd (MapComplementaryInfo wrapper) - Dynamically instantiated empty lxd message
                    var emptyLxd = new Jondo.Unity.Protocol.Messages.lxd();
                    byte[] lxdPacket = NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Lxd), emptyLxd.ToByteArray());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, lxdPacket);
                    LogDebug($"[Game Node] Sent dynamic empty lxd for Map ID: {mapIdToLoad}");

                    // 2. Send jpv (MapComplementaryInformationsDataMessage) - Dynamically built from DB
                    try
                    {
                        byte[] jpvBytes = ConstruirJpv(mapIdToLoad, spawnCellId, subAreaId).ToByteArray();
                        byte[] jpvPacket = NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Jpv), jpvBytes);
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
                    byte[] lsyPacket = NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Lsy), lsyPayload);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, lsyPacket);
                    LogDebug($"[Game Node] Sent dynamic lsy containing SubArea ID: {subAreaId} (matches official capture).");

                    // 4. Send dynamically instantiated kns (Fymx = true)
                    var knsMsg = new Jondo.Unity.Protocol.Messages.kns
                    {
                        Fymx = true
                    };
                    byte[] knsPacket = NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Kns), knsMsg.ToByteArray());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, knsPacket);
                    LogDebug("[Game Node] Sent dynamically instantiated kns (Fymx = true).");

                    // Y lo que dependa de haber llegado aquí: los objetivos que se cumplen pisando
                    // un mapa o una zona, y la marca verde de este mapa.
                    //
                    // Aquí y no en MapChangeHandler porque hay ocho sitios que cambian el mapa —el
                    // borde, el zaap, el zaapi, la puerta de una casa, el .teleport, el merkasako,
                    // salir de un combate, la mazmorra— y todos acaban pidiendo el mapa por kkr.
                    // Éste es el único paso por el que pasan los ocho.
                    await Managers.Quests.OnMapEnteredAsync(stream, mapIdToLoad, subAreaId);
                }
            }
        }

        /// <summary>
        /// El jpv de un mapa: la subzona, el jugador, los NPCs y los grupos de monstruos.
        ///
        /// Está separado del envío a propósito, para que el banco de pruebas pueda construirlo sin
        /// socket y comprobar que los ids que salen de aquí son EXACTAMENTE los mismos que salen
        /// del jss. Que no lo fueran es lo que rompía el ataque: el jss daba a un grupo su MobId
        /// —un -1000000 y bajando— y esto le daba el número que le tocara detrás de los NPCs del
        /// mapa, porque los numeraba por su posición en la lista. En el mapa de los NPCs de Amakna,
        /// medido: -1011567 en el jss y -20052 en el jpv, para el mismo grupo. El cliente se
        /// quedaba con el último que le llegase y devolvía ése al clicar, y el servidor no
        /// encontraba a nadie.
        ///
        /// Ahora ningún id se calcula aquí: el del grupo es su MobId y el del NPC es el que le puso
        /// <see cref="Managers.Npcs"/> al arrancar. Numerar por posición además renumeraba a los de
        /// detrás cada vez que moría un grupo.
        /// </summary>
        public static ProtoMessage ConstruirJpv(long mapId, int spawnCell, int subAreaId)
        {
            var jpvMsg = new ProtoMessage();

            // Field 1: subAreaId (VarInt)
            jpvMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = subAreaId });

            // Field 4: mapId (VarInt)
            jpvMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = mapId });

            // Field 12: subArea message wrapper (lkt)
            var lktMsg = new ProtoMessage();
            lktMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = subAreaId });
            jpvMsg.Fields.Add(new ProtoField { FieldNumber = 12, WireType = 2, BytesValue = lktMsg.ToByteArray() });

            // Field 15: All Map Actors (Polymorphic: Player, NPCs, Monster Groups)
            // A. Add Player Character Actor
            var playerActor = new ProtoMessage();

            // Disposition (Field 1)
            var playerDisp = new ProtoMessage();
            playerDisp.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = spawnCell });
            playerDisp.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = GameState.Orientation });
            playerActor.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = playerDisp.ToByteArray() });

            // Details (Field 2)
            //
            // El aspecto se REHACE aquí, no se coge el que se guardó al entrar. El de
            // GameState.PlayerActorDetails sale de la columna Characters.Look, que es un aspecto
            // del protocolo VIEJO y que además está congelado desde el inicio de sesión: no lleva
            // la montura, ni la ropa que uno se haya puesto después. Por eso al equiparse una
            // Mulagua se veía montado —eso lo manda el jsn de equipar— y al cambiar de mapa volvía
            // a aparecer a pie.
            //
            // BuildLook es el mismo que usa el jss, que sí sabe de monturas y de apariencias.
            var quien = DatabaseManager.GetCharacterById(GameState.CharacterId);
            byte[] aspecto = quien != null
                ? Managers.BreedLookTable.BuildLook(quien.Breed, quien.Sex, quien.HeadId,
                                                    null, quien.Id)
                : GameState.LookBytes;
            byte[] detalles = aspecto != null && aspecto.Length > 0
                ? DatabaseManager.ReconstructActorDetails(aspecto, GameState.CharacterName)
                : GameState.PlayerActorDetails;

            if (detalles != null)
            {
                playerActor.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = detalles });
            }

            // Contextual ID (Field 3)
            playerActor.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = GameState.CharacterId });

            jpvMsg.Fields.Add(new ProtoField { FieldNumber = 15, WireType = 2, BytesValue = playerActor.ToByteArray() });
            int totalActors = 1;

            foreach (var other in SessionRegistry.OnMap(mapId))
            {
                if (other.CharacterId <= 0 || other.CharacterId == GameState.CharacterId) continue;
                var otherCharacter = DatabaseManager.GetCharacterById(other.CharacterId);
                if (otherCharacter == null) continue;
                jpvMsg.Fields.Add(new ProtoField
                {
                    FieldNumber = 15,
                    WireType = 2,
                    BytesValue = ConnectionProtocol.BuildPlayerActorBlock(
                        otherCharacter, other.State.CellId, other.State.Orientation, other.AccountId)
                });
                totalActors++;
            }

            // B. Los NPCs, con el id que ya llevan puesto desde el arranque.
            //
            // Salen de Managers.Npcs y no de una consulta propia: eran dos listas de las mismas
            // filas, una ordenada por Id y la otra sin ORDER BY ninguno, y de ahí salían los ids.
            // Que coincidieran era suerte del plan que eligiera SQLite. De paso se ahorra un
            // recorrido entero de NpcSpawns en CADA carga de mapa.
            var spawns = Managers.Npcs.Of(mapId);
            LogDebug($"[Game Node] Building dynamic jpv for Map ID: {mapId} containing {spawns.Count} database NPCs.");

            foreach (var spawn in spawns)
            {
                var npcActorMsg = BuildNpcActorMsg(spawn);
                jpvMsg.Fields.Add(new ProtoField { FieldNumber = 15, WireType = 2, BytesValue = npcActorMsg.ToByteArray() });
                LogDebug($"[Game Node] Spawned NPC {spawn.NpcId} at Cell {spawn.Cell} with contextual ID {spawn.ContextualId}.");
                totalActors++;
            }

            // C. Add Aggressive Mobs
            var mobs = Managers.MobSpawnManager.GetMobsForMap(mapId);
            int spawnedMobs = 0;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n======================================================================");
            Console.WriteLine($"[MAP LOAD] Loaded Map ID: {mapId} | Mobs on map: {mobs.Count}");

            foreach (var mob in mobs)
            {
                if (mob.Members.Count > 0)
                {
                    byte[] mobBytes = BuildMobGroupActorMsgBytes(mob, mob.MobId);
                    jpvMsg.Fields.Add(new ProtoField { FieldNumber = 15, WireType = 2, BytesValue = mobBytes });
                    spawnedMobs++;
                    totalActors++;

                    Console.WriteLine($"  -> MobGroup #{mob.MobId} at Cell {mob.CellId} ({mob.Members.Count} members):");
                    foreach (var member in mob.Members)
                    {
                        Console.WriteLine($"     * Monster ID: {member.Monster.Id} | Grade: {member.GradeIndex} | Level: {member.Level}");
                    }
                }
            }
            Console.WriteLine($"======================================================================\n");
            Console.ResetColor();

            LogDebug($"[Game Node] Spawned {spawnedMobs} mobs on Map {mapId}. Total actors in jpv (Field 15): {totalActors}");

            return jpvMsg;
        }

        private static ProtoMessage BuildNpcActorMsg(Managers.Npcs.Spawn spawn)
        {
            // 1. Build Disposition (LFJ)
            var lfjMsg = new ProtoMessage();
            lfjMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = spawn.Cell });
            lfjMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = spawn.Orientation });

            // 2. Build root EntityLook (lkr)
            var rootLook = new ProtoMessage();
            rootLook.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = spawn.BoneId }); // Field 1: bonesId = NPC Bone ID!
            rootLook.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3 }); // Field 3: constant 3

            int npcScale = 100;
            if (!string.IsNullOrEmpty(spawn.RawLook) && spawn.RawLook.Contains("|"))
            {
                var parts = spawn.RawLook.Trim('{', '}').Split('|');
                if (parts.Length > 3 && int.TryParse(parts[3], out int sc))
                {
                    npcScale = sc;
                }
            }
            rootLook.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 2, BytesValue = EscalaEmpaquetada(npcScale) }); // Field 8: scale (packed VarInt)

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
            actorMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = spawn.ContextualId });

            return actorMsg;
        }

        /// <summary>
        /// El grupo de monstruos como actor del mapa. Su id contextual es su MobId, el mismo con el
        /// que viaja en el jss y el mismo que el cliente devuelve al clicarlo para atacar.
        /// </summary>
        private static byte[] BuildMobGroupActorMsgBytes(Managers.MobSpawnManager.MobGroup mob, long contextualId)
        {
            var mainMob = mob.Members[0].Monster;

            int defaultBone = 1;
            int npcScale = 100;
            if (!string.IsNullOrEmpty(mainMob.Look))
            {
                var inner = mainMob.Look.Trim('{', '}');
                var parts = inner.Split('|');
                if (parts.Length > 0 && int.TryParse(parts[0], out int b)) defaultBone = b;
                if (parts.Length > 3 && int.TryParse(parts[3], out int sc)) npcScale = sc;
            }

            Console.WriteLine($"[MobSpawnManager] Mob #{mob.MobId} MainMonster ID={mainMob.Id}, Look='{mainMob.Look}', defaultBone={defaultBone}");

            // 1. Build Disposition (lfj)
            var lfjMsg = new ProtoMessage();
            lfjMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = mob.CellId });
            lfjMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 3 });

            // 2. Build root EntityLook (lkr) -> Field 1 of Details!
            var rootLook = new ProtoMessage();
            rootLook.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = defaultBone });
            rootLook.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3 });
            if (npcScale != 100)
            {
                rootLook.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 2, BytesValue = EscalaEmpaquetada(npcScale) });
            }

            // 3. Build Mob Members List (Field 2 of Details)
            var membersPayload = new ProtoMessage();
            for (int i = 0; i < mob.Members.Count; i++)
            {
                var member = mob.Members[i];
                var memberInner = new ProtoMessage();
                memberInner.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = member.Monster.Id });
                memberInner.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = member.GradeIndex > 0 ? member.GradeIndex : 1 });

                // Field 5: Member Look (lkr) -> ONLY for underlings (i > 0)!
                // Leader (i == 0) MUST NOT have Field 5 because its look is defined in rootLook (Details.Field 1).
                if (i > 0)
                {
                    int mBone = 1;
                    int mScale = 100;
                    if (!string.IsNullOrEmpty(member.Monster.Look))
                    {
                        var mInnerStr = member.Monster.Look.Trim('{', '}');
                        var mParts = mInnerStr.Split('|');
                        if (mParts.Length > 0 && int.TryParse(mParts[0], out int mb)) mBone = mb;
                        if (mParts.Length > 3 && int.TryParse(mParts[3], out int msc)) mScale = msc;
                    }

                    var memberLook = new ProtoMessage();
                    memberLook.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = mBone });
                    memberLook.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 3 });
                    if (mScale != 100)
                    {
                        memberLook.Fields.Add(new ProtoField { FieldNumber = 8, WireType = 2, BytesValue = new byte[] { (byte)mScale } });
                    }
                    memberInner.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 2, BytesValue = memberLook.ToByteArray() });
                }

                if (member.Level > 0)
                {
                    memberInner.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = member.Level });
                }

                int memberFieldNum = (i == 0) ? 1 : 3;
                membersPayload.Fields.Add(new ProtoField { FieldNumber = memberFieldNum, WireType = 2, BytesValue = memberInner.ToByteArray() });
            }

            var membersContainer = new ProtoMessage();
            membersContainer.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = -1 });
            membersContainer.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = membersPayload.ToByteArray() });
            membersContainer.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 1 });

            var mobStaticInfoContainer = new ProtoMessage();
            mobStaticInfoContainer.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = membersContainer.ToByteArray() });

            // 4. Build Details Container -> Field 1: lkr, Field 2: mobStaticInfoContainer
            var detailsMsg = new ProtoMessage();
            detailsMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = rootLook.ToByteArray() });
            detailsMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = mobStaticInfoContainer.ToByteArray() });

            // 5. Build Root Actor
            var actorMsg = new ProtoMessage();
            actorMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 2, BytesValue = lfjMsg.ToByteArray() });
            actorMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = detailsMsg.ToByteArray() });
            actorMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = contextualId });

            return actorMsg.ToByteArray();
        }

        private static byte[] SerializeVarInt(ulong value)
        {
            using var ms = new MemoryStream();
            while (value >= 0x80)
            {
                ms.WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            ms.WriteByte((byte)(value & 0x7F));
            return ms.ToArray();
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

        /// <summary>
        /// La escala como lista empaquetada de varints, que es lo que espera el cliente.
        ///
        /// Escribirla como un byte suelto sólo funciona por debajo de 128: la montaña de kamas va a
        /// 200 y eso deja un varint a medias que revienta el parseo del mensaje entero. Lo mismo le
        /// pasaba a los cincuenta y dos NPCs de Astrub cuando iban inflados.
        /// </summary>
        private static byte[] EscalaEmpaquetada(int escala)
        {
            var fuera = new System.Collections.Generic.List<byte>();
            uint valor = (uint)escala;
            while (valor >= 0x80)
            {
                fuera.Add((byte)((valor & 0x7F) | 0x80));
                valor >>= 7;
            }
            fuera.Add((byte)valor);
            return fuera.ToArray();
        }
    }
}
