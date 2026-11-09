using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Selatrop's portals (1181, 1182, 1183) against their six captures: the frames byte for
    /// byte, the network's rules -- which portal is on, which way out, where a spell lands -- and
    /// the sequences the fight sends when one is laid, walked into, gone through by a Teleportal,
    /// switched off, and brought back at a turn start.
    /// </summary>
    [Collection("MapManager")]
    public class PortalTests
    {
        private const long Selatrop = 53000000027;

        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        private static Fighter Character(long id, int cell) => new()
        {
            Id = id, Name = "P" + id, CellId = cell, Level = 200, MaxHP = 3000, CurrentHP = 3000,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, Name = "M" + id, IsMonster = true, CellId = cell, Level = 50, MaxHP = 900, CurrentHP = 900,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, MonsterId = 494,
        };

        /// <summary>A fight on a map with no ground data, the Selatrop blue and the monster red, where the test puts them.</summary>
        private static FightInstance Board(Fighter selatrop, params Fighter[] monsters)
        {
            var fight = new FightInstance(4104, 0, 0);
            int cell = selatrop.CellId;
            fight.AddPlayer(selatrop);
            selatrop.CellId = cell;
            foreach (var monster in monsters)
            {
                int where = monster.CellId;
                fight.AddMonster(monster);
                monster.CellId = where;
            }
            return fight;
        }

        /// <summary>A portal of the Selatrop's, as the Portal spell lays it, already on the board.</summary>
        private static Portal Laid(FightInstance fight, int id, int cell, bool active = true, long owner = Selatrop)
        {
            var portal = new Portal
            {
                Id = id, Owner = owner, Cell = cell, LayingSpell = 14574, Grade = 3,
                DiceNum = 2, DiceSide = 44338, Active = active,
            };
            fight.Portales.Add(portal);
            return portal;
        }

        /// <summary>A 1181 as the engine hands it over, with whoever stood on the cell as it resolved.</summary>
        private static Outcome Laying(int cell, int spell = 14574, int grade = 3, long occupant = 0) => new()
        {
            Efecto = new SpellEffect { EffectId = EffectEngine.ColocaUnPortal, DiceNum = 2, DiceSide = 44338 },
            HechizoOrigen = spell, NivelOrigen = grade, PortalAt = cell, PortalOccupant = occupant,
        };

        // ─── The frames ──────────────────────────────────────────────────────────────────────

        /// <summary>The jwe 401 of a portal: "poner portales de selatrop" 109 and 131, Resonancia's 224, Estela's 12.</summary>
        [Fact]
        public void A_portal_is_its_jwe_401()
        {
            Assert.Equal(Hex("189ba4afb8c5017091038202230a210a0610ff0118ac0210b2da02180220012803300348ee7150ac02609ba4afb8c501"),
                         FightProtocol.BuildPortal(Selatrop, 1, 300, 2, 44338, 3, 14574, active: false));
            Assert.Equal(Hex("189ba4afb8c5017091038202250a230a0610ff01188f0310b2da02180220022803300348ee71508f035801609ba4afb8c501"),
                         FightProtocol.BuildPortal(Selatrop, 2, 399, 2, 44338, 3, 14574, active: true));
            Assert.Equal(Hex("189ba4afb8c5017091038202240a220a0610ff0118a90310b2da02180220032803300148acf20150a903609ba4afb8c501"),
                         FightProtocol.BuildPortal(Selatrop, 3, 425, 2, 44338, 1, 31020, active: false));
            Assert.Equal(Hex("189ba4afb8c5017091038202260a240a0610ff0118f30210b2da021802200e2803300148adf20150f3025801609ba4afb8c501"),
                         FightProtocol.BuildPortal(Selatrop, 14, 371, 2, 44338, 1, 31021, active: true));
        }

        /// <summary>The 1181 of a portal turned off and on: frames 110 and 132, and the turn start's 262 in -1's name.</summary>
        [Fact]
        public void A_portal_s_state_is_its_jwe_1181()
        {
            Assert.Equal(Hex("189ba4afb8c501709d098a01020801"), FightProtocol.BuildPortalState(Selatrop, 1, active: false));
            Assert.Equal(Hex("189ba4afb8c501709d098a010408011001"), FightProtocol.BuildPortalState(Selatrop, 1, active: true));
            Assert.Equal(Hex("18ffffffffffffffffff01709d098a010408071001"), FightProtocol.BuildPortalState(-1, 7, active: true));
        }

        /// <summary>
        /// The casts: Audacia aimed at the portal on 303 lands on 344 through 10, 9, 8 and 7 --
        /// frame 16 of "pegar a traves", on an empty cell, so naming nobody --; Insolencia through
        /// 3, 5, 6 and 4 lands on -1 at 513 (Resonancia's frame 374); and a Portal cast on an empty
        /// cell names nobody either (frame 104 of the placement capture).
        /// </summary>
        [Fact]
        public void A_cast_through_the_portals_names_them()
        {
            Assert.Equal(Hex("189ba4afb8c5013a2122091001209ba4afb8c501280130d8023a0d0a040a09080710817218eeda02400170ac02"),
                         FightProtocol.BuildAction(Selatrop, FightProtocol.Cast,
                             FightProtocol.CastAt(Selatrop, 0, 344, 14593, 44398, critical: true, esteTurno: 1,
                                                  noTarget: true, portals: new[] { 10, 9, 8, 7 }),
                             FightProtocol.CastDetail));
            Assert.Equal(Hex("189ba4afb8c5013a3910ffffffffffffffffff0122180a0d080110ffffffffffffffffff011001209ba4afb8c5013081043a0d0a040305060410ad7218abfb04400170ac02"),
                         FightProtocol.BuildAction(Selatrop, FightProtocol.Cast,
                             FightProtocol.CastAt(Selatrop, -1, 513, 14637, 81323, critical: false, sobreEseObjetivo: 1,
                                                  esteTurno: 1, noTarget: false, portals: new[] { 3, 5, 6, 4 }),
                             FightProtocol.CastDetail));
            Assert.Equal(Hex("189ba4afb8c5013a1922091001209ba4afb8c50130ac023a0710ee7118b5da02400170ac02"),
                         FightProtocol.BuildAction(Selatrop, FightProtocol.Cast,
                             FightProtocol.CastAt(Selatrop, 0, 300, 14574, 44341, critical: false, esteTurno: 1,
                                                  noTarget: true),
                             FightProtocol.CastDetail));
        }

        // ─── The network ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The four portals of "pegar a traves de diferentes portales": 7 on 400, 8 on 329, 9 on
        /// 288, 10 on 303. Every chain the capture shows, from each entry, with the ties going to
        /// the newest -- from 9, 10 and 8 are both three cells away and 10 goes first.
        /// </summary>
        [Theory]
        [InlineData(10, new[] { 10, 9, 8, 7 })]   // frames 16 and 185
        [InlineData(7, new[] { 7, 8, 9, 10 })]    // frames 91 and 386
        [InlineData(9, new[] { 9, 10, 8, 7 })]    // frame 125
        [InlineData(8, new[] { 8, 9, 10, 7 })]    // frame 299
        public void The_chain_goes_to_the_nearest_portal_each_time(int entry, int[] chain)
        {
            var network = new PortalNetwork();
            var fight = Board(Character(Selatrop, 100));
            foreach (var (id, cell) in new[] { (7, 400), (8, 329), (9, 288), (10, 303) })
                network.Add(new Portal { Id = id, Owner = Selatrop, Cell = cell });

            var found = network.Chain(network.ById(entry), fight.OccupantOf);

            Assert.Equal(chain, found.Select(p => p.Id));
        }

        /// <summary>
        /// "usar neutral": from 11 on 346, 10 on 303 and 12 on 386 are both three cells away, and
        /// the Selatrop comes out of 9 -- the chain went 11, 12, 10, 9: the newest first again.
        /// </summary>
        [Fact]
        public void The_newest_of_two_as_near_goes_first()
        {
            var network = new PortalNetwork();
            foreach (var (id, cell) in new[] { (9, 288), (10, 303), (11, 346), (12, 386) })
                network.Add(new Portal { Id = id, Owner = Selatrop, Cell = cell });

            Assert.Equal(new[] { 11, 12, 10, 9 }, network.Chain(network.ById(11), _ => 0).Select(p => p.Id));
        }

        /// <summary>
        /// Where a spell cast at a portal lands: the vector from the caster to the portal, laid
        /// from the way out. Seven casts of three captures, cell for cell.
        /// </summary>
        [Theory]
        [InlineData(359, 303, 400, 344)]   // "pegar", Audacia, frame 16
        [InlineData(359, 400, 303, 344)]   // Mofa, frame 91
        [InlineData(317, 288, 400, 371)]   // Insolencia, frame 125
        [InlineData(358, 329, 288, 259)]   // Aflicción, frame 207
        [InlineData(358, 288, 329, 259)]   // Distribución, frame 228
        [InlineData(368, 425, 456, 513)]   // "trascendencia y resonancia", Insolencia, frame 374
        public void A_projected_spell_lands_where_the_aim_leads(int caster, int entry, int exit, int lands)
        {
            Assert.Equal(lands, PortalNetwork.Projection(caster, entry, exit));
        }

        /// <summary>
        /// On and off, as "poner portales de selatrop" has them: the first alone is off, the second
        /// turns it on; one crossed this turn, switched off or stood on cannot be used.
        /// </summary>
        [Fact]
        public void A_portal_is_on_with_another_to_go_to()
        {
            var network = new PortalNetwork();
            var first = new Portal { Id = 1, Owner = Selatrop, Cell = 300 };
            network.Add(first);
            Assert.False(network.ShouldBeActive(first, _ => 0));

            var second = new Portal { Id = 2, Owner = Selatrop, Cell = 399 };
            network.Add(second);
            Assert.Equal(new[] { 1, 2 }, network.Refresh(_ => 0).Select(p => p.Id));
            Assert.True(first.Active && second.Active);

            // Somebody on the second: both off, nothing left to go to.
            Assert.Equal(new[] { 1, 2 }, network.Refresh(c => c == 399 ? 7L : 0).Select(p => p.Id));
            Assert.False(first.Active || second.Active);

            // Crossed this turn, then a turn begins.
            second.Used = true;
            network.Refresh(_ => 0);
            Assert.False(first.Active);
            network.TurnBegins(-1);
            network.Refresh(_ => 0);
            Assert.True(first.Active && second.Active);

            // Switched off by the Selatrop: back at HIS turn, not at somebody else's.
            first.NeutralisedBy = Selatrop;
            network.Refresh(_ => 0);
            network.TurnBegins(-1);
            network.Refresh(_ => 0);
            Assert.False(first.Active);
            network.TurnBegins(Selatrop);
            network.Refresh(_ => 0);
            Assert.True(first.Active);
        }

        /// <summary>Four a Selatrop: the fifth pushes the oldest out.</summary>
        [Fact]
        public void The_fifth_portal_pushes_the_oldest_out()
        {
            var network = new PortalNetwork();
            for (int i = 1; i <= 3; i++) network.Add(new Portal { Id = i, Owner = Selatrop, Cell = 300 + i });
            Assert.Null(network.Displaced(Selatrop));
            network.Add(new Portal { Id = 4, Owner = Selatrop, Cell = 310 });
            Assert.Equal(1, network.Displaced(Selatrop).Id);
            Assert.Null(network.Displaced(-1));
        }

        /// <summary>
        /// «Cada equipo tiene un límite de cuatro portales, aunque haya varios selotropes en el
        /// equipo»: two allies' portals count together, and are one network; «las redes de dos
        /// selotropes enemigos no se comunican entre sí».
        /// </summary>
        [Fact]
        public void Four_portals_a_side_and_one_network_for_allies()
        {
            const long Ally = Selatrop + 1, Foe = Selatrop + 2;
            var network = new PortalNetwork();
            network.Add(new Portal { Id = 1, Owner = Selatrop, Team = 0, Cell = 301 });
            network.Add(new Portal { Id = 2, Owner = Selatrop, Team = 0, Cell = 302 });
            network.Add(new Portal { Id = 3, Owner = Ally, Team = 0, Cell = 303 });
            Assert.Null(network.Displaced(Ally, 0));
            network.Add(new Portal { Id = 4, Owner = Ally, Team = 0, Cell = 304 });
            // The side's fifth pushes out the side's oldest, whoever laid it.
            Assert.Equal(1, network.Displaced(Ally, 0).Id);
            // The other side has its own four.
            network.Add(new Portal { Id = 5, Owner = Foe, Team = 1, Cell = 305 });
            Assert.Null(network.Displaced(Foe, 1));

            // An ally's portal makes a network with the other's; a foe's does not.
            var lone = new PortalNetwork();
            var mine = new Portal { Id = 1, Owner = Selatrop, Team = 0, Cell = 301 };
            var his = new Portal { Id = 2, Owner = Ally, Team = 0, Cell = 320 };
            var theirs = new Portal { Id = 3, Owner = Foe, Team = 1, Cell = 340 };
            lone.Add(mine); lone.Add(theirs);
            Assert.False(lone.ShouldBeActive(mine, _ => 0));
            lone.Add(his);
            Assert.True(lone.ShouldBeActive(mine, _ => 0));
            Assert.False(lone.ShouldBeActive(theirs, _ => 0));
        }

        /// <summary>
        /// Through a chain the blow and the heal grow by the entry's value and its die per cell
        /// between two portals: 2% a cell, "+#3% daños, +#1% por casilla". Inferred.
        /// </summary>
        [Fact]
        public void The_bonus_is_two_per_cent_a_cell_between_portals()
        {
            var chain = new List<Portal>
            {
                new() { Id = 3, Cell = 425, DiceNum = 2 }, new() { Id = 5, Cell = 426, DiceNum = 2 },
                new() { Id = 6, Cell = 440, DiceNum = 2 }, new() { Id = 4, Cell = 456, DiceNum = 2 },
            };
            Assert.Equal(6, PortalNetwork.CellsBetween(chain));
            Assert.Equal(12, PortalNetwork.BonusPercent(chain));
        }

        /// <summary>
        /// A spell aimed at a portal that is on goes through it; one that switches portals off
        /// does not -- Neutral aimed at 400 switches that portal off ("usar neutral", frame 2)
        /// where Shock, with the very same flags, goes through.
        /// </summary>
        [Fact]
        public void A_spell_that_switches_portals_off_does_not_go_through()
        {
            var selatrop = Character(Selatrop, 359);
            var fight = Board(selatrop, Monster(-1, 100));
            foreach (var (id, cell) in new[] { (7, 400), (8, 329), (9, 288), (10, 303) }) Laid(fight, id, cell);

            var shock = FightHandler.Projection(fight, selatrop, 303, SpellEffects.De(14583, 3));
            Assert.NotNull(shock);
            Assert.Equal(new[] { 10, 9, 8, 7 }, shock.Value.Chain.Select(p => p.Id));
            Assert.Equal(344, shock.Value.Cell);

            Assert.Null(FightHandler.Projection(fight, selatrop, 400, SpellEffects.De(14582, 3)));
            Assert.Null(FightHandler.Projection(fight, selatrop, 305, SpellEffects.De(14583, 3)));
        }

        /// <summary>
        /// Portal aimed at a portal goes through, as the client draws it, and lays its portal where
        /// it comes out; a level flagged PortalProjectionForbidden (bit 12) stays on the aimed cell.
        /// </summary>
        [Fact]
        public void Portal_goes_through_the_portals_unless_its_level_forbids_it()
        {
            var selatrop = Character(Selatrop, 359);
            var fight = Board(selatrop, Monster(-1, 100));
            foreach (var (id, cell) in new[] { (7, 400), (8, 329), (9, 288), (10, 303) }) Laid(fight, id, cell);

            var portal = FightHandler.Projection(fight, selatrop, 303, SpellEffects.De(14574, 3), flags: 2572);
            Assert.NotNull(portal);
            Assert.Equal(344, portal.Value.Cell);

            Assert.Null(FightHandler.Projection(fight, selatrop, 303, SpellEffects.De(14583, 3),
                                                flags: SpellLevelFlags.PortalProjectionForbidden));
        }

        /// <summary>
        /// Insulto pushes -1 two cells, and the first is a portal that is on: he goes through it,
        /// out of the other, and the cell left is walked from there, the same way.
        /// </summary>
        [Fact]
        public async Task A_push_into_a_portal_goes_on_from_the_way_out()
        {
            int Cell(int x, int y) => MapGeometry.PointToCell(x, y);
            var selatrop = Character(Selatrop, Cell(12, 2));
            var enemy = Monster(-1, Cell(13, 2));
            var fight = Board(selatrop, enemy);
            Laid(fight, 1, Cell(14, 2));
            Laid(fight, 2, Cell(14, 8));
            await using var wire = await Wire.Open(Selatrop);

            await FightHandler.AplicarEfectosAsync(null, fight, selatrop, 14575, 3, enemy, EffectEngine.AlLanzar, enemy.CellId);

            Assert.Equal(Cell(15, 8), enemy.CellId);
            Assert.True(fight.Portales.ById(1).Used && fight.Portales.ById(2).Used);
            var frames = (await wire.Drain()).Where(f => f.Op == "jwe").Select(f => f.Payload).ToList();
            byte[] Slide(int from, int to) => FightProtocol.BuildDisplacement(Selatrop, FightProtocol.Alejarse, -1, from, to);
            int into = frames.FindIndex(f => f.SequenceEqual(Slide(Cell(13, 2), Cell(14, 2))));
            int teleport = frames.FindIndex(f => f.SequenceEqual(FightProtocol.BuildTeleport(-1, -1, Cell(14, 8))));
            int onwards = frames.FindIndex(f => f.SequenceEqual(Slide(Cell(14, 8), Cell(15, 8))));
            Assert.True(into >= 0 && teleport > into && onwards > teleport, $"{into}, {teleport}, {onwards}");
        }

        /// <summary>
        /// Terapia aimed at the portal on 353 comes out of the one on 371 onto -1 at 411, and draws
        /// him two cells towards 371, to 384, as the client's preview does -- not towards the
        /// Selatrop on 313, which left him on 382 (the log of 9 October, 00:59:10).
        /// </summary>
        [Fact]
        public async Task A_pull_through_the_portals_draws_towards_the_way_out()
        {
            var selatrop = Character(Selatrop, 313);
            var enemy = Monster(-1, 411);
            var fight = Board(selatrop, enemy);

            fight.CastThroughPortal = true;
            fight.ProjectedFrom = 371;
            await FightHandler.AplicarEfectosAsync(null, fight, selatrop, 14581, 3, enemy, EffectEngine.AlLanzar, 411);
            Assert.Equal(384, enemy.CellId);

            // Cast straight at him, it draws him towards the Selatrop, as it always did.
            fight.CastThroughPortal = false;
            fight.ProjectedFrom = -1;
            enemy.MoverA(411);
            await FightHandler.AplicarEfectosAsync(null, fight, selatrop, 14581, 3, enemy, EffectEngine.AlLanzar, 411);
            Assert.Equal(382, enemy.CellId);
        }

        /// <summary>
        /// Insulto on -1 at 382 with Resonancia on him: the blow sets Resonancia off and its
        /// Teleportal takes him out at 371, and Insulto's push still reaches him there -- as
        /// Shock's pull does after Resonancia in its capture (frames 176-193). Judged after the
        /// blow, its zone found nobody on 382 and he stayed on 371 (the log of 9 October,
        /// 01:01:02). The push is Insulto's chained 26192, cast at him where he stands, so it sends
        /// him two cells away from the Selatrop on 355, from 371 to 400 -- what the same Insulto
        /// did to him there at 01:01:13 with no portal involved.
        /// </summary>
        [Fact]
        public async Task A_push_reaches_the_one_its_blow_teleported()
        {
            var selatrop = Character(Selatrop, 355);
            var enemy = Monster(-1, 382);
            var fight = Board(selatrop, enemy);
            fight.CastLanded = new FightInstance.CastSnapshot(Selatrop, 14575,
                new Dictionary<long, int> { [Selatrop] = 355, [-1] = 382 });
            enemy.MoverA(371);

            await FightHandler.AplicarEfectosAsync(null, fight, selatrop, 14575, 3, enemy, EffectEngine.AlLanzar, 382);

            Assert.Equal(400, enemy.CellId);
        }

        /// <summary>
        /// A pull of the cast's own rows, on one its blow has moved, goes the way the spell found
        /// him: Shock's pull after Resonancia takes -1 from 526 to 513, the one cell from 425,
        /// where he was hit, towards the centre on 411 ("hechizos trascendencia y resonancia",
        /// frames 176-193).
        /// </summary>
        [Fact]
        public async Task A_pull_after_the_teleportal_goes_the_way_the_spell_found_him()
        {
            var selatrop = Character(Selatrop, 368);
            var enemy = Monster(-1, 425);
            var fight = Board(selatrop, enemy);
            fight.CastLanded = new FightInstance.CastSnapshot(Selatrop, 14583,
                new Dictionary<long, int> { [Selatrop] = 368, [-1] = 425 });
            enemy.MoverA(526);

            await FightHandler.AplicarEfectosAsync(null, fight, selatrop, 14583, 3, enemy, EffectEngine.AlLanzar, 411);

            Assert.Equal(513, enemy.CellId);
        }

        /// <summary>
        /// Extinción through the portals hooks its poison on the target -- "99 under TE on a,A,R",
        /// the 3793 and the 99 the capture puts on -1 at the cast (frames 10-11) -- and it burns
        /// at the end of his turn (45-46). Cast straight at him, there is no poison.
        /// </summary>
        [Fact]
        public async Task Extincion_through_a_portal_burns_at_the_end_of_the_target_s_turn()
        {
            var selatrop = Character(Selatrop, 300);
            var enemy = Monster(-1, 302);
            var fight = Board(selatrop, enemy);
            await using var wire = await Wire.Open(Selatrop);

            fight.CastThroughPortal = true;
            await FightHandler.AplicarEfectosAsync(null, fight, selatrop, 14594, 2, enemy, EffectEngine.AlLanzar, 302);
            fight.CastThroughPortal = false;
            var hook = Assert.Single(enemy.Buffs.ActiveSpells, a => a.Hechizo == 14594);
            Assert.True(hook.ThroughPortal);

            int life = enemy.CurrentHP;
            await FightHandler.EngancheAsync(null, fight, enemy, EffectEngine.AlAcabarElTurno);
            Assert.True(enemy.CurrentHP < life);
            Assert.False(fight.CastThroughPortal);

            var straight = Monster(-2, 304);
            fight.AddMonster(straight);
            straight.CellId = 304;
            await FightHandler.AplicarEfectosAsync(null, fight, selatrop, 14594, 2, straight, EffectEngine.AlLanzar, 304);
            life = straight.CurrentHP;
            await FightHandler.EngancheAsync(null, fight, straight, EffectEngine.AlAcabarElTurno);
            Assert.Equal(life, straight.CurrentHP);
        }


        /// <summary>"Teleportal Imposible" (678), which the passive puts on the enemies for the first round, keeps them out.</summary>
        [Fact]
        public void Teleportal_imposible_keeps_out()
        {
            var monster = Monster(-1, 100);
            Assert.True(FightHandler.CanUsePortals(monster));
            monster.Buffs.PonerEstado(678);
            Assert.False(FightHandler.CanUsePortals(monster));
        }

        /// <summary>A walk ends on the first portal of it that is on.</summary>
        [Fact]
        public void A_walk_stops_on_the_portal()
        {
            var selatrop = Character(Selatrop, 358);
            var fight = Board(selatrop, Monster(-1, 100));
            Laid(fight, 8, 329); Laid(fight, 9, 288);

            Assert.Equal(new[] { 358, 344, 329 }, FightHandler.StopAtThePortal(fight, selatrop, new[] { 358, 344, 329, 315 }));
            Assert.Equal(new[] { 358, 344 }, FightHandler.StopAtThePortal(fight, selatrop, new[] { 358, 344 }));
        }

        // ─── The sequences ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// "poner portales de selatrop": the first portal goes down off and says so (109-110), the
        /// second on and turns the first on (131-132), and the fifth pushes the first out (234-235).
        /// </summary>
        [Fact]
        public async Task Laying_portals_is_the_capture()
        {
            var selatrop = Character(Selatrop, 302);
            var fight = Board(selatrop, Monster(-1, 438));
            await using var wire = await Wire.Open(Selatrop);

            await FightHandler.LayPortalAsync(fight, selatrop, Laying(300));
            await FightHandler.LayPortalAsync(fight, selatrop, Laying(399));
            await FightHandler.LayPortalAsync(fight, selatrop, Laying(288));
            await FightHandler.LayPortalAsync(fight, selatrop, Laying(303));
            await FightHandler.LayPortalAsync(fight, selatrop, Laying(346));

            var frames = await wire.Drain();
            Assert.Equal(new[] { "jwe", "jwe", "jwe", "jwe", "jwe", "jwe", "jwe", "jwe" }, frames.Select(f => f.Op));
            Assert.Equal(Hex("189ba4afb8c5017091038202230a210a0610ff0118ac0210b2da02180220012803300348ee7150ac02609ba4afb8c501"), frames[0].Payload);
            Assert.Equal(Hex("189ba4afb8c501709d098a01020801"), frames[1].Payload);
            Assert.Equal(Hex("189ba4afb8c5017091038202250a230a0610ff01188f0310b2da02180220022803300348ee71508f035801609ba4afb8c501"), frames[2].Payload);
            Assert.Equal(Hex("189ba4afb8c501709d098a010408011001"), frames[3].Payload);
            // The third and the fourth, on, with nothing else changing (181, 193).
            StealTests.SameFields(Hex("189ba4afb8c50170b602b201020801"), frames[6].Payload);     // 234
            Assert.Equal(Hex("189ba4afb8c5017091038202250a230a0610ff0118da0210b2da02180220052803300348ee7150da025801609ba4afb8c501"), frames[7].Payload); // 235
            Assert.Equal(new[] { 2, 3, 4, 5 }, fight.Portales.All.Select(p => p.Id).OrderBy(i => i));
        }

        /// <summary>
        /// "pegar a traves de diferentes portales", frames 185-190: walked onto the portal on 303,
        /// the Selatrop comes out of the one on 400, inside his walk -- 307, 1181 off for the way
        /// in and the way out, jwe 4, 307 -- and the walk ends there.
        /// </summary>
        [Fact]
        public async Task Walking_into_a_portal_is_the_capture()
        {
            var selatrop = Character(Selatrop, 317);
            var fight = Board(selatrop, Monster(-1, 100));
            foreach (var (id, cell) in new[] { (7, 400), (8, 329), (9, 288), (10, 303) }) Laid(fight, id, cell);
            await using var wire = await Wire.Open(Selatrop);

            var walked = await FightHandler.WalkPathAsync(fight, selatrop, new[] { 317, 303, 289 }, facing: 7);

            Assert.Equal(new[] { 317, 303 }, walked);
            Assert.Equal(400, selatrop.CellId);
            Assert.Equal(5, selatrop.CurrentMP);
            var frames = await wire.Drain();
            Assert.Equal(new[] { "jto", "jsj", "jto", "jxw", "jwi", "jwe", "jwe", "jwe", "jwe", "jwe", "jwe", "jwi" },
                         frames.Select(f => f.Op));
            Assert.Equal(Hex("0a04bd02af021007289ba4afb8c501"), frames[1].Payload);                        // 180
            Assert.Equal(Hex("189ba4afb8c5014a0c08af02109ba4afb8c501200a70b302"), frames[6].Payload);    // 185
            Assert.Equal(Hex("189ba4afb8c501709d098a0102080a"), frames[7].Payload);                      // 186
            Assert.Equal(Hex("189ba4afb8c501709d098a01020807"), frames[8].Payload);                      // 187
            Assert.Equal(Hex("189ba4afb8c50170049a020a089003109ba4afb8c501"), frames[9].Payload);        // 188
            Assert.Equal(Hex("189ba4afb8c5014a0c089003109ba4afb8c501200770b302"), frames[10].Payload);   // 189

            // He walks off the portal he came out of (frames 192-199) -- crossed this turn, it stays
            // off -- and the turn of -1 brings the two back, in his name, in a sequence of kind 2
            // (261-264).
            selatrop.MoverA(358);
            await FightHandler.RefreshPortalsAsync(fight);
            Assert.Empty(await wire.Drain());
            await FightHandler.PortalsAtTurnStartAsync(fight, fight.Buscar(-1));
            frames = await wire.Drain();
            Assert.Equal(new[] { "jto", "jwe", "jwe", "jwi" }, frames.Select(f => f.Op));
            Assert.Equal(Hex("08ffffffffffffffffff011002"), frames[0].Payload);
            Assert.Equal(Hex("18ffffffffffffffffff01709d098a010408071001"), frames[1].Payload);
            Assert.Equal(Hex("18ffffffffffffffffff01709d098a0104080a1001"), frames[2].Payload);
        }

        /// <summary>
        /// "usar neutral en portales-quedarse encima de portal": the one he came out of, 9, stays
        /// off at -1's turn while he stands on it, and the one he went in through comes back
        /// (frames 187-212).
        /// </summary>
        [Fact]
        public async Task A_portal_stood_on_stays_off()
        {
            var selatrop = Character(Selatrop, 331);
            var fight = Board(selatrop, Monster(-1, 100));
            foreach (var (id, cell) in new[] { (9, 288), (10, 303), (11, 346), (12, 386) }) Laid(fight, id, cell);
            await using var wire = await Wire.Open(Selatrop);

            await FightHandler.WalkPathAsync(fight, selatrop, new[] { 331, 346 }, facing: 1);
            Assert.Equal(288, selatrop.CellId);

            await FightHandler.PortalsAtTurnStartAsync(fight, fight.Buscar(-1));
            var frames = await wire.Drain();
            var last = frames.TakeLast(3).ToList();
            Assert.Equal(new[] { "jto", "jwe", "jwi" }, last.Select(f => f.Op));
            Assert.Equal(Hex("18ffffffffffffffffff01709d098a0104080b1001"), last[1].Payload);          // 212
            Assert.False(fight.Portales.ById(9).Active);
        }

        /// <summary>
        /// Neutral on the portal on 400: off at once (frame 7), back at the Selatrop's next turn
        /// in his name (frames 69-72) and not at -1's.
        /// </summary>
        [Fact]
        public async Task Neutral_switches_a_portal_off_until_the_caster_s_turn()
        {
            var selatrop = Character(Selatrop, 371);
            var fight = Board(selatrop, Monster(-1, 100));
            foreach (var (id, cell) in new[] { (7, 400), (8, 329), (9, 288), (10, 303) }) Laid(fight, id, cell);
            await using var wire = await Wire.Open(Selatrop);

            await FightHandler.SwitchPortalsOffAsync(fight, selatrop, new[] { 400 });
            await FightHandler.SwitchPortalsOffAsync(fight, selatrop, new[] { 329 });
            await FightHandler.PortalsAtTurnStartAsync(fight, fight.Buscar(-1));
            await FightHandler.PortalsAtTurnStartAsync(fight, selatrop);

            var frames = await wire.Drain();
            Assert.Equal(new[] { "jwe", "jwe", "jto", "jwe", "jwe", "jwi" }, frames.Select(f => f.Op));
            Assert.Equal(Hex("189ba4afb8c501709d098a01020807"), frames[0].Payload);                       // 7
            Assert.Equal(Hex("089ba4afb8c5011002"), frames[2].Payload);                                   // 69
            Assert.Equal(Hex("189ba4afb8c501709d098a010408071001"), frames[3].Payload);                   // 70
            Assert.Equal(Hex("189ba4afb8c501709d098a010408081001"), frames[4].Payload);                   // 71
        }

        /// <summary>
        /// Resonancia, frames 224-229: a portal laid under -1 goes down off, and the Teleportal takes
        /// him out of the one on 526 -- the way out off, his jwe 4, the portal on 438 left alone and
        /// off, and the 307 of the way out.
        /// </summary>
        [Fact]
        public async Task Resonancia_s_teleportal_is_the_capture()
        {
            var selatrop = Character(Selatrop, 427);
            var enemy = Monster(-1, 425);
            var fight = Board(selatrop, enemy);
            Laid(fight, 1, 438); Laid(fight, 2, 526);
            fight.SiguienteMarca(); fight.SiguienteMarca();
            await using var wire = await Wire.Open(Selatrop);

            await FightHandler.LayPortalAsync(fight, selatrop, Laying(425, 31020, 1, occupant: -1));
            Assert.True(await FightHandler.CrossPortalAsync(null, fight, enemy, walkedIn: false));

            Assert.Equal(526, enemy.CellId);
            var frames = await wire.Drain();
            Assert.Equal(Hex("189ba4afb8c5017091038202240a220a0610ff0118a90310b2da02180220032803300148acf20150a903609ba4afb8c501"), frames[0].Payload);
            Assert.Equal(Hex("189ba4afb8c501709d098a01020803"), frames[1].Payload);
            Assert.Equal(Hex("189ba4afb8c501709d098a01020802"), frames[2].Payload);
            Assert.Equal(Hex("18ffffffffffffffffff0170049a020e088e0410ffffffffffffffffff01"), frames[3].Payload);
            Assert.Equal(Hex("189ba4afb8c501709d098a01020801"), frames[4].Payload);
            Assert.Equal(Hex("189ba4afb8c5014a10088e0410ffffffffffffffffff01200270b302"), frames[5].Payload);
            Assert.Equal(6, frames.Count);
        }

        /// <summary>
        /// Estela, frames 11-15: the portal under its caster pushes the oldest out, goes down on,
        /// off at once while he stands on it, and on again once he has jumped away.
        /// </summary>
        [Fact]
        public async Task Estela_s_portal_is_the_capture()
        {
            var selatrop = Character(Selatrop, 371);
            var fight = Board(selatrop, Monster(-1, 100));
            Laid(fight, 10, 300); Laid(fight, 11, 346); Laid(fight, 12, 386); Laid(fight, 13, 288);
            for (int i = 0; i < 13; i++) fight.SiguienteMarca();
            await using var wire = await Wire.Open(Selatrop);

            await FightHandler.LayPortalAsync(fight, selatrop, Laying(371, 31021, 1, occupant: Selatrop));
            selatrop.MoverA(427);
            await FightHandler.RefreshPortalsAsync(fight);

            var frames = await wire.Drain();
            Assert.Equal(4, frames.Count);
            StealTests.SameFields(Hex("189ba4afb8c50170b602b20102080a"), frames[0].Payload);                  // 11
            Assert.Equal(Hex("189ba4afb8c5017091038202260a240a0610ff0118f30210b2da021802200e2803300148adf20150f3025801609ba4afb8c501"), frames[1].Payload); // 12
            Assert.Equal(Hex("189ba4afb8c501709d098a0102080e"), frames[2].Payload);                          // 13
            Assert.Equal(Hex("189ba4afb8c501709d098a0104080e1001"), frames[3].Payload);                      // 15
        }

        /// <summary>
        /// Odisea, frames 92-97: stepped back onto the portal on 215, the Selatrop goes through it
        /// and out of the one on 427, 307 and all.
        /// </summary>
        [Fact]
        public async Task A_step_back_onto_a_portal_goes_through_it()
        {
            var selatrop = Character(Selatrop, 215);
            var fight = Board(selatrop, Monster(-1, 288));
            Laid(fight, 15, 427); Laid(fight, 16, 369); Laid(fight, 17, 325); Laid(fight, 18, 215);
            await using var wire = await Wire.Open(Selatrop);

            Assert.True(FightHandler.PortalCatches(fight, selatrop, 215));
            Assert.True(FightHandler.DisplacementCrossesPortals(1041));
            Assert.True(await FightHandler.CrossPortalAsync(null, fight, selatrop, walkedIn: true));

            Assert.Equal(427, selatrop.CellId);
            var frames = await wire.Drain();
            Assert.Equal(Hex("189ba4afb8c5014a0c08d701109ba4afb8c501201270b302"), frames[0].Payload);      // 93
            Assert.Equal(Hex("189ba4afb8c501709d098a01020812"), frames[1].Payload);                      // 94
            Assert.Equal(Hex("189ba4afb8c501709d098a0102080f"), frames[2].Payload);                      // 95
        }

        /// <summary>
        /// Neutral cast at the portal on 400, from the engine to the wire: the portal off (frame
        /// 7), then its AP back -- the sheet in its short sequence and "jwe 120" (frames 8-11).
        /// </summary>
        [Fact]
        public async Task Neutral_is_its_capture()
        {
            var selatrop = Character(Selatrop, 371);
            selatrop.Initiative = 5000;
            var fight = Board(selatrop, Monster(-1, 100));
            foreach (var (id, cell) in new[] { (7, 400), (8, 329), (9, 288), (10, 303) }) Laid(fight, id, cell);
            fight.StartFight();
            Assert.Same(selatrop, fight.CurrentFighter);
            selatrop.CurrentAP = 11;
            await using var wire = await Wire.Open(Selatrop);

            await FightHandler.AplicarEfectosAsync(null, fight, selatrop, 14582, 3, null, EffectEngine.AlLanzar, 400);

            Assert.Equal(12, selatrop.CurrentAP);
            Assert.Equal(Selatrop, fight.Portales.ById(7).NeutralisedBy);
            var frames = await wire.Drain();
            Assert.Equal(new[] { "jwe", "jto", "jxw", "jwi", "jwe" }, frames.Select(f => f.Op));
            Assert.Equal(Hex("189ba4afb8c501709d098a01020807"), frames[0].Payload);                   // 7
            Assert.Equal(Hex("089ba4afb8c5011003"), frames[1].Payload);                               // 8
            Assert.Equal(Hex("189ba4afb8c5017078a201090801109ba4afb8c501"), frames[4].Payload);       // 11
        }

        /// <summary>
        /// Estela cast at 427, from the engine to the wire (frames 10-15): the sub-cast 31021
        /// announced, the oldest portal out, the new one under the Selatrop on and at once off,
        /// his jump, and the portal on again behind him.
        /// </summary>
        [Fact]
        public async Task Estela_is_its_capture()
        {
            var selatrop = Character(Selatrop, 371);
            var fight = Board(selatrop, Monster(-1, 100));
            selatrop.Buffs.PonerEstado(3737);
            Laid(fight, 10, 300); Laid(fight, 11, 346); Laid(fight, 12, 386); Laid(fight, 13, 288);
            for (int i = 0; i < 13; i++) fight.SiguienteMarca();
            await using var wire = await Wire.Open(Selatrop);

            await FightHandler.AplicarEfectosAsync(null, fight, selatrop, 14591, 3, null, EffectEngine.AlLanzar, 427);

            Assert.Equal(427, selatrop.CellId);
            var frames = await wire.Drain();
            Assert.Equal(new[] { "jwe", "jwe", "jwe", "jwe", "jwe", "jwe" }, frames.Select(f => f.Op));
            Assert.Equal(Hex("189ba4afb8c5013a1d109ba4afb8c5012207209ba4afb8c50130f3023a0810adf2011888810570ac02"), frames[0].Payload); // 10
            StealTests.SameFields(Hex("189ba4afb8c50170b602b20102080a"), frames[1].Payload);                                          // 11
            Assert.Equal(Hex("189ba4afb8c5017091038202260a240a0610ff0118f30210b2da021802200e2803300148adf20150f3025801609ba4afb8c501"), frames[2].Payload); // 12
            Assert.Equal(Hex("189ba4afb8c501709d098a0102080e"), frames[3].Payload);                                                  // 13
            Assert.Equal(Hex("189ba4afb8c50170049a020a08ab03109ba4afb8c501"), frames[4].Payload);                                    // 14
            Assert.Equal(Hex("189ba4afb8c501709d098a0104080e1001"), frames[5].Payload);                                              // 15
        }

        /// <summary>
        /// Resonancia from the engine to the wire, "hechizos trascendencia y resonancia": cast on -1
        /// its state, byte for byte (frame 194); the first blow on -1 sets its hook off -- 31020
        /// announced on 425 (219), the state gone (220), its 406 with the f5 of a shown row (223),
        /// and the portal and the Teleportal (224-229). The real server also sends the hook itself
        /// as two hidden rows, "D" and "XD" (195-196), and takes them off with the state (221-222):
        /// this server keeps its hooks off the panel, and that is the difference left.
        /// </summary>
        [Fact]
        public async Task Resonancia_is_its_capture()
        {
            var selatrop = Character(Selatrop, 411);
            var enemy = Monster(-1, 425);
            var fight = Board(selatrop, enemy);
            typeof(FightInstance).GetProperty(nameof(FightInstance.RoundNumber))!.SetValue(fight, 5);
            selatrop.Buffs.PonerEstado(3737);
            Laid(fight, 1, 438); Laid(fight, 2, 526);
            fight.SiguienteMarca(); fight.SiguienteMarca();
            for (int i = 0; i < 16; i++) fight.SiguienteEmbrujo();
            await using var wire = await Wire.Open(Selatrop);

            await FightHandler.AplicarEfectosAsync(null, fight, selatrop, 14611, 2, enemy, EffectEngine.AlLanzar, 425);
            var cast = await wire.Drain();
            Assert.Equal(new[] { "jxm" }, cast.Select(f => f.Op));
            Assert.Equal(Hex("0a490a3d10ffffffffffffffffff0118112002320210063a0149408cda1850eb30621610ffffffffffffffffff0118ffffffffffffffffff017093727802800101109ba4afb8c50118b607"), cast[0].Payload);

            var blow = new SpellEffect { EffectId = 97, EffectUid = 900004, DiceNum = 121, DiceSide = 121, Element = 1, TargetMask = "a,A" };
            await FightHandler.HurtAsync(null, fight, selatrop, 14583, 3, enemy, 425, tirada: new[] { blow });

            Assert.Equal(526, enemy.CellId);
            var frames = (await wire.Drain()).Where(f => f.Op is "jwe" or "jya").ToList();
            Assert.Equal(new[] { "jwe", "jwe", "jya", "jwe", "jwe", "jwe", "jwe", "jwe", "jwe", "jwe" },
                         frames.Select(f => f.Op));
            Assert.Equal(Hex("189ba4afb8c5013a2110ffffffffffffffffff012207209ba4afb8c50130a9033a0810acf2011886810570ac02"), frames[1].Payload); // 219
            Assert.Equal(Hex("08ffffffffffffffffff011011"), frames[2].Payload);                                                           // 220
            StealTests.SameFields(Hex("189ba4afb8c5017096038a021010937220ffffffffffffffffff012801"), frames[3].Payload);                  // 223
            Assert.Equal(Hex("189ba4afb8c5017091038202240a220a0610ff0118a90310b2da02180220032803300148acf20150a903609ba4afb8c501"), frames[4].Payload); // 224
            Assert.Equal(Hex("189ba4afb8c501709d098a01020803"), frames[5].Payload);                                                       // 225
            Assert.Equal(Hex("189ba4afb8c501709d098a01020802"), frames[6].Payload);                                                       // 226
            Assert.Equal(Hex("18ffffffffffffffffff0170049a020e088e0410ffffffffffffffffff01"), frames[7].Payload);                         // 227
            Assert.Equal(Hex("189ba4afb8c501709d098a01020801"), frames[8].Payload);                                                       // 228
            Assert.Equal(Hex("189ba4afb8c5014a10088e0410ffffffffffffffffff01200270b302"), frames[9].Payload);                             // 229
        }

        // ─── The engine ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The engine lays, switches off and sends through; the fight does the rest. Portal's 1181
        /// goes to the aimed cell, Interrupción's 1183 to the whole map, Exilio's 1182 to the one on
        /// the cell.
        /// </summary>
        [Fact]
        public void The_engine_names_the_portal_effects()
        {
            var selatrop = Character(Selatrop, 302);
            var enemy = Monster(-1, 425);
            var fight = Board(selatrop, enemy);
            selatrop.Buffs.PonerEstado(3737);

            var laid = EffectEngine.ResolveEffects(fight, selatrop, 14574, 3, null, EffectEngine.AlLanzar, 1,
                SpellEffects.De(14574, 3), aimedCell: 300);
            Assert.Equal(300, Assert.Single(laid, o => o.PortalAt >= 0).PortalAt);

            var off = EffectEngine.ResolveEffects(fight, selatrop, 14607, 2, null, EffectEngine.AlLanzar, 1,
                SpellEffects.De(14607, 2), aimedCell: 300);
            Assert.Contains(400, Assert.Single(off).PortalsOffAt);

            var exilio = EffectEngine.ResolveEffects(fight, selatrop, 14609, 1, enemy, EffectEngine.AlLanzar, 1,
                SpellEffects.De(14609, 1), aimedCell: 425);
            Assert.Equal(425, exilio.Single(o => o.PortalAt >= 0).PortalAt);
            Assert.Same(enemy, exilio.Single(o => o.Teleportal).Sobre);
        }

        // ─── Frames off a test socket ────────────────────────────────────────────────────────

        /// <summary>A session registered for one character, and what the fight sends it.</summary>
        internal sealed class Wire : IAsyncDisposable
        {
            private TcpListener _listener;
            private TcpClient _client;
            private TcpClient _server;
            private GameSession _session;
            private System.IO.Stream _fromServer;

            /// <summary>The session, for pushing it as the one being served.</summary>
            public GameSession Session => _session;

            public static async Task<Wire> Open(long characterId)
            {
                var wire = new Wire { _listener = new TcpListener(IPAddress.Loopback, 0) };
                wire._listener.Start();
                wire._client = new TcpClient();
                await wire._client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)wire._listener.LocalEndpoint).Port);
                wire._server = await wire._listener.AcceptTcpClientAsync();
                wire._fromServer = wire._client.GetStream();
                wire._session = new GameSession(wire._server.GetStream());
                wire._session.BindAccount(8_900_000_000 + (characterId & 0xFFFF), 1);
                wire._session.State.CharacterId = characterId;
                // A map of its own, no world's: on map 0 it heard every fight the tests running
                // beside it held there (GuildWindowTests caught their jxm).
                wire._session.State.MapId = 9_000_000_000 + characterId;
                wire._session.EnterWorld();
                Assert.True(SessionRegistry.Register(wire._session));
                return wire;
            }

            /// <summary>Every frame that has come, until none comes for a moment.</summary>
            public async Task<List<(string Op, byte[] Payload)>> Drain()
            {
                var frames = new List<(string, byte[])>();
                while (true)
                {
                    // A read left waiting by the last drain is the one that gets the next frame.
                    var read = _pending ?? Jondo.Protocol.NetworkMessage.ReadFrameAsync(_fromServer);
                    _pending = null;
                    var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromMilliseconds(400)));
                    if (first != read)
                    {
                        _pending = read;
                        return frames;
                    }
                    byte[] frame = await read;
                    string op = OpcodeOf(frame);
                    frames.Add((op, ConnectionProtocol.ReadPayload(frame, op)));
                }
            }

            private Task<byte[]> _pending;

            /// <summary>The opcode behind "type.ankama.com/" in a frame's envelope.</summary>
            private static string OpcodeOf(byte[] frame)
            {
                byte[] prefix = System.Text.Encoding.ASCII.GetBytes(Jondo.Unity.Protocol.Op.Prefix);
                for (int i = 0; i + prefix.Length <= frame.Length; i++)
                {
                    if (!frame.AsSpan(i, prefix.Length).SequenceEqual(prefix)) continue;
                    int j = i + prefix.Length;
                    var op = new System.Text.StringBuilder();
                    while (j < frame.Length && frame[j] >= (byte)'a' && frame[j] <= (byte)'z') op.Append((char)frame[j++]);
                    return op.ToString();
                }
                return "";
            }

            public async ValueTask DisposeAsync()
            {
                if (_session != null) SessionRegistry.Unregister(_session);
                _client?.Dispose();
                _server?.Dispose();
                _listener?.Stop();
                await Task.CompletedTask;
            }
        }
    }
}
