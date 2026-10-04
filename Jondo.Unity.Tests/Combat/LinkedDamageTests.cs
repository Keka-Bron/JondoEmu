using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// «Intercepta los daños» (765) and «Comparte los daños» (1061): the rows, measured -- a hidden
    /// row under "D" on each fighter the zone names, in the name of the one who cast it -- and
    /// what a blow does with them, inferred from the sheets: an interceptor takes the blow in the
    /// place of the one hit; linked fighters take equal shares of it.
    /// </summary>
    public class LinkedDamageTests
    {
        private static Fighter Character(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 900, CurrentHP = 900, Level = 50,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 494,
        };

        private static int Beside(int cell, int dx, int dy)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            return MapGeometry.PointToCell(x + dx, y + dy);
        }

        /// <summary>A hundred of earth, the die fixed, on whoever stands on the aimed cell.</summary>
        private static readonly SpellEffect Hundred = new()
        {
            EffectId = 97, EffectUid = 900001, DiceNum = 100, DiceSide = 100, Element = 1, TargetMask = "a,A",
        };

        private static Buff Row(int effect, long caster, int spell, string trigger) => new()
        {
            EffectId = effect, Quien = caster, HechizoOrigen = spell, NivelOrigen = 1,
            Disparador = trigger, CaducaEnRonda = 10,
        };

        /// <summary>
        /// Sacrificio cast by the Sacrógrito next to an ally puts on the ally, in the Sacrógrito's
        /// name, a hidden 765 under "D" -- "jxm 765 'D' f15=7" at frame 62 of "Sacrifice" -- and
        /// none on himself: its mask is "g", the other allies.
        /// </summary>
        [Fact]
        public void Sacrificio_puts_its_row_on_the_allies_around()
        {
            var fight = new FightInstance(1, 1);
            var sacro = Character(56098422883, 0, 300);
            var ally = Character(2, 0, Beside(300, 1, 0));
            fight.AddPlayer(sacro); fight.AddPlayer(ally);

            var outcomes = EffectEngine.ResolveEffects(fight, sacro, 12739, 3, ally, EffectEngine.AlLanzar, 1,
                                                       SpellEffects.De(12739, 3), aimedCell: ally.CellId);

            var row = Assert.Single(outcomes, o => o.Efecto.EffectId == EffectEngine.InterceptaLosDanos);
            Assert.Same(ally, row.Sobre);
            Assert.True(row.FilaEnganchada);
            Assert.Equal("D", row.Buff.Disparador);
            Assert.Equal(sacro.Id, row.Buff.Quien);
            Assert.DoesNotContain(sacro.Buffs.Puestos, b => b.EffectId == EffectEngine.InterceptaLosDanos);
        }

        /// <summary>
        /// A blow on the ally goes to the Sacrógrito: he loses the hundred, the ally nothing. His
        /// own blow on the ally, or one under a kind the row does not name, is not intercepted.
        /// </summary>
        [Fact]
        public async Task The_interceptor_takes_the_blow()
        {
            var fight = new FightInstance(1, 1);
            var sacro = Character(1, 0, 300);
            var ally = Character(2, 0, Beside(300, 1, 0));
            var enemy = Monster(-1, Beside(300, 5, 0));
            fight.AddPlayer(sacro); fight.AddPlayer(ally); fight.AddOpponent(enemy);
            int n = 0;
            ally.Buffs.Poner(Row(EffectEngine.InterceptaLosDanos, sacro.Id, 12739, "D"), () => ++n);

            Assert.Same(sacro, FightHandler.Interceptor(fight, enemy, ally, false));
            Assert.Null(FightHandler.Interceptor(fight, sacro, ally, false));

            await FightHandler.HurtAsync(null, fight, enemy, 1, 1, ally, ally.CellId, tirada: new[] { Hundred });

            Assert.Equal(2000, ally.CurrentHP);
            Assert.Equal(1900, sacro.CurrentHP);
        }

        /// <summary>Albarrama's turret intercepts the blows from next door (DM) only.</summary>
        [Fact]
        public void Albarrama_intercepts_the_melee_blows()
        {
            var fight = new FightInstance(1, 1);
            var ally = Character(2, 0, 300);
            var turret = new Fighter { Id = -5, TeamId = 0, CellId = Beside(300, 0, 1), MaxHP = 500, CurrentHP = 500, IsMonster = true, MonsterId = 5831 };
            var near = Monster(-1, Beside(300, 1, 0));
            var far = Monster(-2, Beside(300, 4, 0));
            fight.AddPlayer(ally); fight.AddOpponent(near); fight.AddOpponent(far);
            fight.Invocar(turret, ally);
            turret.CellId = Beside(300, 0, 1);
            int n = 0;
            ally.Buffs.Poner(Row(EffectEngine.InterceptaLosDanos, turret.Id, 13850, "DM"), () => ++n);

            Assert.Same(turret, FightHandler.Interceptor(fight, near, ally, false));
            Assert.Null(FightHandler.Interceptor(fight, far, ally, false));
        }

        /// <summary>
        /// Don Natural links the allies around the tree: a hundred on one of three linked comes out
        /// as 33 on each of the other two and 34 on him. Rows of another spell do not link.
        /// </summary>
        [Fact]
        public async Task A_shared_blow_is_cut_in_equal_shares()
        {
            var fight = new FightInstance(1, 1);
            var sadida = Character(54063726691, 0, 300);
            var a = Character(2, 0, Beside(300, 1, 0));
            var b = Character(3, 0, Beside(300, -1, 0));
            var other = Character(4, 0, Beside(300, 0, 1));
            var enemy = Monster(-1, Beside(300, 6, 0));
            fight.AddPlayer(sadida); fight.AddPlayer(a); fight.AddPlayer(b); fight.AddPlayer(other); fight.AddOpponent(enemy);
            int n = 0;
            foreach (var linked in new[] { sadida, a, b })
                linked.Buffs.Poner(Row(EffectEngine.ComparteLosDanos, sadida.Id, 13544, "D"), () => ++n);
            other.Buffs.Poner(Row(EffectEngine.ComparteLosDanos, sadida.Id, 31142, "D"), () => ++n);

            Assert.Equal(new[] { sadida, b }.Select(f => f.Id).OrderBy(i => i),
                         FightHandler.Enlazados(fight, a, new[] { "D" }).Select(f => f.Id).OrderBy(i => i));

            await FightHandler.HurtAsync(null, fight, enemy, 1, 1, a, a.CellId, tirada: new[] { Hundred });

            Assert.Equal(2000 - 34, a.CurrentHP);
            Assert.Equal(2000 - 33, b.CurrentHP);
            Assert.Equal(2000 - 33, sadida.CurrentHP);
            Assert.Equal(2000, other.CurrentHP);
        }

        /// <summary>
        /// Don Natural's own rows, off the client's data: its 13544, aimed at a tree, puts 1061
        /// under "D" on the Sadida and the allied summons in the ring of two around it -- "c,h,d,m,i"
        /// -- and nothing on the enemy there. In the DonNaturel capture (frame 334) the Sadida was
        /// the only one of them in reach, and got the only row.
        /// </summary>
        [Fact]
        public void Don_natural_links_the_ones_around_the_tree()
        {
            var fight = new FightInstance(1, 1);
            int tree = 300;
            var sadida = Character(54063726691, 0, Beside(tree, 2, 0));
            var ally = Character(2, 0, 100);
            var enemy = Monster(-1, Beside(tree, -1, 0));
            var bush = new Fighter { Id = -7, TeamId = 0, CellId = Beside(tree, 0, 1), MaxHP = 300, CurrentHP = 300, IsMonster = true, MonsterId = 5894 };
            fight.AddPlayer(sadida); fight.AddPlayer(ally); fight.AddOpponent(enemy);
            fight.Invocar(bush, sadida);
            bush.CellId = Beside(tree, 0, 1);

            var outcomes = EffectEngine.ResolveEffects(fight, sadida, 13544, 2, null, EffectEngine.AlLanzar, 1,
                                                       SpellEffects.De(13544, 2), aimedCell: tree);

            var rows = outcomes.Where(o => o.Efecto.EffectId == EffectEngine.ComparteLosDanos).ToList();
            Assert.Equal(new long[] { sadida.Id, bush.Id }.OrderBy(i => i), rows.Select(r => r.Sobre.Id).OrderBy(i => i));
            Assert.All(rows, r => Assert.Equal("D", r.Buff.Disparador));
        }
    }
}
