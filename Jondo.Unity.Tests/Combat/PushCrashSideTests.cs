using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A push's crash is a blow from next door when the pushed one never moved -- a fighter right
    /// behind him -- and a blow from afar when he travelled a cell or more before it: by then he
    /// is no longer beside whoever pushed him. The Feca's Barricada (melee invulnerability) spares
    /// the first, Bastión (ranged) the second. And a vitality malus never kills; a character on
    /// the other side is drawn as the other side.
    /// </summary>
    public class PushCrashSideTests
    {
        private const int Bastion = 12995, Barricada = 12979, UltimoAliento = 13446;

        /// <summary>Cells along one line: the pusher, the pushed, and what lies behind him.</summary>
        private static int[] Row(int from, int count)
        {
            var (x, y) = MapGeometry.CellToPoint(from);
            return Enumerable.Range(0, count).Select(i => MapGeometry.PointToCell(x + i, y)).ToArray();
        }

        private static async Task<int> CrashTaken(int shield, int wallAt)
        {
            var fight = new FightInstance(1, 1);
            var cells = Row(300, 6);
            var feca = new Fighter { Id = 1, Breed = 1, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var ally = new Fighter { Id = 2, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var wall = new Fighter { Id = 3, Breed = 3, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = 4, Breed = 5, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, PushDamage = 50 };
            fight.AddPlayer(feca); fight.AddPlayer(ally); fight.AddPlayer(wall); fight.AddOpponent(foe);
            foe.CellId = cells[0]; ally.CellId = cells[1]; wall.CellId = cells[wallAt];
            feca.CellId = MapGeometry.PointToCell(MapGeometry.CellToPoint(300).X, MapGeometry.CellToPoint(300).Y + 3);
            // The Feca well out of the push's line.

            await FightHandler.CastEffectsAsync(null, fight, feca, shield, 3, ally, ally.CellId, false, EffectEngine.EfectosSorteados(shield, 3, false));
            int before = ally.CurrentHP;
            var push = new SpellEffect { EffectId = EffectSupport.Push, EffectUid = 990101, DiceNum = 4, TargetMask = "a,A", Forma = 'P', Tamano = 1 };
            await FightHandler.AplicarEfectosAsync(null, fight, foe, 1, 1, ally, EffectEngine.AlLanzar, ally.CellId, tirada: new[] { push });
            return before - ally.CurrentHP;
        }

        [Fact]
        public async Task Blocked_where_he_stands_the_crash_is_melee()
        {
            // The wall right behind him: he never moves.
            Assert.True(await CrashTaken(Bastion, wallAt: 2) > 0, "Bastión (ranged) spared a crash from next door");
            Assert.Equal(0, await CrashTaken(Barricada, wallAt: 2));
        }

        [Fact]
        public async Task Pushed_a_cell_before_the_crash_it_is_ranged()
        {
            // A free cell, then the wall: he travels one cell and crashes away from the pusher.
            Assert.Equal(0, await CrashTaken(Bastion, wallAt: 3));
            Assert.True(await CrashTaken(Barricada, wallAt: 3) > 0, "Barricada (melee) spared a crash after travelling");
        }

        [Fact]
        public async Task Ultimo_Aliento_never_kills_its_caster()
        {
            var fight = new FightInstance(1, 1);
            var tymador = new Fighter { Id = 1, Breed = 13, CellId = 300, MaxHP = 4000, CurrentHP = 300, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = 2, Breed = 8, CellId = 330, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(tymador); fight.AddOpponent(foe);
            tymador.CellId = 300;

            await FightHandler.CastEffectsAsync(null, fight, tymador, UltimoAliento, 2, tymador, tymador.CellId, false,
                                                EffectEngine.EfectosSorteados(UltimoAliento, 2, false));

            Assert.True(tymador.IsAlive, "Último Aliento killed its caster");
            Assert.True(tymador.MaxHP < 4000, "no vitality taken");
            Assert.True(tymador.CurrentHP >= 1 && tymador.CurrentHP <= tymador.MaxHP);
        }

        [Fact]
        public void A_character_of_the_other_side_is_told_as_the_other_side()
        {
            var fight = new FightInstance(1, 1);
            var blue = new Fighter { Id = 1, Breed = 8, IsBot = true, CellId = 300, MaxHP = 4000, CurrentHP = 4000, Level = 200 };
            var red = new Fighter { Id = 2, Breed = 5, IsBot = true, CellId = 330, MaxHP = 4000, CurrentHP = 4000, Level = 200 };
            fight.AddPlayer(blue); fight.AddOpponent(red);

            long Side(Fighter f)
            {
                var top = ProtoMessage.Parse(FightHandler.BloqueDe(fight, f).Build()).Fields;
                var body = ProtoMessage.Parse(top.Single(x => x.FieldNumber == 2).BytesValue).Fields;
                var fighter = ProtoMessage.Parse(body.Single(x => x.FieldNumber == 2).BytesValue).Fields;
                var again = ProtoMessage.Parse(fighter.Single(x => x.FieldNumber == 7).BytesValue).Fields;
                return again.FirstOrDefault(x => x.FieldNumber == 2 && x.WireType == 0)?.VarIntValue ?? 0;
            }
            Assert.Equal(0, Side(blue));
            Assert.Equal(1, Side(red));
        }
    }
}
