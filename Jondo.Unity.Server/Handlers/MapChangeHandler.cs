using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    public static class MapChangeHandler
    {
        public static async Task HandleMapChangeRequest(NetworkStream stream, byte[] payload)
        {
            LogDebug("[Map Change] Received Map Change Request (jos)");
            byte[]? inner = NetworkEnvelope.ExtractMessagePayload(payload, Op.Uri(Op.Jos));
            if (inner != null)
            {
                try
                {
                    // Natively parse jos using the compiled Protobuf class
                    var josMsg = Jondo.Unity.Protocol.Messages.jos.Parser.ParseFrom(inner);
                    long requestedMapId = josMsg.Fuou; // Field 1: destination map ID

                    if (requestedMapId > 0)
                    {
                        long oldMapId = SessionContext.State.MapId;
                        LogDebug($"[Map Change] Client requested map transition to Map ID: {requestedMapId}");
                        
                        if (requestedMapId == Jondo.Unity.Server.Network.SessionContext.State.MapId)
                        {
                            LogDebug("[Map Change] Requested Map ID matches current Map ID. Ignoring transition.");
                            return;
                        }
                        
                        // Calculate spawn cell on the new map based on transition direction
                        string direction = "Right"; // fallback
                        var oldMapInfo = MapManager.GetMapInfo(Jondo.Unity.Server.Network.SessionContext.State.MapId);
                        var newMapInfo = MapManager.GetMapInfo(requestedMapId);
                        if (oldMapInfo != null && newMapInfo != null)
                        {
                            if (newMapInfo.PosX > oldMapInfo.PosX) direction = "Right";
                            else if (newMapInfo.PosX < oldMapInfo.PosX) direction = "Left";
                            else if (newMapInfo.PosY > oldMapInfo.PosY) direction = "Down";
                            else if (newMapInfo.PosY < oldMapInfo.PosY) direction = "Up";
                        }
                        
                        int spawnCellId = GetTransitionSpawnCell(requestedMapId, Jondo.Unity.Server.Network.SessionContext.State.CellId, direction);
                        LogDebug($"[Map Change] Transition direction: {direction} | Last Cell: {Jondo.Unity.Server.Network.SessionContext.State.CellId} | New Spawn Cell: {spawnCellId}");
                        
                        int newOrientation = Jondo.Unity.Server.Network.SessionContext.State.Orientation;
                        if (direction == "Right") newOrientation = 1;
                        else if (direction == "Left") newOrientation = 5;
                        else if (direction == "Down") newOrientation = 3;
                        else if (direction == "Up") newOrientation = 7;
                        Jondo.Unity.Server.Network.SessionContext.State.Orientation = newOrientation;

                        Jondo.Unity.Server.Network.SessionContext.State.CellId = spawnCellId;
                        Jondo.Unity.Server.Network.SessionContext.State.MapId = requestedMapId;
                        DatabaseManager.SaveCurrentCharacter();
                        LogDebug($"[Map Change] Saved updated map, cell (CellId={spawnCellId}), and orientation ({Jondo.Unity.Server.Network.SessionContext.State.Orientation}) to database.");

                        // Natively build and send joh (CurrentMapMessage)
                        var johMsg = new Jondo.Unity.Protocol.Messages.joh
                        {
                            Fumx = requestedMapId // Field 2: Map ID
                        };
                        byte[] johBytes = johMsg.ToByteArray();
                        byte[] johPacket = NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Joh), johBytes);
                        
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, johPacket);

                        // Los dos avisos —jsd al mapa que deja, jsn al que llega— los da la misma
                        // pieza para los cuatro caminos que cambian de mapa. Aquí estaban escritos
                        // a mano, y era la única de las cuatro que los daba: el zaap, el borde y
                        // el .teleport se quedaron sin ellos y sólo se notaba jugando de dos en dos.
                        // Va detrás del joh para que el que llega esté ya contado en el mapa nuevo.
                        //
                        // La dirección que viaja en el jsd es la CARDINAL de las capturas —0
                        // derecha, 2 abajo, 4 izquierda, 6 arriba—, no el newOrientation de aquí
                        // arriba, que usa las impares (1/5/3/7). Las cuatro medidas están en la
                        // cabecera de WorldMoveHandler, sacadas de las capturas de Movimiento.
                        int haciaDonde = direction switch
                        {
                            "Right" => 0,
                            "Down" => 2,
                            "Left" => 4,
                            "Up" => 6,
                            _ => 0,
                        };
                        await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, oldMapId, haciaDonde);

                        LogDebug($"[Map Change] Sent native joh (CurrentMapMessage) for Map ID: {requestedMapId}");
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"[-] Error handling map change request natively: {ex.Message}");
                }
            }
        }

        public static async Task HandleMovementRequest(NetworkStream stream, byte[] payload)
        {
            if (Jondo.Unity.Server.Network.SessionContext.State.IsInFight)
            {
                // GameNodeProxy routes combat movement straight to FightHandler. Getting here means
                // the routing has changed and combat movement would end up being handled as
                // roleplay movement (a teleport, with no MP spent).
                Program.LogDebug("[Movement][WARN] A combat joi reached MapChangeHandler. " +
                                 "Check the GameNodeProxy routing: it must go to FightHandler.");
                return;
            }

            LogDebug("[Movement] Received GameMapMovementRequestMessage (joi)");
            byte[]? inner = NetworkEnvelope.ExtractMessagePayload(payload, "type.ankama.com/joi");
            if (inner != null)
            {
                try
                {
                    // Natively parse joi using the compiled Protobuf class
                    var joiMsg = Jondo.Unity.Protocol.Messages.joi.Parser.ParseFrom(inner);
                    long mapId = joiMsg.Funb;
                    var pathList = joiMsg.Fune;

                    int lastCell = 0;
                    int orientation = Jondo.Unity.Server.Network.SessionContext.State.Orientation;
                    if (pathList.Count > 0)
                    {
                        lastCell = pathList[^1] % 4096;
                        int extractedOrientation = pathList[^1] / 4096;
                        if (extractedOrientation >= 0 && extractedOrientation <= 7)
                        {
                            orientation = extractedOrientation;
                        }
                    }

                    if (lastCell > 0)
                    {
                        Jondo.Unity.Server.Network.SessionContext.State.CellId = lastCell;
                        Jondo.Unity.Server.Network.SessionContext.State.MapId = mapId; // Update MapId from client movement request to prevent desynchronization
                        Jondo.Unity.Server.Network.SessionContext.State.Orientation = orientation;
                        Console.WriteLine($"[Movement] Updated Jondo.Unity.Server.Network.SessionContext.State.CellId to: {lastCell}, Jondo.Unity.Server.Network.SessionContext.State.MapId to: {mapId}, and Jondo.Unity.Server.Network.SessionContext.State.Orientation to: {orientation}");
                        DatabaseManager.SaveCurrentCharacter();
                        Console.WriteLine("[Movement] Saved updated cell, map, and orientation to database.");
                    }

                    // Build and send joo (Movement Broadcast) natively using compiled class
                    var jooMsg = new Jondo.Unity.Protocol.Messages.joo
                    {
                        Funv = Jondo.Unity.Server.Network.SessionContext.State.CharacterId,
                        Funz = 2
                    };
                    jooMsg.Funw.AddRange(pathList);

                    byte[] jooBytes = jooMsg.ToByteArray();
                    byte[] jooPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/joo", jooBytes);
                    int delivered = await SessionRegistry.BroadcastToMapAsync(mapId, jooPacket);
                    Console.WriteLine($"[Movement] Broadcast joo for Character {Jondo.Unity.Server.Network.SessionContext.State.CharacterId} to {delivered} session(s)");

                    // Mob Collision Detection: Check if the destination cell has a mob group
                    if (!Jondo.Unity.Server.Network.SessionContext.State.IsInFight && lastCell > 0)
                    {
                        var mob = Managers.MobSpawnManager.GetMobAtCell(mapId, lastCell);
                        if (mob != null)
                        {
                            Console.ForegroundColor = ConsoleColor.Magenta;
                            Console.WriteLine($"[FIGHT!] Player collided with Mob Group #{mob.MobId} at cell {mob.CellId} on map {mapId}! Initiating PVM combat...");
                            Console.ResetColor();
                            await FightHandler.InitiateFightFromMobCollision(stream, mob, mapId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Movement] Error handling native movement: {ex.Message}");
                }
            }
        }

        public static async Task HandleMovementConfirm(NetworkStream stream)
        {
            Console.WriteLine("[Game Node] Received Movement Confirm (jpp)");
            
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((3 << 3) | 0)); // Field 3, VarInt
            output.WriteInt64(-1); // Validation status / reference
            output.Flush();
            
            byte[] joqPayload = ms.ToArray();
            byte[] joqPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/joq", joqPayload);
            
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, joqPacket);
            Console.WriteLine("[Game Node] Sent dynamically generated joq (Movement Validation)");
        }

        private static int GetTransitionSpawnCell(long targetMapId, int lastCellId, string direction)
        {
            int row = lastCellId / 14;
            int col = lastCellId % 14;

            if (row < 10) row = 10;
            if (row > 26) row = 26;
            if (col < 4) col = 4;
            if (col > 9) col = 9;
            
            int rawCell = lastCellId;
            if (direction == "Right")
            {
                rawCell = row * 14 + 2;
            }
            else if (direction == "Left")
            {
                rawCell = row * 14 + 11;
            }
            else if (direction == "Down")
            {
                rawCell = 8 * 14 + col;
            }
            else if (direction == "Up")
            {
                rawCell = 28 * 14 + col;
            }

            return MapManager.GetNearestWalkableCell(targetMapId, rawCell);
        }

        private static void LogDebug(string msg)
        {
            Program.LogDebug(msg);
        }
    }
}
