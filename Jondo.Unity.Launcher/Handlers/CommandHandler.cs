using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
{
    public static class CommandHandler
    {
        public static async Task HandleCommand(NetworkStream stream, string commandText)
        {
            Console.WriteLine($"[CommandHandler] Processing command: {commandText}");
            var parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            string cmd = parts[0].ToLower();

            try
            {
                switch (cmd)
                {
                    case ".kamas":
                        if (parts.Length < 2) return;
                        if (long.TryParse(parts[1], out long kamasAmount))
                        {
                            long newKamas = Jondo.Unity.Launcher.Network.SessionContext.State.Kamas + kamasAmount;
                            if (newKamas < 0) newKamas = 0;
                            
                            Jondo.Unity.Launcher.Network.SessionContext.State.Kamas = newKamas;
                            DatabaseManager.SaveCurrentCharacter();

                            // Send KamasUpdateMessage (bvr)
                            var bvrMsg = new ProtoMessage();
                            bvrMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = Jondo.Unity.Launcher.Network.SessionContext.State.Kamas });
                            byte[] bvrPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/bvr", bvrMsg.ToByteArray());
                            await NetworkMessage.WriteFrameAsync(stream, bvrPacket);
                            
                            // Send Chat message as feedback
                            var csmMsg = new ProtoMessage();
                            csmMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = 0 });
                            csmMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 2, BytesValue = System.Text.Encoding.UTF8.GetBytes($"[INFO] Kamas updated to {Jondo.Unity.Launcher.Network.SessionContext.State.Kamas}.") });
                            csmMsg.Fields.Add(new ProtoField { FieldNumber = 5, WireType = 0, VarIntValue = 0 }); // Timestamp
                            csmMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 2, BytesValue = System.Text.Encoding.UTF8.GetBytes("") }); // Fingerprint
                            byte[] csmPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/csm", csmMsg.ToByteArray());
                            await NetworkMessage.WriteFrameAsync(stream, csmPacket);
                            
                            Console.WriteLine($"[CommandHandler] Updated Kamas to {Jondo.Unity.Launcher.Network.SessionContext.State.Kamas}");
                        }
                        break;

                    case ".level":
                        if (parts.Length < 2) return;
                        if (int.TryParse(parts[1], out int newLevel))
                        {
                            if (newLevel < 1) newLevel = 1;
                            if (newLevel > 200) newLevel = 200;

                            int oldLevel = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel;
                            Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel = newLevel;
                            
                            // Recalculate remaining points based on level
                            int alreadySpent = Jondo.Unity.Launcher.Network.SessionContext.State.StatStrength + Jondo.Unity.Launcher.Network.SessionContext.State.StatIntelligence + Jondo.Unity.Launcher.Network.SessionContext.State.StatChance + Jondo.Unity.Launcher.Network.SessionContext.State.StatAgility + Jondo.Unity.Launcher.Network.SessionContext.State.StatVitality + Jondo.Unity.Launcher.Network.SessionContext.State.StatWisdom * 3;
                            Jondo.Unity.Launcher.Network.SessionContext.State.CharacterRemainingPoints = ((newLevel - 1) * 5) - alreadySpent;
                            if (Jondo.Unity.Launcher.Network.SessionContext.State.CharacterRemainingPoints < 0) Jondo.Unity.Launcher.Network.SessionContext.State.CharacterRemainingPoints = 0;

                            DatabaseManager.SaveCurrentCharacter();

                            var updatedKri = StatsHandler.BuildUpdatedKriPacket();
                            if (updatedKri != null)
                            {
                                await NetworkMessage.WriteFrameAsync(stream, updatedKri);
                            }

                            // Update stats panel remaining points
                            byte[] krbPacket = StatsHandler.BuildKrbPacket(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterRemainingPoints);
                            await NetworkMessage.WriteFrameAsync(stream, krbPacket);

                            var emptyKrd = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/krd", new byte[0]);
                            await NetworkMessage.WriteFrameAsync(stream, emptyKrd);

                            var bcyMsg = new ProtoMessage();
                            bcyMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = newLevel });
                            bcyMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = oldLevel });
                            bcyMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 0, VarIntValue = 5 * (newLevel - oldLevel) });
                            bcyMsg.Fields.Add(new ProtoField { FieldNumber = 4, WireType = 0, VarIntValue = 5 * (newLevel - oldLevel) });
                            var levelUpPacket = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/bcy", bcyMsg.ToByteArray());
                            await NetworkMessage.WriteFrameAsync(stream, levelUpPacket);

                            Console.WriteLine($"[CommandHandler] Updated Level to {Jondo.Unity.Launcher.Network.SessionContext.State.CharacterLevel}");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CommandHandler] Error processing command: {ex.Message}");
            }
        }
    }
}
