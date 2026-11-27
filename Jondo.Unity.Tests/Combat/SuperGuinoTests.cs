using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Morsagüino Real (2848) starts with Salagüino (2607), whose "792 2609 under X on g" is armed on
    /// each of his allies: at an ally's death that ally casts Super-güino (2609) -- +1000 vitality,
    /// +10% final damage and +1 MP on "F2848,g", for the rest of the fight (duration -1). The rows are
    /// the dying ally's, written undispellable by death (dispellable 3), so they outlive him: every
    /// ally that falls makes the boss stronger.
    /// </summary>
    [Collection("MapManager")]
    public class SuperGuinoTests
    {
        private const int MorsaguinoReal = 2848;
        private const int Salaguino = 2607;
        private const int SuperGuino = 2609;
        private const int Vitality = 125, FinalDamage = 1171, MovementPoints = 128;

        private static bool HasData => SpellEffects.De(Salaguino, 1).Count > 0 && SpellEffects.De(SuperGuino, 1).Count > 0;

        private static Fighter Monster(long id, int template, int cell, int life) => new()
        {
            Id = id, CellId = cell, MaxHP = life, CurrentHP = life, Level = 140, IsMonster = true,
            MonsterId = template, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
        };

        private static async Task<(FightInstance Fight, Fighter Boss, Fighter[] Allies, Fighter Character)> BoardAsync()
        {
            var fight = new FightInstance(1, 1);
            var character = new Fighter
            {
                Id = 10, CellId = 100, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
            };
            fight.AddPlayer(character);
            var boss = Monster(-1, MorsaguinoReal, 400, 8744);
            var allies = new[] { Monster(-2, 2849, 404, 1000), Monster(-3, 2849, 408, 1000) };
            fight.AddMonster(boss);
            foreach (var ally in allies) fight.AddMonster(ally);
            fight.StartFight();

            boss.Conducta = (Salaguino, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            return (fight, boss, allies, character);
        }

        [Fact]
        public async Task Each_ally_that_falls_makes_him_stronger_for_good()
        {
            if (!HasData) return;
            var (fight, boss, allies, character) = await BoardAsync();
            Assert.All(allies, ally => Assert.Contains(ally.Buffs.ActiveSpells, hook => hook.Hechizo == Salaguino));
            int life = boss.MaxHP;

            await FightHandler.TestKillAsync(null, fight, allies[0], character);

            Assert.False(allies[0].IsAlive);
            Assert.Equal(life + 1000, boss.MaxHP);
            Assert.Equal(life + 1000, boss.CurrentHP);
            Assert.Equal(1, boss.Buffs.De(Fighter.CaracteristicaDePuntosDeMovimiento, fight.RoundNumber));
            Assert.Contains(boss.Buffs.Puestos, row => row.HechizoOrigen == SuperGuino && row.EffectId == FinalDamage
                                                       && row.Cuanto == 10);

            await FightHandler.TestKillAsync(null, fight, allies[1], character);

            Assert.Equal(life + 2000, boss.MaxHP);
            Assert.Equal(life + 2000, boss.CurrentHP);
            Assert.Equal(2, boss.Buffs.Puestos.Count(row => row.HechizoOrigen == SuperGuino && row.EffectId == Vitality));
            Assert.Equal(2, boss.Buffs.Puestos.Count(row => row.HechizoOrigen == SuperGuino && row.EffectId == MovementPoints));

            // And they stay as the rounds go by.
            int later = fight.RoundNumber + 3;
            while (fight.RoundNumber < later) fight.NextTurn();
            Assert.Equal(life + 2000, boss.MaxHP);
            Assert.Equal(2, boss.Buffs.De(Fighter.CaracteristicaDePuntosDeMovimiento, fight.RoundNumber));
        }

        /// <summary>
        /// Salagüino also casts 2212 on him, «792 2610 under P|MA, trigger duration 63»: pushed or
        /// pulled at any point of the fight, he casts 2610 (at most every four turns), whose 786
        /// under D heals whoever hits him by all of the blow until the next round.
        /// </summary>
        [Fact]
        public async Task Pushed_he_heals_whoever_hits_him()
        {
            if (!HasData || SpellEffects.De(2212, 1).Count == 0 || SpellEffects.De(2610, 1).Count == 0) return;
            if (!SpellEffects.De(PushSpell, 1).Any(row => row.EffectId == 5)) return;
            var (fight, boss, _, character) = await BoardAsync();
            Assert.Contains(boss.Buffs.ActiveSpells, hook => hook.Hechizo == 2212);

            int round = fight.RoundNumber;
            while (fight.RoundNumber < round + 3) fight.NextTurn();
            boss.Buffs.BarrerEnganches(fight.RoundNumber);
            Assert.Contains(boss.Buffs.ActiveSpells, hook => hook.Hechizo == 2212);

            int bossCell = 330;
            boss.CellId = bossCell;
            character.CellId = Jondo.Unity.World.Maps.MapGeometry.GetNeighbors(bossCell).First();
            await FightHandler.AplicarEfectosAsync(null, fight, character, PushSpell, 1, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);
            Assert.NotEqual(bossCell, boss.CellId);
            Assert.Contains(boss.Buffs.ActiveSpells, hook => hook.Hechizo == 2610);

            // Hit in the same turn (his shell, state 56, already broken), the hurt attacker gets
            // the whole blow back as life.
            boss.Buffs.QuitarEstadoConEmbrujos(56);
            character.CurrentHP = 1000;
            int bossLife = boss.CurrentHP;
            await FightHandler.HurtAsync(null, fight, character, 1, 1, boss, boss.CellId, tirada: new[] { Blow(400) });
            int blow = bossLife - boss.CurrentHP;
            Assert.True(blow > 0);
            Assert.Equal(1000 + blow, character.CurrentHP);

            // And not for good: past his next turn, a hit heals nobody.
            int now = fight.RoundNumber;
            while (fight.RoundNumber < now + 2) fight.NextTurn();
            boss.Buffs.BarrerEnganches(fight.RoundNumber);
            character.CurrentHP = 1000;
            bossLife = boss.CurrentHP;
            await FightHandler.HurtAsync(null, fight, character, 1, 1, boss, boss.CellId, tirada: new[] { Blow(400) });
            Assert.True(boss.CurrentHP < bossLife);
            Assert.Equal(1000, character.CurrentHP);
        }

        /// <summary>
        /// Pushed in the first round, as the live check did, and pushed again once his first 2610
        /// is over and its four-round interval has gone by: each push sets 2212 off. The listener
        /// is Salagüino's for the whole fight, not for his next turn alone.
        /// </summary>
        [Fact]
        public async Task Every_push_of_the_fight_sets_off_his_heal_on_the_attacker()
        {
            if (!HasData || SpellEffects.De(2212, 1).Count == 0 || SpellEffects.De(2610, 1).Count == 0) return;
            if (!SpellEffects.De(PushSpell, 1).Any(row => row.EffectId == 5)) return;
            var (fight, boss, _, character) = await BoardAsync();

            async Task PushAsync()
            {
                boss.CellId = 330;
                character.CellId = Jondo.Unity.World.Maps.MapGeometry.GetNeighbors(330).First();
                await FightHandler.AplicarEfectosAsync(null, fight, character, PushSpell, 1, boss,
                                                       EffectEngine.AlLanzar, boss.CellId);
                Assert.NotEqual(330, boss.CellId);
            }

            await PushAsync();
            Assert.Contains(boss.Buffs.ActiveSpells, hook => hook.Hechizo == 2610);

            int round = fight.RoundNumber;
            while (fight.RoundNumber < round + 5) fight.NextTurn();
            boss.Buffs.BarrerEnganches(fight.RoundNumber);
            Assert.DoesNotContain(boss.Buffs.ActiveSpells, hook => hook.Hechizo == 2610);
            Assert.Contains(boss.Buffs.ActiveSpells, hook => hook.Hechizo == 2212);

            await PushAsync();
            Assert.Contains(boss.Buffs.ActiveSpells, hook => hook.Hechizo == 2610);
        }

        /// <summary>
        /// How his shell (state 56, Salagüino's for good) comes off: Salagüino arms Refresgüino
        /// (2608) on each enemy, whose 1160 under H casts its grade 2 when that enemy is healed --
        /// 96 water to the enemies around and 952 of state 56 on «a,F2848» for six rounds, only
        /// within its zone. Healed next to him, a character opens him to blows; healed away from
        /// him, nothing comes off. 41 rounds of hits with no heal never scratched him.
        /// </summary>
        [Fact]
        public async Task A_heal_next_to_him_breaks_his_shell_for_six_rounds()
        {
            if (!HasData || SpellEffects.De(2608, 2).Count == 0 || SpellEffects.De(SelfHeal, 1).Count == 0) return;
            var (fight, boss, _, character) = await BoardAsync();
            Assert.Contains(56, boss.Buffs.Estados);

            int bossLife = boss.CurrentHP;
            await FightHandler.HurtAsync(null, fight, character, 1, 1, boss, boss.CellId, tirada: new[] { Blow(400) });
            Assert.Equal(bossLife, boss.CurrentHP);

            // Away from him: healed, and the shell stays.
            character.CurrentHP = 1000;
            await FightHandler.AplicarEfectosAsync(null, fight, character, SelfHeal, 1, character,
                                                   EffectEngine.AlLanzar, character.CellId);
            Assert.True(character.CurrentHP > 1000);
            Assert.DoesNotContain(boss.Buffs.Puestos, row => row.HechizoOrigen == 2608 && row.EffectId == 952);

            // Next to him: the shell is off until six rounds on, and the blow lands.
            character.CellId = Jondo.Unity.World.Maps.MapGeometry.GetNeighbors(boss.CellId).First();
            character.CurrentHP = 1000;
            await FightHandler.AplicarEfectosAsync(null, fight, character, SelfHeal, 1, character,
                                                   EffectEngine.AlLanzar, character.CellId);
            Assert.Contains(boss.Buffs.Puestos, row => row.HechizoOrigen == 2608 && row.EffectId == 952);

            await FightHandler.HurtAsync(null, fight, character, 1, 1, boss, boss.CellId, tirada: new[] { Blow(400) });
            Assert.True(boss.CurrentHP < bossLife);
        }

        /// <summary>Elixir Curativo: a heal on oneself, as the live check used.</summary>
        private const int SelfHeal = 4155;

        private static SpellEffect Blow(int amount) => new()
        {
            EffectId = 100, EffectUid = 900100, DiceNum = amount, DiceSide = amount, Element = 0, TargetMask = "a,A",
        };

        private const int PushSpell = 32426;

        /// <summary>
        /// What a monster put that dies with it still goes with it: the rows dispellable by death (2)
        /// and the plainly dispellable ones (1). Only the ones written to outlive their caster stay.
        /// </summary>
        [Fact]
        public async Task What_is_dispellable_by_death_still_goes_with_the_one_who_put_it()
        {
            var fight = new FightInstance(1, 1);
            var character = new Fighter { Id = 10, CellId = 100, MaxHP = 5000, CurrentHP = 5000, Level = 200 };
            fight.AddPlayer(character);
            var boss = Monster(-1, MorsaguinoReal, 400, 5000);
            var ally = Monster(-2, 2849, 404, 1000);
            fight.AddMonster(boss);
            fight.AddMonster(ally);
            fight.StartFight();
            // Numbered by the fight, as every row is: what was put before his death began is his.
            foreach (int dispellable in new[] { 1, 2, 3, 4 })
            {
                boss.Buffs.Poner(new Buff
                {
                    EffectId = 118, Caracteristica = 10, Cuanto = dispellable, Dispellable = dispellable,
                    Quien = ally.Id, HechizoOrigen = 9000 + dispellable, CaducaEnRonda = -1,
                }, fight.SiguienteEmbrujo);
            }

            await FightHandler.TestKillAsync(null, fight, ally, character);

            Assert.Equal(new[] { 3, 4 }, boss.Buffs.Puestos.Where(row => row.Quien == ally.Id)
                                                         .Select(row => row.Dispellable).OrderBy(d => d));
        }
    }
}
