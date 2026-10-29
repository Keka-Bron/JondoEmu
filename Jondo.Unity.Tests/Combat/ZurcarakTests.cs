using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Zurcarák as it was tried in the client: its cards, and what its gear and the kanojedo
    /// did around them.
    /// </summary>
    [Collection("MapManager")]
    public class ZurcarakTests
    {
        private const long Zurcarak = 9_970_301;
        private const int Blakjak = 12870;
        private const int ReinaDeCorazones = 5450;
        private const int CurasRecibidas = 1159;

        private static Fighter Character(int cell) => new()
        {
            Id = Zurcarak, Name = "Z", CellId = cell, Level = 200, MaxHP = 3000, CurrentHP = 3000,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 6,
        };

        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, Name = "M" + id, IsMonster = true, CellId = cell, Level = 50, MaxHP = 900, CurrentHP = 900,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, MonsterId = 494,
        };

        private static FightInstance Board(Fighter zurcarak, params Fighter[] monsters)
        {
            // A board of its own, with no ground data.
            var fight = new FightInstance(4106, 990_004_106);
            int cell = zurcarak.CellId;
            fight.AddPlayer(zurcarak);
            zurcarak.CellId = cell;
            foreach (var monster in monsters)
            {
                int where = monster.CellId;
                fight.AddMonster(monster);
                monster.CellId = where;
            }
            return fight;
        }

        /// <summary>
        /// Blakjak with a heart in hand leaves "curas recibidas" on its target, one row per heart,
        /// waiting on a heal (H) and gone two rounds on -- the jxm 1159 of its capture, cast in
        /// round 5 and dropping in 7. Before, the row was never put, and heals went through whole.
        /// </summary>
        [Fact]
        public void Blakjak_with_a_heart_in_hand_cuts_the_target_s_heals()
        {
            var zurcarak = Character(300);
            var enemy = Monster(-1, 301);
            var fight = Board(zurcarak, enemy);
            int n = 0;
            zurcarak.Buffs.Poner(new Buff { EffectId = 950, Estado = ReinaDeCorazones, HechizoOrigen = 29696, NivelOrigen = 1, CaducaEnRonda = -1 }, () => ++n);
            zurcarak.Buffs.PonerEstado(ReinaDeCorazones);

            EffectEngine.Resolver(fight, zurcarak, Blakjak, 3, enemy, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: enemy.CellId);

            var row = Assert.Single(enemy.Buffs.Puestos, b => b.EffectId == CurasRecibidas);
            Assert.Equal(91, row.Cuanto);
            Assert.Equal(fight.RoundNumber + 2, row.CaducaEnRonda);
            Assert.Equal(91, enemy.Buffs.Multiplicador(CurasRecibidas, fight.RoundNumber));
        }

        /// <summary>Without a heart in hand there is nothing to cut with.</summary>
        [Fact]
        public void Blakjak_with_no_heart_cuts_nothing()
        {
            var zurcarak = Character(300);
            var enemy = Monster(-1, 301);
            var fight = Board(zurcarak, enemy);

            EffectEngine.Resolver(fight, zurcarak, Blakjak, 3, enemy, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: enemy.CellId);

            Assert.DoesNotContain(enemy.Buffs.Puestos, b => b.EffectId == CurasRecibidas);
        }

        /// <summary>
        /// The gear's Guarda Campestre is an attitude: walking puts its "used MP this turn" state
        /// (5165), which keeps its turn-end heal away. Only the hooks fired on walking, and the heal
        /// of one who had not moved came to whoever had walked four cells.
        /// </summary>
        [Fact]
        public async Task Walking_sets_off_the_attitudes()
        {
            var zurcarak = Character(300);
            var fight = Board(zurcarak, Monster(-1, 100));
            zurcarak.Buffs.PonerActitud(28516);

            await FightHandler.StepsWalkedAsync(null, fight, zurcarak, 2);

            Assert.True(zurcarak.Buffs.TieneEstado(5165));
        }

        /// <summary>
        /// A weapon's hit besides its damage goes through the engine with no spell: the Kaiser's
        /// "-1 a -2 PA" and a push, handed as rows, land on the enemy it hits. With no spell the
        /// engine's road was closed, and 116 weapons did nothing but their damage.
        /// </summary>
        [Fact]
        public async Task A_weapon_s_side_lines_land_with_no_spell()
        {
            var wielder = Character(300);
            var enemy = Monster(-1, 301);
            var fight = Board(wielder, enemy);
            var rows = new List<SpellEffect>
            {
                new() { EffectId = 5, EffectUid = -101, DiceNum = 2, TargetMask = "g,A" },
            };

            await FightHandler.AplicarEfectosAsync(null, fight, wielder, 0, 0, enemy, EffectEngine.AlLanzar, 301, false, rows);

            Assert.NotEqual(301, enemy.CellId);
        }

        /// <summary>
        /// Cara o Cruz's coin is "en el lanzador": cast on himself the Zurcarák gets Cara or Cruz,
        /// its point, and the AP it gives back. Cast on an enemy there is no coin -- its "c" names
        /// the caster only when he stands in the zone --, which is what the test in the client saw.
        /// </summary>
        [Fact]
        public void Cara_o_Cruz_tosses_its_coin_only_on_the_caster()
        {
            var zurcarak = Character(300);
            var enemy = Monster(-1, 301);
            var fight = Board(zurcarak, enemy);

            EffectEngine.Resolver(fight, zurcarak, 12841, 3, enemy, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: enemy.CellId);
            Assert.False(zurcarak.Buffs.TieneEstado(5597) || zurcarak.Buffs.TieneEstado(5598));

            EffectEngine.Resolver(fight, zurcarak, 12841, 3, zurcarak, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: zurcarak.CellId);
            Assert.True(zurcarak.Buffs.TieneEstado(5597) ^ zurcarak.Buffs.TieneEstado(5598));
        }

        /// <summary>The kanojedo's puchs are dummies: the Ingball is one, and a monster of the world is not.</summary>
        [Fact]
        public void The_kanojedo_s_puchs_are_known()
        {
            if (Kanojedo.Puchs.Count == 0) return;
            Assert.True(Kanojedo.IsPuch(494));
            Assert.False(Kanojedo.IsPuch(31));
        }
    }
}
