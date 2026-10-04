using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// What a won fight pays, shared among the players who won it: worked out once, for all of
    /// them, before anybody is shown the end.
    /// </summary>
    /// <remarks>
    /// ─── What is measured ─────────────────────────────────────────────────────────────────
    ///
    /// The follow capture ("Combate/entrar a combate con listo automatico y entrada automatica
    /// siguiendo a lider de grupo-combate-victoria") ends two fights won by two players, levels
    /// 447 and 354, and each jyg lists BOTH players' gains -- experience, kamas and items -- to
    /// both of them:
    ///
    ///   one monster     447: 11 xp,  9 kamas, 6903 and 287     354: 25 xp,  4 kamas, 287
    ///   four monsters   447: 343 xp, 30 kamas, 6898 ×2, 6903, 6902   354: 589 xp, 17 kamas, 6903, 6902
    ///
    /// So the items are rolled for each player on his own, the kamas are one sum cut in two
    /// unequal parts, and the experience is shared. Its exact numbers carry what every player
    /// brings of his own -- wisdom, the share given to a mount or a guild, an account's bonus --
    /// which this server does not model for anyone, alone or not.
    ///
    /// ─── What is inferred: the game's own rules ──────────────────────────────────────────
    ///
    /// The shares are Dofus's: the experience grows with the group -- 1, 1.1, 1.5, 2.3, 3.1,
    /// 3.6, 4.2, 4.7 for one to eight players, counting only those at a third of the highest
    /// level or more -- and is cut by level, each player's level counting up to two and a half
    /// times the strongest monster's; the kamas are cut by prospecting. Worked out against what
    /// one player alone would get here, so a player alone gets exactly what he always got.
    /// </remarks>
    public static partial class FightHandler
    {
        /// <summary>What one player takes out of a won fight.</summary>
        internal sealed class Reward
        {
            public long Xp { get; set; }
            public long Kamas { get; set; }
            public Dictionary<int, int> Loot { get; set; } = new();
        }

        /// <summary>The group bonus to experience, by how many players count for it.</summary>
        internal static readonly double[] GroupCoefficients = { 1, 1.1, 1.5, 2.3, 3.1, 3.6, 4.2, 4.7 };

        /// <summary>Each fight's shares, from the moment it is decided to the last player's end.</summary>
        private static readonly ConcurrentDictionary<long, Dictionary<long, Reward>> _rewards = new();

        /// <summary>
        /// One player's experience out of what a player alone would get: the group bonus, then his
        /// part by level -- each level counting up to two and a half times the strongest monster's.
        /// </summary>
        internal static long XpShare(long alone, int level, IReadOnlyList<int> levels, int strongestMonster)
        {
            if (alone <= 0 || levels.Count <= 1) return Math.Max(0, alone);
            int cap = Math.Max(1, (int)Math.Truncate(2.5 * Math.Max(1, strongestMonster)));
            int highest = levels.Max();
            int counting = Math.Clamp(levels.Count(l => l * 3 >= highest), 1, GroupCoefficients.Length);
            double weights = levels.Sum(l => (double)Math.Min(l, cap));
            if (weights <= 0) return 0;
            return (long)Math.Floor(alone * GroupCoefficients[counting - 1] * Math.Min(level, cap) / weights);
        }

        /// <summary>One player's kamas out of the fight's: his prospecting's part of the team's.</summary>
        internal static long KamasShare(long total, int prospecting, IReadOnlyList<int> prospectings)
        {
            if (total <= 0 || prospectings.Count <= 1) return Math.Max(0, total);
            long sum = prospectings.Sum(p => (long)Math.Max(1, p));
            return (long)Math.Floor((double)total * Math.Max(1, prospecting) / sum);
        }

        /// <summary>Prospecting: a hundred, a tenth of chance, and what the gear gives.</summary>
        private static int ProspectingOf(GameSession session)
        {
            using (SessionContext.Push(session))
            {
                Equipment.Bonuses().TryGetValue(ConnectionProtocol.Stat.Prospecting, out long fromGear);
                return 100 + session.State.TotalChance / 10 + (int)fromGear;
            }
        }

        /// <summary>
        /// The shares of a won fight against monsters, for every player who won it: the monsters'
        /// experience and kamas with the challenges' bonus, cut among them, and the items rolled for
        /// each one in his own session. A dream's fights are not cut: in the four-player dream
        /// capture every player gets the same experience and the same items.
        /// </summary>
        /// <remarks>
        /// And a dream's fights pay the dream's loot, not the monsters' (see <see cref="DreamLootOf"/>):
        /// in the jyg of the invitation capture (frame 5572) each of the four players has 17 dream
        /// reflections, one of them a Runa astral legendaria too, and none of them a kama or
        /// anything of the monsters'. The Fin du rêve is won once its minimum of waves has fallen,
        /// whoever is standing at its end -- the client's own words for it, "Una vez pasado el
        /// umbral, la victoria es definitiva" (1178499) -- so its players are paid then even when
        /// the last wave had the better of them.
        /// </remarks>
        private static void PlanRewards(FightInstance fight)
        {
            if (!fight.Reglas.ReparteBotin || fight.Reglas.PagaElKoliseo) return;

            bool won = fight.Azul.Concat(fight.Rojo).Any(f => !f.IsMonster && !f.EsInvocado && fight.HaGanado(f.Id));
            var dream = DreamHandler.PayOf(fight, won);

            var winners = new List<(Fighter Fighter, GameSession Session)>();
            foreach (var fighter in fight.Azul.Concat(fight.Rojo))
            {
                if (fighter.IsMonster || fighter.EsInvocado || fighter.EsIlusion) continue;
                if (!fight.HaGanado(fighter.Id) && dream?.Finished != true) continue;
                var session = SessionRegistry.FindByCharacter(fighter.Id);
                if (session != null) winners.Add((fighter, session));
            }
            if (winners.Count == 0) return;

            var paying = fight.Rojo.Where(m => m.IsMonster && !m.EsInvocado).ToList();
            int extra = ChallengeWatcher.EndBonus(fight, won: true);
            long xpAlone = ConElExtra(paying.Sum(m => (long)m.XpReward), extra);
            long kamasAlone = ConElExtra(paying.Sum(m => 10L + (m.Level * 5L)), extra);
            int strongest = paying.Count == 0 ? 1 : paying.Max(m => m.Level);
            bool shared = !Dreams.IsDreamMap(fight.RoleplayMapId);

            var levels = winners.Select(w => Math.Max(1, w.Session.State.CharacterLevel)).ToList();
            var prospectings = winners.Select(w => ProspectingOf(w.Session)).ToList();

            var plan = new Dictionary<long, Reward>();
            for (int i = 0; i < winners.Count; i++)
            {
                var (fighter, session) = winners[i];
                Dictionary<int, int> loot;
                if (dream != null) loot = DreamLootOf(fight, dream);
                else using (SessionContext.Push(session)) loot = RollLoot(fight, extra);
                plan[fighter.Id] = new Reward
                {
                    Xp = shared ? XpShare(xpAlone, levels[i], levels, strongest) : xpAlone,
                    Kamas = dream != null ? 0 : shared ? KamasShare(kamasAlone, prospectings[i], prospectings) : kamasAlone,
                    Loot = loot,
                };
            }
            _rewards[fight.FightId] = plan;

            if (dream != null)
            {
                Program.LogDebug($"[Sueños] Fight #{fight.FightId} pays {winners.Count} dreamer(s) at a loot bonus of " +
                                 $"{dream.LootBonus}: {Dreams.ReflectionsFor(dream.LootBonus)} reflection(s) each" +
                                 (dream.Finished ? $", {dream.Fragments} dream fragment(s) for {dream.Waves} wave(s)." : "."));
            }

            if (winners.Count > 1)
            {
                Program.LogDebug($"[Combate] Reparto de #{fight.FightId} entre {winners.Count}: " +
                                 string.Join(", ", plan.Select(p => $"{p.Key} {p.Value.Xp} xp {p.Value.Kamas} kamas " +
                                                                    $"{p.Value.Loot.Sum(l => l.Value)} objeto(s)")));
            }
        }

        /// <summary>
        /// A Koliseo fight is over: the ladder moves every player of both sides (see
        /// <see cref="KoliseoLadder"/>). Summons and illusions are nobody's rating.
        /// </summary>
        private static void RecordLadder(FightInstance fight)
        {
            if (!fight.Reglas.PagaElKoliseo || fight.KoliseoMode < 0) return;
            // Against JondoBots it is training: the ladder is not touched.
            if (fight.Todos.Any(f => f.IsBot)) return;
            var players = fight.Azul.Concat(fight.Rojo)
                               .Where(f => !f.IsMonster && !f.EsInvocado && !f.EsIlusion)
                               .ToList();
            var winners = players.Where(f => fight.HaGanado(f.Id)).Select(f => (f.Id, f.Level)).ToList();
            var losers = players.Where(f => !fight.HaGanado(f.Id)).Select(f => (f.Id, f.Level)).ToList();
            var after = KoliseoLadder.Record(fight.KoliseoMode, winners, losers);
            if (after.Count > 0)
            {
                Program.LogDebug($"[Koliseo] Ladder after #{fight.FightId}, mode {fight.KoliseoMode}: " +
                                 string.Join(", ", after.Select(s => $"{s.CharacterId} {s.Rating} " +
                                     (s.Placed ? $"league {s.League}" : $"{s.PlacementLeft} placement left"))));
            }
        }

        /// <summary>
        /// What a dream's fight drops for one of its winners: the dream's loot at the room's loot
        /// bonus (<see cref="Dreams.LootOf"/>), the Jondo coin every monster pays on this server,
        /// and, when the fight finished the dream, the dream fragments of its waves.
        /// </summary>
        private static Dictionary<int, int> DreamLootOf(FightInstance fight, DreamHandler.DreamPay dream)
        {
            var loot = Dreams.LootOf(dream.Difficulty, dream.Row, dream.LootBonus, TirarPorcentaje);
            void Add(int item, int count)
            {
                if (count <= 0) return;
                loot.TryGetValue(item, out int had);
                loot[item] = had + count;
            }

            Add(Managers.JondoCoin.TemplateId,
                fight.Rojo.Where(m => m.IsMonster && !m.EsInvocado).Sum(m => Managers.JondoCoin.RewardFor(m.Level)));
            Add(Dreams.FragmentItem, dream.Fragments);
            return loot;
        }

        /// <summary>This player's share of the fight, if it was planned.</summary>
        private static Reward? RewardOf(FightInstance fight, long characterId)
            => _rewards.TryGetValue(fight.FightId, out var plan) && plan.TryGetValue(characterId, out var reward)
                ? reward
                : null;

        /// <summary>A share as the end screen shows it.</summary>
        private static FightProtocol.Spoils SpoilsOf(Reward reward)
        {
            var spoils = new FightProtocol.Spoils { Kamas = reward.Kamas };
            foreach (var kv in reward.Loot) spoils.Items.Add((kv.Value, kv.Key));
            return spoils;
        }

        /// <summary>The fight is over for everybody: its shares go.</summary>
        private static void ForgetRewards(FightInstance fight) => _rewards.TryRemove(fight.FightId, out _);
    }
}
