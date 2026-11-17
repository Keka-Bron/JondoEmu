using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// «Le mal qui fait bien»: a Malter's copy (the Malterego, monster 6974) dies and its summoner
    /// "becomes visible again and loses 3 MP, 40 % of its resistances, and its damage is cut by
    /// 50 %" (Dofus pour les noobs, Tertre du long sommeil). The data writes it as a hook the
    /// copy carries from its summoning: 21477 grade 1, "792 under X, trigger duration 63", which
    /// casts grade 2 -- and that casts 21475 on the summoner.
    /// </summary>
    [Collection("MapManager")]
    public class MalteregoDeathTests
    {
        private const int Summon = 21476, Hook = 21477;

        private static Fighter Malter(int cell) => new()
        {
            Id = -1, TeamId = 1, CellId = cell, MaxHP = 3100, CurrentHP = 3100, Level = 150,
            IsMonster = true, MonsterId = 6967, MaxAP = 11, CurrentAP = 11, MaxMP = 5, CurrentMP = 5,
        };

        private static Fighter Copy(int cell) => new()
        {
            Id = -2, TeamId = 1, CellId = cell, MaxHP = 2100, CurrentHP = 2100, Level = 150,
            IsMonster = true, MonsterId = 6974, Invocador = -1,
            MaxAP = 11, CurrentAP = 11, MaxMP = 5, CurrentMP = 5,
        };

        /// <summary>The Malter summons its copy in <paramref name="round"/>: what the cast leaves armed on the copy.</summary>
        private static (FightInstance Fight, Fighter Malter, Fighter Copy) Summoned(int round)
        {
            var fight = new FightInstance(1, 1);
            var malter = Malter(300); var copy = Copy(330);
            var hero = new Fighter { Id = 10, TeamId = 0, CellId = 360, MaxHP = 5000, CurrentHP = 5000, Level = 200 };
            fight.AddMonster(malter); fight.AddMonster(copy); fight.AddPlayer(hero);
            var outcomes = EffectEngine.Resolver(fight, malter, Summon, 2, copy, EffectEngine.AlLanzar,
                                                 round, celdaApuntada: copy.CellId);
            FightHandler.EngancharLoPendiente(outcomes, Summon, 2, malter.Id, round);
            return (fight, malter, copy);
        }

        [Fact]
        public void The_death_hook_is_armed_for_good_and_not_to_the_next_round()
        {
            var (fight, _, copy) = Summoned(round: 0);   // the fight is in round 1: a round later than the cast

            var hook = Assert.Single(copy.Buffs.ActiveSpells, h => h.Hechizo == Hook);
            Assert.True(hook.Vivo(fight.RoundNumber));
        }

        [Fact]
        public async Task The_summoner_pays_when_its_copy_falls_rounds_after_the_summoning()
        {
            var (fight, malter, copy) = Summoned(round: 0);
            copy.CurrentHP = 1;   // it fires while the dying one still stands (AlMorirAsync)

            await FightHandler.EngancheAsync(null, fight, copy, EffectEngine.AlMorir);
            copy.CurrentHP = 0;
            await FightHandler.CaenSusInvocadosAsync(null, fight, copy);

            // -3 MP (169), -40 % resistances (1077), -50 % final damage (1172) and the weakened state 2746:
            // what the copy's fall costs the summoner, and it stays when the copy's own rows go.
            var rows = malter.Buffs.Puestos;
            Assert.Contains(rows, b => b.EffectId == 169 && b.Cuanto == -3);
            Assert.Contains(rows, b => b.EffectId == 1077 && b.Cuanto == -40);
            Assert.Contains(rows, b => b.EffectId == 1172 && b.Cuanto == -50);
            Assert.True(malter.Buffs.TieneEstado(2746));
        }

        /// <summary>Only a row waiting on death, with the "no limit" trigger duration, is for good.</summary>
        [Fact]
        public void Only_a_death_row_with_no_limit_outlives_the_round()
        {
            SpellEffect Row(string triggers, int duration, int triggerDuration)
                => new() { EffectId = 792, Triggers = triggers, Duration = duration, TriggerDuration = triggerDuration };

            Assert.Equal(-1, EffectEngine.CaducidadDeLaFila(Row("X", 0, 63), 4, false));
            Assert.Equal(5, EffectEngine.CaducidadDeLaFila(Row("TB", 0, 63), 4, false));   // a turn row keeps its reading
            Assert.Equal(5, EffectEngine.CaducidadDeLaFila(Row("X", 0, 1), 4, false));     // a real round count too
            Assert.Equal(6, EffectEngine.CaducidadDeLaFila(Row("X", 2, 63), 4, false));    // and a duration of its own
            Assert.Equal(-1, EffectEngine.CaducidadDeLaFila(Row("TB", 0, 63), 4, true));   // a behaviour spell's still is
        }
    }
}
