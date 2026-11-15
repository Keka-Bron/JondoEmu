using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Entry initialization combining directly constructed semantic messages,
    /// server-state snapshots and feature hooks.
    /// GameEntryService sends the initial state after selection; map
    /// initialization follows the client's sequence confirmation, with a first-ping fallback.
    /// Initial state and its HAAPI/gift continuation are constructed directly in the same operation.
    /// </summary>
    public static class WorldEntry
    {
        /// <summary>
        /// Initial state after selection: semantic sends, live feature hooks, then
        /// session-owned HAAPI identity and account gifts, without a client wait.
        /// </summary>
        public static async Task<int> SendInitialStateAsync(GameSession session, DbCharacter character)
        {
            using var context = SessionContext.Push(session);
            var stream = session.Stream ?? throw new InvalidOperationException("World entry requires a connected game session.");
            var initial = new GameEntryInitialMessages(
                Managers.HavenBagStore.RoomsOf(character.Id), Managers.Merkasako.Themes,
                session.State, new ProfessionMessages(Handlers.ArtisanHandler.Jobs(), Managers.SkillManager.ForJob),
                Managers.Alignments.Rank(), Managers.ExperienceLimit.Of(character.Id),
                () => AlterationMessages.FromCatalogue.Snapshot(Managers.Alterations.Of(character.Id), DateTime.UtcNow),
                () => ArenaPlayerInformationService.Live.Snapshot(character.Id, character.Level),
                () => GuildRankService.Live.Initial(character.Id),
                () => CharacterSelectionMessages.Success(character),
                () => InventoryMessages.FromCatalogue.Snapshot(session.State),
                EntryEnvironmentMessages.Live.Zaaps, EntryEnvironmentMessages.Live.Calendar,
                () => EntryInformationMessages.PreviousConnection(session.State.PreviousVisit), AfterSelectionMessages.Live,
                InitialPresentationMessages.Live(character));
            int sent = await initial.SendAsync(session);

            // The jobs this character keeps in the public artisans' list, so that the window's
            // box says so after a relog and the next toggle takes them off rather than on.
            await ProfessionMessages.SendSubscriptionsAsync(session);

            // Ranks precede guild membership, header and member list so custom member
            // rank ids are available when the client receives its own guild.
            await Handlers.GuildHandler.SendMembershipAtEntryAsync(session);

            // The alliance, if he has one: its ranks and belonging (mbz).
            await Handlers.AllianceHandler.OnWorldEntryAsync(session);

            // His mutual friends who asked to be warned hear he is in.
            await Handlers.FriendsHandler.OnWorldEntryAsync(session);

            // The world's prisms, for the world map (ife): everybody gets them, alliance or not.
            await Handlers.PrismHandler.OnWorldEntryAsync(stream);

            // What happened to his tax collectors while he was away (hkx).
            await Handlers.TaxCollectorHandler.OnWorldEntryAsync(stream, character.Id);

            // Inside a dream: restore it, or leave it when there is none to go back to.
            await Handlers.DreamHandler.OnWorldEntryAsync(stream);

            // The treasure hunts under way (hhf), as they stand.
            await Handlers.TreasureHuntHandler.OnWorldEntryAsync(stream, character.Id);

            // Inside a house: which one, when the way in was not kept.
            Handlers.HouseHandler.OnWorldEntry(SessionContext.Current.AccountId);

            // The guild's houses, for the guild window's «Casas» tab (jjs).
            await Handlers.HouseHandler.SendGuildHousesAsync(stream, character.Id);

            byte[]? sales = Handlers.MarketplaceHandler.SalesHistoryAtEntry(character.Id, SessionContext.Current.AccountId);
            if (sales != null) await EnviarAsync(stream, ConnectionProtocol.Push(Op.Las, sales));

            await session.SendAsync(CharacteristicsMessages.Live.Snapshot(session));
            sent++;
            Console.WriteLine($"[World] Characteristics sent for {character.Name}: level " +
                              $"{session.State.CharacterLevel}, {session.State.Kamas} kamas.");

            // Keep the continuation after the live hooks and characteristics, without
            // waiting for lqc. That later confirmation triggers map initialization.
            int tail = await GameEntryContinuationMessages.Live.SendAsync(session);
            Console.WriteLine($"[World] Initial state and continuation sent: {sent + tail} directly constructed messages for " +
                              $"{character.Name}.");
            return sent + tail;
        }

        /// <summary>Writes a frame through the entry projection and owning session's traffic log.</summary>
        private static Task EnviarAsync(NetworkStream stream, byte[] message)
            => GameEntryMessages.SendAsync(stream, message);

        /// <summary>Sends map-entry state, then fresh characteristics and known zaaps.</summary>
        /// <param name="fightToRejoin">
        /// Set when the character is going straight back into a fight. The opening uses
        /// the fight context and arena, followed by the reconnection notice and actors.
        /// </param>
        public static async Task<int> SendMapEntryStateAsync(GameSession session, DbCharacter character, long mapId,
                                                    Jondo.Unity.World.Fights.FightInstance? fightToRejoin = null)
        {
            using var context = SessionContext.Push(session);
            _ = session.Stream ?? throw new InvalidOperationException("Map entry requires a connected game session.");
            int sent = await GameEntryMapMessages.Live.SendAsync(session,
                MapEntryOpeningMessages.Live, fightToRejoin == null
                    ? Jondo.Unity.Protocol.Game.GameContext.RolePlay : Jondo.Unity.Protocol.Game.GameContext.Fight,
                mapId, fightToRejoin == null ? null : SendReconnectionAsync);

            // Snapshot after the map sequence commits the roleplay regeneration timestamp.
            await session.SendAsync(CharacteristicsMessages.Live.Snapshot(session));
            await session.SendAsync(EntryEnvironmentMessages.Live.Zaaps());
            sent += 2;

            Task<int> SendReconnectionAsync()
                => EntryInformationMessages.SendFightReconnectionAsync(session, character.Name);

            Console.WriteLine($"[World] Block 3 sent: {sent} messages, map {mapId}, characteristics " +
                              $"and known zaaps resent.");
            return sent;
        }
    }
}
