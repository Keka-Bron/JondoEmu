using System;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// A game session's way out, whichever listener it came in through: off its map, out of
    /// what it left half done, off a fight's arena, saved, and its people told.
    /// </summary>
    /// <remarks>
    /// The two listeners each had their own, and they had drifted apart. Port 5555's -- the one the
    /// real client plays through (GameServerProxy) -- saved the character as it stood, so a client
    /// closed in the middle of a fight was saved on the arena: the next login stood him on the
    /// Kanojedo's tactical map, no way out and monsters around him. It also left a trade, a
    /// commission or a workshop open and told neither the guild nor the raid. The Game Node's had
    /// all that and told neither the party nor the alliance.
    /// </remarks>
    internal static class SessionTeardown
    {
        /// <param name="listener">The listener's tag for the console: "Game Server" or "Game Node".</param>
        public static async Task LeaveAsync(GameSession session, string listener)
        {
            if (session.IsInWorld)
            {
                try
                {
                    await SessionRegistry.RemoveFromMapAsync(session.MapId, session.CharacterId, session.Id);
                }
                catch { }

                // And out of the party, if he was in one: the ones who stay have to be told,
                // because otherwise they see a member who no longer exists.
                try
                {
                    using (SessionContext.Push(session))
                        await PartyHandler.DisconnectedAsync(session.CharacterId);
                }
                catch { }

                // And his alliance sees him disconnected.
                try
                {
                    await AllianceHandler.DisconnectedAsync(session.CharacterId);
                }
                catch { }

                session.LeaveWorld();
            }

            // A tax collector's items left in the recycling window go to the bag.
            try { RecycleHandler.Abandon(session); } catch { }
            ConquestHandler.Forget(session.State.CharacterId);

            // A commission half done ends for the one left behind too, and so do a trade and a
            // workshop.
            try { await CommissionHandler.AbandonAsync(session); } catch { }
            try { await TradeHandler.AbandonAsync(session); } catch { }
            try { await ArtisanHandler.LeftAsync(session); } catch { }

            if (session.State.CharacterId > 0)
            {
                try
                {
                    using (SessionContext.Push(session))
                    {
                        // Off the arena first, a spectator too: he keeps his roleplay map where a
                        // fighter does. The fight goes on without him, and he can come back to it.
                        FightHandler.ForgetSpectator(session);
                        FightHandler.BackToRoleplayMap();
                        DatabaseManager.SaveCurrentCharacter();
                    }
                    Console.WriteLine($"[{listener}] {session.State.CharacterName} saved on the way out: " +
                                      $"map {session.State.MapId}, cell {session.State.CellId}.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{listener}] Could not save session {session.Id}: {ex.Message}");
                }
            }

            long gone = session.State.CharacterId;
            SessionRegistry.Unregister(session);
            await FriendsHandler.LeftAsync(session.AccountId);
            if (gone != 0)
            {
                try { await GuildRaidHandler.OnLeaveWorldAsync(gone); }
                catch (Exception ex) { Console.WriteLine($"[Raids] Could not tell the raid {gone} left: {ex.Message}"); }
                try { await GuildHandler.TellConnectionAsync(gone, online: false); }
                catch (Exception ex) { Console.WriteLine($"[Guild] Could not tell the guild {gone} left: {ex.Message}"); }
            }
        }
    }
}
