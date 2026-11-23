using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Minotot's Audacia (23918). Grade 1 puts 3501 on him, and its "792 on H" hands each
    /// enemy player grade 3. That grade's TE row casts grade 4 when the player ends his turn.
    /// Grade 4's 2160 picks the nearest Tofu (5502) and casts grade 5, whose 1023 swaps the
    /// player with that Tofu. The hook is his for the whole fight: the Tofus only come in round 2.
    /// </summary>
    [Collection("MapManager")]
    public class MinototAudaciaTests
    {
        private const int Audacia = 23918, Minotot = 827, Tofu = 5502;

        [Fact]
        public async Task The_player_swaps_with_the_nearest_tofu_at_his_turn_end()
        {
            if (!SpellEffects.De(Audacia, 5).Any(row => row.EffectId == 1023)) return;   // no spell data
            var player = new Fighter
            {
                Id = 9_970_827, Name = "P", CellId = 250, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var boss = new Fighter
            {
                Id = -1, Name = "Minotot", IsMonster = true, CellId = 400, Level = 200, MaxHP = 20000,
                CurrentHP = 20000, MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4, MonsterId = Minotot,
            };
            var fight = new FightInstance(827, 990_000_827);
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            Assert.Equal(1, ExpeditionModifiers.Give(fight, new ExpeditionModifiers.Modifier(167, Minotot, Audacia, "audacia"),
                                                     new[] { Minotot }));
            fight.StartFight();
            await FightHandler.AplicarEfectosAsync(null, fight, boss, Audacia, 1, boss,
                                                   EffectEngine.AlLanzar, boss.CellId, armar: false,
                                                   conducta: true);   // as the attitude cascade casts it
            // The Tofus come from his 11458 in round 2 on; the hook must outlive round 1.
            for (int round = fight.RoundNumber, t = 0; t < 10 && fight.RoundNumber == round; t++) fight.NextTurn();
            var near = await FightHandler.SummonAsync(null, fight, boss, Tofu, 1, 278);
            var far = await FightHandler.SummonAsync(null, fight, boss, Tofu, 1, 480);
            Assert.NotNull(near);
            Assert.NotNull(far);

            await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlAcabarElTurno);
            foreach (var t in new[] { near!, far! }) { t.Buffs.PonerEstado(96); t.Buffs.PonerEstado(56); }
            int tofuCell = near!.CellId, playerCell = player.CellId;
            await FightHandler.EngancheAsync(null, fight, player, EffectEngine.AlAcabarElTurno);

            Assert.Equal(tofuCell, player.CellId);
            Assert.Equal(playerCell, near.CellId);
        }
    }
}
