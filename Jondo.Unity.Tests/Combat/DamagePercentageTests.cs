using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The eight damage percentages (DamagePercentages): read off the gear with the real server's
    /// signs, and multiplying the blow -- the striker's for a spell's or a weapon's blow, at melee or
    /// at range, and the target's the same two ways.
    /// </summary>
    public class DamagePercentageTests
    {
        private static Fighter Character(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 5000, CurrentHP = 5000, Level = 50,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 494,
        };

        private static int Beside(int cell, int dx, int dy)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            return MapGeometry.PointToCell(x + dx, y + dy);
        }

        /// <summary>A hundred of earth damage, from a caster with nothing to grow it.</summary>
        private static readonly SpellEffect Hundred = new()
        {
            EffectId = 97, EffectUid = 900101, DiceNum = 100, DiceSide = 100, Element = 1, TargetMask = "A",
        };

        /// <summary>What one spell blow of a hundred takes from a target this far from the caster.</summary>
        private static async Task<int> SpellBlow(int dx, System.Action<Fighter, Fighter> set)
        {
            var fight = new FightInstance(1, 1);
            var caster = Character(1, 0, 300);
            var target = Monster(-1, Beside(300, dx, 0));
            fight.AddPlayer(caster); fight.AddOpponent(target);
            set(caster, target);
            await FightHandler.HurtAsync(null, fight, caster, 1, 1, target, target.CellId, tirada: new[] { Hundred });
            return 5000 - target.CurrentHP;
        }

        /// <summary>
        /// The client's catalogue, with the resistances turned round: the shields' "+X %
        /// resistencia distancia" (2807) lowers 121, as the real server's sheet does (-10 for the
        /// shields of "equipando todas los escudos").
        /// </summary>
        [Fact]
        public void A_resistance_lowers_the_multiplier_it_is_taken_by()
        {
            if (DatabaseManager.EffectMeta(2812).Characteristic == 0) return;
            Assert.Equal((123, 1), DatabaseManager.EffectMeta(2812));   // +% daños a los hechizos
            Assert.Equal((120, 1), DatabaseManager.EffectMeta(2804));   // +% daños distancia
            Assert.Equal((121, -1), DatabaseManager.EffectMeta(2807));  // +% resistencia distancia
            Assert.Equal((121, 1), DatabaseManager.EffectMeta(2806));   // -% resistencia distancia
            Assert.Equal((124, -1), DatabaseManager.EffectMeta(2803));  // +% resistencia CaC
            Assert.Equal((141, -1), DatabaseManager.EffectMeta(2815));  // +% resistencia a hechizos
            Assert.Equal((142, -1), DatabaseManager.EffectMeta(2811));  // +% resistencia a armas
        }

        [Fact]
        public void A_blow_reads_its_kind_and_its_reach()
        {
            Assert.Equal((123, 125), DamagePercentages.Dealt(spell: true, melee: true));
            Assert.Equal((122, 120), DamagePercentages.Dealt(spell: false, melee: false));
            Assert.Equal((141, 121), DamagePercentages.Taken(spell: true, melee: false));
            Assert.Equal((142, 124), DamagePercentages.Taken(spell: false, melee: true));
        }

        /// <summary>"100 % daños a los hechizos" doubles a spell's blow.</summary>
        [Fact]
        public async Task Spell_damage_multiplies_a_spell_s_blow()
        {
            int bare = await SpellBlow(3, (_, _) => { });
            int doubled = await SpellBlow(3, (caster, _) => caster.Otras[DamagePercentages.Spells] = 100);
            Assert.Equal(100, bare);
            Assert.Equal(200, doubled);

            // A weapon's percentage does nothing to it.
            Assert.Equal(100, await SpellBlow(3, (caster, _) => caster.Otras[DamagePercentages.Weapons] = 100));
        }

        /// <summary>Melee damage counts side by side, ranged damage further off -- and not the other way.</summary>
        [Fact]
        public async Task Melee_and_ranged_damage_go_by_the_distance()
        {
            Assert.Equal(150, await SpellBlow(1, (caster, _) => caster.Otras[DamagePercentages.Melee] = 50));
            Assert.Equal(100, await SpellBlow(3, (caster, _) => caster.Otras[DamagePercentages.Melee] = 50));
            Assert.Equal(130, await SpellBlow(3, (caster, _) => caster.Otras[DamagePercentages.Ranged] = 30));
            Assert.Equal(100, await SpellBlow(1, (caster, _) => caster.Otras[DamagePercentages.Ranged] = 30));
        }

        /// <summary>
        /// The target's resistances in percent, as the gear leaves them -- 20 % against spells is 141
        /// at 80 -- and both ways at once with the striker's.
        /// </summary>
        [Fact]
        public async Task The_target_s_resistances_cut_the_blow()
        {
            Assert.Equal(80, await SpellBlow(3, (_, target) => target.Otras[DamagePercentages.SpellsTaken] = -20));
            Assert.Equal(90, await SpellBlow(3, (_, target) => target.Otras[DamagePercentages.RangedTaken] = -10));
            Assert.Equal(100, await SpellBlow(3, (_, target) => target.Otras[DamagePercentages.MeleeTaken] = -10));
            Assert.Equal(160, await SpellBlow(3, (caster, target) =>
            {
                caster.Otras[DamagePercentages.Spells] = 100;
                target.Otras[DamagePercentages.SpellsTaken] = -20;
            }));
        }
    }
}
