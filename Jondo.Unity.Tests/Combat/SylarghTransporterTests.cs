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
    /// Sylargh's Transporter as the dungeon plays it, run through the real engine on the real data
    /// (Dofus pour les noobs, «Transporteur de Sylargh»): the Güinórdido's Rotatinvernal, and the
    /// zombies -- where they come back, how many, and what their death leaves.
    /// </summary>
    [Collection("MapManager")]
    public class SylarghTransporterTests
    {
        private const int Sylargh = 3409, Brikolosote = 3405, Guinordido = 3406;
        private const int Rotaplumas = 3597, Necromignon = 3607, Zombidule = 3608;
        private const int Zombi = 74, PlusDamage = 112;

        private static Fighter Monster(long id, int template, int cell, int life = 2000) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = life, CurrentHP = life, Level = 200,
            IsMonster = true, MonsterId = template, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
        };

        private static Fighter Person(long id, int cell) => new()
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 50000, CurrentHP = 50000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static int Beside(int cell, int dx)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            return MapGeometry.PointToCell(x + dx, y);
        }

        private static SpellEffect Blow(int amount) => new()
        {
            EffectId = 100, EffectUid = 900100, DiceNum = amount, DiceSide = amount, Element = 0, TargetMask = "a,A",
        };

        /// <summary>
        /// «La réinvocation s'effectue toujours à exactement 6PO de son invocateur ... dans le sens
        /// des aiguilles d'une montre», and «les monstres éliminés sont réinvoqués à leur droite».
        /// </summary>
        [Fact]
        public void A_ring_is_taken_from_its_nearest_band_and_the_right_clockwise()
        {
            const int centre = 300;     // row 21, column 6
            var ring = Zone.Casillas(Zone.Circulo, 10, centre, centre, 6);
            Assert.DoesNotContain(ring, c => MapGeometry.Distance(centre, c) < 6);

            int first = FightHandler.PrimeraDelAnillo(ring, centre, _ => true);
            Assert.Equal(centre + 3, first);                      // the same row, to the right
            Assert.Equal(6, MapGeometry.Distance(centre, first));

            int next = FightHandler.PrimeraDelAnillo(ring, centre, c => c != first);
            Assert.Equal(6, MapGeometry.Distance(centre, next));
            Assert.True(next / MapGeometry.MapWidth > centre / MapGeometry.MapWidth);  // clockwise: below

            Assert.Equal(-1, FightHandler.PrimeraDelAnillo(ring, centre, _ => false));
        }

        /// <summary>
        /// Nécromignon brings the last dead ally back 6 cells from its caster, and one alone however
        /// many stand in its ring; the one brought back keeps none of the hooks he had.
        /// </summary>
        [Fact]
        public async Task Necromignon_brings_one_back_six_cells_away()
        {
            if (SpellEffects.De(Necromignon, 1).Count == 0) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var boss = Monster(-1, Sylargh, 300, 19000);
            var first = Monster(-2, Brikolosote, 400);
            var second = Monster(-3, Guinordido, 410);
            var me = Person(10, Beside(300, 7));                   // in the ring
            var other = Person(11, Beside(300, -7));               // in it too
            fight.AddPlayer(me); fight.AddPlayer(other);
            fight.AddMonster(boss); fight.AddMonster(first); fight.AddMonster(second);
            foreach (var dead in new[] { first, second })
            {
                dead.Buffs.ActiveSpells.Add(new Buffs.ActiveSpell { Hechizo = Necromignon, Grado = 1, Lanzador = boss.Id, CaducaEnRonda = -1 });
                dead.CurrentHP = 0;
                fight.Muertos.Add(dead);
            }

            await FightHandler.AplicarEfectosAsync(null, fight, boss, Necromignon, 1, boss, EffectEngine.AlLanzar, boss.CellId);

            Assert.True(second.IsAlive);                            // the last to fall
            Assert.False(first.IsAlive);                            // and him alone
            Assert.Equal(6, MapGeometry.Distance(boss.CellId, second.CellId));
            Assert.Equal(boss.Id, second.Invocador);
            Assert.True(second.Buffs.TieneEstado(Zombi));
            Assert.DoesNotContain(second.Buffs.ActiveSpells, h => h.Hechizo == Necromignon);
        }

        /// <summary>
        /// Brought back by Sylargh's behaviour, the zombie keeps what the revival hooks on him for
        /// good: the 3691 that re-arms his own Nécromignon at every turn start of his. Written as a
        /// plain cast's, it lasted a round, and a zombie revived nobody after his first turn.
        /// </summary>
        [Fact]
        public async Task A_zombie_brought_back_by_the_behaviour_keeps_its_hooks_for_good()
        {
            if (SpellEffects.De(Necromignon, 1).Count == 0) return;
            const int ReArm = 3691;
            var fight = new FightInstance(1, 1);
            var boss = Monster(-1, Sylargh, 300, 19000);
            var dead = Monster(-2, Brikolosote, 400);
            var me = Person(10, Beside(300, 7));
            fight.AddPlayer(me); fight.AddMonster(boss); fight.AddMonster(dead);
            dead.CurrentHP = 0;
            fight.Muertos.Add(dead);

            await FightHandler.AplicarEfectosAsync(null, fight, boss, Necromignon, 1, boss, EffectEngine.AlLanzar,
                                                   boss.CellId, conducta: true);

            Assert.True(dead.IsAlive);
            Assert.Contains(dead.Buffs.ActiveSpells, h => h.Hechizo == ReArm && h.CaducaEnRonda < 0);
        }

        /// <summary>
        /// A zombie a zombie brought back plays behind it, as every summon plays behind its
        /// summoner. Taken for loose, it went to the end of the side and reached the player's
        /// turn without the turn start that hooks its Zombidule.
        /// </summary>
        [Fact]
        public void A_summon_s_summon_plays_behind_it()
        {
            var fight = new FightInstance(1, 1);
            var me = Person(10, 100);
            var boss = Monster(-1, Sylargh, 300, 19000);
            var other = Monster(-2, Guinordido, 310);
            var zombie = Monster(-3, Brikolosote, 320);
            var hisZombie = Monster(-4, Brikolosote, 330);
            boss.Initiative = 900; other.Initiative = 500; zombie.Initiative = 800; hisZombie.Initiative = 700;
            zombie.Invocador = boss.Id;
            hisZombie.Invocador = zombie.Id;
            fight.AddPlayer(me);
            foreach (var m in new[] { boss, other, zombie, hisZombie }) fight.AddMonster(m);

            var order = fight.BuildAlternatingTurnOrder().Where(f => f.TeamId == 1).Select(f => f.Id).ToList();

            Assert.Equal(new long[] { boss.Id, zombie.Id, hisZombie.Id, other.Id }, order);
        }

        /// <summary>
        /// A zombie's death: its Zombidule gives «100 dommages ... pour 2 tours (débuffable et
        /// cumulable 3 fois)» round it, kills the zombies near it, and those explode in turn --
        /// «des réactions en chaîne en plaçant le Sylargh proche de plusieurs monstres Zombi».
        /// </summary>
        [Fact]
        public async Task A_zombie_s_death_sets_the_next_one_off_and_its_boost_stays()
        {
            if (SpellEffects.De(Zombidule, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var me = Person(10, Beside(300, -1));
            var a = Monster(-2, Brikolosote, 300, 100);
            var b = Monster(-3, Guinordido, Beside(300, 2), 100);
            var boss = Monster(-1, Sylargh, 100, 19000);            // far off: no 952 on him
            fight.AddPlayer(me); fight.AddMonster(boss); fight.AddMonster(a); fight.AddMonster(b);
            foreach (var zombie in new[] { a, b })
            {
                zombie.Buffs.PonerEstado(Zombi);
                // Its own Nécromignon, as 3691 casts it at its turn start: nobody to bring back,
                // and the Zombidule hooked on its death.
                await FightHandler.AplicarEfectosAsync(null, fight, zombie, Necromignon, 1, zombie,
                                                       EffectEngine.AlLanzar, zombie.CellId);
            }

            await FightHandler.HurtAsync(null, fight, me, 1, 1, a, a.CellId, tirada: new[] { Blow(5000) });

            Assert.False(a.IsAlive);
            Assert.False(b.IsAlive);
            var boosts = me.Buffs.Puestos.Where(r => r.EffectId == PlusDamage && r.HechizoOrigen == Zombidule).ToList();
            Assert.Contains(boosts, r => r.Quien == a.Id);
            Assert.Contains(boosts, r => r.Quien == b.Id);
        }

        /// <summary>
        /// Rotatinvernal: hit from afar, the Güinórdido «renvoie 100% de ses dégâts à toutes les
        /// entités alignées». Its 1123 reads the blow; the hook fired before the blow was written
        /// down, and returned nothing.
        /// </summary>
        [Fact]
        public async Task Rotatinvernal_sends_the_ranged_blow_down_its_lines()
        {
            if (SpellEffects.De(Rotaplumas, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var guino = Monster(-1, Guinordido, 300, 20000);
            var ally = Monster(-2, Brikolosote, Beside(300, -3), 20000);
            var me = Person(10, Beside(300, 4));
            fight.AddPlayer(me); fight.AddMonster(guino); fight.AddMonster(ally);

            await FightHandler.AplicarEfectosAsync(null, fight, guino, Rotaplumas, 1, guino, EffectEngine.AlLanzar, guino.CellId);
            await FightHandler.HurtAsync(null, fight, me, 1, 1, guino, guino.CellId, tirada: new[] { Blow(400) });

            Assert.True(guino.CurrentHP < 20000);
            Assert.True(ally.CurrentHP < 20000, "the aligned ally took nothing");
        }
    }
}
