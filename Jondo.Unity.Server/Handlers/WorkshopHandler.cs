using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// A workshop: the craft station of every craft job, the magus table of smithmagic, and the
    /// grinder that breaks items into runes.
    /// </summary>
    /// <remarks>
    /// ─── The cycle, measured ────────────────────────────────────────────────────────────────
    ///
    /// Open (every workshop capture, the oven, the mill, the alembic, the five magus tables):
    ///
    ///   C iwo { skill instance, element }
    ///   S iwn { f1: 1, element, skill, who }   ivx (with the kamas)   hlm {}   kgq { skill }
    ///   C itr { f3: 2 }                        S ivx   hlm {}
    ///
    /// Craft (the tutorial's ring, the grinder's rune fusion):
    ///
    ///   C kew { f2: result }          S kfb, one per stack the recipe needs
    ///   C kcr { f1: ±n, f2: uid }     S kfb when it enters, kex when it changes, kfs when it leaves
    ///   C kdx { f1: count }           S kgl { f1: count }
    ///   C kep { f1: true, f2: step }  S ium or ivj and kfs for each stack, itf or itu for the
    ///                                   result, kdr { f2 { f4: item }, f3: 2 }, isz on a new level,
    ///                                   irq, iun, and kgl { f1: 1 } after a craft of several
    ///
    /// Runes (two smithmagic sessions, 114 runes), two ways:
    ///
    ///   C kcr { f1: 1, f2: item }       S kfb: the item goes on the table
    ///   C kcj { f1: rune, f3: 1, f6 }   S kfb (the rune), irq when it entered, ivj or ium, kfs,
    ///                                     kdr { f2 { pool change, pool, item }, f3 }, kex, iun, kdb
    ///   C kcr { f1: 1, f2: rune }       S kfb: the rune in its slot
    ///   C kep { f1: true, f2: step }    S irq, ivj, kfs, kdr, kex, iun: the same, without kfb and kdb
    ///
    /// A signature rune laid on the table with kcr goes with the next rune: spent either way, the
    /// item signed only when that rune enters.
    ///
    /// Break (the grinder, skill 181):
    ///
    ///   C iwo                           S iwn, kbv {}
    ///   C kcr { f1: ±n, f2: uid }       S kfb (no float), kfs
    ///   C kbj { f2: true, f3: step }    S kgt { f3: 1, f4: me }, ium per item, ivj or iua per
    ///                                     rune, iun, kfp { per item: its runes, its coefficient }
    ///   C kla                           S khd { f3: 11 }
    ///
    /// Close: C kla   S khd { f3: 11 }, ivx, hlm {}.
    ///
    /// A magus table can also be a commission's (see <see cref="CommissionHandler"/>): then the
    /// item on it is the customer's, a rune or a signature may be theirs too, and every message
    /// the magus gets goes to the customer as well, marked as the other one's doing.
    /// </remarks>
    public static class WorkshopHandler
    {
        /// <summary>What one character has open.</summary>
        public sealed class Bench
        {
            public int ElementId { get; init; }
            public int SkillId { get; init; }

            /// <summary>A magus table: one item on it, and runes instead of recipes.</summary>
            public bool Magus { get; init; }

            /// <summary>The grinder breaking items.</summary>
            public bool Breaker { get; init; }

            /// <summary>An artisans' book: the directory is open.</summary>
            public bool Book { get; init; }

            /// <summary>A commission's table: the item on it is the customer's.</summary>
            public Commission? Commission { get; init; }

            /// <summary>What lies on a craft bench or the grinder: which stack and how many of it.</summary>
            public List<(long Uid, int Quantity)> Slots { get; } = new List<(long, int)>();

            /// <summary>How many times the recipe is to be crafted.</summary>
            public int Count { get; set; } = 1;

            /// <summary>The item on the magus table; zero when there is none.</summary>
            public long Item { get; set; }

            /// <summary>The rune laid on the magus table, and whether it is the customer's.</summary>
            public (long Uid, bool Customers)? RuneSlot { get; set; }

            /// <summary>The signature rune laid on the magus table, and whether it is the customer's.</summary>
            public (long Uid, bool Customers)? SignatureSlot { get; set; }
        }

        /// <summary>The "Base" job: the grinder's fusions and the odd quest recipe. It has no level.</summary>
        public const int BaseJob = 1;

        /// <summary>"Ninguna forjamagia futura": an item that carries it takes no rune.</summary>
        public const int NoMoreSmithmagic = Forgemagic.NoMoreSmithmagic;

        /// <summary>No more crafts at once than this, whatever the client asks.</summary>
        public const int MaxCount = 10000;

        [ThreadStatic] private static Random? _random;
        private static Random Dice => _random ??= new Random();

        public static bool IsOpen => SessionContext.State.Workshop != null;

        private static Task SendAsync(NetworkStream stream, string opcode, byte[] body)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(opcode, body));

        private static Task SendAsync(GameSession? session, string opcode, byte[] body)
            => session == null ? Task.CompletedTask : session.SendAsync(ConnectionProtocol.Push(opcode, body));

        // ─── Open and close ─────────────────────────────────────────────────────────────────

        public static async Task OpenAsync(NetworkStream stream, int elementId, int skillId)
        {
            if (SessionContext.State.Commission != null)
            {
                Console.WriteLine($"[Workshop] {SessionContext.State.CharacterId} is in a commission; element {elementId} not opened.");
                return;
            }

            if (skillId == Workshops.BookSkill)
            {
                await ArtisanHandler.OpenBookAsync(stream, elementId, skillId);
                return;
            }

            if (skillId == Breaking.Skill)
            {
                SessionContext.State.Workshop = new Bench { ElementId = elementId, SkillId = skillId, Breaker = true };
                await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    elementId, skillId, SessionContext.State.CharacterId));
                await SendAsync(stream, Op.Kbv, Array.Empty<byte>());
                Console.WriteLine($"[Workshop] Element {elementId} opened to break items.");
                return;
            }

            if (!SkillManager.TryGet(skillId, out var skill))
            {
                Console.WriteLine($"[Workshop] Unknown skill {skillId} on element {elementId}.");
                return;
            }
            if (!skill.IsForgemagus && RecipeManager.ForSkill(skillId).Count == 0)
            {
                Console.WriteLine($"[Workshop] Skill {skillId} has no recipe; element {elementId} not opened.");
                return;
            }

            SessionContext.State.Workshop = new Bench { ElementId = elementId, SkillId = skillId, Magus = skill.IsForgemagus };

            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(
                elementId, skillId, SessionContext.State.CharacterId));
            await SendAsync(stream, Op.Ivx, ConnectionProtocol.BuildInventory());
            await SendAsync(stream, Op.Hlm, Array.Empty<byte>());
            await SendAsync(stream, Op.Kgq, WorkshopProtocol.BuildOpened(skillId));

            Console.WriteLine($"[Workshop] Element {elementId} opened with skill {skillId} " +
                              (skill.IsForgemagus ? "(magus table)." : $"({RecipeManager.ForSkill(skillId).Count} recipes)."));
        }

        public static async Task CloseAsync(NetworkStream stream)
        {
            var bench = SessionContext.State.Workshop;
            SessionContext.State.Workshop = null;
            SessionContext.State.DirectoryJob = 0;
            await SendAsync(stream, Op.Khd, ConnectionProtocol.BuildShopClosed());
            if (bench?.Breaker == true || bench?.Book == true) return;   // the grinder's close, and the book's, is the khd alone
            await SendAsync(stream, Op.Ivx, ConnectionProtocol.BuildInventory());
            await SendAsync(stream, Op.Hlm, Array.Empty<byte>());
        }

        /// <summary>Nothing open survives a map change; a commission or a trade ends for both.</summary>
        public static void Forget()
        {
            var session = SessionContext.Current;
            session.State.Workshop = null;
            if (session.State.Commission != null) _ = CommissionHandler.AbandonAsync(session);
            if (session.State.Trade != null) _ = TradeHandler.AbandonAsync(session);
        }

        /// <summary>itr: the client wants its inventory again. ivx and an empty hlm, twelve of twelve.</summary>
        public static async Task InventoryAsync(NetworkStream stream)
        {
            await SendAsync(stream, Op.Ivx, ConnectionProtocol.BuildInventory());
            await SendAsync(stream, Op.Hlm, Array.Empty<byte>());
        }

        // ─── The craft bench ────────────────────────────────────────────────────────────────

        /// <summary>kew: a recipe picked in the list fills the bench with the stacks it needs.</summary>
        public static async Task SelectRecipeAsync(NetworkStream stream, byte[] payload)
        {
            var bench = SessionContext.State.Workshop;
            byte[]? kew = ConnectionProtocol.ReadPayload(payload, Op.Kew);
            if (bench == null || bench.Magus || bench.Breaker || kew == null) return;

            int result = (int)VarOf(kew, 2);
            if (!CraftHandler.TryResolveRecipe(bench.SkillId, result, out var recipe, out string error))
            {
                Console.WriteLine($"[Workshop] Recipe {result} refused: {error}");
                return;
            }

            await EmptyBenchAsync(stream, bench);
            foreach (var ingredient in recipe.Ingredients)
            {
                int left = ingredient.Quantity;
                foreach (var stack in BagStacksOf(ingredient.ItemId))
                {
                    if (left <= 0) break;
                    int take = Math.Min(left, stack.Quantity);
                    bench.Slots.Add((stack.Uid, take));
                    left -= take;
                    await SendAsync(stream, Op.Kfb, WorkshopProtocol.BuildAdded(stack.Template, stack.Effects, take, stack.Uid));
                }
            }
            Console.WriteLine($"[Workshop] Recipe {result}: {bench.Slots.Count} stack(s) on the bench.");
        }

        /// <summary>
        /// kcr inside a workshop: a stack onto the bench or the grinder, off it, or onto the magus
        /// table -- the item, a rune in its slot, the signature in its own.
        /// </summary>
        /// <returns>False when no workshop is open, so the chest gets it.</returns>
        public static async Task<bool> MoveAsync(NetworkStream stream, byte[] payload)
        {
            var bench = SessionContext.State.Workshop;
            if (bench == null) return false;
            byte[]? kcr = ConnectionProtocol.ReadPayload(payload, Op.Kcr);
            if (kcr == null) return true;

            // f1 is a signed int32: -1 travels as the ten-byte varint and reads back negative.
            int delta = (int)VarOf(kcr, 1);
            long uid = VarOf(kcr, 2);
            if (uid == 0 || delta == 0) return true;

            var item = Equipment.ByUid(uid);
            if (bench.Magus)
            {
                await MoveOnTableAsync(stream, bench, item, uid, delta);
                return true;
            }

            // The grinder only takes what gives runes; a craft bench takes anything.
            if (bench.Breaker && delta > 0 && (item == null || !Breaking.Breakable(item.Effects))) return true;

            int index = bench.Slots.FindIndex(s => s.Uid == uid);
            int had = index >= 0 ? bench.Slots[index].Quantity : 0;
            int now = item == null || item.Position != Equipment.Bag ? 0 : Math.Clamp(had + delta, 0, item.Quantity);
            if (now == had) return true;

            bool withFloat = !bench.Breaker;
            if (now == 0)
            {
                bench.Slots.RemoveAt(index);
                await SendAsync(stream, Op.Kfs, WorkshopProtocol.BuildRemoved(uid));
            }
            else if (index < 0)
            {
                bench.Slots.Add((uid, now));
                await SendAsync(stream, Op.Kfb, WorkshopProtocol.BuildAdded(item!.Template, item.Effects, now, uid, withFloat));
            }
            else
            {
                bench.Slots[index] = (uid, now);
                await SendAsync(stream, Op.Kex, WorkshopProtocol.BuildModified(item!.Template, item.Effects, now, uid));
            }
            return true;
        }

        /// <summary>kdx: how many times to craft.</summary>
        public static async Task CountAsync(NetworkStream stream, byte[] payload)
        {
            var bench = SessionContext.State.Workshop;
            byte[]? kdx = ConnectionProtocol.ReadPayload(payload, Op.Kdx);
            if (bench == null || bench.Magus || bench.Breaker || kdx == null) return;

            bench.Count = (int)Math.Clamp(VarOf(kdx, 1), 1, MaxCount);
            await SendAsync(stream, Op.Kgl, WorkshopProtocol.BuildCount(bench.Count));
        }

        /// <summary>kep in a workshop: the craft button, or the magus' "apply the rune in the slot".</summary>
        /// <returns>False when no workshop is open, so a trade gets it.</returns>
        public static async Task<bool> CraftAsync(NetworkStream stream, byte[] payload)
        {
            var bench = SessionContext.State.Workshop;
            if (bench == null) return false;
            byte[]? kep = ConnectionProtocol.ReadPayload(payload, Op.Kep);
            if (kep == null || bench.Breaker || VarOf(kep, 1) == 0) return true;

            if (bench.Magus)
            {
                await ApplySlottedRuneAsync(stream, bench);
                return true;
            }

            // What is on the bench, by template, the signature rune apart: it signs, it is no ingredient.
            var ingredients = new Dictionary<int, int>();
            bool signs = false;
            foreach (var (uid, quantity) in bench.Slots)
            {
                var item = Equipment.ByUid(uid);
                if (item == null || item.Position != Equipment.Bag) continue;
                if (item.Template == Forgemagic.SignatureRune) { signs = true; continue; }
                ingredients[item.Template] = ingredients.TryGetValue(item.Template, out int had) ? had + quantity : quantity;
            }

            var recipe = CraftHandler.Match(bench.SkillId, ingredients);
            int jobLevel = recipe == null ? 0 : SessionContext.State.JobLevel(recipe.JobId);
            var template = recipe == null ? null : Forgemagic.TemplateOf(recipe.ResultId);
            int count = bench.Count;
            if (recipe != null)
            {
                foreach (var i in recipe.Ingredients) count = Math.Min(count, BagCount(i.ItemId) / i.Quantity);
            }

            string? refusal = recipe == null ? "the ingredients make no recipe of this workshop"
                            : recipe.JobId != BaseJob && jobLevel < recipe.ResultLevel && !SessionContext.State.ForgeGod
                                ? $"job {recipe.JobId} is level {jobLevel}, the recipe asks {recipe.ResultLevel}"
                            : template == null ? $"no template for {recipe.ResultId}"
                            : count <= 0 ? "the bag does not hold one craft's worth"
                            : null;
            if (refusal != null)
            {
                await SendAsync(stream, Op.Kdr, WorkshopProtocol.BuildImpossible());
                Console.WriteLine($"[Workshop] Craft refused: {refusal}.");
                return true;
            }

            // Out of the bag: the stacks on the bench first, then any other of the same template.
            var owed = recipe!.Ingredients.GroupBy(i => i.ItemId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity) * count);
            int signatures = signs ? Math.Min(count, BagCount(Forgemagic.SignatureRune)) : 0;
            if (signatures > 0) owed[Forgemagic.SignatureRune] = signatures;
            foreach (var (uid, _) in bench.Slots.ToList())
            {
                var item = Equipment.ByUid(uid);
                if (item != null && owed.TryGetValue(item.Template, out int left) && left > 0)
                {
                    int take = Math.Min(left, item.Quantity);
                    await ConsumeAsync(SessionContext.Current, item, take);
                    owed[item.Template] = left - take;
                }
                await SendAsync(stream, Op.Kfs, WorkshopProtocol.BuildRemoved(uid));
            }
            bench.Slots.Clear();
            foreach (var gid in owed.Keys.ToList())
            {
                foreach (var stack in BagStacksOf(gid).ToList())
                {
                    if (owed[gid] <= 0) break;
                    int take = Math.Min(owed[gid], stack.Quantity);
                    await ConsumeAsync(SessionContext.Current, stack, take);
                    owed[gid] -= take;
                }
            }

            // Into the bag.
            var (reportEffects, reportUid) = await ProduceAsync(stream, recipe.ResultId, template!, count, signatures);
            await SendAsync(stream, Op.Kdr, WorkshopProtocol.BuildCrafted(recipe.ResultId, reportEffects, count, reportUid));

            int experience = 0;
            if (recipe.JobId != BaseJob)
            {
                experience = JobExperience.Craft(jobLevel, recipe.ResultLevel) * count;
                await GiveJobExperienceAsync(stream, recipe.JobId, experience);
            }
            await SendPodsAsync(SessionContext.Current);

            // "Fabricar 1 objeto": the tutorial earns it right here, after the ring.
            await Managers.Achievements.AfterCraftAsync(stream, count);

            if (bench.Count > 1)
            {
                bench.Count = 1;
                await SendAsync(stream, Op.Kgl, WorkshopProtocol.BuildCount(1));
            }

            Console.WriteLine($"[Workshop] Crafted {count} x {recipe.ResultId} (recipe level {recipe.ResultLevel}, " +
                              $"job {recipe.JobId} level {jobLevel}), +{experience} xp" +
                              (signatures > 0 ? $", {signatures} signed." : "."));
            return true;
        }

        /// <summary>
        /// What a craft puts in the bag. Something that rolls nothing joins a stack of the same
        /// thing (itu), or starts one (itf); anything that rolls is one new item each, each rolled
        /// on its own, and so is anything signed. Returns what kdr reports.
        /// </summary>
        private static async Task<(IReadOnlyList<Equipment.ItemEffect> Effects, long Uid)> ProduceAsync(
            NetworkStream stream, int gid, Forgemagic.Template template, int count, int signatures)
        {
            string name = SessionContext.State.CharacterName;
            if (Forgemagic.Stacks(template) && signatures == 0)
            {
                var effects = Forgemagic.Roll(template, Dice);
                string key = Forgemagic.Serialize(effects);
                var stack = BagStacksOf(gid).FirstOrDefault(s => Forgemagic.Serialize(s.Effects) == key);
                if (stack != null && Equipment.Rewrite(stack, stack.Quantity + count, stack.Effects.ToList()))
                {
                    await SendAsync(stream, Op.Itu, WorkshopProtocol.BuildStackGrew(stack.Uid, stack.Quantity));
                    return (effects, stack.Uid);
                }

                var created = Equipment.Create(gid, count, effects);
                if (created == null) return (effects, 0);
                await SendAsync(stream, Op.Itf, WorkshopProtocol.BuildItemsArrived(new[] { Arrival(created) }));
                return (effects, created.Uid);
            }

            var arrived = new List<Equipment.Item>();
            for (int i = 0; i < count; i++)
            {
                var effects = Forgemagic.Roll(template, Dice);
                if (i < signatures) effects = Forgemagic.Signed(effects, Forgemagic.CraftedBy, name);
                var created = Equipment.Create(gid, 1, effects);
                if (created != null) arrived.Add(created);
            }
            if (arrived.Count == 0) return (Array.Empty<Equipment.ItemEffect>(), 0);
            await SendAsync(stream, Op.Itf, WorkshopProtocol.BuildItemsArrived(arrived.Select(Arrival)));
            return (arrived[0].Effects, arrived[0].Uid);
        }

        private static (int, IReadOnlyList<Equipment.ItemEffect>, int, long) Arrival(Equipment.Item item)
            => (item.Template, item.Effects, item.Quantity, item.Uid);

        // ─── The magus table ────────────────────────────────────────────────────────────────

        /// <summary>
        /// kcr on a magus table: a rune goes into the rune slot, the signature into its own, and
        /// on a table of one's own an item of the table's types onto it.
        /// </summary>
        private static async Task MoveOnTableAsync(NetworkStream stream, Bench bench, Equipment.Item? item, long uid, int delta)
        {
            var customer = bench.Commission == null ? null : SessionRegistry.FindByCharacter(bench.Commission.CustomerId);
            bool withFloat = bench.Commission == null;

            if (delta < 0)
            {
                if (bench.RuneSlot?.Uid == uid && !bench.RuneSlot.Value.Customers) bench.RuneSlot = null;
                else if (bench.SignatureSlot?.Uid == uid && !bench.SignatureSlot.Value.Customers) bench.SignatureSlot = null;
                else if (bench.Item == uid && bench.Commission == null) bench.Item = 0;
                else return;
                await SendAsync(stream, Op.Kfs, WorkshopProtocol.BuildRemoved(uid));
                await SendAsync(customer, Op.Kfs, WorkshopProtocol.BuildRemoved(uid, remote: true));
                return;
            }

            if (item == null || item.Position != Equipment.Bag) return;
            if (item.Template == Forgemagic.SignatureRune || Forgemagic.RuneOf(item.Template) != null
                || Forgemagic.TranscendenceOf(item.Template) != null)
            {
                bool signature = item.Template == Forgemagic.SignatureRune;
                var slot = signature ? bench.SignatureSlot : bench.RuneSlot;
                if (slot != null)
                {
                    await SendAsync(stream, Op.Kfs, WorkshopProtocol.BuildRemoved(slot.Value.Uid));
                    await SendAsync(customer, Op.Kfs, WorkshopProtocol.BuildRemoved(slot.Value.Uid, remote: true));
                }
                if (signature) bench.SignatureSlot = (uid, false);
                else bench.RuneSlot = (uid, false);
                await SendAsync(stream, Op.Kfb, WorkshopProtocol.BuildAdded(item.Template, item.Effects, 1, uid, withFloat));
                await SendAsync(customer, Op.Kfb, WorkshopProtocol.BuildAdded(item.Template, item.Effects, 1, uid, false, remote: true));
                return;
            }

            // An item to mage: only on a table of one's own; a commission's item is the customer's.
            if (bench.Commission != null) return;
            var template = Forgemagic.TemplateOf(item.Template);
            if (template == null || !SkillManager.TryGet(bench.SkillId, out var skill)
                || (!skill.ModifiableItemTypeIds.Contains(template.Type) && !SessionContext.State.ForgeGod))
            {
                Console.WriteLine($"[Workshop] Item {item.Template} (type {template?.Type}) is not for skill {bench.SkillId}.");
                return;
            }

            if (bench.Item != 0 && bench.Item != uid)
                await SendAsync(stream, Op.Kfs, WorkshopProtocol.BuildRemoved(bench.Item));
            bench.Item = uid;
            await SendAsync(stream, Op.Kfb, WorkshopProtocol.BuildAdded(item.Template, item.Effects, 1, uid));
        }

        /// <summary>kcj: one rune out of the bag, straight onto the item on the magus table.</summary>
        public static async Task RuneAsync(NetworkStream stream, byte[] payload)
        {
            var bench = SessionContext.State.Workshop;
            byte[]? kcj = ConnectionProtocol.ReadPayload(payload, Op.Kcj);
            if (bench == null || !bench.Magus || kcj == null) return;

            var rune = Equipment.ByUid(VarOf(kcj, 1));
            if (rune == null || rune.Position != Equipment.Bag)
            {
                Console.WriteLine("[Workshop] Rune refused: no such rune in the bag.");
                return;
            }
            await ApplyAsync(stream, bench, SessionContext.Current, rune, typed: true);
        }

        /// <summary>kep on a magus table: the rune in the slot goes onto the item.</summary>
        private static async Task ApplySlottedRuneAsync(NetworkStream stream, Bench bench)
        {
            if (bench.RuneSlot is not { } slot) return;
            var owner = slot.Customers && bench.Commission != null
                ? SessionRegistry.FindByCharacter(bench.Commission.CustomerId)
                : SessionContext.Current;
            if (owner == null) return;
            var rune = CommissionHandler.As(owner, () => Equipment.ByUid(slot.Uid));
            if (rune == null || rune.Position != Equipment.Bag)
            {
                bench.RuneSlot = null;
                return;
            }
            await ApplyAsync(stream, bench, owner, rune, typed: false);
        }

        /// <summary>
        /// One rune on the item on the table, from whichever bag it comes, and the item written in
        /// whichever bag it lives. On a commission's table the customer sees every step too.
        /// </summary>
        private static async Task ApplyAsync(NetworkStream stream, Bench bench, GameSession runeOwner,
                                             Equipment.Item rune, bool typed)
        {
            var commission = bench.Commission;
            if (commission != null) await commission.Gate.WaitAsync();
            try
            {
                var crafter = SessionContext.Current;
                var customer = commission == null ? null : SessionRegistry.FindByCharacter(commission.CustomerId);
                var itemOwner = commission == null ? crafter : customer;
                if (itemOwner == null || (commission != null && commission.Ended)) return;

                var item = CommissionHandler.As(itemOwner, () => Equipment.ByUid(bench.Item));
                if (item == null || item.Position != Equipment.Bag)
                {
                    Console.WriteLine("[Workshop] Rune refused: no item on the table.");
                    return;
                }
                var template = Forgemagic.TemplateOf(item.Template);
                bool god = crafter.State.ForgeGod;
                if (template == null || (!god && item.Effects.Any(e => e.Effect == NoMoreSmithmagic)))
                {
                    Console.WriteLine($"[Workshop] Item {item.Template} takes no rune.");
                    return;
                }

                bool signatureRune = rune.Template == Forgemagic.SignatureRune;
                var transcendence = Forgemagic.TranscendenceOf(rune.Template);
                Forgemagic.Result result;
                if (transcendence is { } t)
                {
                    // On someone else's item the customer is asked first ("¿Aceptas que el artesano
                    // fusione este objeto con una runa de trascendencia?"), and that question is not
                    // captured. A transcendence the customer laid down themselves is their consent;
                    // the magus' own is refused.
                    string? refusal = god ? null
                        : commission != null && !ReferenceEquals(runeOwner, customer)
                            ? "a commission takes only the customer's own transcendence"
                            : Forgemagic.TranscendenceRefusal(template, item.Effects, t);
                    if (refusal != null)
                    {
                        await RefuseTranscendenceAsync(stream, customer, item, typed, rune.Template, refusal);
                        return;
                    }
                    result = god || Dice.Next(100) < t.Chance
                        ? Forgemagic.Transcend(item.Effects, t)
                        : new Forgemagic.Result
                        {
                            Outcome = Forgemagic.Outcome.Failure, PoolChange = Forgemagic.PoolChange.Same,
                            Pool = Forgemagic.PoolOf(item.Effects), Effects = item.Effects.ToList(),
                        };
                }
                else if (signatureRune)
                {
                    var signed = Forgemagic.Signed(item.Effects, Forgemagic.ModifiedBy, crafter.State.CharacterName);
                    result = new Forgemagic.Result
                    {
                        Outcome = Forgemagic.Outcome.Clean, PoolChange = Forgemagic.PoolChange.Same,
                        Pool = Forgemagic.PoolOf(signed), Effects = signed,
                    };
                }
                else
                {
                    var stat = Forgemagic.RuneOf(rune.Template);
                    if (stat == null)
                    {
                        Console.WriteLine($"[Workshop] {rune.Template} is no rune this table knows.");
                        return;
                    }
                    // Forgegod: every rune goes in clean -- no loss, no cap on an over or an exo,
                    // as many AP of exo as there are runes.
                    result = god
                        ? Forgemagic.Resolve(template, item.Effects, stat.Value, Forgemagic.Outcome.Clean,
                                             new Forgemagic.Odds(1, 0, 0), Dice)
                        : Forgemagic.Apply(template, item.Effects, stat.Value, Dice);
                }

                // The signature laid on the table goes with this rune, and signs only if it entered.
                GameSession? signatureOwner = null;
                Equipment.Item? signature = null;
                if (!signatureRune && bench.SignatureSlot is { } sig)
                {
                    signatureOwner = sig.Customers ? customer : crafter;
                    signature = signatureOwner == null ? null : CommissionHandler.As(signatureOwner, () => Equipment.ByUid(sig.Uid));
                    if (signature != null && result.Succeeded)
                        result = new Forgemagic.Result
                        {
                            Outcome = result.Outcome, PoolChange = result.PoolChange, Pool = result.Pool,
                            Odds = result.Odds, Lost = result.Lost,
                            Effects = Forgemagic.Signed(result.Effects, Forgemagic.ModifiedBy, crafter.State.CharacterName),
                        };
                }

                // 1. The rune enters (kcj only: a slotted rune is already on the table).
                if (typed)
                {
                    await SendAsync(stream, Op.Kfb, WorkshopProtocol.BuildAdded(rune.Template, rune.Effects, 1, rune.Uid, commission == null));
                    await SendAsync(customer, Op.Kfb, WorkshopProtocol.BuildAdded(rune.Template, rune.Effects, 1, rune.Uid, false, remote: true));
                }

                // 2. The magus' experience, when it went in.
                int experience = 0;
                if (result.Succeeded && !signatureRune && SkillManager.TryGet(bench.SkillId, out var skill))
                {
                    int level = crafter.State.JobLevel(skill.ParentJobId);
                    experience = JobExperience.Magus(level, template.Level);
                    await GiveJobExperienceAsync(stream, skill.ParentJobId, experience);
                }

                // 3. The rune is spent, out of whoever's bag it came, and leaves the table.
                long runeUid = rune.Uid;
                await ConsumeAsync(runeOwner, rune, 1);
                await SendAsync(stream, Op.Kfs, WorkshopProtocol.BuildRemoved(runeUid));
                await SendAsync(customer, Op.Kfs, WorkshopProtocol.BuildRemoved(runeUid, remote: true));
                if (!typed) bench.RuneSlot = null;

                // 4. So is the signature laid beside it.
                if (signature != null && signatureOwner != null)
                {
                    long signatureUid = signature.Uid;
                    await ConsumeAsync(signatureOwner, signature, 1);
                    await SendAsync(stream, Op.Kfs, WorkshopProtocol.BuildRemoved(signatureUid));
                    await SendAsync(customer, Op.Kfs, WorkshopProtocol.BuildRemoved(signatureUid, remote: true));
                    bench.SignatureSlot = null;
                }

                // 5. The item, in its owner's bag and on both screens.
                CommissionHandler.As(itemOwner, () => Equipment.Rewrite(item, item.Quantity, result.Effects));
                byte[] kdr = WorkshopProtocol.BuildRuneResult(result, item.Template, item.Uid);
                await SendAsync(stream, Op.Kdr, kdr);
                await SendAsync(stream, Op.Kex, WorkshopProtocol.BuildModified(item.Template, item.Effects, 1, item.Uid));
                await SendAsync(customer, Op.Kdr, kdr);
                await SendAsync(customer, Op.Kex, WorkshopProtocol.BuildModified(item.Template, item.Effects, 1, item.Uid, remote: true));
                if (ReferenceEquals(runeOwner, crafter) || ReferenceEquals(signatureOwner, crafter))
                    await SendPodsAsync(crafter);
                if (typed)
                {
                    await SendAsync(stream, Op.Kdb, WorkshopProtocol.BuildRuneDone());
                    await SendAsync(customer, Op.Kdb, WorkshopProtocol.BuildRuneDone());
                }
                if (commission != null) commission.Worked = true;

                Console.WriteLine($"[Workshop] Rune {rune.Template} on {item.Template}" +
                                  (commission != null ? $" (customer {commission.CustomerId})" : "") +
                                  (god ? " [forgegod]" : "") +
                                  $": {result.Outcome}, pool {result.Pool:0.##} ({result.PoolChange}), odds " +
                                  $"{result.Odds.Clean:P0}/{result.Odds.Partial:P0}/{result.Odds.Failure:P0}" +
                                  (result.Lost.Count > 0 ? ", lost " + string.Join(" ", result.Lost.Select(l => $"{l.Value}x{l.Key}")) : "") +
                                  (signature != null ? (result.Succeeded ? ", signed" : ", signature spent") : "") +
                                  (experience > 0 ? $", +{experience} xp." : "."));
            }
            finally { commission?.Gate.Release(); }
        }

        /// <summary>
        /// A transcendence that cannot go on: "receta fallida", the item as it was, and the rune
        /// left where it was -- nothing is spent on a rune the item refuses.
        /// </summary>
        private static async Task RefuseTranscendenceAsync(NetworkStream stream, GameSession? customer, Equipment.Item item,
                                                           bool typed, int runeGid, string why)
        {
            var unchanged = new Forgemagic.Result
            {
                Outcome = Forgemagic.Outcome.Failure, PoolChange = Forgemagic.PoolChange.Same,
                Pool = Forgemagic.PoolOf(item.Effects), Effects = item.Effects.ToList(),
            };
            byte[] kdr = WorkshopProtocol.BuildRuneResult(unchanged, item.Template, item.Uid);
            await SendAsync(stream, Op.Kdr, kdr);
            await SendAsync(customer, Op.Kdr, kdr);
            if (typed)
            {
                await SendAsync(stream, Op.Kdb, WorkshopProtocol.BuildRuneDone());
                await SendAsync(customer, Op.Kdb, WorkshopProtocol.BuildRuneDone());
            }
            Console.WriteLine($"[Workshop] Transcendence {runeGid} refused on {item.Template}: {why}.");
        }

        // ─── The grinder ────────────────────────────────────────────────────────────────────

        /// <summary>kbj: everything on the grinder breaks into runes.</summary>
        public static async Task BreakAsync(NetworkStream stream, byte[] payload)
        {
            var bench = SessionContext.State.Workshop;
            byte[]? kbj = ConnectionProtocol.ReadPayload(payload, Op.Kbj);
            if (bench == null || !bench.Breaker || kbj == null || bench.Slots.Count == 0) return;

            var me = SessionContext.Current;
            int focus = FocusOf(kbj);
            await SendAsync(stream, Op.Kgt, WorkshopProtocol.BuildReady(true, me.CharacterId));

            var report = new List<(long Uid, IReadOnlyList<(int Rune, int Quantity)> Runes, float Coefficient)>();
            var gained = new Dictionary<int, int>();
            foreach (var (uid, quantity) in bench.Slots.ToList())
            {
                var item = Equipment.ByUid(uid);
                if (item == null || item.Position != Equipment.Bag) continue;
                var template = Forgemagic.TemplateOf(item.Template);
                if (template == null) continue;
                int units = Math.Min(quantity, item.Quantity);
                double coefficient = Breaking.CoefficientOf(item.Template);

                var runes = new Dictionary<int, int>();
                for (int u = 0; u < units; u++)
                    foreach (var (rune, n) in Breaking.Yield(template.Level, item.Effects, coefficient, Dice, focus))
                        runes[rune] = (runes.TryGetValue(rune, out int had) ? had : 0) + n;
                Breaking.Broke(item.Template, units);

                await ConsumeAsync(me, item, units, inWorkshop: false);
                foreach (var (rune, n) in runes) gained[rune] = (gained.TryGetValue(rune, out int had) ? had : 0) + n;
                report.Add((uid, runes.Select(r => (r.Key, r.Value)).ToList(), (float)coefficient));
            }
            bench.Slots.Clear();

            foreach (var (rune, n) in gained) await GiveAsync(stream, rune, n);
            await SendPodsAsync(me);
            await SendAsync(stream, Op.Kfp, WorkshopProtocol.BuildBroken(report));
            Console.WriteLine($"[Workshop] Broke {report.Count} item(s){(focus != 0 ? $", focus on effect {focus}" : "")}: " +
                              string.Join(", ", gained.Select(g => $"{g.Value} x {g.Key}")) + ".");
        }

        /// <summary>
        /// The characteristic a breaking focuses on, if any. No capture focuses: kbj carries ready
        /// in f2 and the step in f3, and leaves f1 and f4, two int32, unused. Whichever of them
        /// comes set is taken for the focus -- an effect, or a rune whose effect it is -- and the
        /// raw frame goes to the console so that the first real one settles which it is.
        /// </summary>
        private static int FocusOf(byte[] kbj)
        {
            long f1 = VarOf(kbj, 1), f4 = VarOf(kbj, 4);
            if (f1 == 0 && f4 == 0) return 0;
            Console.WriteLine($"[Workshop] Breaking with f1={f1} f4={f4}: kbj {Convert.ToHexString(kbj)}");
            foreach (long candidate in new[] { f1, f4 })
            {
                if (candidate <= 0 || candidate > int.MaxValue) continue;
                int value = (int)candidate;
                if (Breaking.BaseRuneOf(value) != null) return value;
                var rune = Forgemagic.RuneOf(value);
                if (rune != null) return rune.Value.Effect;
            }
            return 0;
        }

        /// <summary>
        /// Something into the bag outside a craft -- the runes of the grinder, the ingredients of
        /// .receta. What rolls nothing joins a stack of the same thing with the same effects (ivj)
        /// or starts one (iua); what rolls is one item each, each rolled on its own, the way a
        /// craft makes them. False when the item has no template or nothing could be stored.
        /// </summary>
        public static async Task<bool> GiveAsync(NetworkStream stream, int gid, int quantity)
        {
            var template = Forgemagic.TemplateOf(gid);
            if (template == null || quantity <= 0) return false;
            if (Forgemagic.Stacks(template))
            {
                var effects = Forgemagic.Roll(template, Dice);
                string key = Forgemagic.Serialize(effects);
                var stack = BagStacksOf(gid).FirstOrDefault(s => Forgemagic.Serialize(s.Effects) == key);
                if (stack != null && Equipment.Rewrite(stack, stack.Quantity + quantity, stack.Effects.ToList()))
                {
                    await SendAsync(stream, Op.Ivj, ConnectionProtocol.BuildItemQuantity(stack.Uid, stack.Quantity));
                    return true;
                }
                return await ArriveAsync(stream, gid, quantity, effects);
            }

            bool any = false;
            for (int i = 0; i < quantity; i++)
                any |= await ArriveAsync(stream, gid, 1, Forgemagic.Roll(template, Dice));
            return any;
        }

        /// <summary>A new stack in the bag, and the iua that shows it.</summary>
        private static async Task<bool> ArriveAsync(NetworkStream stream, int gid, int quantity, IReadOnlyList<Equipment.ItemEffect> effects)
        {
            var created = Equipment.Create(gid, quantity, effects);
            if (created == null) return false;
            await SendAsync(stream, Op.Iua, ConnectionProtocol.BuildItemArrived(3, new HavenBagStore.StoredItem
            {
                Uid = created.Uid, Gid = gid, Quantity = quantity, Effects = Forgemagic.Serialize(created.Effects),
            }));
            return true;
        }

        // ─── Shared ─────────────────────────────────────────────────────────────────────────

        /// <summary>Experience for a job, its level-up if there is one, and the save.</summary>
        public static async Task GiveJobExperienceAsync(NetworkStream stream, int jobId, long amount)
        {
            if (amount <= 0) return;
            amount = Managers.Almanax.WithBonus(Managers.Almanax.BonusType.JobExperience, amount);
            bool up = SessionContext.State.AddJobExperience(jobId, amount, out long total, out int level);
            DatabaseManager.SaveJobExperience(SessionContext.State.CharacterId, jobId, total);
            if (up) await SendLevelUpAsync(stream, jobId, level);
            await SendAsync(stream, Op.Irq, ConnectionProtocol.BuildJobExperience(
                jobId, JobExperience.Next(level), level, JobExperience.Floor(level), total));
            if (up) await Managers.Achievements.AfterJobLevelAsync(stream);
        }

        /// <summary>isz: the job's new level and all of its skills, the way the tutorial's ring sends it.</summary>
        public static Task SendLevelUpAsync(NetworkStream stream, int jobId, int level)
        {
            var skills = SkillManager.ForJob(jobId).Select(s => s.IsGathering
                ? (s.Id, true, 1, level >= s.LevelMin ? GatheringHandler.Ceiling(level, s.LevelMin) : 1)
                : (s.Id, false, 0, 0));
            return SendAsync(stream, Op.Isz, WorkshopProtocol.BuildJobLevelUp(jobId, level, skills));
        }

        /// <summary>
        /// Takes units off a stack in someone's bag and tells them: ium when it goes, ivj when
        /// some are left -- the workshop's ivj, or the plain one the grinder sends.
        /// </summary>
        private static async Task ConsumeAsync(GameSession owner, Equipment.Item item, int quantity, bool inWorkshop = true)
        {
            long uid = item.Uid;
            int left = item.Quantity - quantity;
            bool done = CommissionHandler.As(owner, () =>
            {
                if (!DatabaseManager.DestroyCharacterItem(owner.CharacterId, uid, quantity)) return false;
                Equipment.Remove(uid, quantity);
                return true;
            });
            if (!done) return;
            if (left <= 0)
                await SendAsync(owner, Op.Ium, ConnectionProtocol.BuildItemGone(uid));
            else
                await SendAsync(owner, Op.Ivj, inWorkshop ? WorkshopProtocol.BuildStackUsed(uid, left)
                                                          : ConnectionProtocol.BuildItemQuantity(uid, left));
        }

        private static async Task EmptyBenchAsync(NetworkStream stream, Bench bench)
        {
            foreach (var (uid, _) in bench.Slots)
                await SendAsync(stream, Op.Kfs, WorkshopProtocol.BuildRemoved(uid));
            bench.Slots.Clear();
        }

        private static Task SendPodsAsync(GameSession session)
            => SendAsync(session, Op.Iun, ConnectionProtocol.BuildPods(0, 1000 + 5L * session.State.TotalStrength));

        /// <summary>The bag's stacks of a template, oldest first. Worn items never go into a workshop.</summary>
        private static IEnumerable<Equipment.Item> BagStacksOf(int gid)
            => Equipment.All.Where(i => i.Template == gid && i.Position == Equipment.Bag && i.Quantity > 0)
                            .OrderBy(i => i.Uid).ToList();

        private static int BagCount(int gid) => BagStacksOf(gid).Sum(i => i.Quantity);

        private static long VarOf(byte[] body, int field)
        {
            foreach (var f in ProtoMessage.Parse(body).Fields)
                if (f.FieldNumber == field && f.WireType == 0) return f.VarIntValue;
            return 0;
        }
    }
}
