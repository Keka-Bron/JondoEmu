using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Gigalodón's fourth floor: the Cangrancio (the guides' Exécrabe) changes form four times
    /// in its fight, and its four statues have to be worked in that same order to go down.
    /// </summary>
    /// <remarks>
    /// THE FIGHT is the client's data, whole. Its starting spell 32199 sets four life thresholds,
    /// "Palier 80/60/40/20" (effect 2872), and each one crossed fires "Clean Seuil N", which casts
    /// the four forms' spells for that step: Oursin, Coquillage, Perle and Poulpe, "Check 1" to
    /// "Check 4". Each puts its phase state (6724 to 6727), its look (149: 3446 to 3443), its
    /// trick -- Oursin reflects damage, Coquillage a 5000 shield, Perle takes 63 range away,
    /// Poulpe pulls -- and takes the previous form away. All four are cast and nothing in the
    /// data picks one, so the server does: one at random among those the fight has not shown yet
    /// (the guides: "in a random order"). Which one is the raid's to remember.
    ///
    /// THE STATUES are the four elements of graphics 142002 to 142008 on floor -4 [9,12], the map
    /// of the way down. The client calls them levers ("activad las palancas siguiendo el orden
    /// correcto", its raid text), so they are declared as its type 127 "Palanca" with skill 244
    /// "Utilizar (palanca)". Which statue is which form is not written anywhere: they go in the
    /// forms' own order, the order of their spells and of their looks, and that matches their
    /// drawings -- the spiky one, the shell, the one with the pearl, the one with the eye.
    ///
    /// Worked in the order the fight showed, the fourth meets goal 16, "Enfrentarse a Cangrancio
    /// para resolver el enigma y acceder a la planta -5", which opens floor -5. A mistake costs
    /// 1000 of score -- it may go below nothing -- and starts the sequence over (the guides).
    /// The goal is not met by beating the Cangrancio any more: it is the enigma's.
    /// </remarks>
    public static class GuildRaidExecrabe
    {
        /// <summary>The Cangrancio, the monster goal 16 names.</summary>
        public const int Boss = 8332;

        /// <summary>The four "Clean Seuil" spells, one per threshold, each casting the four forms.</summary>
        public static readonly IReadOnlyList<int> ThresholdSpells = new[] { 32205, 32206, 32207, 32208 };

        /// <summary>The forms' phase states, in the forms' order: Oursin, Coquillage, Perle, Poulpe.</summary>
        public static readonly IReadOnlyList<int> Forms = new[] { 6724, 6725, 6726, 6727 };

        /// <summary>The statues' graphics, in the same order as the forms.</summary>
        public static readonly IReadOnlyList<int> StatueGfx = new[] { 142002, 142004, 142006, 142008 };

        /// <summary>The client's lever: interactive type 127 "Palanca", skill 244 "Utilizar (palanca)".</summary>
        public const int StatueType = 127;
        public const int StatueSkill = 244;

        /// <summary>What a statue worked out of order costs (the guides).</summary>
        public const int MistakeCost = 1000;

        /// <summary>A statue's state: lit once its form has been seen. Not measured, like the Luminarium's.</summary>
        public const int Lit = 1;
        public const int Out = 0;

        /// <summary>Where the raid keeps the forms' order and how far the statues have gone.</summary>
        public const string OrderSequence = "Execrabe_Forms";
        public const string ProgressVariable = "Execrabe_Statues";

        /// <summary>Each fight's forms shown so far, and each threshold's choice, by fight.</summary>
        private static readonly ConcurrentDictionary<long, List<int>> _shown = new();
        private static readonly ConcurrentDictionary<(long Fight, int Threshold), int> _chosen = new();

        // ─── The fight ──────────────────────────────────────────────────────────────────────

        /// <summary>The form a form spell puts on: its phase state, or 0 when it is none of them.</summary>
        public static int FormOf(int spell)
        {
            foreach (var effect in SpellEffects.De(spell, 1))
            {
                int state = effect.Value != 0 ? effect.Value : effect.DiceNum;
                if (effect.EffectId == Jondo.Unity.World.Combat.EffectSupport.AddState && Forms.Contains(state)) return state;
            }
            return 0;
        }

        /// <summary>
        /// Whether a threshold's cast of this form spell goes through: the one form chosen for
        /// that threshold of that fight, at random among those it has not shown yet.
        /// </summary>
        public static bool Allows(FightInstance fight, int threshold, int candidate, Random dice)
        {
            if (fight == null) return true;
            int chosen = _chosen.GetOrAdd((fight.FightId, threshold), _ => Choose(fight, threshold, dice));
            return candidate == chosen;
        }

        private static int Choose(FightInstance fight, int threshold, Random dice)
        {
            var shown = _shown.GetOrAdd(fight.FightId, _ => new List<int>());
            var candidates = SpellEffects.De(threshold, 1)
                                         .Where(e => e.EffectId == EffectEngine.EfectoQueLanzaHechizo && e.DiceNum > 0)
                                         .Select(e => e.DiceNum).ToList();
            List<int> fresh;
            lock (shown) fresh = candidates.Where(c => !shown.Contains(FormOf(c))).ToList();
            if (fresh.Count == 0) fresh = candidates;
            if (fresh.Count == 0) return 0;
            int chosen = fresh[dice.Next(fresh.Count)];
            int form = FormOf(chosen);
            lock (shown) shown.Add(form);
            Remember(fight, shown);
            Program.LogDebug($"[Raids] The Cangrancio takes form {form} at threshold {threshold} (spell {chosen}).");
            return chosen;
        }

        /// <summary>The order the fight has shown, onto the raid of whoever fights it.</summary>
        private static void Remember(FightInstance fight, List<int> shown)
        {
            var player = fight.Azul.Concat(fight.Rojo).FirstOrDefault(f => !f.IsMonster && !f.EsInvocado);
            var raid = player == null ? null : GuildRaidManager.RaidOf(player.Id);
            if (raid == null) return;
            lock (shown) raid.SetSequence(OrderSequence, shown);
            raid.Set(ProgressVariable, 0);
        }

        /// <summary>A fight over: its choices go.</summary>
        public static void Forget(long fightId)
        {
            _shown.TryRemove(fightId, out _);
            foreach (var key in _chosen.Keys.Where(k => k.Fight == fightId).ToList()) _chosen.TryRemove(key, out _);
        }

        // ─── The statues ────────────────────────────────────────────────────────────────────

        /// <summary>The statues on their map: element and the form each stands for, or null without them.</summary>
        public static (long MapId, List<(Interactives.Element Statue, int Form)> Statues)? StatuesOf(RaidKind kind)
        {
            if (kind == null || kind.Id != Raids.Gigalodon) return null;
            foreach (int subArea in kind.Floors)
            foreach (long mapId in DatabaseManager.MapsOfSubArea(subArea))
            {
                var statues = Interactives.ElementsOf(mapId)
                                          .Where(e => StatueGfx.Contains(e.Gfx))
                                          .Select(e => (e, Forms[StatueGfx.ToList().IndexOf(e.Gfx)]))
                                          .ToList();
                if (statues.Count == StatueGfx.Count) return (mapId, statues);
            }
            return null;
        }

        /// <summary>The goal the statues meet: the one naming the Cangrancio.</summary>
        public static GuildRaidCatalogue.Goal EnigmaGoal(int raidId)
            => GuildRaidCatalogue.GoalsOf(raidId).FirstOrDefault(g => g.Monsters?.Contains(Boss) == true);

        /// <summary>A statue's state for a raid: lit once its form has been seen in the fight.</summary>
        public static int StateFor(RaidInstance raid, int form)
            => raid != null && raid.SequenceOf(OrderSequence).Contains(form) ? Lit : Out;

        public static int StateFor(long characterId, long mapId, int elementId)
        {
            var raid = GuildRaidManager.RaidOf(characterId);
            var statues = StatuesOf(raid == null ? null : Raids.Of(raid.RaidId));
            if (statues == null || statues.Value.MapId != mapId) return Out;
            var statue = statues.Value.Statues.FirstOrDefault(s => s.Statue.Id == elementId);
            return statue.Form == 0 ? Out : StateFor(raid, statue.Form);
        }

        public static Task<bool> PressAsync(NetworkStream stream, long characterId, long mapId, int elementId, int skillId)
            => PressAsync(stream, GuildRaidManager.RaidOf(characterId), mapId, elementId, skillId);

        /// <summary>
        /// A statue worked: the next in the order moves the sequence on and the fourth meets the
        /// goal; one out of order costs score and starts over. Nothing before the fight has shown
        /// all four forms, or once the enigma is solved.
        /// </summary>
        public static async Task<bool> PressAsync(NetworkStream stream, RaidInstance raid, long mapId, int elementId, int skillId)
        {
            var statues = StatuesOf(raid == null ? null : Raids.Of(raid.RaidId));
            if (statues == null || statues.Value.MapId != mapId) return false;
            var statue = statues.Value.Statues.FirstOrDefault(s => s.Statue.Id == elementId);
            if (statue.Form == 0) return false;

            if (stream != null)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Iwi, ConnectionProtocol.BuildInteractiveUseEnded(elementId, skillId)));
            }

            var goal = EnigmaGoal(raid.RaidId);
            if (goal == null || raid.GoalAt(goal.Id) >= Math.Max(1, goal.Value)) return true;     // solved
            var order = raid.SequenceOf(OrderSequence);
            if (order.Count < Forms.Count)
            {
                await TellRaidOnMapAsync(raid, mapId, Notice("raid.statues.unknown"));
                return true;
            }

            int progress = (int)raid.Get(ProgressVariable);
            if (order[progress] != statue.Form)
            {
                raid.Set(ProgressVariable, 0);
                raid.Add(RaidInstance.ScoreVariable, -MistakeCost);
                await Handlers.GuildRaidHandler.TellRunningStateAsync(raid);
                await TellRaidOnMapAsync(raid, mapId, Notice("raid.statues.wrong", MistakeCost));
                Console.WriteLine($"[Raid] {raid.Uuid}: a statue out of order, -{MistakeCost} score.");
                return true;
            }

            raid.Set(ProgressVariable, ++progress);
            if (progress < order.Count) return true;

            await GuildRaidManager.AdvanceGoalAsync(raid, goal.Id, Math.Max(1, goal.Value));
            await TellRaidOnMapAsync(raid, mapId, Notice("raid.goal.met", goal.Name));
            Console.WriteLine($"[Raid] {raid.Uuid}: the Cangrancio's statues are solved; floor {goal.UnlocksFloor} opens.");
            return true;
        }

        private static Func<string, byte[]> Notice(string key, object argument = null)
            => language => ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(
                   argument == null ? Handlers.CommandTexts.Get(language, key) : Handlers.CommandTexts.Get(language, key, argument)));

        /// <summary>A notice to the raid's members standing on a map, each in his language.</summary>
        private static async Task TellRaidOnMapAsync(RaidInstance raid, long mapId, Func<string, byte[]> frame)
        {
            foreach (long member in raid.Members.ToList())
            {
                var session = SessionRegistry.FindByCharacter(member);
                if (session != null && session.State.MapId == mapId) await session.SendAsync(frame(session.State.Language));
            }
        }
    }
}
