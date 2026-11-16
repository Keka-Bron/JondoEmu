using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Recycling: resources destroyed for nuggets (item 14635), at the Temple of Alliances' neutral
    /// prism or while collecting a tax collector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules are the client's own (Translations 1149998). Any character of level 50 or more
    /// recycles. A recipe's item and a dungeon guardian's drop give more. At the neutral prism the
    /// player gets 60 % of the nuggets and the rest is destroyed (update 3.3, RecycleHandler.NeutralPrismShare);
    /// while collecting a tax collector they are split between the
    /// player and his alliance's pot, half each, and controlling territories next to the tax
    /// collector's gives more.
    /// </para>
    /// <para>
    /// The amounts are the client's own reckoning, so that what it estimates in its window
    /// (RecycleUi) is what is given:
    /// </para>
    /// <list type="bullet">
    ///   <item>An item breaks down into the base resources of its recipe, recursively (me.ohr).</item>
    ///   <item>Each resource is worth its quantity × its data's <c>recyclingNuggets</c> × a bonus
    ///     (RecycleUi.GetBonus):
    ///     <list type="bullet">
    ///       <item>×1.5 when the subarea the player stands in is among its <c>favoriteRecyclingSubareas</c>;</item>
    ///       <item>×3 when every monster that drops it is a boss;</item>
    ///       <item>×1.5 when the item recycled has a recipe.</item>
    ///     </list></item>
    ///   <item>The player's share and the alliance's are the window's percentages of that sum,
    ///     both × the adjacency bonus: 1 + 0.5 × the controlled neighbours ÷ the neighbours
    ///     (RecycleUi.PrismBonus), for a player in an alliance.</item>
    /// </list>
    /// <para>
    /// Whole nuggets are given: the shares are rounded down.
    /// </para>
    /// </remarks>
    public static class Recycling
    {
        /// <summary>The nugget, «Pepita».</summary>
        public const int Nugget = 14635;

        /// <summary>The level from which anyone recycles.</summary>
        public const int MinimumLevel = 50;

        /// <summary>The bonuses of RecycleUi: favourite subarea, recipe, boss drop.</summary>
        public const double ZoneBonus = 1.5, CraftBonus = 1.5, BossBonus = 3.0;

        /// <summary>At most this many items in the window, as the client counts them («x / 800»).</summary>
        public const int MaxItems = 800;

        private sealed record ItemRecycling(double Nuggets, HashSet<int> FavouriteSubAreas, int[] DroppedBy);

        private static readonly ConcurrentDictionary<int, ItemRecycling> _items = new();

        /// <summary>The nuggets some items are worth, recycled in that subarea, before any split.</summary>
        public static double NuggetsOf(int gid, int quantity, int subAreaId)
        {
            if (quantity <= 0) return 0;
            bool isCraft = RecipeManager.TryGetByResult(gid, out _);
            double total = 0;
            foreach (var (resource, count) in BaseResourcesOf(gid, quantity))
            {
                var data = Of(resource);
                if (data.Nuggets <= 0) continue;
                double bonus = (data.FavouriteSubAreas.Contains(subAreaId) ? ZoneBonus : 1.0)
                               * (DroppedByBossesOnly(data) ? BossBonus : 1.0)
                               * (isCraft ? CraftBonus : 1.0);
                total += count * data.Nuggets * bonus;
            }
            return total;
        }

        /// <summary>Whether something is worth recycling at all.</summary>
        public static bool Recyclable(int gid) => NuggetsOf(gid, 1, 0) > 0;

        /// <summary>A pet food's bonuses (me.oht): a boss's drop and a recipe's item.</summary>
        public const double PetFoodBossBonus = 5.0, PetFoodCraftBonus = 1.5;

        /// <summary>
        /// What one of an item is worth to a pet before its boost (me.ohq): its base resources'
        /// nuggets, ×5 for one only bosses drop and ×1.5 when the item has a recipe, with no zone's
        /// bonus; rounded down to five decimals, as the client keeps it.
        /// </summary>
        public static double PetFoodWorth(int gid)
        {
            bool isCraft = RecipeManager.TryGetByResult(gid, out _);
            double total = 0;
            foreach (var (resource, count) in BaseResourcesOf(gid, 1))
            {
                var data = Of(resource);
                if (data.Nuggets <= 0) continue;
                total += count * data.Nuggets
                         * (DroppedByBossesOnly(data) ? PetFoodBossBonus : 1.0)
                         * (isCraft ? PetFoodCraftBonus : 1.0);
            }
            return Math.Floor((float)(total * 100000.0)) / 100000.0;
        }

        /// <summary>
        /// The base resources some items break down into: an item with a recipe into its
        /// ingredients, each into its own, down to what no recipe makes (me.ohr).
        /// </summary>
        public static Dictionary<int, int> BaseResourcesOf(int gid, int quantity)
        {
            var resources = new Dictionary<int, int>();
            Decompose(gid, quantity, resources, 0);
            return resources;
        }

        private static void Decompose(int gid, int quantity, Dictionary<int, int> into, int depth)
        {
            // A recipe that came back to an item it is made of would never end; the client's data
            // has none, but nine levels is far past any real chain.
            if (depth < 9 && RecipeManager.TryGetByResult(gid, out var recipe) && recipe.Ingredients.Count > 0)
            {
                foreach (var ingredient in recipe.Ingredients)
                    Decompose(ingredient.ItemId, ingredient.Quantity * quantity, into, depth + 1);
                return;
            }
            into[gid] = (into.TryGetValue(gid, out int had) ? had : 0) + quantity;
        }

        /// <summary>The adjacency bonus: 1 + 0.5 × the share of the neighbouring territories controlled.</summary>
        public static double AdjacencyBonus(int possessed, int unpossessed)
        {
            int total = possessed + unpossessed;
            return total == 0 ? 1.0 : 1.0 + 0.5 / total * possessed;
        }

        /// <summary>
        /// The territories next to a subarea that can be held, split by whether this alliance holds
        /// them (its prism stands there): what the window shows, and what the bonus counts.
        /// </summary>
        public static (List<int> Possessed, List<int> Unpossessed) NeighboursOf(int subAreaId, long allianceId)
        {
            var possessed = new List<int>();
            var unpossessed = new List<int>();
            foreach (int neighbour in Interactives.NeighboursOfSubArea(subAreaId))
            {
                if (!Interactives.IsCapturable(neighbour)) continue;
                if (allianceId > 0 && PrismStore.InSubArea(neighbour)?.AllianceId == allianceId) possessed.Add(neighbour);
                else unpossessed.Add(neighbour);
            }
            return (possessed, unpossessed);
        }

        /// <summary>
        /// The two shares of some nuggets, the player's and the alliance's, rounded as the window
        /// rounds the total it promises: RecycleUi.UpdateGrid shows Math.Round of its
        /// m_myFinalNugget, the nuggets times the share times the prism's bonus. Rounded down, an
        /// amber the neutral prism's window showed at 3 gave 2.
        /// </summary>
        public static (long Player, long Alliance) Split(double nuggets, int percentToPlayer, int percentToAlliance, double bonus)
            => ((long)Math.Round(nuggets * percentToPlayer / 100.0 * bonus),
                (long)Math.Round(nuggets * percentToAlliance / 100.0 * bonus));

        private static bool DroppedByBossesOnly(ItemRecycling data)
            => data.DroppedBy.Length > 0 && data.DroppedBy.All(Dreams.IsBoss);

        private static ItemRecycling Of(int gid) => _items.GetOrAdd(gid, Read);

        private static ItemRecycling Read(int gid)
        {
            var none = new ItemRecycling(0, new HashSet<int>(), Array.Empty<int>());
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Data FROM ItemTemplates WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", gid);
                if (command.ExecuteScalar() is not string data) return none;
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                double nuggets = root.TryGetProperty("recyclingNuggets", out var n) && n.TryGetDouble(out double value) ? value : 0;
                return new ItemRecycling(nuggets, new HashSet<int>(Ints(root, "favoriteRecyclingSubareas")),
                                         Ints(root, "dropMonsterIds").ToArray());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Recycling] Could not read item {gid}: {ex.Message}");
                return none;
            }
        }

        private static IEnumerable<int> Ints(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var list)) yield break;
            if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("Array", out var array)) list = array;
            if (list.ValueKind != JsonValueKind.Array) yield break;
            foreach (var e in list.EnumerateArray())
                if (e.TryGetInt32(out int v)) yield return v;
        }

        /// <summary>For the tests: forget what was read.</summary>
        internal static void Forget() => _items.Clear();
    }
}
