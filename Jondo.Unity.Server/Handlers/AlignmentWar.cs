using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The war between the cities in the field: an aggression between Bontarians and Brakmarians,
    /// settled in honour (<see cref="Honour"/>) when it ends, and the head hunts it closes
    /// (<see cref="HeadHunts"/>).
    /// </summary>
    /// <remarks>
    /// The settlement is worked out before the end screen goes out, so that its honour column
    /// carries it (<see cref="ResultOf"/>), and told to each one after, with the client's own
    /// messages, his sheet and his wings drawn again: a new grade changes them. An aggression in a
    /// running conquest's zone belongs to the conquest and gives no honour.
    /// </remarks>
    public static class AlignmentWar
    {
        private sealed record Settled(FightProtocol.HonourResult Result, Honour.Change Change, int GradeBefore);

        private static readonly ConcurrentDictionary<long, Dictionary<long, Settled>> _settled = new();

        /// <summary>When each winner last beat each loser, for «Hace poco luchaste con honra».</summary>
        private static readonly ConcurrentDictionary<(long Winner, long Loser), DateTime> _beaten = new();

        /// <summary>Whether this fight is an aggression of the cities' war: no conquest in its zone.</summary>
        public static bool IsCitiesWar(FightInstance fight)
            => fight.Reglas == FightRules.Agresion
               && Conquests.In(MapManager.GetMapInfo(fight.RoleplayMapId)?.SubAreaId ?? 0) == null;

        /// <summary>
        /// Works the fight's honour out and keeps it, before anybody is told: called with the
        /// fight's rewards, ahead of the end screen.
        /// </summary>
        public static void Plan(FightInstance fight, bool attackersWon)
        {
            if (!IsCitiesWar(fight)) return;
            var winners = Sworn(attackersWon ? fight.Azul : fight.Rojo);
            var losers = Sworn(attackersWon ? fight.Rojo : fight.Azul);
            // Honour is between the two cities: losers of the winners' own city do not count.
            int winnersSide = winners.Select(w => w.Side).FirstOrDefault();
            losers = losers.Where(l => l.Side != winnersSide).ToList();

            var now = DateTime.UtcNow;
            var changes = Honour.Settle(winners, losers, attackersWon,
                (winner, loser) => _beaten.TryGetValue((winner, loser), out var when) && now - when < Honour.Lately);
            foreach (var winner in winners)
                foreach (var loser in losers) _beaten[(winner.Id, loser.Id)] = now;

            var settled = new Dictionary<long, Settled>();
            foreach (var fighter in winners.Concat(losers))
            {
                var change = changes.FirstOrDefault(c => c.Id == fighter.Id) ?? new Honour.Change(fighter.Id, 0, 0, 0, 0);
                int before = Alignments.Of(fighter.Id).HonourGrade;
                var after = Alignments.AddHonour(fighter.Id, change.Honour, change.Dishonour);
                int grade = Math.Max(1, after.HonourGrade);
                settled[fighter.Id] = new Settled(
                    new FightProtocol.HonourResult(grade, Honour.FloorOf(grade), Honour.CeilingOf(grade), after.Honour, change.Honour),
                    change, before);
            }
            _settled[fight.FightId] = settled;
            if (changes.Count > 0)
                Console.WriteLine($"[Alignment] Fight #{fight.FightId}: " +
                                  string.Join(", ", changes.Select(c => $"{c.Id} {c.Honour:+0;-0;0} honour {c.Dishonour:+0;-0;0} dishonour")) + ".");
        }

        /// <summary>One fighter's honour for the end screen, or null when the fight gave none.</summary>
        public static FightProtocol.HonourResult? ResultOf(FightInstance fight, long fighter)
            => _settled.TryGetValue(fight.FightId, out var settled) && settled.TryGetValue(fighter, out var s) ? s.Result : null;

        /// <summary>
        /// After the end screen: each one is told what he won or lost, his sheet sent again, and his
        /// look when his grade -- his wings -- changed; then the head hunts the fight closed.
        /// </summary>
        public static async Task AnnounceAsync(FightInstance fight, bool attackersWon)
        {
            if (_settled.TryRemove(fight.FightId, out var settled))
            {
                foreach (var (id, s) in settled)
                {
                    var session = SessionRegistry.FindByCharacter(id);
                    if (session == null) continue;
                    using (SessionContext.Push(session))
                    {
                        if (s.Change.Message != 0)
                            await session.SendAsync(ConnectionProtocol.Push(Op.Lqn,
                                ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, s.Change.Message, s.Change.Amount.ToString())));
                        await session.SendAsync(ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
                        if (Alignments.Of(id).HonourGrade != s.GradeBefore && session.IsInWorld)
                            await SlotLookHandler.RedrawAsync(session.Stream);
                    }
                }
            }
            if (IsCitiesWar(fight)) await HeadHunts.AggressionEndedAsync(fight, attackersWon);
        }

        private static List<Honour.Fighter> Sworn(IEnumerable<Fighter> side)
            => side.Where(f => !f.IsMonster && !f.EsInvocado && !f.IsBot && f.Id > 0)
                   .Select(f => (Fighter: f, Alignment: Alignments.Of(f.Id)))
                   .Where(f => f.Alignment.Sworn)
                   .Select(f => new Honour.Fighter(f.Fighter.Id, f.Fighter.Level, f.Alignment.Side, f.Alignment.Honour, f.Alignment.Dishonour))
                   .ToList();
    }
}
