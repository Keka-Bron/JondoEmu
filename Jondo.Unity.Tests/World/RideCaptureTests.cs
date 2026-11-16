using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Catching wild rides with a net: the ten nets and the spells they give, as the client's data
    /// pairs them; what each takes and how far; once a fight; and the rides a won fight gives.
    /// </summary>
    [Collection("MapManager")]
    public class RideCaptureTests
    {
        private const int Universal = 32521, ReinforcedDragoturkeys = 32522, MultiplierDragoturkeys = 32525,
                          ReinforcedMultiplierMuldos = 32530;
        private const int AlmondDragoturkey = 171, GingerDragoturkey = 200, IndigoMuldo = 4434;

        private static Fighter Wild(long id, int monster, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, IsMonster = true, MonsterId = monster,
            MaxHP = 500, CurrentHP = 500, MaxAP = 6, CurrentAP = 6,
        };

        private static Fighter Breeder(long id, int cell) => new()
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 500, CurrentHP = 500, MaxAP = 6, CurrentAP = 6,
        };

        private static int At(int centre, int distance)
            => Enumerable.Range(0, 560).First(c => c != centre && MapGeometry.Distance(centre, c) == distance);

        /// <summary>
        /// Ten nets of type 99, each giving its own domestication spell: the universal one
        /// «Domesticación de Montura» (32260), the others 32270 to 32278 in the order of their ids.
        /// </summary>
        [Fact]
        public void Each_net_gives_its_domestication_spell()
        {
            if (Breeding.NetByItem(Universal) == null) return;   // no client data

            Assert.Equal(32260, Breeding.NetByItem(Universal)!.Spell);
            Assert.Equal(Enumerable.Range(32270, 9), Enumerable.Range(32522, 9).Select(i => Breeding.NetByItem(i)!.Spell));
            Assert.Equal(ReinforcedDragoturkeys, Breeding.NetBySpell(32270)!.Item);
            Assert.True(RideCapture.IsDomestication(32260));
            Assert.False(RideCapture.IsDomestication(0));
            Assert.Null(Breeding.NetByItem(33001));
        }

        /// <summary>
        /// The universal net takes the three species one at a time; a reinforced one takes its own
        /// species only, within 3 cells; a multiplier gives each ride twice.
        /// </summary>
        [Fact]
        public void What_each_net_takes_and_how_far()
        {
            if (Breeding.NetByItem(Universal) == null) return;

            var universal = Breeding.NetByItem(Universal)!;
            Assert.Contains(AlmondDragoturkey, universal.Wild);
            Assert.Contains(IndigoMuldo, universal.Wild);
            Assert.Contains(5308, universal.Wild);
            Assert.Equal(0, universal.Radius);
            Assert.Equal(1, universal.Multiplier);

            var reinforced = Breeding.NetByItem(ReinforcedDragoturkeys)!;
            Assert.Contains(AlmondDragoturkey, reinforced.Wild);
            Assert.DoesNotContain(IndigoMuldo, reinforced.Wild);
            Assert.Equal(3, reinforced.Radius);

            Assert.Equal(2, Breeding.NetByItem(MultiplierDragoturkeys)!.Multiplier);
            Assert.Equal(0, Breeding.NetByItem(MultiplierDragoturkeys)!.Radius);
            var both = Breeding.NetByItem(ReinforcedMultiplierMuldos)!;
            Assert.Equal((3, 2), (both.Radius, both.Multiplier));
            Assert.Contains(IndigoMuldo, both.Wild);
        }

        /// <summary>«Dragopavo almendrado salvaje» is caught as the «Dragopavo almendrado».</summary>
        [Fact]
        public void A_wild_ride_is_caught_as_its_colour()
        {
            if (Breeding.NetByItem(Universal) == null) return;

            Assert.Equal("Dragopavo almendrado", Breeding.RideOfWild(AlmondDragoturkey)!.Name);
            Assert.Equal("Dragopavo pelirrojo", Breeding.RideOfWild(GingerDragoturkey)!.Name);
            Assert.True(Breeding.RideOfWild(IndigoMuldo)!.Item > 0);
            Assert.Null(Breeding.RideOfWild(31));
        }

        /// <summary>
        /// A reinforced net's circle takes the wild rides of its species within 3 cells, not the
        /// one further, not another species, not a dead one; the universal net only the one aimed at.
        /// </summary>
        [Fact]
        public void The_net_reaches_its_species_in_its_circle()
        {
            if (Breeding.NetByItem(Universal) == null) return;

            const int centre = 270;
            var fight = new FightInstance(9_100_001, 1);
            fight.AddPlayer(Breeder(9_100_100, 300));
            var aimed = Wild(-1, AlmondDragoturkey, centre);
            var near = Wild(-2, GingerDragoturkey, At(centre, 3));
            var far = Wild(-3, AlmondDragoturkey, At(centre, 4));
            var muldo = Wild(-4, IndigoMuldo, At(centre, 1));
            var dead = Wild(-5, AlmondDragoturkey, At(centre, 2));
            dead.CurrentHP = 0;
            foreach (var f in new[] { aimed, near, far, muldo, dead }) fight.AddMonster(f);

            var reached = RideCapture.Reached(fight, Breeding.NetByItem(ReinforcedDragoturkeys)!, centre);
            Assert.Equal(new long[] { -2, -1 }, reached.Select(f => f.Id).OrderBy(i => i));

            var one = RideCapture.Reached(fight, Breeding.NetByItem(Universal)!, centre);
            Assert.Equal(-1, Assert.Single(one).Id);
            Assert.Equal(-4, Assert.Single(RideCapture.Reached(fight, Breeding.NetByItem(Universal)!, muldo.CellId)).Id);
        }

        /// <summary>
        /// Once a fight: the second cast is refused. What it marked is the caster's, twice with a
        /// multiplier, and an ally's net no longer takes it; nothing is given if the fight is lost.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task Once_a_fight_and_the_catches_are_the_casters()
        {
            if (Breeding.NetByItem(Universal) == null) return;

            const int centre = 270;
            var fight = new FightInstance(9_100_002, 1);
            var breeder = Breeder(9_100_200, 300);
            var ally = Breeder(9_100_201, 301);
            fight.AddPlayer(breeder);
            fight.AddPlayer(ally);
            var almond = Wild(-1, AlmondDragoturkey, centre);
            fight.AddMonster(almond);

            var player = GameSession.SinSocket();
            player.State.CharacterId = breeder.Id;
            using (SessionContext.Push(player))
            {
                int spell = Breeding.NetByItem(MultiplierDragoturkeys)!.Spell;
                Assert.True(RideCapture.MayCast(fight, breeder, spell));
                var caught = await RideCapture.CastAsync(null!, fight, breeder, spell, centre);
                Assert.Equal(almond, Assert.Single(caught));
                Assert.False(RideCapture.MayCast(fight, breeder, spell));
                Assert.True(RideCapture.MayCast(fight, breeder, 0));   // anything else still goes
                Assert.Empty(await RideCapture.CastAsync(null!, fight, breeder, spell, centre));

                // The ally's net finds it marked already.
                int universal = Breeding.NetByItem(Universal)!.Spell;
                Assert.True(RideCapture.MayCast(fight, ally, universal));
                Assert.Empty(await RideCapture.CastAsync(null!, fight, ally, universal, centre));

                var rides = RideCapture.RidesOf(fight.FightId, breeder.Id);
                Assert.Equal(2, rides[Breeding.RideOfWild(AlmondDragoturkey)!.Id]);
                Assert.Empty(RideCapture.RidesOf(fight.FightId, ally.Id));

                // Lost: nothing, and the fight's catches are forgotten.
                await RideCapture.FightEndedAsync(player, fight, won: false);
                Assert.Empty(RideCapture.RidesOf(fight.FightId, breeder.Id));
                Assert.True(RideCapture.MayCast(fight, breeder, spell));
            }
        }

        /// <summary>
        /// The net in the combat consumables' slot (31) gives its spell to the fight's bar, on a
        /// free slot; in the bag it gives nothing.
        /// </summary>
        [Fact]
        public void The_equipped_net_puts_its_spell_on_the_fight_bar()
        {
            if (Breeding.NetByItem(Universal) == null) return;

            var player = GameSession.SinSocket();
            player.State.CharacterId = 9_100_300;
            using (SessionContext.Push(player))
            {
                Equipment.Add(9_100_301, ReinforcedDragoturkeys, 2, Equipment.Bag, null);
                Assert.Equal(0, RideCapture.SpellOfEquippedNet());

                Equipment.Remove(9_100_301, 2);
                Equipment.Add(9_100_302, ReinforcedDragoturkeys, 2, RideCapture.ConsumableSlot, null);
                Assert.Equal(32270, RideCapture.SpellOfEquippedNet());

                var layout = FightSpellLayout.ForFight(1, 1, 0);
                Assert.Contains(layout.Spells, s => s.Spell == 32270);
                var slot = Assert.Single(layout.Bar, b => b.Spell == 32270).Slot;
                Assert.InRange(slot, 1, FightSpellLayout.SlotCount - 1);
                Assert.Single(layout.Bar, b => b.Slot == slot);
            }
        }
    }
}
