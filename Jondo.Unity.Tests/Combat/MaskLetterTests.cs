using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The target-mask letters the class spells write and the engine now reads: the class of a
    /// character (B/b), a shield (PB/pb), a portal (R/r), the carried one (K), the one who set the
    /// spell off in the zone (o) and the summon whose coming did (u), the companions (D/d). Each
    /// is read off the sheet of a spell that writes it.
    /// </summary>
    public class MaskLetterTests
    {
        private static Fighter Character(long id, int team, int cell, int breed = 0) => new()
        {
            Id = id, TeamId = team, CellId = cell, Breed = breed, MaxHP = 2000, CurrentHP = 2000, Level = 200,
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

        private static System.Collections.Generic.List<Outcome> One(FightInstance fight, Fighter caster, Fighter target,
                                                                     SpellEffect row, int aimed)
            => EffectEngine.ResolveEffects(fight, caster, 1, 1, target, EffectEngine.AlLanzar, 1, new[] { row }, aimedCell: aimed);

        /// <summary>
        /// Disparos Lejanos' "117 on g,b9" in a circle of 2 gives range "a los aliados (excepto
        /// ocras)": the Feca next to the Ocra gets it, the other Ocra does not.
        /// </summary>
        [Fact]
        public void b_is_anybody_not_of_that_class()
        {
            var fight = new FightInstance(1, 1);
            var ocra = Character(1, 0, 300, breed: 9);
            var otherOcra = Character(2, 0, Beside(300, 1, 0), breed: 9);
            var feca = Character(3, 0, Beside(300, -1, 0), breed: 1);
            fight.AddPlayer(ocra); fight.AddPlayer(otherOcra); fight.AddPlayer(feca);

            EffectEngine.Resolver(fight, ocra, 32558, 1, ocra, EffectEngine.AlLanzar, 1, celdaApuntada: 300);

            Assert.Equal(2, feca.Buffs.De(19, 1));
            Assert.Equal(0, otherOcra.Buffs.De(19, 1));
            Assert.Equal(0, ocra.Buffs.De(19, 1));
        }

        /// <summary>B is the class itself: Traición's "A,B16" reaches a Selatrop and no one else.</summary>
        [Fact]
        public void B_is_a_character_of_that_class()
        {
            var fight = new FightInstance(1, 1);
            var me = Character(1, 0, 300);
            var selatrop = Character(2, 1, 301, breed: 16);
            var sram = Character(3, 1, 301, breed: 4);
            fight.AddPlayer(me); fight.AddOpponent(selatrop);

            var row = new SpellEffect { EffectId = 950, Value = 1, TargetMask = "A,B16", Triggers = "I" };
            Assert.Single(One(fight, me, selatrop, row, 301));

            fight.AddOpponent(sram); selatrop.CellId = 302;
            Assert.Empty(One(fight, me, sram, row, 301));
        }

        /// <summary>
        /// Flecha Percutiente hits "A,pb" for 31-34 and "A,PB" for 35-39: "mayores en los objetivos
        /// que tienen escudo". A shielded enemy takes the second die and not the first.
        /// </summary>
        [Fact]
        public void PB_is_a_target_with_shield_points()
        {
            var fight = new FightInstance(1, 1);
            var ocra = Character(1, 0, 300);
            var bare = Monster(-1, Beside(300, 4, 0));
            fight.AddPlayer(ocra); fight.AddOpponent(bare);

            var blows = EffectEngine.Golpes(fight, ocra, 32429, 1, bare, bare.CellId);
            Assert.Equal(31, Assert.Single(blows).Efecto.DiceNum);

            bare.Escudar(100, 5);
            blows = EffectEngine.Golpes(fight, ocra, 32429, 1, bare, bare.CellId);
            Assert.Equal(35, Assert.Single(blows).Efecto.DiceNum);
        }

        /// <summary>
        /// Afrenta's "117 on C,R" -- range "si el hechizo se proyecta por un portal" -- does
        /// nothing on a cast that went through none, and its "r" twin in Audacia does.
        /// </summary>
        [Fact]
        public void R_is_a_cast_through_a_portal()
        {
            var fight = new FightInstance(1, 1);
            var me = Character(1, 0, 300);
            var enemy = Monster(-1, 301);
            fight.AddPlayer(me); fight.AddOpponent(enemy);

            var through = new SpellEffect { EffectId = 117, DiceNum = 1, Duration = 2, TargetMask = "C,R", Triggers = "I" };
            var notThrough = new SpellEffect { EffectId = 117, DiceNum = 1, Duration = 2, TargetMask = "C,r", Triggers = "I" };
            Assert.Empty(One(fight, me, enemy, through, 301));
            Assert.Single(One(fight, me, enemy, notThrough, 301));

            fight.CastThroughPortal = true;
            Assert.Single(One(fight, me, enemy, through, 301));
            Assert.Empty(One(fight, me, enemy, notThrough, 301));
        }

        /// <summary>
        /// Aguardiente throws what the Pandawa carries, then heals "a,K" and hurts "A,K": the one
        /// he carried as the spell began, wherever the throw left him -- an ally healed, an enemy
        /// not.
        /// </summary>
        [Fact]
        public void K_is_the_one_the_caster_carried()
        {
            var fight = new FightInstance(1, 1);
            var pandawa = Character(1, 0, 300);
            var ally = Character(2, 0, 300);
            var enemy = Monster(-1, Beside(300, 3, 0));
            ally.CurrentHP = 1000;
            fight.AddPlayer(pandawa); fight.AddPlayer(ally); fight.AddOpponent(enemy);
            ally.CarriedBy = pandawa.Id; pandawa.Carrying = ally.Id;

            var heal = new SpellEffect { EffectId = 2998, DiceNum = 16, DiceSide = 18, Element = 3, TargetMask = "a,K", Triggers = "I" };
            var healed = One(fight, pandawa, null, heal, Beside(300, 2, 0));
            Assert.Equal(ally, Assert.Single(healed).Sobre);

            var hurt = new SpellEffect { EffectId = 96, DiceNum = 16, DiceSide = 18, Element = 3, TargetMask = "A,K", Triggers = "I" };
            Assert.Empty(EffectEngine.Golpes(fight, pandawa, 1, 1, null, Beside(300, 2, 0), efectos: new[] { hurt }));
        }

        /// <summary>
        /// Escudo Elemental's 1160 on "A,o" in a circle of 63 puts the elemental state "sobre el
        /// atacante": of the enemies in the zone, the one who hit.
        /// </summary>
        [Fact]
        public void o_is_the_one_who_set_it_off_inside_the_zone()
        {
            var fight = new FightInstance(1, 1);
            var hipermago = Character(1, 0, 300);
            var attacker = Monster(-1, 310);
            var bystander = Monster(-2, 320);
            fight.AddPlayer(hipermago); fight.AddOpponent(attacker); fight.AddOpponent(bystander);

            var row = new SpellEffect { EffectId = 950, Value = 292, TargetMask = "A,o", Forma = 'C', Tamano = 63, Triggers = "I" };
            Assert.Empty(One(fight, hipermago, hipermago, row, 300));

            fight.TriggeringAttacker = attacker;
            Assert.Equal(attacker, Assert.Single(One(fight, hipermago, hipermago, row, 300)).Sobre);
        }

        /// <summary>
        /// Caja de Herramientas' grade 4, fired when its bearer summons (CI), makes "j,P,u"
        /// pacifist on the whole map: the summon that just came, handed over as the one who set
        /// it off, and not the ones already out.
        /// </summary>
        [Fact]
        public void u_is_the_summon_whose_coming_set_it_off()
        {
            var fight = new FightInstance(1, 1);
            var anutrof = Character(1, 0, 300);
            var old = new Fighter { Id = -1, TeamId = 0, CellId = 301, MaxHP = 100, CurrentHP = 100, IsMonster = true, MonsterId = 3000, Invocador = 1 };
            var fresh = new Fighter { Id = -2, TeamId = 0, CellId = 302, MaxHP = 100, CurrentHP = 100, IsMonster = true, MonsterId = 3000, Invocador = 1 };
            fight.AddPlayer(anutrof); fight.Invocar(old, anutrof); fight.Invocar(fresh, anutrof);

            fight.TriggeringAttacker = fresh;
            EffectEngine.Resolver(fight, anutrof, 13384, 4, anutrof, EffectEngine.AlLanzar, 1, celdaApuntada: 300);

            Assert.True(fresh.Buffs.TieneEstado(218));
            Assert.False(old.Buffs.TieneEstado(218));
        }

        /// <summary>
        /// Terraplenado splits its blows between "h,m,d,H,M,D" and "j,J" -- "mayores sobre las
        /// invocaciones": d and D are neither characters nor monsters nor summons. Nobody in this
        /// build is a companion, so the letters add nobody.
        /// </summary>
        [Fact]
        public void D_names_no_one_here()
        {
            var fight = new FightInstance(1, 1);
            var anutrof = Character(1, 0, 300);
            var enemy = Character(2, 1, 301);
            fight.AddPlayer(anutrof); fight.AddOpponent(enemy);

            var row = new SpellEffect { EffectId = 950, Value = 1, TargetMask = "D,d", Triggers = "I" };
            Assert.Empty(One(fight, anutrof, enemy, row, 301));
            var both = new SpellEffect { EffectId = 950, Value = 1, TargetMask = "h,m,d,H,M,D", Triggers = "I" };
            Assert.Equal(enemy, Assert.Single(One(fight, anutrof, enemy, both, 301)).Sobre);
        }
    }
}
