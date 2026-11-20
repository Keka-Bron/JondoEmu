using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The hero mode: the account's other characters connected beside the one in the world, each
    /// in a session of his own with no socket, and into his master's fights as his to play.
    /// </summary>
    /// <remarks>
    /// With the item and equipment tests: connecting a hero loads his bag and what he wears
    /// (Equipment), which they read too.
    /// </remarks>
    [Collection("forgemagic")]
    public sealed class HeroesTests
    {
        private const long Account = 8_900_000_311;
        /// <summary>
        /// A map of these tests' own, no other test's connection on it: a hero connecting is drawn
        /// for everybody on his map (HeroPresence), and on a map the others share, their pipes read
        /// a jsn they never asked for.
        /// </summary>
        private const long Map = 990_311_001;

        /// <summary>A real map whose walkable cells the walking test lends to <see cref="Map"/>.</summary>
        private const long RealMap = 191105026;

        private static long Character(long account, string name, int breed) => DatabaseManager.CreateCharacter(account, 1, name, breed, 0, 1,
            new long[] { -1, -1, -1, -1, -1 }, Map, 50, 0, 0, Array.Empty<(int, int)>());

        [Fact]
        public async Task A_character_of_the_account_is_connected_as_a_hero_and_let_go()
        {
            string tag = Guid.NewGuid().ToString("N")[..6];
            long master = Character(Account, "Maestro-" + tag, 8);
            long hero = Character(Account, "Heroe-" + tag, 1);
            long stranger = Character(Account + 1, "Ajeno-" + tag, 9);
            await using var pipe = await ClientPipe.OpenAsync(Account, master, Map, "Maestro-" + tag);
            try
            {
                Assert.Equal("self", Heroes.Connect(pipe.Session, master));
                Assert.Equal("account", Heroes.Connect(pipe.Session, stranger));

                Assert.Equal("ok", Heroes.Connect(pipe.Session, hero));
                Assert.Equal("already", Heroes.Connect(pipe.Session, hero));
                var connected = Assert.Single(Heroes.Of(master));
                Assert.Equal(hero, connected.CharacterId);
                // His own sheet, in a session nobody sends to and no map draws.
                Assert.Equal("Heroe-" + tag, connected.Session.State.CharacterName);
                Assert.Equal(1, connected.Session.State.Breed);
                Assert.Null(SessionRegistry.FindByCharacter(hero));

                Assert.False(Heroes.Disconnect(master + 1, hero));
                Assert.True(Heroes.Disconnect(master, hero));
                Assert.Empty(Heroes.Of(master));
                Assert.Null(Heroes.AsHero(hero));
            }
            finally
            {
                Heroes.DisconnectAll(master);
                SessionRegistry.Unregister(pipe.Session);
            }
        }

        [Fact]
        public async Task Quest_sharing_respects_opt_in_and_each_heros_quest_requirements()
        {
            const int posterQuest = 1565;
            const int cellarQuest = 1633, inspectCellar = 9895, lureRat = 9769;
            const long cellar = 153358340;
            const int tap = 489505, lemonade = 8543;
            QuestEngine.Load();

            string tag = Guid.NewGuid().ToString("N")[..6];
            long master = Character(Account, "Maestro-" + tag, 8);
            long optedIn = Character(Account, "Sigue-" + tag, 1);
            long lowLevel = Character(Account, "Nivel-" + tag, 1);
            long optedOut = Character(Account, "Solo-" + tag, 1);
            await using var pipe = await ClientPipe.OpenAsync(Account, master, Map, "Maestro-" + tag);
            try
            {
                Assert.Equal("ok", Heroes.Connect(pipe.Session, optedIn));
                Assert.Equal("ok", Heroes.Connect(pipe.Session, lowLevel));
                Assert.Equal("ok", Heroes.Connect(pipe.Session, optedOut));
                Assert.False(Heroes.SetQuestSync(master + 1, optedIn, true));
                Assert.True(Heroes.SetQuestSync(master, optedIn, true));
                Assert.True(Heroes.SetQuestSync(master, lowLevel, true));

                var eligible = Heroes.SessionOf(optedIn)!.State;
                var ineligible = Heroes.SessionOf(lowLevel)!.State;
                var disabled = Heroes.SessionOf(optedOut)!.State;
                eligible.CharacterLevel = disabled.CharacterLevel = pipe.Session.State.CharacterLevel = 150;

                using (SessionContext.Push(pipe.Session))
                {
                    QuestEngine.LoadFrom(master);
                    pipe.Session.State.DocumentOpen = true;
                    await QuestHandler.StartFromDocumentAsync(pipe.Session,
                        ConnectionProtocol.Push("ido", Pb.New().Var(1, posterQuest).Build()));

                    Assert.True(QuestEngine.Log!.Active(posterQuest));
                    Assert.True(eligible.Quests!.Active(posterQuest));
                    Assert.False(ineligible.Quests!.Active(posterQuest));
                    Assert.False(disabled.Quests!.Active(posterQuest));

                    Assert.True(await QuestEngine.StartSharedAsync(pipe.Session, cellarQuest));
                    Assert.True(eligible.Quests.Active(cellarQuest));
                    Assert.True(ineligible.Quests.Active(cellarQuest));
                    Assert.True(Heroes.SetQuestSync(master, lowLevel, false));
                    eligible.MapId = ineligible.MapId = pipe.Session.State.MapId = cellar;

                    Assert.True(await QuestEngine.OnInteractiveUsedAsync(pipe.Session, cellar, tap));
                    Assert.True(QuestEngine.Log.ObjectiveDone(inspectCellar));
                    Assert.True(eligible.Quests.ObjectiveDone(inspectCellar));
                    Assert.False(ineligible.Quests.ObjectiveDone(inspectCellar));

                    Assert.True(await Equipment.GiveAsync(pipe.ToClient, lemonade, 1));
                    Assert.True(await QuestEngine.OnInteractiveUsedAsync(pipe.Session, cellar, tap));
                    Assert.True(QuestEngine.Log.ObjectiveDone(lureRat));
                    Assert.False(eligible.Quests.ObjectiveDone(lureRat));
                }

                Assert.True(Heroes.Disconnect(master, optedIn));
                Assert.Equal("ok", Heroes.Connect(pipe.Session, optedIn));
                Assert.True(Heroes.SharesQuests(optedIn));
                Assert.True(Heroes.SessionOf(optedIn)!.State.Quests!.ObjectiveDone(inspectCellar));
            }
            finally
            {
                Heroes.SetQuestSync(master, optedIn, false);
                Heroes.SetQuestSync(master, lowLevel, false);
                Heroes.DisconnectAll(master);
                SessionRegistry.Unregister(pipe.Session);
                foreach (long character in new[] { master, optedIn, lowLevel, optedOut })
                    DatabaseManager.DeleteCharacter(character, Account);
            }
        }

        [Fact]
        public async Task A_hero_chosen_from_a_client_of_his_own_is_nobodys_hero_any_more()
        {
            string tag = Guid.NewGuid().ToString("N")[..6];
            long master = Character(Account, "Amo-" + tag, 8);
            long hero = Character(Account, "Libre-" + tag, 7);
            await using var pipe = await ClientPipe.OpenAsync(Account, master, Map, "Amo-" + tag);
            try
            {
                Assert.Equal("ok", Heroes.Connect(pipe.Session, hero));
                Heroes.Release(hero);
                Assert.Empty(Heroes.Of(master));
            }
            finally
            {
                Heroes.DisconnectAll(master);
                SessionRegistry.Unregister(pipe.Session);
            }
        }

        [Fact]
        public async Task The_group_panels_switches_pass_a_heros_turns_and_send_the_items_to_one()
        {
            string tag = Guid.NewGuid().ToString("N")[..6];
            long master = Character(Account, "Guia-" + tag, 8);
            long hero = Character(Account, "Paje-" + tag, 9);
            await using var pipe = await ClientPipe.OpenAsync(Account, master, Map, "Guia-" + tag);
            try
            {
                Assert.Equal("ok", Heroes.Connect(pipe.Session, hero));

                Assert.False(Heroes.SetAutoPass(master + 1, hero, true));
                Assert.True(Heroes.SetAutoPass(master, hero, true));
                Assert.True(Heroes.PassesOnHisOwn(hero));
                Assert.False(Heroes.PassesOnHisOwn(master));

                Assert.False(Heroes.SetLootTo(master, master + 999));
                Assert.True(Heroes.SetLootTo(master, hero));
                var plan = new System.Collections.Generic.Dictionary<long, FightHandler.Reward>
                {
                    [master] = new() { Xp = 100, Kamas = 10, Loot = new() { [287] = 2, [6903] = 1 } },
                    [hero] = new() { Xp = 50, Kamas = 5, Loot = new() { [287] = 1 } },
                    [777] = new() { Xp = 1, Kamas = 1, Loot = new() { [287] = 4 } },   // somebody else's
                };
                FightHandler.GatherHeroLoot(plan);
                Assert.Empty(plan[master].Loot);
                Assert.Equal(3, plan[hero].Loot[287]);
                Assert.Equal(1, plan[hero].Loot[6903]);
                Assert.Equal(100, plan[master].Xp);   // each keeps his experience and kamas
                Assert.Equal(10, plan[master].Kamas);
                Assert.Equal(4, plan[777].Loot[287]);

                // A quest's item dropped for one alone stays his: the bag of jewels of 1648, which
                // all went to the recipient and left the others' quests stuck.
                plan = new System.Collections.Generic.Dictionary<long, FightHandler.Reward>
                {
                    [master] = new() { Loot = new() { [16540] = 1, [287] = 2 }, Own = new() { [16540] = 1 } },
                    [hero] = new() { Loot = new() { [16540] = 1 }, Own = new() { [16540] = 1 } },
                };
                FightHandler.GatherHeroLoot(plan);
                Assert.Equal(1, plan[master].Loot[16540]);
                Assert.False(plan[master].Loot.ContainsKey(287));
                Assert.Equal(1, plan[hero].Loot[16540]);
                Assert.Equal(2, plan[hero].Loot[287]);

                // The same one again: back to each his own.
                Assert.True(Heroes.SetLootTo(master, hero));
                Assert.Equal(0, Heroes.LootTo(master));
            }
            finally
            {
                Heroes.DisconnectAll(master);
                SessionRegistry.Unregister(pipe.Session);
            }
        }

        /// <summary>
        /// Taking a hero in hand is changing character: the client leaves and comes back with him,
        /// and the group is his then, the one who led it a hero of his, the switches kept.
        /// </summary>
        [Fact]
        public async Task Coming_back_as_one_of_the_group_takes_the_group_in_hand()
        {
            string tag = Guid.NewGuid().ToString("N")[..6];
            long first = Character(Account + 7, "Primero-" + tag, 8);
            long second = Character(Account + 7, "Segundo-" + tag, 1);
            long third = Character(Account + 7, "Tercero-" + tag, 9);
            var pipe = await ClientPipe.OpenAsync(Account + 7, first, Map, "Primero-" + tag);
            try
            {
                Assert.Equal("ok", Heroes.Connect(pipe.Session, second));
                Assert.Equal("ok", Heroes.Connect(pipe.Session, third));
                Assert.True(Heroes.SetAutoPass(first, third, true));
                Assert.True(Heroes.SetLootTo(first, third));
                Heroes.DisconnectAll(pipe.Session);
                SessionRegistry.Unregister(pipe.Session);
            }
            finally
            {
                await pipe.DisposeAsync();
            }

            await using var back = await ClientPipe.OpenAsync(Account + 7, second, Map, "Segundo-" + tag);
            try
            {
                Heroes.Regroup(back.Session);
                var group = Heroes.Of(second).Select(h => h.CharacterId).ToList();
                Assert.Equal(new[] { first, third }, group);
                Assert.True(Heroes.PassesOnHisOwn(third));
                Assert.False(Heroes.PassesOnHisOwn(first));
                Assert.Equal(third, Heroes.LootTo(second));
            }
            finally
            {
                Heroes.DisconnectAll(second);
                SessionRegistry.Unregister(back.Session);
            }
        }

        /// <summary>Taking a hero in hand is asked of the server, which hands him to the next way in, once.</summary>
        [Fact]
        public async Task A_hero_asked_for_is_the_one_the_next_way_in_picks()
        {
            string tag = Guid.NewGuid().ToString("N")[..6];
            long master = Character(Account + 9, "Rey-" + tag, 8);
            long hero = Character(Account + 9, "Valido-" + tag, 1);
            await using var pipe = await ClientPipe.OpenAsync(Account + 9, master, Map, "Rey-" + tag);
            try
            {
                Assert.False(Heroes.AskSwitch(pipe.Session, hero));   // not a hero of his yet
                Assert.Equal("ok", Heroes.Connect(pipe.Session, hero));
                Assert.True(Heroes.AskSwitch(pipe.Session, hero));
                Assert.True(Heroes.SwitchPending(Account + 9));
                Assert.Equal(hero, Heroes.TakeSwitch(Account + 9));
                Assert.False(Heroes.SwitchPending(Account + 9));
                Assert.Equal(0, Heroes.TakeSwitch(Account + 9));
            }
            finally
            {
                Heroes.DisconnectAll(master);
                SessionRegistry.Unregister(pipe.Session);
            }
        }

        /// <summary>
        /// Taking a hero in hand at once: the same connection is the hero's from then on, where
        /// the master stood, and the master is one of his heroes with the group's switches.
        /// </summary>
        [Fact]
        public async Task A_hero_is_taken_in_hand_in_the_same_connection()
        {
            string tag = Guid.NewGuid().ToString("N")[..6];
            long master = Character(Account + 11, "Mano-" + tag, 8);
            long hero = Character(Account + 11, "Brazo-" + tag, 9);
            long other = Character(Account + 11, "Pie-" + tag, 1);
            await using var pipe = await ClientPipe.OpenAsync(Account + 11, master, Map, "Mano-" + tag);
            // Whatever the server sends goes read, as a client reads it.
            using var reading = new System.Threading.CancellationTokenSource();
            var drain = Task.Run(async () =>
            {
                var buffer = new byte[65536];
                try { while (!reading.IsCancellationRequested && await pipe.FromServer.ReadAsync(buffer, reading.Token) > 0) { } }
                catch { }
            });
            try
            {
                // With the maps loaded, a map not among them is left for a real one on selection:
                // these tests' own map is lent a real one's. Loaded here, as a full run has them.
                if (!MapManager.Maps.ContainsKey(RealMap)) MapManager.Initialize();
                if (MapManager.Maps.TryGetValue(RealMap, out var real)) MapManager.Maps[Map] = real;
                if (MapManager.WalkableCells.TryGetValue(RealMap, out var cells)) MapManager.WalkableCells[Map] = cells;
                using (SessionContext.Push(pipe.Session)) DatabaseManager.LoadCharacter(master);
                pipe.Session.State.CellId = 300;
                Assert.Equal("ok", Heroes.Connect(pipe.Session, hero));
                Assert.Equal("ok", Heroes.Connect(pipe.Session, other));
                Assert.True(Heroes.SetAutoPass(master, other, true));

                var party = Parties.Of(master);
                Assert.NotNull(party);

                Assert.Equal("hero", await HeroSwitch.SwitchAsync(pipe.Session, master + 999));
                Assert.Equal("ok", await HeroSwitch.SwitchAsync(pipe.Session, hero));

                // The same party: nobody out of it, the one played now leading it.
                Assert.Same(party, Parties.Of(hero));
                Assert.Equal(hero, party!.LeaderId);
                Assert.Equal(new[] { master, hero, other }.OrderBy(i => i), Parties.MembersOf(party).OrderBy(i => i));

                Assert.Equal(hero, pipe.Session.State.CharacterId);
                Assert.Equal(9, pipe.Session.State.Breed);
                Assert.Equal(Map, pipe.Session.State.MapId);
                Assert.Equal(300, pipe.Session.State.CellId);
                Assert.Same(pipe.Session, SessionRegistry.FindByCharacter(hero));
                Assert.Null(SessionRegistry.FindByCharacter(master));
                // The group in its round from the one after him: master, hero, other -- so other first.
                Assert.Equal(new[] { other, master }, Heroes.Of(hero).Select(h => h.CharacterId));
                Assert.True(Heroes.PassesOnHisOwn(other));
                Assert.Null(Heroes.AsHero(hero));

                // The first hero taken in hand each time, as Tab does, goes round them all.
                Assert.Equal("ok", await HeroSwitch.SwitchAsync(pipe.Session, other));
                Assert.Equal(new[] { master, hero }, Heroes.Of(other).Select(h => h.CharacterId));
                Assert.Equal("ok", await HeroSwitch.SwitchAsync(pipe.Session, master));
                Assert.Equal(new[] { hero, other }, Heroes.Of(master).Select(h => h.CharacterId));
                Assert.Same(party, Parties.Of(master));
                Assert.Equal(master, party.LeaderId);
            }
            finally
            {
                reading.Cancel();
                Heroes.DisconnectAll(other);
                Heroes.DisconnectAll(hero);
                Heroes.DisconnectAll(master);
                SessionRegistry.Unregister(pipe.Session);
            }
        }

        /// <summary>
        /// Taking a hero in hand sends his own quest journal and achievements: with no map loaded
        /// again, the client kept showing those of the one played before until the next login (#198).
        /// </summary>
        [Fact]
        public async Task A_hero_taken_in_hand_is_shown_his_own_quest_journal_and_achievements()
        {
            QuestEngine.Load();
            Achievements.Load();
            string tag = Guid.NewGuid().ToString("N")[..6];
            long master = Character(Account + 12, "Diario-" + tag, 8);
            long hero = Character(Account + 12, "Pagina-" + tag, 9);
            // The same quest, done by the master and still under way for the hero.
            DatabaseManager.SaveQuestProgress(master, new DatabaseManager.QuestRow(1648, 0, "", true));
            DatabaseManager.SaveQuestProgress(hero, new DatabaseManager.QuestRow(1648, 2268, "", false));
            var codec = new Recording();
            await using var pipe = await ClientPipe.OpenAsync(Account + 12, master, Map, "Diario-" + tag, codec);
            using var reading = new System.Threading.CancellationTokenSource();
            var drain = Task.Run(async () =>
            {
                var buffer = new byte[65536];
                try { while (!reading.IsCancellationRequested && await pipe.FromServer.ReadAsync(buffer, reading.Token) > 0) { } }
                catch { }
            });
            try
            {
                if (!MapManager.Maps.ContainsKey(RealMap)) MapManager.Initialize();
                if (MapManager.Maps.TryGetValue(RealMap, out var real)) MapManager.Maps[Map] = real;
                if (MapManager.WalkableCells.TryGetValue(RealMap, out var cells)) MapManager.WalkableCells[Map] = cells;
                using (SessionContext.Push(pipe.Session)) DatabaseManager.LoadCharacter(master);
                Assert.Equal("ok", Heroes.Connect(pipe.Session, hero));

                int before = codec.Frames.Count;
                Assert.Equal("ok", await HeroSwitch.SwitchAsync(pipe.Session, hero));

                var journal = Assert.Single(codec.Frames.Skip(before).Select(f => f.Content).OfType<Jondo.Unity.Protocol.Game.QuestsEvent>());
                Assert.Contains(journal.ActiveQuests, quest => quest.QuestId == 1648);
                Assert.DoesNotContain(journal.FinishedQuests, quest => quest.QuestId == 1648);
                // And his achievements: the window kept the one played before's as well.
                Assert.Single(codec.Frames.Skip(before).Select(f => f.Content).OfType<Jondo.Unity.Protocol.Game.AchievementsEvent>());
            }
            finally
            {
                reading.Cancel();
                Heroes.DisconnectAll(hero);
                Heroes.DisconnectAll(master);
                SessionRegistry.Unregister(pipe.Session);
            }
        }

        /// <summary>
        /// A master trades with a hero of his: the window opens at once, the hero takes what is
        /// laid down as soon as his master is ready. Asking another player's hero asks the character
        /// that player plays (#199).
        /// </summary>
        [Fact]
        public async Task A_master_trades_with_his_own_hero_and_anothers_goes_to_his_master()
        {
            string tag = Guid.NewGuid().ToString("N")[..6];
            long master = Character(Account + 13, "Dador-" + tag, 8);
            long hero = Character(Account + 13, "Tomador-" + tag, 9);
            long stranger = Character(Account + 14, "Vecino-" + tag, 1);
            long strangersHero = Character(Account + 14, "Escudero-" + tag, 2);
            await using var pipe = await ClientPipe.OpenAsync(Account + 13, master, Map, "Dador-" + tag);
            await using var other = await ClientPipe.OpenAsync(Account + 14, stranger, Map, "Vecino-" + tag);
            using var reading = new System.Threading.CancellationTokenSource();
            foreach (var p in new[] { pipe, other })
                _ = Task.Run(async () =>
                {
                    var buffer = new byte[65536];
                    try { while (!reading.IsCancellationRequested && await p.FromServer.ReadAsync(buffer, reading.Token) > 0) { } }
                    catch { }
                });
            long uid = DatabaseManager.NextItemUid();
            try
            {
                Assert.Equal("ok", Heroes.Connect(pipe.Session, hero));
                Assert.Equal("ok", Heroes.Connect(other.Session, strangersHero));
                var heroSession = Heroes.SessionOf(hero)!;
                pipe.Session.State.Kamas = 5_000;
                long heroKamas = heroSession.State.Kamas;
                using (SessionContext.Push(pipe.Session))
                {
                    Assert.True(DatabaseManager.InsertCharacterItem(uid, master, 6902, 3, Equipment.Bag, null));
                    Equipment.Add(uid, 6902, 3, Equipment.Bag, null);
                }

                byte[] Ask(long whom) => ConnectionProtocol.Push(Op.Keu, Pb.New().Var(2, whom).Build());

                // Another player's hero: asked of the character that player plays, who can accept.
                using (SessionContext.Push(pipe.Session)) await TradeHandler.RequestAsync(null!, Ask(strangersHero));
                var asked = pipe.Session.State.Trade;
                Assert.NotNull(asked);
                Assert.Equal(stranger, asked!.TargetId);
                Assert.False(asked.Accepted);
                Assert.Same(asked, other.Session.State.Trade);
                Assert.Null(Heroes.SessionOf(strangersHero)!.State.Trade);
                using (SessionContext.Push(other.Session)) Assert.True(await TradeHandler.CloseAsync());
                Assert.Null(pipe.Session.State.Trade);

                // His own: open at once, nobody to accept.
                using (SessionContext.Push(pipe.Session)) await TradeHandler.RequestAsync(null!, Ask(hero));
                var trade = pipe.Session.State.Trade;
                Assert.NotNull(trade);
                Assert.True(trade!.Accepted);
                Assert.Same(trade, heroSession.State.Trade);

                using (SessionContext.Push(pipe.Session))
                {
                    await TradeHandler.MoveAsync(null!, ConnectionProtocol.Push(Op.Kcr, Pb.New().Var(1, 2).Var(2, uid).Build()));
                    await TradeHandler.KamasAsync(null!, ConnectionProtocol.Push(Op.Kee, Pb.New().Var(1, 3_000).Build()));
                    // His "ready" is enough: the hero always is.
                    await TradeHandler.ReadyAsync(null!, ConnectionProtocol.Push(Op.Kep, Pb.New().Var(1, 1).Var(2, 3).Build()));
                }

                Assert.True(trade.Ended);
                Assert.Null(pipe.Session.State.Trade);
                Assert.Null(heroSession.State.Trade);
                Assert.Equal(2_000, pipe.Session.State.Kamas);
                Assert.Equal(heroKamas + 3_000, heroSession.State.Kamas);
                using (SessionContext.Push(pipe.Session)) Assert.Equal(1, Equipment.ByUid(uid)!.Quantity);
                using (SessionContext.Push(heroSession))
                    Assert.Contains(Equipment.All, i => i.Template == 6902 && i.Quantity == 2 && i.Uid != uid);
            }
            finally
            {
                reading.Cancel();
                foreach (long id in new[] { master, hero })
                    foreach (var row in DatabaseManager.LoadInventory(id))
                        DatabaseManager.DestroyCharacterItem(id, row.Uid, 0);
                Heroes.DisconnectAll(master);
                Heroes.DisconnectAll(stranger);
                SessionRegistry.Unregister(pipe.Session);
                SessionRegistry.Unregister(other.Session);
            }
        }

        /// <summary>The real codec, keeping what it encodes.</summary>
        private sealed class Recording : Jondo.Unity.Protocol.Game.IGameCodec
        {
            private readonly Jondo.Unity.Protocol.Game.Wire.GameCodec _real = new();
            public readonly System.Collections.Concurrent.ConcurrentQueue<Jondo.Unity.Protocol.Game.GameFrame> Queue = new();
            public System.Collections.Generic.List<Jondo.Unity.Protocol.Game.GameFrame> Frames => Queue.ToList();
            public Jondo.Unity.Protocol.Game.GameFrame Decode(ReadOnlySpan<byte> payload) => _real.Decode(payload);
            public byte[] Encode(Jondo.Unity.Protocol.Game.GameFrame message)
            {
                Queue.Enqueue(message);
                return _real.Encode(message);
            }
        }

        /// <summary>A connected hero stands on his master's map beside him, and is in the world for his guild.</summary>
        [Fact]
        public async Task A_hero_stands_beside_his_master_and_is_online()
        {
            string tag = Guid.NewGuid().ToString("N")[..6];
            long master = Character(Account + 13, "Sol-" + tag, 8);
            long hero = Character(Account + 13, "Sombra-" + tag, 9);
            await using var pipe = await ClientPipe.OpenAsync(Account + 13, master, Map, "Sol-" + tag);
            try
            {
                pipe.Session.State.CellId = 300;
                Assert.False(SessionRegistry.IsOnline(hero));
                Assert.Equal("ok", Heroes.Connect(pipe.Session, hero));

                Assert.True(SessionRegistry.IsOnline(hero));
                var shown = Assert.Single(HeroPresence.On(Map), h => h.Character.Id == hero);
                Assert.NotEqual(-1, shown.Cell);
                Assert.Equal(Map, Heroes.SessionOf(hero)!.State.MapId);

                Assert.True(Heroes.Disconnect(master, hero));
                Assert.False(SessionRegistry.IsOnline(hero));
                Assert.DoesNotContain(HeroPresence.On(Map), h => h.Character.Id == hero);
            }
            finally
            {
                Heroes.DisconnectAll(master);
                SessionRegistry.Unregister(pipe.Session);
            }
        }

        /// <summary>
        /// The master walks: each hero walks to a cell of his own beside where the master stops --
        /// not onto him, not onto another hero, not back to where the master set off.
        /// </summary>
        [Fact]
        public async Task Heroes_follow_to_cells_of_their_own_beside_where_their_master_stops()
        {
            if (!MapManager.WalkableCells.ContainsKey(RealMap)) MapManager.Initialize();
            Assert.True(MapManager.WalkableCells.TryGetValue(RealMap, out var cells) && cells.Count >= 20);
            MapManager.WalkableCells[Map] = cells;
            string tag = Guid.NewGuid().ToString("N")[..6];
            long master = Character(Account + 15, "Guia2-" + tag, 8);
            long first = Character(Account + 15, "Uno-" + tag, 9);
            long second = Character(Account + 15, "Dos-" + tag, 1);
            await using var pipe = await ClientPipe.OpenAsync(Account + 15, master, Map, "Guia2-" + tag);
            using var reading = new System.Threading.CancellationTokenSource();
            var drain = Task.Run(async () =>
            {
                var buffer = new byte[65536];
                try { while (!reading.IsCancellationRequested && await pipe.FromServer.ReadAsync(buffer, reading.Token) > 0) { } }
                catch { }
            });
            try
            {
                int start = cells[0], end = cells[cells.Count / 2];
                pipe.Session.State.CellId = start;
                Assert.Equal("ok", Heroes.Connect(pipe.Session, first));
                Assert.Equal("ok", Heroes.Connect(pipe.Session, second));

                pipe.Session.State.CellId = end;
                await HeroPresence.MasterWalkedAsync(pipe.Session, new List<int> { start, end });

                int a = Heroes.SessionOf(first)!.State.CellId, b = Heroes.SessionOf(second)!.State.CellId;
                Assert.NotEqual(end, a);
                Assert.NotEqual(end, b);
                Assert.NotEqual(a, b);
                Assert.True(Jondo.Unity.World.Maps.MapGeometry.Distance(end, a) <= 3);
                Assert.True(Jondo.Unity.World.Maps.MapGeometry.Distance(end, b) <= 3);
            }
            finally
            {
                reading.Cancel();
                Heroes.DisconnectAll(master);
                SessionRegistry.Unregister(pipe.Session);
            }
        }

        /// <summary>
        /// Heroes are in their master's party, the game's own window: he leads it, a hero never
        /// does, and it breaks up when the last of them is let go.
        /// </summary>
        [Fact]
        public async Task Heroes_are_in_their_masters_party_and_he_leads_it()
        {
            string tag = Guid.NewGuid().ToString("N")[..6];
            long master = Character(Account + 17, "Jefe-" + tag, 8);
            long first = Character(Account + 17, "Paje-" + tag, 9);
            long second = Character(Account + 17, "Escudero-" + tag, 1);
            await using var pipe = await ClientPipe.OpenAsync(Account + 17, master, Map, "Jefe-" + tag);
            using var reading = new System.Threading.CancellationTokenSource();
            var drain = Task.Run(async () =>
            {
                var buffer = new byte[65536];
                try { while (!reading.IsCancellationRequested && await pipe.FromServer.ReadAsync(buffer, reading.Token) > 0) { } }
                catch { }
            });
            try
            {
                Assert.Equal("ok", Heroes.Connect(pipe.Session, first));
                Assert.Equal("ok", Heroes.Connect(pipe.Session, second));

                var party = Parties.Of(master);
                Assert.NotNull(party);
                Assert.Equal(master, party!.LeaderId);
                Assert.Equal(new[] { master, first, second }, Parties.MembersOf(party));

                Assert.True(Heroes.Disconnect(master, first));
                Assert.Equal(new[] { master, second }, Parties.MembersOf(party));
                Assert.True(Heroes.Disconnect(master, second));
                Assert.Null(Parties.Of(master));
                Assert.Null(Parties.Of(second));
            }
            finally
            {
                reading.Cancel();
                Heroes.DisconnectAll(master);
                SessionRegistry.Unregister(pipe.Session);
            }
        }

        /// <summary>
        /// A hero made from the window: by the creation screen's rules
        /// -- a name that follows them, one nobody has, a slot left on the account --, with his
        /// class's colours, and connected beside his master at once.
        /// </summary>
        [Fact]
        public async Task A_hero_is_made_by_the_creation_rules_and_connected_at_once()
        {
            // An account of its own each run: the slots are counted on it.
            long account = Account + 100_000 + Random.Shared.Next(1_000_000);
            string letters = new string(Enumerable.Range(0, 7).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
            long master = Character(account, "Padre-" + letters, 8);
            await using var pipe = await ClientPipe.OpenAsync(account, master, Map, "Padre-" + letters);
            using var reading = new System.Threading.CancellationTokenSource();
            var drain = Task.Run(async () =>
            {
                var buffer = new byte[65536];
                try { while (!reading.IsCancellationRequested && await pipe.FromServer.ReadAsync(buffer, reading.Token) > 0) { } }
                catch { }
            });
            var colors = Enumerable.Repeat(-1L, 6).ToList();
            try
            {
                Assert.Equal("name", CharacterCreationHandler.Make(account, 1, "no-capital" + letters, 5, 0, 0, colors).Why);
                Assert.Equal("taken", CharacterCreationHandler.Make(account, 1, "Padre-" + letters, 5, 0, 0, colors).Why);
                Assert.Equal("breed", CharacterCreationHandler.Make(account, 1, "Hijo-" + letters, 0, 0, 0, colors).Why);

                var (id, why) = CharacterCreationHandler.Make(account, 1, "Hijo-" + letters, 5, 1, 0, colors);
                Assert.Equal("", why);
                var made = DatabaseManager.GetCharacterById(id);
                Assert.NotNull(made);
                Assert.Equal(5, made!.Breed);
                Assert.Equal(1, made.Sex);
                Assert.Equal(1, made.Level);

                Assert.Equal("ok", Heroes.Connect(pipe.Session, id));
                Assert.Equal(new[] { id }, Heroes.Of(master).Select(h => h.CharacterId));

                // The slots run out: ServerSettings.CharacterSlots, two made already.
                for (int i = 0; i < Jondo.Unity.Server.Network.Connection.ConnectionService.MaxCharactersPerServer - 2; i++)
                    Assert.Equal("", CharacterCreationHandler.Make(account, 1, $"Otro-{letters}-{(char)('a' + i)}", 1, 0, 0, colors).Why);
                Assert.Equal("full", CharacterCreationHandler.Make(account, 1, "Ultimo-" + letters, 1, 0, 0, colors).Why);
            }
            finally
            {
                reading.Cancel();
                Heroes.DisconnectAll(master);
                SessionRegistry.Unregister(pipe.Session);
            }
        }

        [Fact]
        public async Task A_hero_comes_into_his_masters_fight_as_his_to_play()
        {
            string tag = Guid.NewGuid().ToString("N")[..6];
            long master = Character(Account, "Jefe-" + tag, 8);
            long hero = Character(Account, "Escudero-" + tag, 1);
            await using var pipe = await ClientPipe.OpenAsync(Account, master, Map, "Jefe-" + tag);
            try
            {
                Assert.Equal("ok", Heroes.Connect(pipe.Session, hero));
                var fight = new FightInstance(990_311, Map, Map);
                fight.SetPlacementCells(new System.Collections.Generic.List<int> { 300, 314 }, new System.Collections.Generic.List<int> { 100, 114 });
                fight.AddPlayer(new Fighter { Id = master, Name = "Jefe-" + tag, Level = 50, MaxHP = 500, CurrentHP = 500 });

                FightHandler.AddHeroes(fight, master);

                var fighter = Assert.Single(fight.Azul, f => f.Id == hero);
                Assert.Equal(0, fighter.TeamId);
                Assert.True(fighter.IsPlayedAlly);
                Assert.True(fighter.ControlledBy(master));
                Assert.True(fighter.IsReady);
                Assert.True(fighter.IsCharacter);
                Assert.Equal(1, fighter.Breed);
                // His class's spells, his own, to cast from his master's client.
                Assert.NotEmpty(fighter.HechizosDeInvocado);
                var jyy = ProtoMessage.Parse(FightHandler.PlayedAllyBarForTests(fighter, master)).Fields;
                Assert.Equal(hero, jyy.Single(f => f.FieldNumber == 3).VarIntValue);
                Assert.Equal(master, jyy.Single(f => f.FieldNumber == 4).VarIntValue);
                Assert.Equal(fighter.HechizosDeInvocado.Count + 1, jyy.Count(f => f.FieldNumber == 6));
                Assert.True(jyy.Count(f => f.FieldNumber == 7) > 1);
                Assert.NotEqual(fight.Azul.Single(f => f.Id == master).CellId, fighter.CellId);
                Assert.True(Heroes.SessionOf(hero)!.State.IsInFight);

                // In a fight, he stays connected until it is over.
                Assert.False(Heroes.Disconnect(master, hero));
            }
            finally
            {
                Heroes.DisconnectAll(master);
                SessionRegistry.Unregister(pipe.Session);
            }
        }
    }
}
