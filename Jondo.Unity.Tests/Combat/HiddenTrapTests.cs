using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A Sram's trap is his side's to see until it goes off: «los adversarios podrán ver…
    /// casillas donde se active una trampa». The other side is shown it where it goes off,
    /// before it does -- told to go off unseen, the enemy's client "tries to trigger an unknown
    /// mark" -- and one that never goes off is taken away only from whoever knew it.
    /// </summary>
    public class HiddenTrapTests
    {
        private const long Sram = 54000000081, Foe = 54000000082;
        private const int TrampaMiserable = 12931, Invisibilidad = 12913;
        private const long Visibility = Jondo.Unity.World.Combat.EffectSupport.Visibility;

        private static long Action(byte[] payload)
            => ProtoMessage.Parse(payload).Fields.FirstOrDefault(f => f.FieldNumber == 14 && f.WireType == 0)?.VarIntValue ?? 0;

        private static List<long> Actions(IEnumerable<(string Op, byte[] Payload)> frames)
            => frames.Where(f => f.Op == "jwe").Select(f => Action(f.Payload)).ToList();

        private static (FightInstance Fight, Fighter Sram, Fighter Foe, int Trap) Board()
        {
            var fight = new FightInstance(Sram, 1);
            var sram = new Fighter { Id = Sram, Breed = 4, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = Foe, Breed = 8, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(sram); fight.AddOpponent(foe);
            var (x, y) = MapGeometry.CellToPoint(300);
            sram.CellId = 300;
            foe.CellId = MapGeometry.PointToCell(x + 3, y);
            return (fight, sram, foe, MapGeometry.PointToCell(x + 2, y));
        }

        [Fact]
        public async Task The_other_side_sees_the_trap_only_where_it_goes_off()
        {
            var (fight, sram, foe, trap) = Board();
            await using var mine = await PortalTests.Wire.Open(Sram);
            await using var theirs = await PortalTests.Wire.Open(Foe);

            await FightHandler.AplicarEfectosAsync(null, fight, sram, TrampaMiserable, 2, null, EffectEngine.AlLanzar, trap);
            Assert.Contains(FightProtocol.PlacedTrap, Actions(await mine.Drain()));
            Assert.DoesNotContain(FightProtocol.PlacedTrap, Actions(await theirs.Drain()));

            // As the move handler does: the walk, then whatever lies where he ended up.
            await FightHandler.WalkPathAsync(fight, foe, new[] { foe.CellId, trap });
            await FightHandler.DispararLosGlifosAsync(theirs.Session.Stream, fight, foe, alPisar: true, skipWalls: true);
            var seen = Actions(await theirs.Drain());
            int shown = seen.IndexOf(FightProtocol.PlacedTrap), fired = seen.IndexOf(FightProtocol.EnteredGlyph);
            Assert.True(fired >= 0, "the trap did not go off");
            Assert.True(shown >= 0 && shown < fired, $"the trap went off unseen on the other side: {string.Join(", ", seen)}");
        }

        /// <summary>
        /// Invisible, the Sram is a ghost to his own side (the client's 1) and nothing at all to the
        /// other (0): told the ghost too, the enemy's client drew him walking about.
        /// </summary>
        [Fact]
        public async Task Invisible_he_is_a_ghost_to_his_side_and_unseen_by_the_other()
        {
            var (fight, sram, _, _) = Board();
            await using var mine = await PortalTests.Wire.Open(Sram);
            await using var theirs = await PortalTests.Wire.Open(Foe);

            await FightHandler.CastEffectsAsync(null, fight, sram, Invisibilidad, 1, sram, sram.CellId, false,
                                                EffectEngine.EfectosSorteados(Invisibilidad, 1, false));

            long StateTold(List<(string Op, byte[] Payload)> frames)
            {
                var jwe = frames.Where(f => f.Op == "jwe" && Action(f.Payload) == Visibility).Select(f => f.Payload).Single();
                var block = ProtoMessage.Parse(jwe).Fields.Single(f => f.FieldNumber == 34).BytesValue;
                return ProtoMessage.Parse(block).Fields.FirstOrDefault(f => f.FieldNumber == 1)?.VarIntValue ?? 0;
            }
            Assert.Equal(FightProtocol.Hidden, StateTold(await mine.Drain()));
            Assert.Equal(FightProtocol.Unseen, StateTold(await theirs.Drain()));
        }

        /// <summary>
        /// A cast that leaves him invisible -- a trap laid -- shows his foes a silhouette on the
        /// cell he cast from (jwe f36), and his own side nothing new.
        /// </summary>
        [Fact]
        public async Task A_trap_laid_unseen_shows_his_foes_where_he_stood()
        {
            var (fight, sram, _, trap) = Board();
            sram.Buffs.Poner(new Buff { EffectId = Jondo.Unity.World.Combat.EffectSupport.Visibility, Disparador = "I", CaducaEnRonda = 5, EmpiezaEnRonda = 0 }, () => 1);
            fight.StartFight();
            await using var mine = await PortalTests.Wire.Open(Sram);
            await using var theirs = await PortalTests.Wire.Open(Foe);
            Assert.Same(sram, fight.CurrentFighter);

            var active = (System.Collections.Concurrent.ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            active[fight.FightId] = fight;
            try
            {
                using (SessionContext.Push(mine.Session))
                    await FightHandler.CastAsync(mine.Session.Stream, ConnectionProtocol.Push(Op.Jwh, Pb.New().Var(1, trap).Var(4, TrampaMiserable).Build()));
            }
            finally { active.TryRemove(fight.FightId, out _); }

            int? Silhouette(List<(string Op, byte[] Payload)> frames)
            {
                var jwe = frames.Where(f => f.Op == "jwe").Select(f => ProtoMessage.Parse(f.Payload).Fields)
                                .FirstOrDefault(fields => fields.Any(x => x.FieldNumber == 36));
                return jwe == null ? null : (int)ProtoMessage.Parse(jwe.Single(x => x.FieldNumber == 36).BytesValue).Fields.Single(x => x.FieldNumber == 1).VarIntValue;
            }
            Assert.Equal(sram.CellId, Silhouette(await theirs.Drain()));
            Assert.Null(Silhouette(await mine.Drain()));
        }

        [Fact]
        public async Task A_trap_that_never_went_off_goes_only_from_its_side()
        {
            var (fight, sram, _, trap) = Board();
            await using var mine = await PortalTests.Wire.Open(Sram);
            await using var theirs = await PortalTests.Wire.Open(Foe);

            await FightHandler.AplicarEfectosAsync(null, fight, sram, TrampaMiserable, 2, null, EffectEngine.AlLanzar, trap);
            await mine.Drain(); await theirs.Drain();

            // The harness's clean board takes every mark off.
            await FightHandler.FreshTurnAsync(mine.Session.Stream, fight, sram, keepBoard: false, withoutSummons: true);
            Assert.Contains(FightProtocol.RemovedGlyph, Actions(await mine.Drain()));
            Assert.DoesNotContain(FightProtocol.RemovedGlyph, Actions(await theirs.Drain()));
        }
    }
}
