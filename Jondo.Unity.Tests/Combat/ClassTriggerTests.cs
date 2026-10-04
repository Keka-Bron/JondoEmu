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
    /// The triggers the last class spells wait on, each fired where its sheet says: K when the
    /// bearer kills (the Zurcarák's Emperatriz), CS when he puts a shield (El Crupier), PT when
    /// he goes through a portal (Coalición), CPT when somebody goes through one of his (Ayuda
    /// Mutua), and PST when he casts through one (the Selatrop's passive).
    /// </summary>
    public class ClassTriggerTests
    {
        private static Fighter Character(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Monster(long id, int cell, int life = 900) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = life, CurrentHP = life, Level = 50,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 494,
        };

        private static int Beside(int cell, int dx, int dy)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            return MapGeometry.PointToCell(x + dx, y + dy);
        }

        /// <summary>A card of the tarot hooked on a fighter, in the Zurcarák's name, the way the card lands.</summary>
        private static void Hook(Fighter bearer, int spell, int grade, long caster)
            => bearer.Buffs.ActiveSpells.Add(new Buffs.ActiveSpell { Hechizo = spell, Grado = grade, Lanzador = caster, CaducaEnRonda = -1 });

        /// <summary>
        /// III. La Emperatriz: "aumenta los PM de una entidad si esta acaba con otra entidad". Its
        /// grade 1 waits on K, and the kill gives the killer the +2 MP of grade 2.
        /// </summary>
        [Fact]
        public async Task A_kill_sets_off_K_on_the_killer()
        {
            var fight = new FightInstance(1, 1);
            var zurcarak = Character(1, 0, 300);
            var killer = Character(2, 0, Beside(300, 3, 0));
            var victim = Monster(-1, Beside(300, 4, 0), life: 50);
            fight.AddPlayer(zurcarak); fight.AddPlayer(killer); fight.AddOpponent(victim);
            Hook(killer, 30005, 1, zurcarak.Id);

            var blow = new SpellEffect { EffectId = 97, EffectUid = 900002, DiceNum = 100, DiceSide = 100, Element = 1, TargetMask = "A" };
            await FightHandler.HurtAsync(null, fight, killer, 1, 1, victim, victim.CellId, tirada: new[] { blow });

            Assert.False(victim.IsAlive);
            Assert.Contains(killer.Buffs.Puestos, b => b.HechizoOrigen == 30005 && b.Caracteristica == 23 && b.Cuanto == 2);
        }

        /// <summary>
        /// XI. El Crupier: "aumenta los PM de una entidad si esta aplica un escudo". Its grade 1
        /// waits on CS, and a shield put gives the one who put it the +1 MP of grade 2.
        /// </summary>
        [Fact]
        public async Task A_shield_sets_off_CS_on_the_one_who_puts_it()
        {
            var fight = new FightInstance(1, 1);
            var zurcarak = Character(1, 0, 300);
            var feca = Character(2, 0, Beside(300, 3, 0));
            fight.AddPlayer(zurcarak); fight.AddPlayer(feca);
            Hook(feca, 30013, 1, zurcarak.Id);

            var shield = new SpellEffect { EffectId = 1020, EffectUid = 900003, DiceNum = 100, Duration = 1, TargetMask = "a" };
            await FightHandler.AplicarEfectosAsync(null, fight, feca, 13889, 1, feca, EffectEngine.AlLanzar,
                                                   feca.CellId, tirada: new[] { shield });

            Assert.True(feca.PuntosDeEscudo > 0);
            Assert.Contains(feca.Buffs.Puestos, b => b.HechizoOrigen == 30013 && b.Caracteristica == 23 && b.Cuanto == 1);
        }

        /// <summary>
        /// Coalición "cura al objetivo cuando atraviesa un portal": the ally it hooked goes through
        /// one and gets his 3% back (31018); Ayuda Mutua, hooked on the Selatrop, gives him his
        /// +1 AP (14598) when somebody goes through one of his portals.
        /// </summary>
        [Fact]
        public async Task Going_through_a_portal_sets_off_PT_and_CPT()
        {
            var fight = new FightInstance(1, 1);
            var selatrop = Character(7001, 0, 100);
            var ally = Character(2, 0, 317);
            fight.AddPlayer(selatrop); fight.AddPlayer(ally);
            foreach (var (id, cell) in new[] { (7, 400), (10, 303) })
                fight.Portales.Add(new Portal { Id = id, Owner = selatrop.Id, Cell = cell, DiceNum = 2, Active = true });
            ally.CurrentHP = 1000;
            Hook(ally, 14621, 1, selatrop.Id);
            Hook(selatrop, 14596, 2, selatrop.Id);

            await FightHandler.WalkPathAsync(fight, ally, new[] { 317, 303 });

            Assert.Equal(400, ally.CellId);
            Assert.Equal(1000 + 60, ally.CurrentHP);
            Assert.Contains(selatrop.Buffs.Puestos, b => b.HechizoOrigen == 14598 && b.Caracteristica == 1 && b.Cuanto == 1);
        }

        /// <summary>
        /// The passive's grade 1 waits on PST, and a cast through a portal gives the Selatrop the
        /// +2% of final damage and healing of grade 2 -- the jxm 1171 and 2971 at frames 25-29 of
        /// "pegar a traves de diferentes portales".
        /// </summary>
        [Fact]
        public async Task A_cast_through_a_portal_sets_off_PST()
        {
            var fight = new FightInstance(1, 1);
            var selatrop = Character(7001, 0, 359);
            fight.AddPlayer(selatrop);
            Hook(selatrop, 14631, 1, selatrop.Id);

            await FightHandler.DispararAsync(null, fight, selatrop, EffectEngine.AlProyectarPorUnPortal);

            Assert.Contains(selatrop.Buffs.Puestos, b => b.HechizoOrigen == 14631 && b.EffectId == 1171);
            Assert.Contains(selatrop.Buffs.Puestos, b => b.HechizoOrigen == 14631 && b.EffectId == 2971);
        }
    }
}
