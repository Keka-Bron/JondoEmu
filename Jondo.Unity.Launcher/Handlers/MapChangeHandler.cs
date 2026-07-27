using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
{
    public static class MapChangeHandler
    {
        public static async Task HandleMapChangeRequest(NetworkStream stream, byte[] payload)
        {
            LogDebug("[Map Change] Received Map Change Request (jos)");
            byte[]? inner = NetworkEnvelope.ExtractMessagePayload(payload, "type.ankama.com/jos");
            if (inner != null)
            {
                try
                {
                    // Natively parse jos using the compiled Protobuf class
                    var josMsg = Jondo.Unity.Protocol.Messages.jos.Parser.ParseFrom(inner);
                    long requestedMapId = josMsg.Fuou; // Field 1: destination map ID

                    if (requestedMapId > 0)
                    {
                        LogDebug($"[Map Change] Client requested map transition to Map ID: {requestedMapId}");
                        
                        if (requestedMapId == GameState.MapId)
                        {
                            LogDebug("[Map Change] Requested Map ID matches current Map ID. Ignoring transition.");
                            return;
                        }
                        
                        // Calculate spawn cell on the new map based on transition direction
                        string direction = "Right"; // fallback
                        var oldMapInfo = MapManager.GetMapInfo(GameState.MapId);
                        var newMapInfo = MapManager.GetMapInfo(requestedMapId);
                        if (oldMapInfo != null && newMapInfo != null)
                        {
                            if (newMapInfo.PosX > oldMapInfo.PosX) direction = "Right";
                            else if (newMapInfo.PosX < oldMapInfo.PosX) direction = "Left";
                            else if (newMapInfo.PosY > oldMapInfo.PosY) direction = "Down";
                            else if (newMapInfo.PosY < oldMapInfo.PosY) direction = "Up";
                        }
                        
                        int spawnCellId = GetTransitionSpawnCell(requestedMapId, GameState.CellId, direction);
                        LogDebug($"[Map Change] Transition direction: {direction} | Last Cell: {GameState.CellId} | New Spawn Cell: {spawnCellId}");
                        
                        int newOrientation = GameState.Orientation;
                        if (direction == "Right") newOrientation = 1;
                        else if (direction == "Left") newOrientation = 5;
                        else if (direction == "Down") newOrientation = 3;
                        else if (direction == "Up") newOrientation = 7;
                        GameState.Orientation = newOrientation;

                        GameState.CellId = spawnCellId;
                        GameState.MapId = requestedMapId;
                        DatabaseManager.SaveCurrentCharacter();
                        LogDebug($"[Map Change] Saved updated map, cell (CellId={spawnCellId}), and orientation ({GameState.Orientation}) to database.");

                        // Natively build and send joh (CurrentMapMessage)
                        var johMsg = new Jondo.Unity.Protocol.Messages.joh
                        {
                            Fumx = requestedMapId // Field 2: Map ID
                        };
                        byte[] johBytes = johMsg.ToByteArray();
                        byte[] johPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/joh", johBytes);
                        
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, johPacket);
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
                    int orientation = GameState.Orientation;
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
                        GameState.CellId = lastCell;
                        GameState.MapId = mapId; // Update MapId from client movement request to prevent desynchronization
                        GameState.Orientation = orientation;
                        Console.WriteLine($"[Movement] Updated GameState.CellId to: {lastCell}, GameState.MapId to: {mapId}, and GameState.Orientation to: {orientation}");
                        DatabaseManager.SaveCurrentCharacter();
                        Console.WriteLine("[Movement] Saved updated cell, map, and orientation to database.");
                    }

                    // Build and send joo (Movement Broadcast) natively using compiled class
                    var jooMsg = new Jondo.Unity.Protocol.Messages.joo
                    {
                        Funv = GameState.CharacterId,
                        Funz = 2
                    };
                    jooMsg.Funw.AddRange(pathList);

                    byte[] jooBytes = jooMsg.ToByteArray();
                    byte[] jooPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/joo", jooBytes);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, jooPacket);
                    Console.WriteLine($"[Movement] Sent native joo (Movement Broadcast) for Character {GameState.CharacterId}");
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
