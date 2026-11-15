using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The hero mode: the account's other characters connected beside the one in the world, each
    /// in a session of his own with no socket, and into his master's fights as his to play.
    /// </summary>
    public sealed class HeroesTests
    {
        private const long Account = 8_900_000_311;
        private const long Map = 191105026;

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
