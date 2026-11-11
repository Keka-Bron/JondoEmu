using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using System;
using System.Collections.Generic;
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
        /// <summary>Every characteristic id in the measured catalogue, in catalogue order.</summary>
        public static IReadOnlyList<int> CharacteristicIds => _characteristicIds;
        private static readonly List<int> _characteristicIds = new List<int>();
        private static readonly Dictionary<int, int> _containers = new Dictionary<int, int>();

        public static int ContainerOf(int characteristicId)
            => _containers.TryGetValue(characteristicId, out int field) ? field : 4;

        /// <summary>
        /// Loads the characteristics catalogue independently of world-entry packets.
        /// The catalogue specifies both the declared ids and each value's container.
        /// </summary>
        public static void Initialize()
        {
            _characteristicIds.Clear();
            _containers.Clear();
            try
            {
                string path = Paths.CharacteristicFieldsJson;
                if (!System.IO.File.Exists(path))
                {
                    Console.WriteLine("[World] datos/caracteristicas_kub.json is not there: the characteristics " +
                                      "sheet will come out short.");
                    return;
                }

                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    int id = entry.GetProperty("id").GetInt32();
                    int container = entry.GetProperty("hueco").GetInt32();
                    if (!_characteristicIds.Contains(id)) _characteristicIds.Add(id);
                    _containers[id] = container;
                }

                Console.WriteLine($"[World] {_characteristicIds.Count} characteristics read from " +
                                  $"datos/caracteristicas_kub.json.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[World] Could not read the characteristics list: {ex.Message}");
            }
        }

        /// <summary>
        /// Initial state after selection: semantic sends, live feature hooks, then
        /// session-owned HAAPI identity and account gifts, without a client wait.
        /// </summary>
        public static async Task<int> SendInitialStateAsync(GameSession session, DatabaseManager.DbCharacter character)
        {
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

            await EnviarAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
            Console.WriteLine($"[World] Characteristics sent for {character.Name}: level " +
                              $"{SessionContext.State.CharacterLevel}, {SessionContext.State.Kamas} kamas.");

            Console.WriteLine($"[World] Initial state sent: {sent} directly constructed messages for " +
                              $"{character.Name}.");

            // Keep the continuation after the live hooks and characteristics, without
            // waiting for lqc. That later confirmation triggers map initialization.
            return sent + await GameEntryContinuationMessages.Live.SendAsync(session);
        }

        /// <summary>Writes a frame through the entry projection and owning session's traffic log.</summary>
        private static Task EnviarAsync(NetworkStream stream, byte[] message)
            => GameEntryMessages.SendAsync(stream, message);

        /// <summary>Direct current map, spell and shortcut state.</summary>
        /// <param name="fightToRejoin">
        /// Set when the character is going straight back into a fight. The opening uses
        /// the fight context and arena, followed by the reconnection notice and actors.
        /// </param>
        public static async Task<int> SendMapAsync(GameSession session, DatabaseManager.DbCharacter character, long mapId,
                                                    Jondo.Unity.World.Fights.FightInstance? fightToRejoin = null)
        {
            using var context = SessionContext.Push(session);
            var stream = session.Stream ?? throw new InvalidOperationException("Map entry requires a connected game session.");
            int sent = await GameEntryMapMessages.Live.SendAsync(session,
                MapEntryOpeningMessages.Live, fightToRejoin == null
                    ? Jondo.Unity.Protocol.Game.GameContext.RolePlay : Jondo.Unity.Protocol.Game.GameContext.Fight,
                mapId, fightToRejoin == null ? null : SendReconnectionAsync);

            async Task<int> SendReconnectionAsync()
            {
                await EnviarAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildBackInTheFight(character.Name)));
                await EnviarAsync(stream, ConnectionProtocol.BuildActorsComplete());
                return 2;
            }

            // Repeat the characteristics at map entry; the client keeps this second sheet.
            await EnviarAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));

            // Both entry snapshots use the same live catalogue policy for known zaaps.
            var discovered = EntryEnvironmentMessages.Live.Zaaps();
            await session.SendAsync(discovered);

            Console.WriteLine($"[World] Block 3 sent: {sent} messages, map {mapId}, characteristics " +
                              $"resent, {discovered.Destinations.Count} zaaps descubiertos.");
            return sent;
        }
    }
}
