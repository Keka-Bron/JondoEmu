using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The boxes an item opens into: its effect 222, "¿Qué hay ahí dentro?", names one of the
    /// client's random drop groups in its value and how many draws in its die
    /// (datos/random_drop_groups_*.json, extract_random_drops.py).
    /// </summary>
    /// <remarks>
    /// A group either weighs its items -- the Cofre de la sima's 534 is 690, 100, 100, 100 and 10,
    /// which the client shows as 69 %, 10 %, 10 %, 10 % and 1 % -- and each draw takes one of them
    /// by weight, or gives every one of them, its weights all -1 (96 of the 314 groups). Each item
    /// comes in a quantity between its two bounds. 454 of the 456 items carrying the effect draw
    /// once; the other two say 0, which is read as once.
    /// </remarks>
    public static class LootBoxes
    {
        /// <summary>"¿Qué hay ahí dentro?".</summary>
        public const int OpensIntoEffect = 222;

        public sealed record DropItem(int ItemId, double Weight, int MinQuantity, int MaxQuantity);

        public sealed record Group(int Id, bool DisplayChances, IReadOnlyList<DropItem> Items)
        {
            /// <summary>Whether it gives everything it lists rather than drawing among it.</summary>
            public bool GivesAll => Items.Count > 0 && Items.All(i => i.Weight < 0);
        }

        private sealed record Document(List<Group> Groups);

        private static Dictionary<int, Group> _groups;
        private static readonly object _lock = new();

        private static Dictionary<int, Group> Groups
        {
            get
            {
                if (_groups != null) return _groups;
                lock (_lock)
                {
                    _groups ??= Load(Paths.RandomDropGroupsJson);
                    return _groups;
                }
            }
        }

        private static Dictionary<int, Group> Load(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Items] {path} not found: no box can be opened.");
                return new Dictionary<int, Group>();
            }
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), options);
            return (document?.Groups ?? new()).ToDictionary(g => g.Id);
        }

        public static Group GroupOf(int id) => Groups.TryGetValue(id, out var group) ? group : null;

        /// <summary>What an item opens into, as its template says: the group and the draws, or null.</summary>
        public static (Group Group, int Draws)? BoxOf(int gid)
        {
            foreach (var effect in DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(gid)))
            {
                if (effect.EffectId != OpensIntoEffect) continue;
                var group = GroupOf(effect.Value);
                if (group != null) return (group, Math.Max(1, effect.DiceNum));
            }
            return null;
        }

        /// <summary>What one box gives: every item for a group that gives all, else a draw by weight per draw.</summary>
        public static List<(int ItemId, int Quantity)> Open(Group group, int draws, Random dice)
        {
            var given = new List<(int, int)>();
            if (group == null || group.Items.Count == 0) return given;

            int QuantityOf(DropItem item)
                => item.MaxQuantity > item.MinQuantity ? dice.Next(item.MinQuantity, item.MaxQuantity + 1) : Math.Max(1, item.MinQuantity);

            if (group.GivesAll)
            {
                foreach (var item in group.Items) given.Add((item.ItemId, QuantityOf(item)));
                return given;
            }

            var weighed = group.Items.Where(i => i.Weight > 0).ToList();
            double total = weighed.Sum(i => i.Weight);
            if (total <= 0) return given;
            for (int draw = 0; draw < draws; draw++)
            {
                double roll = dice.NextDouble() * total;
                foreach (var item in weighed)
                {
                    roll -= item.Weight;
                    if (roll >= 0) continue;
                    given.Add((item.ItemId, QuantityOf(item)));
                    break;
                }
            }
            return given;
        }
    }
}
