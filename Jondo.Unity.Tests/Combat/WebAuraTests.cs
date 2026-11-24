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
    /// The Web (3999) of the Rac Queen's lair lays 5507: an aura glyph (1091) of 5508 grade 1, a ring of 2.
    /// Whoever is in it gets state 7 (heaviness: no swaps) and 157, loses all his MP, and nothing
    /// is to move him out of it (5504's push, 5505's teleport) while he stands there.
    /// </summary>
    [Collection("MapManager")]
    public class WebAuraTests
    {
        private const int Web = 3999, Lay = 5507, Aura = 5508, Push = 5504, Teleport = 5505;
        private const int WebCell = 300;

        private static Fighter Monster(long id, int monsterId, int cell) => new()
        {
            Id = id, Name = "M", IsMonster = true, CellId = cell, Level = 150, MaxHP = 9000,
            CurrentHP = 9000, TeamId = 1, MaxAP = 11, CurrentAP = 11, MaxMP = 4, CurrentMP = 4,
            MonsterId = monsterId,
        };

        private static (FightInstance fight, Fighter player, Fighter web, Fighter queen) Board(int playerCell)
        {
            var player = new Fighter
            {
                Id = 9_970_931, Name = "P", CellId = playerCell, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var web = Monster(-1, Web, WebCell);
            var queen = Monster(-2, 3994, 700);
            var fight = new FightInstance(89, 990_000_089);
            fight.AddPlayer(player);
            fight.AddMonster(web);
            fight.AddMonster(queen);
            player.CellId = playerCell; web.CellId = WebCell; queen.CellId = 700;
            fight.StartFight();
            return (fight, player, web, queen);
        }

        private static bool HasSpellData() => SpellEffects.De(Aura, 1).Any(r => r.EffectId == 950);

        private static async Task LayTheWeb(FightInstance fight, Fighter web)
            => await FightHandler.AplicarEfectosAsync(null, fight, web, Lay, 1, web, EffectEngine.AlLanzar, web.CellId);

        private static int CellAt(int distance) => Enumerable.Range(0, 560)
            .First(c => MapGeometry.Distance(c, WebCell) == distance && c > WebCell);

        [Fact]
        public async Task Stepping_in_catches_and_leaving_lets_go()
        {
            if (!HasSpellData()) return;
            int inside = CellAt(2), outside = 560 - 1;
            var (fight, p, web, _) = Board(outside);
            await LayTheWeb(fight, web);
            Assert.True(fight.Glifos.Any(g => g.Tipo == 1091 && g.Hechizo == Aura), "the aura glyph is laid");
            Assert.False(p.Buffs.TieneEstado(157));
            p.CellId = inside;
            await FightHandler.DispararLosGlifosAsync(null, fight, p, alPisar: true);
            Assert.True(p.Buffs.TieneEstado(7), "state 7 on stepping in");
            Assert.True(p.Buffs.TieneEstado(157), "state 157 on stepping in");
            Assert.Equal(0, p.CurrentMP);   // the -100 MP row (169) takes his whole MP pool
            p.CellId = outside;
            await FightHandler.DispararLosGlifosAsync(null, fight, p, alPisar: true);
            Assert.False(p.Buffs.TieneEstado(7), "state 7 gone on leaving");
            Assert.False(p.Buffs.TieneEstado(157), "state 157 gone on leaving");
        }

        [Fact]
        public async Task Standing_where_it_is_laid_is_caught()
        {
            if (!HasSpellData()) return;
            var (fight, p, web, _) = Board(CellAt(1));
            await LayTheWeb(fight, web);
            Assert.True(p.Buffs.TieneEstado(7));
            Assert.True(p.Buffs.TieneEstado(157));
        }

        [Fact]
        public async Task Mp_is_taken_in_the_aura()
        {
            if (!HasSpellData()) return;
            var (fight, p, web, _) = Board(CellAt(2));
            Assert.Equal(6, p.CurrentMP);
            await LayTheWeb(fight, web);
            Assert.Equal(0, p.CurrentMP);
        }

        // Both spells move a free fighter on this board (the controls), and neither moves one in the web.
        [Fact]
        public async Task A_push_and_a_teleport_leave_him_where_he_is()
        {
            if (!HasSpellData()) return;
            int pc = CellAt(2);
            foreach (int spell in new[] { Push, Teleport })
            {
                // A caster cell and an aim that move a free fighter standing on pc (the control).
                int queenCell = -1, aim = -1;
                for (int qc = 250; qc < 400 && queenCell < 0; qc++)
                    foreach (int a in new[] { pc, pc + 1, pc - 1, pc + 14, pc - 14, pc + 15, pc - 15, pc + 28, pc - 28 })
                    {
                        if (qc == pc || qc == WebCell) continue;
                        var (free, p0, _, q0) = Board(pc);
                        q0.CellId = qc;
                        await FightHandler.AplicarEfectosAsync(null, free, q0, spell, 1, p0, EffectEngine.AlLanzar, a);
                        if (p0.CellId != pc) { queenCell = qc; aim = a; break; }
                    }
                Assert.True(queenCell >= 0, $"control: spell {spell} moves a free fighter on {pc}");

                var (fight, p, web, q) = Board(pc);
                q.CellId = queenCell;
                await LayTheWeb(fight, web);
                Assert.True(p.Buffs.TieneEstado(7) && p.Buffs.TieneEstado(157), "caught as it is laid");
                await FightHandler.AplicarEfectosAsync(null, fight, q, spell, 1, p, EffectEngine.AlLanzar, aim);
                Assert.Equal(pc, p.CellId);
            }
        }

        // Pushed, or teleported, from outside into the ring: the displacement fires the aura as well.
        [Fact]
        public async Task Being_pushed_or_teleported_into_the_aura_catches()
        {
            if (!HasSpellData()) return;
            var cases = new System.Collections.Generic.Dictionary<int, int>();
            foreach (int spell in new[] { Push, Teleport })
            {
                bool done = false;
                for (int pc = 250; pc < 400 && !done; pc++)
                {
                    if (MapGeometry.Distance(pc, WebCell) <= 2) continue;
                    for (int qc = 250; qc < 400 && !done; qc += 7)
                        foreach (int aim in new[] { pc, pc + 1, pc - 1, pc + 14, pc - 14, pc + 15, pc - 15, pc + 28, pc - 28 })
                        {
                            if (qc == pc || qc == WebCell || aim < 0 || aim > 559) continue;
                            var (fight, p, web, q) = Board(pc);
                            q.CellId = qc;
                            await LayTheWeb(fight, web);
                            if (p.Buffs.TieneEstado(157)) continue;
                            await FightHandler.AplicarEfectosAsync(null, fight, q, spell, 1, p, EffectEngine.AlLanzar, aim);
                            if (p.CellId == pc || !fight.Glifos.Any(g => g.Tipo == 1091 && g.Cubre(p.CellId))) continue;
                            Assert.True(p.Buffs.TieneEstado(7), $"spell {spell}: {pc}->{p.CellId} state 7");
                            Assert.True(p.Buffs.TieneEstado(157), $"spell {spell}: {pc}->{p.CellId} state 157");
                            Assert.Equal(0, p.CurrentMP);
                            done = true;
                            break;
                        }
                }
                cases[spell] = done ? 1 : 0;
            }
            Assert.True(cases[Push] == 1, "a push that ends inside the aura was found");
            Assert.True(cases[Teleport] == 1, "a teleport that ends inside the aura was found");
        }
    }
}
