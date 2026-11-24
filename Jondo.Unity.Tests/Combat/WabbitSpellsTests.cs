using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Wa Wabbit's burrow (dungeon 17) as Dofus pour les noobs tells it, run through the real
    /// engine on the real data: «Aspiwation» keeps the Cawotte it summons, and the Wobot's
    /// «Weuche» does not set off its own «Louwdeuw».
    /// </summary>
    [Collection("MapManager")]
    public class WabbitSpellsTests
    {
        private const int BlackWoWabbit = 3461, Aspiwation = 3795, RoyalCarrot = 3458;
        private const int Wobot = 3462, Weuche = 3799, Louwdeuw = 3798;

        private static Fighter Monster(long id, int template, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 100,
            IsMonster = true, MonsterId = template, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4,
        };

        private static Fighter Person(long id, int cell) => new()
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 5000, CurrentHP = 5000, Level = 150,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
        };

        /// <summary>
        /// "Quand une nouvelle Cawotte Woyale est invoquée, la précédente disparaît": the spell
        /// summons (181) and then strikes down "a,P,F3458" on the whole board. That row picks the
        /// Cawottes there as the spell went off; the one it has just brought out stays.
        /// </summary>
        [Fact]
        public async Task Aspiwation_strikes_down_the_previous_carrot_and_keeps_the_new_one()
        {
            var rows = SpellEffects.De(Aspiwation, 1);
            if (rows.Count == 0) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var me = Person(10, 318);
            var wabbit = Monster(-1, BlackWoWabbit, 272);
            var previous = Monster(-2, RoyalCarrot, 100);
            previous.Invocador = wabbit.Id;
            fight.AddPlayer(me); fight.AddMonster(wabbit); fight.AddMonster(previous);
            fight.StartFight();

            await FightHandler.LanzarPorOrdenAsync(null, fight, wabbit, Aspiwation, 1, me, me.CellId,
                                                   EffectEngine.AlLanzar, rows);

            Assert.False(previous.IsAlive);
            var born = Assert.Single(fight.Todos, f => f.MonsterId == RoyalCarrot && f != previous);
            Assert.True(born.IsAlive);
            Assert.True(born.CurrentHP > 0);
            Assert.Equal(wabbit.Id, born.Invocador);
        }

        /// <summary>
        /// «Louwdeuw»: "quand le Wobot est poussé ou quand il échange de position, son attaquant
        /// subit 70 de dégâts feu" (792 under MS|P). «Weuche» steps the Wobot 4 cells forward
        /// (1042) before its water blow: walking is no push, and the Wobot loses no life of its own.
        /// </summary>
        [Fact]
        public async Task Weuche_does_not_burn_the_wobot_through_its_own_passive()
        {
            var rows = SpellEffects.De(Weuche, 1);
            if (rows.Count == 0 || SpellEffects.De(Louwdeuw, 1).Count == 0) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var me = Person(10, 318);
            var wobot = Monster(-1, Wobot, 245);
            fight.AddPlayer(me); fight.AddMonster(wobot);
            fight.StartFight();
            wobot.Conducta = (Louwdeuw, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, wobot);
            Assert.Contains(wobot.Buffs.ActiveSpells, h => h.Hechizo == Louwdeuw && h.Filas != null);

            await FightHandler.LanzarPorOrdenAsync(null, fight, wobot, Weuche, 1, me, me.CellId,
                                                   EffectEngine.AlLanzar, rows);

            Assert.NotEqual(245, wobot.CellId);                 // it did step forward
            Assert.Equal(wobot.MaxHP, wobot.CurrentHP);
            Assert.True(me.CurrentHP < me.MaxHP);               // and the blow landed on the enemy
        }

        /// <summary>
        /// Being pushed is still being pushed: a push on the Wobot sets «Louwdeuw» off on the one
        /// who pushed it.
        /// </summary>
        [Fact]
        public async Task A_push_on_the_wobot_still_burns_the_pusher()
        {
            if (SpellEffects.De(Louwdeuw, 1).Count == 0) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var me = Person(10, 259);
            var wobot = Monster(-1, Wobot, 273);                    // next to me
            fight.AddPlayer(me); fight.AddMonster(wobot);
            fight.StartFight();
            wobot.Conducta = (Louwdeuw, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, wobot);

            var push = new SpellEffect
            {
                EffectId = Jondo.Unity.World.Combat.EffectSupport.Push, DiceNum = 2, TargetMask = "A",
            };
            await FightHandler.LanzarPorOrdenAsync(null, fight, me, 1, 1, wobot, wobot.CellId,
                                                   EffectEngine.AlLanzar, new[] { push });

            Assert.NotEqual(273, wobot.CellId);
            Assert.True(me.CurrentHP < me.MaxHP);
            Assert.Equal(wobot.MaxHP, wobot.CurrentHP);
        }
    }
}
