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
    /// Obsidiantre's starting spell, Obligación (673): invulnerable from the start, and two 792 under
    /// PMD armed at the cast on whoever their masks name over the whole board -- 676 on his enemies
    /// ("A"), 672, Objecto, on his other allies ("g"). The children are cast by the one pushed, around
    /// himself: 676's "952 on A,F2924, X1" is the Obsidiantre next to him, his invulnerability off,
    /// with "+50 damage on A"; 672 heals the allies next to the monster pushed and gives them +50.
    /// The guide: push an enemy into him and he is vulnerable; push one of his allies into him and he
    /// is healed. A summon that came in after the cast carries nothing, and does nothing.
    /// </summary>
    public class ObsidiantreObligationTests
    {
        private const int Obsidiantre = 2924;
        private const int Obligacion = 673;
        private const int Lifted = 676;
        private const int Objecto = 672;
        private const int Invulnerable = 56;
        private const int DamageBonus = 112;

        private static bool HasData => SpellEffects.De(Obligacion, 1).Count > 0
                                       && SpellEffects.De(Lifted, 1).Count > 0
                                       && SpellEffects.De(Objecto, 1).Count > 0;

        /// <summary>Three cells in a straight line: the pusher, the one pushed, and the boss behind him.</summary>
        private static (int Pusher, int Pushed, int Behind) ALineOfThree()
        {
            int pusher = 288;
            int pushed = MapGeometry.GetNeighbors(pusher).First();
            int behind = MapGeometry.GetNeighbors(pushed)
                                    .First(c => c != pusher && MapGeometry.Distance(pusher, c) == 2);
            return (pusher, pushed, behind);
        }

        private static Fighter Monster(long id, int template, int cell, int life) => new()
        {
            Id = id, CellId = cell, MaxHP = life, CurrentHP = life, Level = 160, IsMonster = true,
            MonsterId = template, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
        };

        private static Fighter Character(long id, int cell) => new()
        {
            Id = id, CellId = cell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
            MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
        };

        /// <summary>
        /// The fight: two characters, the Obsidiantre with one of his monsters, his Obligación cast as
        /// a fight casts it. Everybody stands off the line; each test puts the three it needs on it.
        /// </summary>
        private static async Task<(FightInstance Fight, Fighter Boss, Fighter Ally, Fighter Character, Fighter Friend)> BoardAsync()
        {
            var fight = new FightInstance(1, 1);
            var character = Character(10, 100);
            var friend = Character(11, 102);
            fight.AddPlayer(character);
            fight.AddPlayer(friend);
            var boss = Monster(-1, Obsidiantre, 400, 8000);
            var ally = Monster(-2, 2850, 404, 3000);
            fight.AddMonster(boss);
            fight.AddMonster(ally);
            fight.StartFight();

            boss.Conducta = (Obligacion, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            return (fight, boss, ally, character, friend);
        }

        /// <summary>A plain push of three cells, cast by <paramref name="pusher"/> at <paramref name="pushed"/>.</summary>
        private static Task PushAsync(FightInstance fight, Fighter pusher, Fighter pushed)
            => FightHandler.AplicarEfectosAsync(null, fight, pusher, 1, 1, pushed, EffectEngine.AlLanzar, pushed.CellId,
                   tirada: new[] { new SpellEffect { EffectId = 5, DiceNum = 3, TargetMask = "a,A", Triggers = "I" } });

        [Fact]
        public async Task His_obligation_is_armed_on_his_enemies_and_on_his_allies_not_on_him()
        {
            if (!HasData) return;
            var (_, boss, ally, character, friend) = await BoardAsync();

            Assert.True(boss.Buffs.TieneEstado(Invulnerable));
            Assert.Contains(character.Buffs.ActiveSpells, hook => hook.Hechizo == Obligacion && hook.Filas != null);
            Assert.Contains(friend.Buffs.ActiveSpells, hook => hook.Hechizo == Obligacion && hook.Filas != null);
            Assert.Contains(ally.Buffs.ActiveSpells, hook => hook.Hechizo == Obligacion && hook.Filas != null);
        }

        /// <summary>
        /// A character pushed into him: the crash sets off the 676 the character carries, cast by the
        /// character around himself, and the Obsidiantre next to him loses his invulnerability and
        /// takes +50 damage.
        /// </summary>
        [Fact]
        public async Task An_enemy_pushed_into_him_lifts_his_invulnerability()
        {
            if (!HasData) return;
            var (fight, boss, _, character, friend) = await BoardAsync();
            var (pusherCell, pushedCell, behind) = ALineOfThree();
            friend.CellId = pusherCell;
            character.CellId = pushedCell;
            boss.CellId = behind;

            await PushAsync(fight, friend, character);

            Assert.Equal(pushedCell, character.CellId);           // he crashed where he stood
            Assert.False(boss.Buffs.TieneEstado(Invulnerable));
            Assert.Contains(boss.Buffs.Puestos, row => row.HechizoOrigen == Lifted && row.EffectId == DamageBonus
                                                       && row.Cuanto == 50);
            // Nothing of Objecto: that one is his allies'.
            Assert.DoesNotContain(boss.Buffs.Puestos, row => row.HechizoOrigen == Objecto);
        }

        /// <summary>
        /// One of his own monsters pushed into him: Objecto, cast by that monster around itself, heals
        /// him and gives him +50 damage, and he stays invulnerable.
        /// </summary>
        [Fact]
        public async Task An_ally_pushed_into_him_heals_him()
        {
            if (!HasData) return;
            var (fight, boss, ally, character, _) = await BoardAsync();
            var (pusherCell, pushedCell, behind) = ALineOfThree();
            character.CellId = pusherCell;
            ally.CellId = pushedCell;
            boss.CellId = behind;
            boss.CurrentHP = 1000;

            await PushAsync(fight, character, ally);

            Assert.True(boss.Buffs.TieneEstado(Invulnerable));
            Assert.True(boss.CurrentHP > 1000, $"healed: {boss.CurrentHP}");
            Assert.Contains(boss.Buffs.Puestos, row => row.HechizoOrigen == Objecto && row.EffectId == DamageBonus
                                                       && row.Cuanto == 50);
            Assert.DoesNotContain(boss.Buffs.Puestos, row => row.HechizoOrigen == Lifted);
        }

        /// <summary>A summon that came in after his Obligación carries none of it: pushed into him, nothing.</summary>
        [Fact]
        public async Task A_summon_pushed_into_him_does_nothing()
        {
            if (!HasData) return;
            var (fight, boss, _, character, _) = await BoardAsync();
            var (pusherCell, pushedCell, behind) = ALineOfThree();
            var summon = new Fighter
            {
                Id = -10, CellId = pushedCell, MaxHP = 500, CurrentHP = 500, Level = 100, IsMonster = true,
                MonsterId = 2849, SummonCost = 1,
            };
            fight.Invocar(summon, character);
            character.CellId = pusherCell;
            boss.CellId = behind;

            await PushAsync(fight, character, summon);

            Assert.True(boss.Buffs.TieneEstado(Invulnerable));
            Assert.DoesNotContain(boss.Buffs.Puestos, row => row.HechizoOrigen == Lifted || row.HechizoOrigen == Objecto);
        }
    }
}
