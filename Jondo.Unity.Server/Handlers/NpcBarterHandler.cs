using Jondo.Protocol;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// An NPC's barter: the exchange window of type 2 (the client's kbn), where the player lays
    /// things down and the NPC answers with what it gives for them -- Gobiere Trokeur's piou eggs,
    /// bread and trool cakes for bwork beer.
    /// </summary>
    /// <remarks>
    /// The client opens the window on kbn { f1: the NPC's actor }, its exchange frame calling it
    /// the exchange of type 2 (emr's handler of kbn). The rest is the player trade's: kcr lays or
    /// takes back a stack (kfb, kfs; the NPC's side with f… remote), kep is the ready button and
    /// the window shuts on khd. What the NPC gives is content/npcs/barters.json: a recipe is taken
    /// when what lies on the table is exactly its price, or a whole multiple of it.
    /// </remarks>
    public static class NpcBarterHandler
    {
        public const string AuthoredFile = "npcs/barters.json";

        /// <summary>One of an NPC's swaps: what it gives for what.</summary>
        public sealed class Recipe
        {
            public IReadOnlyList<(int Item, int Count)> Gives { get; init; } = Array.Empty<(int, int)>();
            public IReadOnlyList<(int Item, int Count)> Takes { get; init; } = Array.Empty<(int, int)>();
        }

        /// <summary>An open barter window.</summary>
        public sealed class Barter
        {
            public int NpcId { get; init; }
            public long ActorId { get; init; }
            public List<(long Uid, int Quantity)> Laid { get; } = new();
            public List<(int Item, int Count)> Offer { get; set; } = new();
        }

        /// <summary>The uids the NPC's side shows its offer under, out of the way of real ones.</summary>
        private const long OfferUid = 9_000_000_000_000L;

        private static Dictionary<int, List<Recipe>>? _recipes;

        public static void Forget() => _recipes = null;

        private static Dictionary<int, List<Recipe>> Recipes => _recipes ??= Load();

        private static Dictionary<int, List<Recipe>> Load()
        {
            var all = new Dictionary<int, List<Recipe>>();
            string path = Paths.ContentFile(AuthoredFile);
            if (!File.Exists(path)) return all;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("barters", out var list)) return all;
                foreach (var row in list.EnumerateArray())
                {
                    int npc = row.GetProperty("npc").GetInt32();
                    var recipes = new List<Recipe>();
                    foreach (var r in row.GetProperty("recipes").EnumerateArray())
                        recipes.Add(new Recipe { Gives = Pairs(r.GetProperty("gives")), Takes = Pairs(r.GetProperty("takes")) });
                    all[npc] = recipes;
                }
                Console.WriteLine($"[Barter] {all.Count} NPC(s) barter.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Barter] Could not read {AuthoredFile}: {ex.Message}");
            }
            return all;
        }

        private static List<(int, int)> Pairs(JsonElement list)
            => list.EnumerateArray().Select(p => (p.GetProperty("item").GetInt32(), Math.Max(1, p.TryGetProperty("count", out var c) ? c.GetInt32() : 1))).ToList();

        /// <summary>Whether this NPC barters.</summary>
        public static bool Barters(int npcId) => Recipes.ContainsKey(npcId);

        /// <summary>The window opens on the NPC (its «Intercambiar» action).</summary>
        public static async Task OpenAsync(NetworkStream stream, int npcId, long actorId)
        {
            SessionContext.State.Barter = new Barter { NpcId = npcId, ActorId = actorId };
            await NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kbn, Pb.New().Var(1, actorId).Build()));
            Console.WriteLine($"[Barter] The barter with {npcId} opens.");
        }

        /// <summary>kcr with the window open: a stack laid down, more or less of it, or taken back.</summary>
        /// <returns>False when no barter is open.</returns>
        public static async Task<bool> MoveAsync(NetworkStream stream, byte[] payload)
        {
            var barter = SessionContext.State.Barter;
            if (barter == null) return false;
            byte[]? kcr = ConnectionProtocol.ReadPayload(payload, Op.Kcr);
            if (kcr == null) return true;
            int delta = 0; long uid = 0;
            foreach (var f in ProtoMessage.Parse(kcr).Fields)
            {
                if (f.WireType != 0) continue;
                if (f.FieldNumber == 1) delta = (int)f.VarIntValue;
                else if (f.FieldNumber == 2) uid = f.VarIntValue;
            }

            var item = Equipment.ByUid(uid);
            int index = barter.Laid.FindIndex(o => o.Uid == uid);
            int had = index >= 0 ? barter.Laid[index].Quantity : 0;
            int now = item == null || item.Position != Equipment.Bag ? 0 : Math.Clamp(had + delta, 0, item.Quantity);
            if (now == had) return true;

            if (now == 0)
            {
                barter.Laid.RemoveAt(index);
                await WriteAsync(stream, Op.Kfs, WorkshopProtocol.BuildRemoved(uid));
            }
            else
            {
                if (index >= 0) barter.Laid[index] = (uid, now);
                else barter.Laid.Add((uid, now));
                await WriteAsync(stream, Op.Kfb, TradeProtocol.BuildLaid(item!.Template, item.Effects, now, uid, remote: false));
            }
            await OfferAsync(stream, barter);
            return true;
        }

        /// <summary>What the NPC answers for what lies on the table, shown on its side.</summary>
        private static async Task OfferAsync(NetworkStream stream, Barter barter)
        {
            for (int i = 0; i < barter.Offer.Count; i++)
                await WriteAsync(stream, Op.Kfs, WorkshopProtocol.BuildRemoved(OfferUid + i, remote: true));

            barter.Offer = Answer(barter);
            for (int i = 0; i < barter.Offer.Count; i++)
            {
                var (gid, count) = barter.Offer[i];
                if (!DatabaseManager.TryGetItemTemplateEffects(gid, out string effects)) continue;
                await WriteAsync(stream, Op.Kfb, TradeProtocol.BuildLaid(gid, Equipment.ParseEffects(effects), count, OfferUid + i, remote: true));
            }
        }

        /// <summary>
        /// The recipe whose price is what lies on the table, exactly or times a whole number: the
        /// exact one first (sixteen beers are a gratrooll), then the dearest that fits a whole number
        /// of times (four beers are two eggs).
        /// </summary>
        private static List<(int Item, int Count)> Answer(Barter barter)
        {
            var laid = new Dictionary<int, int>();
            foreach (var (uid, quantity) in barter.Laid)
            {
                var item = Equipment.ByUid(uid);
                if (item == null) continue;
                laid[item.Template] = laid.GetValueOrDefault(item.Template) + quantity;
            }
            if (laid.Count == 0 || !Recipes.TryGetValue(barter.NpcId, out var recipes)) return new();

            int Times(Recipe r)
            {
                if (r.Takes.Count != laid.Count) return 0;
                int k = 0;
                foreach (var (item, count) in r.Takes)
                {
                    if (!laid.TryGetValue(item, out int have) || have % count != 0) return 0;
                    int times = have / count;
                    if (k != 0 && times != k) return 0;
                    k = times;
                }
                return k;
            }

            var exact = recipes.FirstOrDefault(r => Times(r) == 1);
            var chosen = exact ?? recipes.Where(r => Times(r) > 0).OrderByDescending(r => r.Takes.Sum(t => t.Count)).FirstOrDefault();
            if (chosen == null) return new();
            int k = Times(chosen);
            return chosen.Gives.Select(g => (g.Item, g.Count * k)).ToList();
        }

        /// <summary>kep with the window open: the swap is made, if the NPC has something to give.</summary>
        /// <returns>False when no barter is open.</returns>
        public static async Task<bool> ReadyAsync(NetworkStream stream, byte[] payload)
        {
            var barter = SessionContext.State.Barter;
            if (barter == null) return false;
            if (barter.Offer.Count == 0) return true;

            var laid = barter.Laid.Select(l => (Equipment.ByUid(l.Uid), l.Quantity)).Where(l => l.Item1 != null).ToList();
            foreach (var (item, quantity) in laid)
                await Equipment.TakeAsync(stream, item!.Template, quantity);
            foreach (var (gid, count) in barter.Offer)
            {
                if (!await Equipment.GiveAsync(stream, gid, count)) continue;
                await WriteAsync(stream, Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                    InfoMessages.ItemGained, count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    gid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            Console.WriteLine($"[Barter] {barter.NpcId}: {string.Join(", ", barter.Offer.Select(o => $"{o.Count}x{o.Item}"))} " +
                              $"for {string.Join(", ", laid.Select(l => $"{l.Quantity}x{l.Item1!.Template}"))}.");
            SessionContext.State.Barter = null;
            await WriteAsync(stream, Op.Khd, TradeProtocol.BuildClosed(done: true));
            return true;
        }

        /// <summary>The window shut by the player (kla), nothing swapped.</summary>
        /// <returns>False when no barter is open.</returns>
        public static async Task<bool> CloseAsync(NetworkStream stream)
        {
            if (SessionContext.State.Barter == null) return false;
            SessionContext.State.Barter = null;
            await WriteAsync(stream, Op.Khd, TradeProtocol.BuildClosed(done: false));
            return true;
        }

        private static Task WriteAsync(NetworkStream stream, string op, byte[] body)
            => NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(op, body));
    }
}
