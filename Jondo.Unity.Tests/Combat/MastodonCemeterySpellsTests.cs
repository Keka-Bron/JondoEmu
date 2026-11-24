using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Mastodon Cemetery's Yenaparda (4619) and its Mort Sûre (7222): «une seconde fois deux tours
    /// après» the bite kills. Grade 1 hurts unless the target holds 449, puts 859 on it and has the
    /// caster cast grade 4 at it -- the kill, "A,*E858" -- and, under TB with a delay of one, grade 2
    /// at the target's next turn: 449 on it, 437 on the dog and a hook "when 859 is put on me, the dog
    /// casts grade 3", which gives the dog 858. Recast two rounds later, the 859 of the second bite
    /// brings 858 on the dog before grade 4 reads it.
    /// </summary>
    [Collection("MapManager")]
    public class MastodonCemeterySpellsTests
    {
        private const int Yenaparda = 4619, MortSure = 7222;

        private static Fighter Monster(long id, int template, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 100,
            IsMonster = true, MonsterId = template, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4,
        };

        private static Fighter Person(long id, int cell) => new()
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 50000, CurrentHP = 50000, Level = 150,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
        };

        private static Task BiteAsync(FightInstance fight, Fighter dog, Fighter me)
            => FightHandler.LanzarPorOrdenAsync(null, fight, dog, MortSure, 1, me, me.CellId,
                                                EffectEngine.AlLanzar, SpellEffects.De(MortSure, 1));

        /// <summary>The next turn, started as the fight starts it: its sweep, then its TB hooks.</summary>
        private static async Task<Fighter> NextTurnAsync(FightInstance fight)
        {
            var ending = fight.CurrentFighter;
            await FightHandler.EngancheAsync(null, fight, ending, EffectEngine.AlAcabarElTurno);
            var next = fight.NextTurn();
            FightHandler.TurnStartSweep(fight, next);
            await FightHandler.EngancheAsync(null, fight, next, EffectEngine.AlEmpezarElTurno);
            return next;
        }

        /// <summary>
        /// The second bite's 859 is what makes the dog cast grade 3 (858 on him), and grade 4's kill
        /// reads 858: the hook on 859 must go off before the rest of the cast. Resolved in one run
        /// with the 950, grade 4 read the caster's states before the hook had given him 858.
        /// </summary>
        [Fact]
        public async Task The_bite_after_its_grade_two_kills()
        {
            if (SpellEffects.De(MortSure, 1).Count == 0) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var me = Person(10, 258);
            var dog = Monster(-1, Yenaparda, 244);
            fight.AddPlayer(me); fight.AddMonster(dog);
            fight.StartFight();

            await BiteAsync(fight, dog, me);
            Assert.True(me.IsAlive);
            Assert.True(me.CurrentHP < me.MaxHP);
            await FightHandler.LanzarPorOrdenAsync(null, fight, dog, MortSure, 2, me, me.CellId,
                                                   EffectEngine.AlLanzar, SpellEffects.De(MortSure, 2));
            await BiteAsync(fight, dog, me);
            Assert.False(me.IsAlive);
        }

        /// <summary>
        /// The whole mechanic over real turns, whoever plays first: bitten in round 1, recast in
        /// round 3 (every two rounds), the second bite kills. With the player first, grade 2 went
        /// off at his round-2 turn start, before the dog's round-2 turn; 449 and the 859 hook fell
        /// with that turn of the dog, and the round-3 bite only hurt.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_recast_two_rounds_later_kills(bool playerFirst)
        {
            if (SpellEffects.De(MortSure, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var me = Person(10, 258);
            var dog = Monster(-1, Yenaparda, 244);
            me.Initiative = playerFirst ? 5000 : 1000;
            dog.Initiative = playerFirst ? 1000 : 5000;
            fight.AddPlayer(me); fight.AddMonster(dog);
            fight.StartFight();
            fight.CloseTheStart();
            Assert.Same(playerFirst ? me : dog, fight.CurrentFighter);

            int bites = 0;
            for (int i = 0; i < 12 && fight.RoundNumber <= 3 && me.IsAlive; i++)
            {
                if (fight.CurrentFighter == dog && fight.RoundNumber is 1 or 3)
                {
                    int before = me.CurrentHP;
                    await BiteAsync(fight, dog, me);
                    bites++;
                    if (bites == 1) Assert.True(me.CurrentHP < before);
                }
                await NextTurnAsync(fight);
            }
            Assert.Equal(2, bites);
            Assert.False(me.IsAlive);
        }

        /// <summary>
        /// Provocaspion (7227) pulls every foe three cells towards the boss, in line or not: e6 with
        /// mask A and zone C63 from the boss's own cell. On the real board a foe stays put only when
        /// the board blocks the way (a cell off the fight walkable set), which is geometry, not range.
        /// </summary>
        [Theory]
        [InlineData(371)]
        [InlineData(372)]
        [InlineData(358)]
        public async Task Provocaspion_pulls_a_foe_at_range(int myCell)
        {
            if (SpellEffects.De(7227, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var me = Person(10, myCell);
            var boss = Monster(-1, 4621, 302);
            fight.AddPlayer(me); fight.AddMonster(boss);
            fight.StartFight();
            me.CellId = myCell; boss.CellId = 302;
            int before = Jondo.Unity.World.Maps.MapGeometry.Distance(me.CellId, boss.CellId);
            await FightHandler.LanzarPorOrdenAsync(null, fight, boss, 7227, 1, boss, boss.CellId,
                                                   EffectEngine.AlLanzar, SpellEffects.De(7227, 1));
            Assert.True(Jondo.Unity.World.Maps.MapGeometry.Distance(me.CellId, boss.CellId) < before);
        }

        /// <summary>
        /// Enarenamiento (7240) lays an aura of 7257 round the scorpion: -20% life and -2 MP, mask
        /// A. It takes the foes in it, none of the scorpion's allies -- standing in it or walking
        /// in -- and what it took comes back to whoever walks out.
        /// </summary>
        [Fact]
        public async Task Enarenamiento_takes_foes_only_and_gives_back_on_leaving()
        {
            if (SpellEffects.De(7240, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var me = Person(10, 330);
            var scorpion = Monster(-1, 4615, 316);
            var ally = Monster(-2, 4617, 302);
            fight.AddPlayer(me); fight.AddMonster(scorpion); fight.AddMonster(ally);
            fight.StartFight();
            await FightHandler.LanzarPorOrdenAsync(null, fight, scorpion, 7240, 1, scorpion, scorpion.CellId,
                                                   EffectEngine.AlLanzar, SpellEffects.De(7240, 1));
            Assert.Contains(me.Buffs.Puestos, r => r.HechizoOrigen == 7257);
            Assert.True(me.MaxHP < 50000);
            Assert.DoesNotContain(ally.Buffs.Puestos, r => r.HechizoOrigen == 7257);

            ally.CellId = 301;
            await FightHandler.DispararLosGlifosAsync(null, fight, ally, true);
            Assert.DoesNotContain(ally.Buffs.Puestos, r => r.HechizoOrigen == 7257);

            me.CellId = 400;
            await FightHandler.DispararLosGlifosAsync(null, fight, me, true);
            Assert.DoesNotContain(me.Buffs.Puestos, r => r.HechizoOrigen == 7257);
            Assert.Equal(50000, me.MaxHP);
        }

        /// <summary>
        /// 4620's 7217 (3 AP, range three in line) hits its target in earth on the aimed cell (P1)
        /// and the cells round it in air (Q1, a cross without its centre). Weighed by the cross
        /// alone it hit nobody and the monster never cast it: three cells in line from its only
        /// foe, with 7218 spent, it casts 7217.
        /// </summary>
        [Fact]
        public void The_stomp_three_cells_in_line_is_cast()
        {
            const long Room = 107485184;
            if (SpellEffects.De(7217, 1).Count == 0) return;
            if (Jondo.Unity.Server.MapManager.Maps.Count == 0) Jondo.Unity.Server.MapManager.Initialize();
            var walkable = Jondo.Unity.Server.MapManager.GetFightWalkable(Room);
            if (walkable.Count == 0) return;
            bool Free(int c) => c >= 0 && c < Jondo.Unity.World.Maps.MapGeometry.MaxCells && walkable.Contains(c);
            // A foe and a cell three cells in line from him with the cells between free.
            int mine = -1, from = -1;
            foreach (int c in walkable.OrderBy(c => c))
            {
                var (x, y) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(c);
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var line = Enumerable.Range(1, 3).Select(k => Jondo.Unity.World.Maps.MapGeometry.PointToCell(x + dx * k, y + dy * k)).ToList();
                    if (line.All(Free) && Jondo.Unity.World.Maps.MapGeometry.Distance(c, line[2]) == 3) { mine = c; from = line[2]; break; }
                }
                if (mine >= 0) break;
            }
            Assert.True(mine >= 0);

            var fight = new FightInstance(9_999_153, Room, Room);
            var me = Person(10, mine);
            var monster = Monster(-1, 4620, from);
            monster.MaxMP = 0; monster.CurrentMP = 0;
            foreach (int spell in new[] { 7217, 7218 }) { monster.SpellIds.Add(spell); monster.SpellGrades[spell] = 1; }
            fight.AddPlayer(me); fight.AddOpponent(monster);
            var usable = FightHandler.TacticsOf(monster).Where(s => s.Id == 7217 && SpellCriteria.Allows(monster, s.Id, s.Grade)).ToList();
            Assert.Single(usable);

            var plan = MonsterTactics.PlanTurn(FightHandler.BoardOf(fight), monster, usable);
            Assert.Contains(plan, a => a.Spell?.Id == 7217 && a.TargetCell == mine);
        }
    }
}
