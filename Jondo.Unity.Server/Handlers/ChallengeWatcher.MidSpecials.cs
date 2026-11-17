using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Event hooks for the remaining dungeon specials whose rules can be judged from combat events.
    /// FightHandler should call these beside the corresponding general challenge hook.
    /// </summary>
    public static partial class ChallengeWatcher
    {
        private const int Hamrack = 2955;
        private const int KaoRock = 5667;
        private const int SpiritLantern = 17295;
        private const int SpiritLanternEffect = 2020;
        private const int ShogunTofugawa = 6388;
        private const int KawabungaState = 4271;
        private const int NanoimpatienceV = 3541;
        private const int Destinos = 812;
        private static readonly HashSet<int> GuerrilleroSpells = new() { 7199, 7203 };

        private sealed class MidSpecialNotes
        {
            // Enemy id -> number of successful displacements since its last turn began.
            public Dictionary<long, int> DisplacementsSinceTurn { get; } = new();
            public HashSet<long> EnemiesBetweenTurns { get; } = new();

            // Enemy ids successfully displaced this round/turn by an ally. Parsimony is reset
            // when an ally starts a turn, so damage later in that same ally turn is detectable.
            public Dictionary<long, long> DisplacedByCurrentAllyTurn { get; } = new();
            public long CurrentAllyTurnOwner { get; set; }

            // Enemy ids already displaced at least once since they last began a turn.
            public HashSet<long> DisplacedBeforeNextTurn { get; } = new();

            // Shell turns are counted by entity id; a shell must die before its third turn begins.
            public Dictionary<long, int> ShellTurns { get; } = new();
        }

        private static readonly ConcurrentDictionary<long, MidSpecialNotes> _midSpecialNotes = new();

        private static MidSpecialNotes MidNotes(FightInstance fight)
            => _midSpecialNotes.GetOrAdd(fight.FightId, _ => new MidSpecialNotes());

        private static bool HasSpecial(FightInstance fight, int id)
            => fight.ChallengesFixed.Any(challenge => challenge.Id == id)
               && !fight.ChallengesBroken.Contains(id);

        /// <summary>
        /// Called after the existing enemy-turn-start logic. Resets displacement windows and
        /// fails the egg-shell challenge when an un-killed shell begins its third turn.
        /// </summary>
        public static async Task MidSpecialEnemyTurnStartedAsync(NetworkStream stream, FightInstance fight,
                                                                  Fighter enemy)
        {
            if (!Alguno(fight) || enemy.TeamId == 0) return;

            if (_midSpecialNotes.TryGetValue(fight.FightId, out var existing))
            {
                existing.DisplacementsSinceTurn.Remove(enemy.Id);
                existing.DisplacedBeforeNextTurn.Remove(enemy.Id);
                existing.EnemiesBetweenTurns.Remove(enemy.Id);
            }

            if (!HasSpecial(fight, 1031) || !IsCocabuliaShell(enemy) || !enemy.IsAlive) return;

            var notes = existing ?? MidNotes(fight);
            notes.ShellTurns.TryGetValue(enemy.Id, out int turns);
            turns++;
            notes.ShellTurns[enemy.Id] = turns;
            if (turns >= 3)
                await BreakOneAsync(stream, fight, 1031,
                                    $"the summoned shell {enemy.Name} begins its third turn alive");
        }

        private static void MidSpecialEnemyTurnEnded(FightInstance fight, Fighter enemy)
        {
            if (HasSpecial(fight, 1038))
                MidNotes(fight).EnemiesBetweenTurns.Add(enemy.Id);
        }

        /// <summary>Called when an ally starts a turn to begin Parsimony's per-turn window.</summary>
        public static void MidSpecialAllyTurnStarted(FightInstance fight, Fighter ally)
        {
            if (ally.TeamId == 0 && HasSpecial(fight, 1032))
            {
                var notes = MidNotes(fight);
                notes.DisplacedByCurrentAllyTurn.Clear();
                notes.CurrentAllyTurnOwner = OwnerOf(ally);
            }
        }

        /// <summary>
        /// Called only for a successful displacement. Records displacement-first rules and catches
        /// an enemy displaced more than once before its next turn.
        /// </summary>
        public static async Task MidSpecialDisplacedAsync(NetworkStream stream, FightInstance fight,
                                                            Fighter target, Fighter source)
        {
            if (!Alguno(fight) || target.TeamId != 1) return;

            long sourceOwner = OwnerOf(source);
            if (source.TeamId == 0 && HasSpecial(fight, 1032)
                && fight.CurrentFighter is { TeamId: 0 } playing && OwnerOf(playing) == sourceOwner)
            {
                var notes = MidNotes(fight);
                notes.CurrentAllyTurnOwner = sourceOwner;
                notes.DisplacedByCurrentAllyTurn[target.Id] = sourceOwner;
            }
            if (HasSpecial(fight, 1035)) MidNotes(fight).DisplacedBeforeNextTurn.Add(target.Id);
            if (source.TeamId == 0 && target.Buffs.TieneEstado(74) && HasSpecial(fight, 1065))
                await BreakOneAsync(stream, fight, 1065,
                                    $"the zombified enemy {target.Name} is displaced");

            if (HasSpecial(fight, 1038) && MidNotes(fight).EnemiesBetweenTurns.Contains(target.Id))
            {
                var notes = MidNotes(fight);
                notes.DisplacementsSinceTurn.TryGetValue(target.Id, out int count);
                count++;
                notes.DisplacementsSinceTurn[target.Id] = count;
                if (count > 1)
                    await BreakOneAsync(stream, fight, 1038,
                                        $"{target.Name} is displaced more than once before its next turn");
            }
        }

        /// <summary>
        /// Called on positive damage, beside DamagedAsync. Implements Parsimony, Intermediary,
        /// and the source-specific Hamrack damage prohibition.
        /// </summary>
        public static async Task MidSpecialDamageAsync(NetworkStream stream, FightInstance fight,
                                                        Fighter target, Fighter source)
        {
            if (!Alguno(fight)) return;

            if (target.TeamId == 1 && (HasSpecial(fight, 1032) || HasSpecial(fight, 1035)))
            {
                var notes = MidNotes(fight);
                long sourceOwner = OwnerOf(source);
                bool sameAllyStillPlaying = fight.CurrentFighter is { TeamId: 0 } playing
                                            && OwnerOf(playing) == sourceOwner;
                if (source.TeamId == 0 && sameAllyStillPlaying && HasSpecial(fight, 1032)
                    && notes.CurrentAllyTurnOwner == sourceOwner
                    && notes.DisplacedByCurrentAllyTurn.TryGetValue(target.Id, out long displacedBy)
                    && displacedBy == sourceOwner)
                    await BreakOneAsync(stream, fight, 1032,
                                        $"an ally damages {target.Name} after displacing it in the same turn");

                if (HasSpecial(fight, 1035) && !notes.DisplacedBeforeNextTurn.Contains(target.Id))
                    await BreakOneAsync(stream, fight, 1035,
                                        $"{target.Name} takes damage before being displaced");
            }

            if (target.TeamId == 0 && source.TeamId == 1 && source.MonsterId == Hamrack
                && HasSpecial(fight, 1044))
                await BreakOneAsync(stream, fight, 1044, $"{target.Name} takes damage from a Hamrack");

            if (target.TeamId == 0 && source.TeamId == 1 && IsCocabuliaShell(source)
                && HasSpecial(fight, 1031))
                await BreakOneAsync(stream, fight, 1031,
                                    $"{target.Name} takes damage caused by an exploding shell");
        }

        /// <summary>
        /// A spell/effect-specific hook. The caller resolves spell identity and whether an effect
        /// was actually applied, since an attempted cast alone is not enough for these rules.
        /// </summary>
        public static async Task MidSpecialEffectAppliedAsync(NetworkStream stream, FightInstance fight,
                                                               Fighter target, int spellId)
        {
            if (!Alguno(fight)) return;
            if (spellId == Destinos && target.TeamId == 0 && HasSpecial(fight, 1048))
                await BreakOneAsync(stream, fight, 1048, $"{target.Name} is affected by Destinos");
        }

        public static async Task MidSpecialStateAddedAsync(NetworkStream stream, FightInstance fight,
                                                            Fighter target, int state)
        {
            // The original Special listens for this state in spell 26083, grades 1 and 2.
            if (state == SpiritLanternEffect && target.TeamId == 1 && target.MonsterId != ShogunTofugawa
                && HasSpecial(fight, 1052))
                await BreakOneAsync(stream, fight, 1052, $"{target.Name} is affected by the spirit lantern");

            // State IDs are the EON triggers of the catalogue's challenge spells 25986 and 26001.
            if (state == KawabungaState && target.TeamId == 0 && HasSpecial(fight, 1043))
                await BreakOneAsync(stream, fight, 1043, $"{target.Name} is affected by Kawabunga");
            if (state == NanoimpatienceV && target.TeamId == 1 && HasSpecial(fight, 1068))
                await BreakOneAsync(stream, fight, 1068, $"{target.Name} enters Nanoimpatience V");
        }

        /// <summary>Challenge 1034: the Guerrillero spell must not damage an ally.</summary>
        public static async Task MidSpecialGuerrilleroDamageAsync(NetworkStream stream, FightInstance fight,
                                                                   Fighter target, Fighter source, int spellId)
        {
            if (target.TeamId == 0 && source.TeamId == 0 && source.Id != target.Id
                && GuerrilleroSpells.Contains(spellId) && HasSpecial(fight, 1034))
                await BreakOneAsync(stream, fight, 1034,
                                    $"{source.Name} damages ally {target.Name} with Guerrillero");
        }

        /// <summary>Called when an ally begins casting the no-lantern spell (regardless of outcome).</summary>
        public static async Task MidSpecialForbiddenSpellCastAsync(NetworkStream stream, FightInstance fight,
                                                                     Fighter caster, int spellId)
        {
            if (caster.TeamId == 0 && spellId == SpiritLantern
                && HasSpecial(fight, 1057))
                await BreakOneAsync(stream, fight, 1057, $"{caster.Name} casts the spirit lantern");
        }

        /// <summary>
        /// Apply the Zombi kill-radius rule after confirming that the victim carried the actual
        /// state id from content data. Radius uses the fight's cell distance and living fighters.
        /// </summary>
        public static async Task MidSpecialZombieEnemyDiedAsync(NetworkStream stream, FightInstance fight,
                                                                 Fighter victim, bool wasZombie)
        {
            wasZombie |= victim.Buffs.TieneEstado(74);
            if (!wasZombie || victim.TeamId != 1 || !HasSpecial(fight, 1069)) return;
            foreach (var ally in fight.Azul)
            {
                if (ally.IsAlive && Jondo.Unity.World.Maps.MapGeometry.Distance(ally.CellId, victim.CellId) <= 5)
                {
                    await BreakOneAsync(stream, fight, 1069,
                                        $"the zombified enemy {victim.Name} dies within 5 cells of {ally.Name}");
                    return;
                }
            }
            foreach (var enemy in fight.Rojo)
            {
                if (enemy.Id != victim.Id && enemy.IsAlive
                    && Jondo.Unity.World.Maps.MapGeometry.Distance(enemy.CellId, victim.CellId) <= 5)
                {
                    await BreakOneAsync(stream, fight, 1069,
                                        $"the zombified enemy {victim.Name} dies within 5 cells of {enemy.Name}");
                    return;
                }
            }
        }

        /// <summary>Call when a non-summoned enemy dies; Kao rocks are summons and remain in fight.Rojo.</summary>
        public static async Task MidSpecialKaoEnemyDiedAsync(NetworkStream stream, FightInstance fight,
                                                              Fighter victim)
        {
            if (HasSpecial(fight, 1406) && victim.TeamId == 1 && !victim.EsInvocado
                && victim.MonsterId != KaoRock
                && fight.Rojo.Any(enemy => enemy.MonsterId == KaoRock && enemy.IsAlive))
                await BreakOneAsync(stream, fight, 1406,
                                    $"{victim.Name} dies while Kao rocks remain on the field");
        }

        /// <summary>Clear the supplementary per-fight state from ChallengeWatcher.Forget.</summary>
        private static void ForgetMidSpecials(FightInstance fight)
            => _midSpecialNotes.TryRemove(fight.FightId, out _);

        // These are the four Cocabulia shell templates in challenge spells 26020 and 26021.
        private static bool IsCocabuliaShell(Fighter fighter)
            => fighter.MonsterId is >= 5815 and <= 5818;
    }
}
