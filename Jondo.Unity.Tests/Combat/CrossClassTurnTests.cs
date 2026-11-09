using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Characters of every class fighting each other turn by turn, through the server's own doors:
    /// each casts as his client does (a jwh, so that AP, reach, line of sight, cooldowns and
    /// criteria are the server's to judge), passes his turn, and the next one's turn is confirmed
    /// as the client confirms it -- the turn starts and ends with their expiries, poisons, glyphs
    /// and turn triggers. After every cast and every turn, the rules every fight must keep
    /// (FightInvariants), the expired rows among them.
    /// </summary>
    /// <remarks>
    /// JONDO_CROSS_TURN_FIGHTS raises the number of fights for a long local run.
    /// </remarks>
    [Collection("MapManager")]
    public class CrossClassTurnTests
    {
        private static readonly int[] Breeds = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20 };
        private const long Map = 191105026;

        private static readonly ConcurrentDictionary<long, FightInstance> Active =
            (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private readonly ITestOutputHelper _out;
        public CrossClassTurnTests(ITestOutputHelper output) { _out = output; }

        private static Fighter Character(long id, string name, int breed, int cell)
        {
            var f = new Fighter
            {
                Id = id, Name = name, Breed = breed, Level = 200, CellId = cell,
                MaxHP = Life, CurrentHP = Life, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
                Strength = 400, Intelligence = 400, Chance = 400, Agility = 400, Initiative = 1000 + (int)(id % 97),
            };
            var known = SpellTable.PairsOf(breed)
                .SelectMany(p => new[] { (p.Base, SpellTable.GradeFor(p.Base, 200)), (p.Variant, SpellTable.GradeFor(p.Variant, 200)) })
                .Where(s => s.Item1 > 0 && s.Item2 > 0).ToList();
            int passive = ClassPassives.PassiveOf(breed);
            foreach (var (spell, grade) in ClassPassives.ForFight(breed, known))
                if (spell != passive) f.Buffs.PonerActitud(spell, grade);
            if (passive != 0) f.Buffs.PonerActitud(passive, SpellTable.PassiveGrade(passive, 200));
            return f;
        }

        private static List<(int Spell, int Grade)> SpellsOf(Fighter f)
            => f.EsInvocado
                ? f.HechizosDeInvocado.Select(h => (h.Spell, Math.Max(1, h.Grade))).ToList()
                : SpellTable.PairsOf(f.Breed)
                    .SelectMany(p => new[] { (p.Base, SpellTable.GradeFor(p.Base, 200)), (p.Variant, SpellTable.GradeFor(p.Variant, 200)) })
                    .Where(s => s.Item1 > 0 && s.Item2 > 0).ToList();

        /// <summary>A cell the spell may be aimed at: mostly a fighter's, else one near the caster.</summary>
        private static int Aim(Random rng, FightInstance fight, Fighter caster, int spell, int grade, List<int> walk)
        {
            var limits = FightHandler.LimitesDeGrado(spell, grade);
            int max = Math.Max(limits.AlcanceMaximo, limits.AlcanceMinimo);
            var alive = fight.Azul.Concat(fight.Rojo).Where(f => f != null && f.IsAlive).ToList();
            var near = alive.Where(f => MapGeometry.Distance(caster.CellId, f.CellId) <= Math.Max(1, max)).ToList();
            if (near.Count > 0 && rng.Next(10) < 7) return near[rng.Next(near.Count)].CellId;
            var cells = walk.Where(c => MapGeometry.Distance(caster.CellId, c) <= Math.Max(1, max)).ToList();
            return cells.Count == 0 ? caster.CellId : cells[rng.Next(cells.Count)];
        }

        /// <summary>
        /// Still being fought: not over, and not waiting for the client to have seen the blow that
        /// ended it (FinPendiente), which this test does not send.
        /// </summary>
        private static bool Going(FightInstance fight) => fight.State == FightState.Ongoing && fight.FinPendiente == 0;

        /// <summary>JONDO_CROSS_LIFE: the characters' life, lower for fights that end.</summary>
        private static readonly int Life = int.TryParse(Environment.GetEnvironmentVariable("JONDO_CROSS_LIFE"), out int l) && l > 0 ? l : 4000;

        private int _casts, _landed, _turns, _deaths, _fightsEnded, _maxRound;

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task Classes_fighting_turn_by_turn_keep_every_rule(int seed)
        {
            int fights = int.TryParse(Environment.GetEnvironmentVariable("JONDO_CROSS_TURN_FIGHTS"), out int many) && many > 0 ? many : 2;
            if (!MapManager.WalkableCells.ContainsKey(Map)) MapManager.Initialize();
            var walk = MapManager.WalkableCells[Map].OrderBy(c => c).ToList();
            var rng = new Random(seed);
            long account = 8_997_200_000 + seed;
            // JONDO_CROSS_LOG: the server's whole log of the run into that file.
            var logTo = Environment.GetEnvironmentVariable("JONDO_CROSS_LOG");
            var console = Console.Out;
            System.IO.StreamWriter log = null;
            if (!string.IsNullOrEmpty(logTo)) { log = new System.IO.StreamWriter(logTo + $".{seed}.txt") { AutoFlush = true }; Console.SetOut(log); }

            var ids = new long[6];
            var names = new string[6];
            var pipes = new ClientPipe[6];
            using var stop = new CancellationTokenSource();
            var drains = new List<Task>();
            var broken = new List<string>();
            try
            {
                for (int i = 0; i < 6; i++)
                {
                    names[i] = $"Cruce-{seed}-{i}-{Environment.ProcessId % 1000}";
                    ids[i] = DatabaseManager.CreateCharacter(account, 1, names[i], 1, 0, 1, new long[] { -1, -1, -1, -1, -1 }, Map, 200, 0, 0, Array.Empty<(int, int)>());
                    Assert.True(ids[i] > 0, $"character {names[i]} not created");
                    pipes[i] = await ClientPipe.OpenAsync(account, ids[i], Map, names[i]);
                    pipes[i].Session.State.CharacterLevel = 200;
                    var stream = pipes[i].FromServer;
                    // What the server tells them is read and dropped, so that its writes never wait.
                    drains.Add(Task.Run(async () =>
                    {
                        try { while (!stop.IsCancellationRequested && await Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream) != null) { } }
                        catch { }
                    }));
                }

                for (int n = 0; n < fights; n++)
                    broken.AddRange(await OneFight(rng, n, ids, names, pipes, walk));
            }
            finally
            {
                stop.Cancel();
                if (log != null) { Console.SetOut(console); log.Dispose(); }
                foreach (var pipe in pipes.Where(p => p != null)) await pipe.DisposeAsync();
                foreach (var id in ids.Where(i => i > 0)) DatabaseManager.DeleteCharacter(id, account);
            }

            _out.WriteLine($"{fights} fights ({_fightsEnded} fought to the end, rounds up to {_maxRound}), {_turns} turns, {_casts} casts tried, {_landed} landed, {_deaths} deaths");
            foreach (var group in broken.GroupBy(b => string.Join(" | ", b.Split(" | ").Take(2))).OrderByDescending(g => g.Count()))
                foreach (var one in group.Take(3)) _out.WriteLine($"{group.Count(),4}x {group.Key}\n      e.g. {one.Split(" | ", 3)[2]}");
            Assert.Empty(broken);
        }

        private async Task<List<string>> OneFight(Random rng, int n, long[] ids, string[] names, ClientPipe[] pipes, List<int> walk)
        {
            var broken = new List<string>();
            var breeds = Breeds.OrderBy(_ => rng.Next()).Take(6).ToArray();
            int centre = walk[walk.Count / 2];
            var cells = walk.Where(c => MapGeometry.Distance(centre, c) <= 5).OrderBy(_ => rng.Next()).ToList();
            var fight = new FightInstance(ids[0] + n * 1000 + rng.Next(999), Map);
            for (int i = 0; i < 6; i++)
            {
                var f = Character(ids[i], names[i], breeds[i], cells[i]);
                if (i < 3) fight.AddPlayer(f); else fight.AddOpponent(f);
                f.CellId = cells[i];
                pipes[i].Session.State.Breed = breeds[i];
            }
            string lineUp = string.Join(" ", breeds.Take(3)) + " vs " + string.Join(" ", breeds.Skip(3));
            var told = new HashSet<(string, long)>();
            void Note(string what, int spell, IEnumerable<FightInvariants.Violation> found)
            {
                foreach (var v in found)
                    if (told.Add((v.Rule, v.Fighter)) || v.Rule is "hurt-while-invulnerable" or "moved-while-unmovable" or "healed-while-incurable")
                        broken.Add($"{v.Rule} | {spell} | fight {n} ({lineUp}) round {fight.RoundNumber}: {what} -> {v}");
            }
            ClientPipe PipeOf(Fighter f)
            {
                long who = f.EsInvocado ? f.Invocador : f.Id;
                int at = Array.IndexOf(ids, who);
                return at >= 0 ? pipes[at] : pipes[0];
            }

            // Every turn that starts is confirmed, as the client confirms each jzc: one the server
            // plays by itself -- a turret's -- passes on to the next, which waits for its own.
            async Task ConfirmTurns()
            {
                for (int i = 0; i < 20 && Going(fight); i++)
                {
                    var cur = fight.CurrentFighter;
                    if (cur == null || fight.LastTurnBegun == (fight.RoundNumber, cur.Id)) return;
                    using (SessionContext.Push(PipeOf(cur).Session)) await FightHandler.ConfirmAsync(PipeOf(cur).ToClient);
                }
            }

            Active[fight.FightId] = fight;
            try
            {
                fight.StartFight();
                await ConfirmTurns();
                for (int turn = 0; turn < 150 && Going(fight); turn++)
                {
                    var current = fight.CurrentFighter;
                    if (current == null) break;
                    var pipe = PipeOf(current);
                    using (SessionContext.Push(pipe.Session))
                    {
                        if (current.IsAlive && (!current.EsInvocado || current.HechizosDeInvocado.Count > 0))
                        {
                            var spells = SpellsOf(current);
                            for (int c = 0, tries = rng.Next(3, 7); c < tries && spells.Count > 0 && current.IsAlive && Going(fight) && fight.CurrentFighter == current; c++)
                            {
                                var (spell, grade) = spells[rng.Next(spells.Count)];
                                int cell = Aim(rng, fight, current, spell, grade, walk);
                                if (cell == 0) continue;
                                var before = FightInvariants.Snapshot(fight, current, spell);
                                int alive = fight.Azul.Concat(fight.Rojo).Count(f => f.IsAlive);
                                int apBefore = current.CurrentAP;
                                try
                                {
                                    await FightHandler.CastAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Jwh, Pb.New().Var(1, cell).Var(4, spell).Build()));
                                }
                                catch (Exception ex) { broken.Add($"exception | {spell} | fight {n} ({lineUp}): {current.Id} casts {spell} at {cell}: {ex.GetType().Name}: {ex.Message}"); continue; }
                                _casts++;
                                if (current.CurrentAP < apBefore) _landed++;
                                _deaths += Math.Max(0, alive - fight.Azul.Concat(fight.Rojo).Count(f => f.IsAlive));
                                Note($"{current.Id} (breed {current.Breed}{(current.EsInvocado ? $", summon {current.MonsterId}" : "")}) casts {spell}/{grade} at cell {cell}", spell,
                                     FightInvariants.Check(fight, withTime: true).Concat(FightInvariants.AfterCast(fight, before)));
                            }
                        }
                        if (!Going(fight)) break;
                        if (fight.CurrentFighter == current) await FightHandler.PassTurnAsync(pipe.ToClient);
                    }
                    if (!Going(fight)) break;
                    await ConfirmTurns();
                    var next = fight.CurrentFighter;
                    if (next == null) break;
                    _turns++;
                    Note($"the turn passes to {next.Id}", 0, FightInvariants.Check(fight, withTime: true));
                }
                if (!Going(fight)) _fightsEnded++;
                if (n < 6)
                    _out.WriteLine($"fight {n} ({lineUp}): {fight.State}, round {fight.RoundNumber}; blue " +
                                   string.Join(" ", fight.Azul.Where(f => !f.EsInvocado).Select(f => $"{f.Id % 100}:{f.CurrentHP}/{f.MaxHP}")) + "; red " +
                                   string.Join(" ", fight.Rojo.Where(f => !f.EsInvocado).Select(f => $"{f.Id % 100}:{f.CurrentHP}/{f.MaxHP}")));
                _maxRound = Math.Max(_maxRound, fight.RoundNumber);
            }
            catch (Exception ex)
            {
                broken.Add($"exception | 0 | fight {n} ({lineUp}) round {fight.RoundNumber}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                Active.TryRemove(fight.FightId, out _);
            }
            return broken;
        }
    }
}
