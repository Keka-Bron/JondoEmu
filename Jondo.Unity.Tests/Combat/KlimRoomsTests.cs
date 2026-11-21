using System.Collections.Generic;
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
    /// Klime's private rooms as the dungeon plays them, run through the real engine on the real
    /// data (Dofus pour les noobs, «Salons privés de Klime»): the Cuerola's Bolaloca and
    /// Exhalación, and the glyphs a fallen monster leaves.
    /// </summary>
    [Collection("MapManager")]
    public class KlimRoomsTests
    {
        private const int Cuerola = 3383, Cuereno = 3379;
        private const int Bolaloca = 3482, Exhalacion = 3484, Carcassetagne = 3487;
        private const int PlusDamage = 112;

        private static Fighter Monster(long id, int template, int cell, int life = 6000) => new()
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

        /// <summary>
        /// «Ce sort attire également la cible de 10 cases puis la repousse de 10»: the pull brings
        /// him to the Cuerola and the push sends him back the way he came. Judged from where the
        /// pull had left him, the push drove him into the Cuerola, and both took the crash.
        /// </summary>
        [Fact]
        public async Task Bolaloca_pulls_in_and_then_pushes_back_out()
        {
            if (SpellEffects.De(Bolaloca, 1).Count == 0) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var cuerola = Monster(-1, Cuerola, 300);
            var me = Person(10, Beside(300, 4));
            fight.AddPlayer(me); fight.AddMonster(cuerola);

            await FightHandler.AplicarEfectosAsync(null, fight, cuerola, Bolaloca, 1, me, EffectEngine.AlLanzar, me.CellId);

            Assert.Equal(cuerola.MaxHP, cuerola.CurrentHP);         // nobody crashed into him
            Assert.True(MapGeometry.Distance(cuerola.CellId, me.CellId) > 1, $"left on {me.CellId}, beside him");
            Assert.True(MapGeometry.CellToPoint(me.CellId).Item1 > MapGeometry.CellToPoint(cuerola.CellId).Item1,
                        "pushed out to the other side");
        }

        /// <summary>
        /// Exhalación: «un sort qui le soigne et lui donne 600 dommages quand il est détaclé». Its
        /// rows wait on «CT» on the Cuerola himself, and walking out of his contact fires it.
        /// </summary>
        [Fact]
        public async Task Walking_out_of_the_Cuerola_s_contact_sets_off_his_Exhalacion()
        {
            if (SpellEffects.De(Exhalacion, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var cuerola = Monster(-1, Cuerola, 300);
            cuerola.CurrentHP = 3000;
            var me = Person(10, Beside(300, 1));
            fight.AddPlayer(me); fight.AddMonster(cuerola);

            await FightHandler.AplicarEfectosAsync(null, fight, cuerola, Exhalacion, 1, cuerola, EffectEngine.AlLanzar, cuerola.CellId);
            Assert.Equal(3000, cuerola.CurrentHP);                   // armed, not yet set off

            await FightHandler.WalkPathAsync(fight, me, new[] { Beside(300, 1), Beside(300, 2), Beside(300, 3) });

            Assert.True(cuerola.CurrentHP > 3000, "no heal");
            Assert.Contains(cuerola.Buffs.Puestos, r => r.EffectId == PlusDamage && r.HechizoOrigen == Exhalacion);
        }

        /// <summary>
        /// Galuchat, Klim's behaviour, gives the players Peau lisse «pour 2 tours» as the fight
        /// begins: «au tour 3, il sera effectif uniquement à partir du monstre qui joue son tour
        /// après le Klime». It lives Klim's turn of round 3 and falls at the next turn start; it
        /// fell after his second, and the monsters after him made the players Pacifist in round 2.
        /// </summary>
        [Fact]
        public async Task Peau_lisse_from_the_start_lives_through_Klim_s_third_turn()
        {
            const int Klim = 3384, Galuchat = 3485, PeauLisse = 152;
            if (SpellEffects.De(Galuchat, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var me = Person(10, 100);
            me.Initiative = 5000;
            var before = Monster(-2, Cuereno, 400);
            before.Initiative = 3000;
            var klim = Monster(-1, Klim, 300, 19000);
            klim.Initiative = 2000;
            klim.Conducta = (Galuchat, 1);
            var after = Monster(-3, Cuerola, 420);
            after.Initiative = 1000;
            fight.AddPlayer(me);
            foreach (var m in new[] { before, klim, after }) fight.AddMonster(m);
            fight.StartFight();
            await FightHandler.LanzarLaConductaAsync(null, fight, klim);
            fight.CloseTheStart();
            Assert.True(me.Buffs.TieneEstado(PeauLisse));

            // Round 1 is under way with my turn; each next turn start is swept as the fight sweeps it.
            var seen = new List<(int Round, long Who, bool Has)>();
            for (int i = 0; i < 12 && fight.RoundNumber <= 4; i++)
            {
                var next = fight.NextTurn();
                FightHandler.TurnStartSweep(fight, next);
                seen.Add((fight.RoundNumber, next.Id, me.Buffs.TieneEstado(PeauLisse)));
            }

            Assert.Contains((2, after.Id, true), seen);               // round 2 whole
            Assert.Contains((3, klim.Id, true), seen);                // Klim's own third turn start
            Assert.Contains((3, after.Id, false), seen);              // from the monster after him
        }

        /// <summary>
        /// «Si un des monstres est achevé, les deux glyphes qu'il a posé disparaissent»: his
        /// Carcassetagne glyphs go the moment he falls, not at the next turn start.
        /// </summary>
        [Fact]
        public async Task A_fallen_monster_s_glyphs_go_with_him()
        {
            var fight = new FightInstance(1, 1);
            var cuereno = Monster(-2, Cuereno, 300, 100);
            var other = Monster(-3, Cuerola, 120);
            var me = Person(10, Beside(300, -1));
            fight.AddPlayer(me); fight.AddMonster(cuereno); fight.AddMonster(other);
            foreach (var (owner, cell) in new[] { (cuereno, 300), (cuereno, 302), (other, 120) })
                fight.Glifos.Add(new Glifo(owner.Id, new[] { cell }, Carcassetagne, 1, 0xFFFFFF,
                                           fight.RoundNumber + 2, "A", Disparo.AlAcabarElTurno));

            var blow = new SpellEffect { EffectId = 100, EffectUid = 900100, DiceNum = 5000, DiceSide = 5000, TargetMask = "a,A" };
            await FightHandler.HurtAsync(null, fight, me, 1, 1, cuereno, cuereno.CellId, tirada: new[] { blow });

            Assert.False(cuereno.IsAlive);
            Assert.DoesNotContain(fight.Glifos, g => g.Dueno == cuereno.Id);
            Assert.Contains(fight.Glifos, g => g.Dueno == other.Id);
        }
    }
}
