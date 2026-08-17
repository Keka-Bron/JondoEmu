using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Launcher.Managers;
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
                    case ".help":
                        await SendFeedbackAsync(stream,
                            "Commandes: .help, .kamas <montant>, .level <1-200>, .tp <MapId>.");
                        break;

                    case ".kamas":
                        if (parts.Length != 2 || !long.TryParse(parts[1], out long kamasAmount))
                        {
                            await SendFeedbackAsync(stream, "Utilisation: .kamas <montant>");
                            break;
                        }

                        long currentKamas = SessionContext.State.Kamas;
                        long newKamas;
                        try { newKamas = checked(currentKamas + kamasAmount); }
                        catch (OverflowException) { newKamas = kamasAmount > 0 ? long.MaxValue : 0; }

                        SessionContext.State.Kamas = Math.Max(0, newKamas);
                        DatabaseManager.SaveCurrentCharacter();

                        // Current 3.6 protocol: ivf updates the purse and kub refreshes the sheet.
                        await NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.Push("ivf", ConnectionProtocol.BuildKamas(SessionContext.State.Kamas)));
                        await SendCharacteristicsAsync(stream);
                        await SendFeedbackAsync(stream, $"Kamas: {SessionContext.State.Kamas}.");
                        Console.WriteLine($"[CommandHandler] Updated Kamas to {SessionContext.State.Kamas}");
                        break;

                    case ".level":
                        if (parts.Length != 2 || !int.TryParse(parts[1], out int newLevel) ||
                            newLevel is < 1 or > 200)
                        {
                            await SendFeedbackAsync(stream, "Utilisation: .level <1-200>");
                            break;
                        }

                        int oldLevel = Math.Clamp(SessionContext.State.CharacterLevel, 1, 200);
                        int spentCapital = Math.Max(0,
                            ((oldLevel - 1) * 5) - SessionContext.State.CharacterRemainingPoints);

                        SessionContext.State.CharacterLevel = newLevel;
                        SessionContext.State.CharacterRemainingPoints = Math.Max(0,
                            ((newLevel - 1) * 5) - spentCapital);
                        SessionContext.State.Experience = ExperienceTable.LevelFloor(newLevel);
                        DatabaseManager.SaveCurrentCharacter();

                        // kub is the live 3.6 characteristics packet. The previous bcy/krb/krd
                        // sequence belongs to the retired protocol and is ignored by this client.
                        await SendCharacteristicsAsync(stream);
                        await SendFeedbackAsync(stream,
                            $"Niveau: {oldLevel} -> {newLevel} ({SessionContext.State.CharacterRemainingPoints} points disponibles).");
                        Console.WriteLine($"[CommandHandler] Updated Level to {newLevel}");
                        break;

                    case ".tp":
                        if (parts.Length != 2 || !long.TryParse(parts[1], out long targetMapId) ||
                            targetMapId <= 0)
                        {
                            await SendFeedbackAsync(stream, "Utilisation: .tp <MapId>");
                            break;
                        }

                        if (SessionContext.State.IsInFight)
                        {
                            await SendFeedbackAsync(stream, "Impossible de se teleporter pendant un combat.");
                            break;
                        }

                        int? arrivalCell = await WorldMoveHandler.TeleportAsync(stream, targetMapId);
                        if (!arrivalCell.HasValue)
                        {
                            await SendFeedbackAsync(stream,
                                $"Teleportation impossible: la map {targetMapId} est inconnue ou sans cellule marchable.");
                            break;
                        }

                        Console.WriteLine($"[CommandHandler] Teleported to map {targetMapId}, cell {arrivalCell.Value}");
                        break;

                    default:
                        await SendFeedbackAsync(stream,
                            $"Commande inconnue: {cmd}. Tapez .help pour voir la liste.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CommandHandler] Error processing command: {ex.Message}");
                await SendFeedbackAsync(stream, "La commande a echoue. Consultez la console du serveur.");
            }
        }

        private static Task SendCharacteristicsAsync(NetworkStream stream) =>
            NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("kub", ConnectionProtocol.BuildCharacteristics()));

        private static Task SendFeedbackAsync(NetworkStream stream, string text)
        {
            byte[] line = ConnectionProtocol.BuildChatLine("Commandes", 0, 0, text, 0);
            return NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push("kti", line));
        }
    }
}
