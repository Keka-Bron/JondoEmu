using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The guardian's modifier in the Audacia and Bravura expeditions: a spell the boss holds from
    /// the start of the fight, as an attitude.
    /// </summary>
    /// <remarks>
    /// Each boss has two such spells in the client, "Initial &lt;boss&gt; Expedition 1 - Fun" (state
    /// 3501, Expedicion de Audacia) and "... - Hard" (state 3500, Expedicion de Bravura). Their first
    /// grade, under I, puts the state on the boss and chains the rest through 792: Merkator's Audacia
    /// shares the damage among his monsters and adds final damage per hit, his Bravura keeps him
    /// invulnerable and gives him a movement point each time he becomes vulnerable. Nothing in the
    /// data ties a dungeon to its spell but the boss's name, so the pairs are written in
    /// <c>content/fights/expedition_modifiers.json</c>.
    /// </remarks>
    public static class ExpeditionModifiers
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "fights/expedition_modifiers.json";

        /// <summary>One expedition's modifier: its boss and the spell he holds.</summary>
        public sealed record Modifier(int Dungeon, int Boss, int Spell, string Kind);

        private static Dictionary<int, Modifier>? _byDungeon;
        private static readonly object _lock = new();

        /// <summary>The modifier of a dungeon, or null for one without (every dungeon but 166-205).</summary>
        public static Modifier? Of(int dungeonId)
        {
            Load();
            return _byDungeon!.TryGetValue(dungeonId, out var modifier) ? modifier : null;
        }

        /// <summary>How many expeditions have one.</summary>
        public static int Count
        {
            get
            {
                Load();
                return _byDungeon!.Count;
            }
        }

        /// <summary>
        /// Gives the boss of a fight in an Audacia or Bravura room his modifier, and returns how many
        /// fighters took it. The boss is the file's, or the one the dungeon names (the expedition's
        /// own copy of him); a room he is not in is left as it is.
        /// </summary>
        public static int OnFightCreated(FightInstance fight)
        {
            var dungeon = DungeonManager.OfRoom(fight.RoleplayMapId);
            var modifier = dungeon == null ? null : Of(dungeon.Id);
            if (modifier == null) return 0;
            return Give(fight, modifier, dungeon!.Bosses);
        }

        /// <summary>
        /// The attitude on each boss of the fight, or on every monster of it when the spell is
        /// written for all of them; for the tests too.
        /// </summary>
        internal static int Give(FightInstance fight, Modifier modifier, IReadOnlyCollection<int> bosses)
        {
            bool forEveryMonster = HeldByEveryMonster(modifier);
            int given = 0;
            foreach (var monster in fight.Rojo.Where(m => m.IsMonster && !m.EsInvocado))
            {
                if (!forEveryMonster && monster.MonsterId != modifier.Boss && !bosses.Contains(monster.MonsterId)) continue;
                monster.Buffs.PonerActitud(modifier.Spell);
                given++;
            }
            if (given > 0)
                Program.LogDebug($"[Expeditions] {modifier.Kind} of dungeon {modifier.Dungeon} on {given} monster(s): spell {modifier.Spell}.");
            return given;
        }

        /// <summary>
        /// Whether the spell's first grade names the boss's template in a mask: then every monster
        /// of the fight holds it, and the boss's own rows say "F&lt;boss&gt;". Capitan Meno's Audacia
        /// (23938) is written so: "3501 on C,F4460" is his, "1160 grade 4 on g,F4460" is each of
        /// his monsters casting at him, which marks that monster with 3599, and its "792 24489"
        /// then swaps its Mutación Inestable for the Audacia one (invulnerable, +4 MP, half its
        /// life, dead two turns later). Held by the boss alone, none of that reaches a monster.
        /// </summary>
        internal static bool HeldByEveryMonster(Modifier modifier)
        {
            string plain = "F" + modifier.Boss, not = "f" + modifier.Boss;
            foreach (var effect in SpellEffects.De(modifier.Spell, 1))
                foreach (var part in (effect.TargetMask ?? "").Split(','))
                {
                    string t = part.Trim().TrimStart('*');
                    if (t == plain || t == not) return true;
                }
            return false;
        }

        /// <summary>For the tests: read the file again.</summary>
        internal static void Forget()
        {
            lock (_lock) _byDungeon = null;
        }

        private static void Load()
        {
            lock (_lock)
            {
                if (_byDungeon != null) return;
                var byDungeon = new Dictionary<int, Modifier>();
                try
                {
                    string path = Paths.ContentFile(AuthoredFile);
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var entry in doc.RootElement.GetProperty("modifiers").EnumerateArray())
                        {
                            var modifier = new Modifier(entry.GetProperty("dungeon").GetInt32(),
                                                        entry.GetProperty("boss").GetInt32(),
                                                        entry.GetProperty("spell").GetInt32(),
                                                        entry.GetProperty("kind").GetString() ?? "");
                            if (modifier.Dungeon > 0 && modifier.Spell > 0) byDungeon[modifier.Dungeon] = modifier;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Expeditions] Could not read {AuthoredFile}: {ex.Message}");
                }

                _byDungeon = byDungeon;
            }
        }
    }
}
