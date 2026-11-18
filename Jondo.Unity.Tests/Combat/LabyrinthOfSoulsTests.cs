using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// «Como alma en pena» (1528): the Laberinto de las almas, the soulless mummy against the age-old
    /// minogolem, through the real engine on the real data.
    /// </summary>
    /// <remarks>
    /// Dofus pour les noobs («Une âme en peine»): the minogolem splits into four, one at each corner;
    /// each turn they swap, and the one that plays raises a wall on the cell of the mummy's melee on
    /// the far side from it, «si cette case n'est pas libre, pas de mur». The mummy walks the maze to
    /// the green cell, whose trap kills the minogolem.
    /// </remarks>
    [Collection("MapManager")]
    public class LabyrinthOfSoulsTests
    {
        private const long Labyrinth = 136317440;
        private const int Golem = 3740, Salt = 3741, Gold = 3742, Blood = 3743, Murito = 3744;
        private const int Starting = 4762, Sustitucion = 4758, Pared = 4763, FirstDraw = 4759;
        private const int WayIn = 524, Corner = 14;

        private static Fighter Minogolem(int cell) => new()
        {
            Id = -1, TeamId = 1, CellId = cell, MaxHP = 1, CurrentHP = 1, Level = 1,
            IsMonster = true, MonsterId = Golem, MaxAP = 2, CurrentAP = 2,
        };

        private static Fighter Mummy(int cell) => new()
        {
            Id = 10, TeamId = 0, CellId = cell, MaxHP = 50, CurrentHP = 50, Level = 130,
            MaxAP = 10, CurrentAP = 10, MaxMP = 1, CurrentMP = 1,
        };

        private static Fighter Summoned(long id, int template, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 1, CurrentHP = 1, Level = 1,
            IsMonster = true, MonsterId = template, Invocador = -1,
        };

        /// <summary>The board flags one cell for each side: the maze's one way in, and the minogolem's corner.</summary>
        [Fact]
        public void The_labyrinth_places_the_mummy_at_its_way_in_and_the_minogolem_in_its_corner()
        {
            if (MapPlacements.Of(Labyrinth) is not { } own) return;   // no data
            Assert.Equal(new[] { WayIn }, own.Blue);
            Assert.Equal(new[] { Corner }, own.Red);
            Assert.NotNull(MapPlacements.Fitting(Labyrinth, 1, 1));
            Assert.Null(MapPlacements.Fitting(Labyrinth, 2, 1));
        }

        /// <summary>
        /// "793 4759 under TB, delay 1" goes off once, at the turn start of the round after the
        /// cast: 4788 takes the draws on from there. Every turn, the draws piled up.
        /// </summary>
        [Fact]
        public void The_first_draw_of_its_states_goes_off_once()
        {
            if (SpellEffects.De(Starting, 1).Count == 0) return;   // no data
            var fight = new FightInstance(1, Labyrinth, Labyrinth);
            var golem = Minogolem(Corner);
            fight.AddMonster(golem);
            fight.AddPlayer(Mummy(WayIn));
            int put = fight.RoundNumber;

            bool Draws(int round) => EffectEngine.Resolver(fight, golem, Starting, 1, golem, EffectEngine.AlEmpezarElTurno,
                                                           round, celdaApuntada: golem.CellId, rondaDelEnganche: put,
                                                           armar: false, soloAlObjetivo: true)
                                                 .Any(o => o.HechizoOrigen == FirstDraw);

            Assert.False(Draws(put));
            Assert.True(Draws(put + 1));
            Assert.False(Draws(put + 2));
            Assert.False(Draws(put + 5));
        }

        /// <summary>Pared aimed at the mummy: the Murito behind her, on the far side from the minogolem, and none when that cell is taken.</summary>
        [Fact]
        public void The_wall_rises_behind_the_mummy_or_not_at_all()
        {
            if (SpellEffects.De(Pared, 1).Count == 0) return;   // no data
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            var walkable = MapManager.GetFightWalkable(Labyrinth);
            if (walkable == null || walkable.Count == 0) return;   // no data

            // The blood one's corner, bottom right, with the mummy at the way in: behind her, on the
            // far side from it, is 510 -- the first cell of her way up the maze.
            var fight = new FightInstance(1, Labyrinth, Labyrinth);
            var golem = Minogolem(559);
            var mummy = Mummy(WayIn);
            fight.AddMonster(golem);
            fight.AddPlayer(mummy);
            Assert.Equal(new[] { WayIn, 510 }, EffectEngine.BehindInItsMelee(golem.CellId, mummy.CellId, 1));

            var cast = EffectEngine.Resolver(fight, golem, Pared, 1, mummy, EffectEngine.AlLanzar, fight.RoundNumber,
                                             celdaApuntada: mummy.CellId);
            var wall = Assert.Single(cast, o => o.Invoca == Murito);
            Assert.Equal(510, wall.CasillaDeLaInvocacion);
            Assert.Equal(1, MapGeometryDistance(mummy.CellId, wall.CasillaDeLaInvocacion));
            Assert.True(MapGeometryDistance(golem.CellId, wall.CasillaDeLaInvocacion) > MapGeometryDistance(golem.CellId, mummy.CellId));

            // With that cell taken, no wall: not next to her wherever there is room.
            fight.AddMonster(Summoned(-9, Murito, 510));
            var again = EffectEngine.Resolver(fight, golem, Pared, 1, mummy, EffectEngine.AlLanzar, fight.RoundNumber,
                                              celdaApuntada: mummy.CellId);
            Assert.DoesNotContain(again, o => o.Invoca == Murito);

            // Nor where the maze has no ground: from the minogolem's own corner the cell behind
            // her is outside it (539).
            var fromHisCorner = new FightInstance(2, Labyrinth, Labyrinth);
            var secular = Minogolem(Corner);
            var alone = Mummy(WayIn);
            fromHisCorner.AddMonster(secular);
            fromHisCorner.AddPlayer(alone);
            Assert.DoesNotContain(EffectEngine.Resolver(fromHisCorner, secular, Pared, 1, alone, EffectEngine.AlLanzar,
                                                        fromHisCorner.RoundNumber, celdaApuntada: alone.CellId),
                                  o => o.Invoca == Murito);
        }

        /// <summary>
        /// Sustitución is aimed at the one of the three its state names, and played before Pared;
        /// with none of its states on, it is not cast.
        /// </summary>
        [Fact]
        public void It_swaps_with_the_one_its_state_names_before_raising_the_wall()
        {
            if (SpellEffects.De(Sustitucion, 1).Count == 0) return;   // no data
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            var fight = new FightInstance(1, Labyrinth, Labyrinth);
            var golem = Minogolem(Corner);
            var mummy = Mummy(WayIn);
            fight.AddMonster(golem);
            fight.AddPlayer(mummy);
            var salt = Summoned(-2, Salt, 27);
            var gold = Summoned(-3, Gold, 532);
            var blood = Summoned(-4, Blood, 559);
            foreach (var one in new[] { salt, gold, blood }) fight.AddMonster(one);

            var spells = FightHandler.TacticsFor(golem, new[] { (Sustitucion, 1), (Pared, 1) });
            var swap = spells.Single(s => s.Id == Sustitucion);
            Assert.NotEmpty(swap.UtilityAllyMasks);
            Assert.True(swap.UtilityScripted);

            // No state on: it reaches none of them.
            Assert.DoesNotContain(new[] { salt, gold, blood }, f => swap.UtilityReaches(golem, f));

            // State 202 names the gold one (3742), state 200 the blood one (3743).
            golem.Buffs.PonerEstado(202);
            Assert.True(swap.UtilityReaches(golem, gold));
            Assert.False(swap.UtilityReaches(golem, salt));
            Assert.False(swap.UtilityReaches(golem, blood));
            golem.Buffs.QuitarEstado(202);
            golem.Buffs.PonerEstado(200);
            Assert.True(swap.UtilityReaches(golem, blood));
            Assert.False(swap.UtilityReaches(golem, gold));
        }

        /// <summary>
        /// Sustitución's "-1 round" (1075) takes off the state it swapped on, and leaves the next
        /// round's draw waiting: a state every round, as the draws are written.
        /// </summary>
        [Fact]
        public void The_swap_ends_its_state_but_not_the_one_drawn_for_next_round()
        {
            if (SpellEffects.De(Sustitucion, 1).Count == 0) return;   // no data
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            var fight = new FightInstance(1, Labyrinth, Labyrinth);
            var golem = Minogolem(Corner);
            var mummy = Mummy(WayIn);
            fight.AddMonster(golem);
            fight.AddPlayer(mummy);
            var salt = Summoned(-2, Salt, 27);
            fight.AddMonster(salt);
            int now = fight.RoundNumber;

            // Its rows are 1s in the catalogue, as a dispel would take: the 1075 shortens those alone.
            golem.Buffs.Poner(new Buff { EffectId = 950, Estado = 201, HechizoOrigen = FirstDraw, NivelOrigen = 1,
                                         Quien = golem.Id, EmpiezaEnRonda = now, CaducaEnRonda = now + 1,
                                         Dispellable = Buffs.DispellableByADispel },
                              fight.SiguienteEmbrujo);
            golem.Buffs.PonerEstado(201);
            golem.Buffs.Poner(new Buff { EffectId = 950, Estado = 202, HechizoOrigen = FirstDraw, NivelOrigen = 1,
                                         Quien = golem.Id, Pendiente = true, Disparador = EffectEngine.Esperando,
                                         EmpiezaEnRonda = now + 1, CaducaEnRonda = now + 2 },
                              fight.SiguienteEmbrujo);

            var cast = EffectEngine.Resolver(fight, golem, Sustitucion, 1, salt, EffectEngine.AlLanzar, now,
                                             celdaApuntada: salt.CellId);

            Assert.Contains(cast, o => o.RondasAcortadas == 1 && o.EmbrujosCaidos == 1);
            Assert.False(golem.Buffs.TieneEstado(201));
            Assert.Contains(golem.Buffs.Puestos, b => b.Pendiente && b.Estado == 202);
        }

        private static int MapGeometryDistance(int a, int b) => Jondo.Unity.World.Maps.MapGeometry.Distance(a, b);
    }
}
