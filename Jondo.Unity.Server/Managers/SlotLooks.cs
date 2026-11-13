using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What the bonus slots put on a character as the map sees him: the disguise of a roleplay buff
    /// or a curse, and the followers walking behind him. The mutation, which is also his fights' look,
    /// is <see cref="Mutations"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A disguise is 149 «Cambia la apariencia» on an item of the curses' slots or the roleplay
    /// buffer (24 to 26), its value an APPEARANCE, not bones: «Nativo del signo Bworka» (13347)
    /// is 1039, the same as Bworknroll's «Ritual de la cerveza», and bones 1039 are a ghost
    /// dragoturkey. The client no longer carries those appearances; their looks are recovered in
    /// content/world/appearance_looks.json (<see cref="Cosmetics.LookOfAppearance"/>), and one
    /// still unknown is not drawn. INFERRED: no capture shows one worn; the class attitudes carry
    /// a 1 in their first number (the Feca's «1, 888»), which is not read.
    /// </para>
    /// <para>
    /// A follower is 148 «¡Alguien te sigue!»: in its first number what it is -- 1 an NPC, 2 a
    /// monster, 4 an appearance, the balloons' and the pets' --, in its value which. It travels in
    /// the humanoid's option f6 { f1 { f1 look, f2 index } }, the two fields the client reads of it
    /// (giv.bkix).
    /// </para>
    /// </remarks>
    public static class SlotLooks
    {
        public const int ChangesAppearance = 149;
        public const int Follows = 148;
        public const int DisappearsOnMoving = 760;

        public const int FirstCurse = 24;
        public const int RoleplayBuffer = 26;
        public const int Follower = 27;

        /// <summary>A follower's first number: what its value names.</summary>
        public const int FollowerNpc = 1;
        public const int FollowerMonster = 2;
        public const int FollowerAppearance = 4;

        /// <summary>An item in a bonus slot, as far as the map cares.</summary>
        public sealed record Worn(long Uid, int Position, IReadOnlyList<Equipment.ItemEffect> Effects);

        /// <summary>
        /// What each character's bonus slots hold: kept up to date by his own session, read once
        /// from the base for anyone else.
        /// </summary>
        private static readonly ConcurrentDictionary<long, IReadOnlyList<Worn>> _known = new();

        /// <summary>The bonus slots of the character in session, remembered for the others to see.</summary>
        public static IReadOnlyList<Worn> Current()
        {
            long me = SessionContext.State.CharacterId;
            var worn = Equipment.All.Where(i => BonusSlots.IsBonusSlot(i.Position))
                                    .Select(i => new Worn(i.Uid, i.Position, i.Effects.ToList()))
                                    .ToList();
            if (me != 0) _known[me] = worn;
            return worn;
        }

        /// <summary>Any character's bonus slots: his session's when it is his.</summary>
        public static IReadOnlyList<Worn> Of(long characterId)
        {
            if (characterId == 0) return Array.Empty<Worn>();
            if (characterId == SessionContext.State.CharacterId) return Current();
            return _known.GetOrAdd(characterId, Read);
        }

        /// <summary>Whether an item's effects change how the map draws its wearer.</summary>
        public static bool ChangesTheLook(IEnumerable<Equipment.ItemEffect> effects)
            => effects.Any(e => e.Effect is ChangesAppearance or Follows);

        /// <summary>
        /// The look a disguise gives, or null: the roleplay buffer's before a curse's, the first whose
        /// appearance is known.
        /// </summary>
        public static string? DisguiseLook(IReadOnlyList<Worn> worn)
            => worn.Where(w => w.Position >= FirstCurse && w.Position <= RoleplayBuffer)
                   .OrderByDescending(w => w.Position)
                   .SelectMany(w => w.Effects)
                   .Where(e => e.Effect == ChangesAppearance)
                   .Select(e => LookOfAppearance((int)(e.Value != 0 ? e.Value : e.DiceSide)))
                   .FirstOrDefault(look => look != null);

        private static readonly ConcurrentDictionary<int, bool> _unknownTold = new();

        /// <summary>An appearance's look, saying once in the log when it is one still unknown.</summary>
        private static string? LookOfAppearance(int appearance)
        {
            if (appearance <= 0) return null;
            string? look = Cosmetics.LookOfAppearance(appearance);
            if (look == null && _unknownTold.TryAdd(appearance, true))
                Console.WriteLine($"[SlotLooks] Appearance {appearance} has no known look; it is not drawn.");
            return look;
        }

        /// <summary>The looks of the followers walking behind, in their slots' order.</summary>
        public static List<string> FollowerLooks(IReadOnlyList<Worn> worn)
            => worn.Where(w => w.Position >= FirstCurse && w.Position <= Follower)
                   .OrderBy(w => w.Position)
                   .SelectMany(w => w.Effects)
                   .Where(e => e.Effect == Follows)
                   .Select(e => LookOfFollower((int)e.DiceNum, (int)e.Value))
                   .Where(look => !string.IsNullOrEmpty(look))
                   .Select(look => look!)
                   .ToList();

        /// <summary>A follower's look: the NPC's or the monster's template, or the appearance's.</summary>
        public static string? LookOfFollower(int kind, int id)
        {
            if (id <= 0) return null;
            switch (kind)
            {
                case FollowerNpc:
                    return Npcs.EnsureTemplate(id)?.Look;
                case FollowerMonster:
                    MobSpawnManager.EnsureMonsterData();
                    return MobSpawnManager.GetMonsterData(id)?.Look;
                case FollowerAppearance:
                    return LookOfAppearance(id);
                default:
                    return null;
            }
        }

        private static IReadOnlyList<Worn> Read(long characterId)
        {
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Uid, Position, Effects FROM CharacterItems " +
                                      "WHERE CharacterId = $id AND Position BETWEEN $first AND $last;";
                command.Parameters.AddWithValue("$id", characterId);
                command.Parameters.AddWithValue("$first", BonusSlots.First);
                command.Parameters.AddWithValue("$last", BonusSlots.Last);
                var worn = new List<Worn>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string effects = reader.IsDBNull(2) ? "[]" : reader.GetString(2);
                    worn.Add(new Worn(reader.GetInt64(0), reader.GetInt32(1), Equipment.ParseEffects(effects)));
                }
                return worn;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SlotLooks] Could not read {characterId}'s bonus slots: {ex.Message}");
                return Array.Empty<Worn>();
            }
        }
    }
}
