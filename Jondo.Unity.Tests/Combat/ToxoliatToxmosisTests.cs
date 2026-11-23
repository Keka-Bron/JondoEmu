using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Toxoliat (3752) answers every blow with Toxmosis (4796): its 1017 casts 4795 on the
    /// attacker, which marks him (state 3481) and leaves a poison (1137) that hurts at the end of
    /// his turn for each MP he used. The mark came, the poison never did.
    /// </summary>
    [Collection("MapManager")]
    public class ToxoliatToxmosisTests
    {
        private const int Toxoliat = 3752, Toxmosis = 4796, Poison = 4795;

        [Fact]
        public async Task A_blow_on_him_leaves_the_per_MP_poison_on_the_attacker()
        {
            if (!SpellEffects.De(Poison, 1).Any(row => row.EffectId == 1137)) return;   // no spell data
            var player = new Fighter
            {
                Id = 9_970_752, Name = "P", CellId = 250, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var boss = new Fighter
            {
                Id = -1, Name = "T", IsMonster = true, Level = 180, MaxHP = 8000, CurrentHP = 8000, TeamId = 1,
                MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, MonsterId = Toxoliat,
            };
            var fight = new FightInstance(Toxoliat, 990_003_752);
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            player.CellId = 250; boss.CellId = 251;
            fight.StartFight();

            boss.Conducta = (Toxmosis, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            Assert.Contains(boss.Buffs.ActiveSpells, s => s.Hechizo == Toxmosis);

            var blow = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);
            await FightHandler.UnGolpeAsync(null, fight, player, 6773, blow, 4, boss, 95, 0, false);
            Assert.True(player.Buffs.TieneEstado(3481));
            int poisonRow = SpellEffects.De(Poison, 1).First(e => e.EffectId == 1137).EffectUid;
            Assert.Contains(player.Buffs.ActiveSpells,
                            s => s.Hechizo == Poison && (s.Filas == null || s.Filas.Contains(poisonRow)));

            // Three MP walked this turn: the poison bites when it ends.
            player.MPAtTurnStart = 6;
            player.CurrentMP = 3;
            int life = player.CurrentHP;
            await FightHandler.EngancheAsync(null, fight, player, EffectEngine.AlAcabarElTurno);
            Assert.True(player.CurrentHP < life, "the per-MP poison did not bite at the end of the turn");
        }
    }
}
