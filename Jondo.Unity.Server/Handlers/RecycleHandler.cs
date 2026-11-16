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
    /// Recycling and the alliance's nugget pot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No capture recycles or hands nuggets out: the shapes are the client's handlers'.
    /// </para>
    /// <para>
    /// <b>The window</b> (RecycleUi) is an exchange that opens with kga:
    /// </para>
    /// <code>
    ///   kga { 1 the items already in it, 2 % to the player, 4 neighbouring territories held,
    ///         5 % to the alliance, 6 neighbouring territories not held }      (emr.jcm)
    /// </code>
    /// <para>
    /// It is filled and emptied like a workshop's bench:
    /// </para>
    /// <list type="bullet">
    ///   <item>kcr { ±n, uid } is answered with kfb when an item enters, kex when it changes and
    ///     kfs when it leaves;</item>
    ///   <item>«Reciclar» is the kep of a craft;</item>
    ///   <item>the result is kgh { 1 the alliance's nuggets, 2 the player's } (emr.czv → bgq).</item>
    /// </list>
    /// <para>
    /// After the result comes the information 1706, «Has obtenido {0} $item{1} y {2} han sido añadidas
    /// al bote de la alianza», or 1707 without the pot.
    /// </para>
    /// <para>
    /// <b>Where it opens</b> (Translations 1149998):
    /// </para>
    /// <list type="bullet">
    ///   <item>At the neutral prism beside the Temple of Alliances: interactive type 451 «Prisma
    ///     neutral», skill 457 «Reciclar recursos», element 539669. Level 50; 60 % to the player and
    ///     none to his alliance, the rest destroyed (see <see cref="NeutralPrismShare"/>).</item>
    ///   <item>When collecting a tax collector: its bag already in the window, half to the player and
    ///     half to the pot. «Se calcula a partir del conjunto de recicladores utilizados por la
    ///     alianza» (1094889): the prism modules that would change it do not exist here, so it is
    ///     the help's even split. What is taken out of the window goes to the bag; what is left in
    ///     it on closing is gone with the tax collector: «Cuando cierres la ventana de recolecta, el
    ///     recaudador, así como los recursos que todavía posee, desaparecen» (Translations 1149321).</item>
    /// </list>
    /// <para>
    /// <b>The pot</b> (NuggetsUi):
    /// </para>
    /// <list type="bullet">
    ///   <item>Opening the tab sends mdw; it is answered with mec { 1 the pot } (fgv → bbg). Closing it
    ///     sends mdt, which needs no answer.</item>
    ///   <item>«Distribuir» sends meb { 1 [ { 2 character, 3 nuggets } ] }. The right 7 «Distribuir
    ///     pepitas» is needed, and the right 6 «Ver el saldo de pepitas» to see the pot.</item>
    ///   <item>Each share goes to the member's bank, with the information 1576 «Tu personaje {0} ha
    ///     recibido {1} pepitas en el banco de parte de {2} ({3})».</item>
    /// </list>
    /// </remarks>
    public static class RecycleHandler
    {
        /// <summary>The neutral prism beside the Temple of Alliances.</summary>
        public const long PrismMap = 115081729;
        public const int PrismElement = 539669;
        public const int PrismType = 451;
        public const int RecycleSkill = 457;

        /// <summary>Information messages: the result, with and without the pot, and a share received.</summary>
        public const int RecycledWithPot = 1706, Recycled = 1707, SharedInBank = 1576;

        /// <summary>«No cumples los requisitos.» Under level 50 at the prism.</summary>
        public const int NotAllowed = 1;

        /// <summary>The tax collector's even split.</summary>
        public const int TaxCollectorShare = 50;

        /// <summary>
        /// The player's part at the neutral prism: «Le taux de recyclage est de 60% pour le joueur
        /// et 40% pour le prisme» (update 3.3), the 40 % going to nobody -- «on en obtient que 60%
        /// et le reste est supprimé» (the official forum) --, as the client's help has it too: «las
        /// pepitas no se comparten con su alianza» (Translations 1149998).
        /// </summary>
        public const int NeutralPrismShare = 60;

        /// <summary>Where the tax collector's items are numbered in the window, clear of the bag's uids.</summary>
        private const long LootUidBase = 2_100_000_000;

        /// <summary>A recycling under way: its split, where it happens, and a tax collector's bag.</summary>
        public sealed class Session
        {
            public int ToPlayer { get; init; }
            public int ToAlliance { get; init; }
            public long AllianceId { get; init; }
            public int SubAreaId { get; init; }
            public List<int> Possessed { get; init; } = new();
            public List<int> Unpossessed { get; init; } = new();

            /// <summary>The tax collector's items in the window, by the uid they were given there.</summary>
            public Dictionary<long, (int Gid, int Quantity)> Loot { get; } = new();

            public double Bonus => AllianceId > 0 ? Recycling.AdjacencyBonus(Possessed.Count, Unpossessed.Count) : 1.0;
        }

        // ─── Opening ─────────────────────────────────────────────────────────────────────────

        /// <summary>The neutral prism's «Reciclar recursos».</summary>
        public static async Task OpenAtPrismAsync(NetworkStream stream, int elementId, int skillId)
        {
            var me = SessionContext.State;
            await Write(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(elementId, skillId, me.CharacterId));
            if (me.CharacterLevel < Recycling.MinimumLevel)
            {
                await Write(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, NotAllowed));
                return;
            }

            var session = new Session { ToPlayer = NeutralPrismShare, ToAlliance = 0, SubAreaId = SubAreaOf(me.MapId) };
            me.Workshop = new WorkshopHandler.Bench { ElementId = elementId, SkillId = skillId, Recycle = session };
            await Write(stream, "kga", BuildOpened(session));
            Console.WriteLine($"[Recycling] {me.CharacterName} opens the neutral prism.");
        }

        /// <summary>
        /// A tax collector collected: its bag in the window, to recycle or to keep. False when there
        /// is nothing to recycle, and the items are the caller's to give.
        /// </summary>
        public static async Task<bool> OpenForTaxCollectorAsync(NetworkStream stream, long allianceId, int subAreaId,
                                                                IReadOnlyList<(int Gid, int Quantity)> items)
        {
            if (items.Count == 0) return false;
            var (possessed, unpossessed) = Recycling.NeighboursOf(subAreaId, allianceId);
            var session = new Session
            {
                ToPlayer = TaxCollectorShare, ToAlliance = TaxCollectorShare, AllianceId = allianceId,
                SubAreaId = subAreaId, Possessed = possessed, Unpossessed = unpossessed,
            };
            var bench = new WorkshopHandler.Bench { Recycle = session };
            long uid = LootUidBase;
            foreach (var (gid, quantity) in items)
            {
                session.Loot[uid] = (gid, quantity);
                bench.Slots.Add((uid, quantity));
                uid++;
            }
            SessionContext.State.Workshop = bench;
            await Write(stream, "kga", BuildOpened(session));
            Console.WriteLine($"[Recycling] {SessionContext.State.CharacterName} recycles a tax collector's {items.Count} stack(s).");
            return true;
        }

        /// <summary>kga: the window, with what is already in it.</summary>
        internal static byte[] BuildOpened(Session session)
        {
            var kga = Pb.New();
            foreach (var (uid, (gid, quantity)) in session.Loot)
                kga.Msg(1, Pb.New().Var(1, Equipment.Bag)
                    .Msg(5, ConnectionProtocol.ItemBody(gid, Array.Empty<Equipment.ItemEffect>(), quantity, uid)));
            kga.VarIfNotZero(2, session.ToPlayer);
            if (session.Possessed.Count > 0) kga.Packed(4, session.Possessed.Select(s => (long)s));
            kga.VarIfNotZero(5, session.ToAlliance);
            if (session.Unpossessed.Count > 0) kga.Packed(6, session.Unpossessed.Select(s => (long)s));
            return kga.Build();
        }

        // ─── The window ──────────────────────────────────────────────────────────────────────

        /// <summary>kcr in a recycling: an item in or out; a tax collector's item out goes to the bag.</summary>
        internal static async Task MoveAsync(NetworkStream stream, WorkshopHandler.Bench bench, int delta, long uid)
        {
            var session = bench.Recycle!;
            int index = bench.Slots.FindIndex(s => s.Uid == uid);
            int had = index >= 0 ? bench.Slots[index].Quantity : 0;

            if (session.Loot.TryGetValue(uid, out var loot))
            {
                if (delta >= 0 || index < 0) return;
                int left = Math.Max(0, had + delta);
                int taken = had - left;
                await KeepAsync(stream, loot.Gid, taken);
                if (left == 0)
                {
                    session.Loot.Remove(uid);
                    bench.Slots.RemoveAt(index);
                    await Write(stream, Op.Kfs, WorkshopProtocol.BuildRemoved(uid));
                }
                else
                {
                    session.Loot[uid] = (loot.Gid, left);
                    bench.Slots[index] = (uid, left);
                    await Write(stream, Op.Kex, WorkshopProtocol.BuildModified(loot.Gid, Array.Empty<Equipment.ItemEffect>(), left, uid));
                }
                return;
            }

            var item = Equipment.ByUid(uid);
            if (delta > 0 && (item == null || !Recycling.Recyclable(item.Template))) return;
            if (index < 0 && bench.Slots.Count >= Recycling.MaxItems) return;
            int now = item == null || item.Position != Equipment.Bag ? 0 : Math.Clamp(had + delta, 0, item.Quantity);
            if (now == had) return;

            if (now == 0)
            {
                bench.Slots.RemoveAt(index);
                await Write(stream, Op.Kfs, WorkshopProtocol.BuildRemoved(uid));
            }
            else if (index < 0)
            {
                bench.Slots.Add((uid, now));
                await Write(stream, Op.Kfb, WorkshopProtocol.BuildAdded(item!.Template, item.Effects, now, uid, withFloat: false));
            }
            else
            {
                bench.Slots[index] = (uid, now);
                await Write(stream, Op.Kex, WorkshopProtocol.BuildModified(item!.Template, item.Effects, now, uid));
            }
        }

        /// <summary>kep in a recycling: everything in the window becomes nuggets.</summary>
        internal static async Task RecycleAsync(NetworkStream stream, WorkshopHandler.Bench bench)
        {
            var session = bench.Recycle!;
            var me = SessionContext.Current;
            if (bench.Slots.Count == 0) return;

            double nuggets = 0;
            int stacks = 0;
            foreach (var (uid, quantity) in bench.Slots.ToList())
            {
                if (session.Loot.TryGetValue(uid, out var loot))
                {
                    nuggets += Recycling.NuggetsOf(loot.Gid, loot.Quantity, session.SubAreaId);
                    session.Loot.Remove(uid);
                    stacks++;
                    continue;
                }
                var item = Equipment.ByUid(uid);
                if (item == null || item.Position != Equipment.Bag) continue;
                int units = Math.Min(quantity, item.Quantity);
                if (!await WorkshopHandler.ConsumeAsync(me, item, units, inWorkshop: false)) continue;
                nuggets += Recycling.NuggetsOf(item.Template, units, session.SubAreaId);
                stacks++;
            }
            bench.Slots.Clear();

            var (toPlayer, toAlliance) = Recycling.Split(nuggets, session.ToPlayer, session.ToAlliance, session.Bonus);
            if (toPlayer > 0) await WorkshopHandler.GiveAsync(stream, Recycling.Nugget, (int)Math.Min(toPlayer, int.MaxValue));
            // The alliance's share goes to its prisms, by their zones' levels (AllianceNuggets.Collect).
            if (toAlliance > 0)
                AllianceNuggets.Collect(session.AllianceId, toAlliance,
                    PrismStore.OfAlliance(session.AllianceId).Select(p => (p.Id, Interactives.LevelOfSubArea(p.SubAreaId))).ToList());
            await ItemUseHandler.WeighAsync(stream);

            await Write(stream, "kgh", Pb.New().VarIfNotZero(1, toAlliance).VarIfNotZero(2, toPlayer).Build());
            await Write(stream, Op.Lqn, session.ToAlliance > 0
                ? ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, RecycledWithPot,
                    toPlayer.ToString(), Recycling.Nugget.ToString(), toAlliance.ToString())
                : ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, Recycled,
                    toPlayer.ToString(), Recycling.Nugget.ToString()));
            Console.WriteLine($"[Recycling] {me.State.CharacterName} recycles {stacks} stack(s): {toPlayer} nugget(s), " +
                              $"{toAlliance} to the pot of alliance {session.AllianceId}.");
        }

        /// <summary>
        /// The window closes: a tax collector's items left in it are gone with it -- «Cuando cierres
        /// la ventana de recolecta, el recaudador, así como los recursos que todavía posee,
        /// desaparecen» (Translations 1149321).
        /// </summary>
        internal static Task ClosingAsync(NetworkStream stream, WorkshopHandler.Bench bench)
        {
            Drop(SessionContext.Current, bench);
            return Task.CompletedTask;
        }

        /// <summary>
        /// The player is gone with a tax collector's items still in the window -- disconnected, or
        /// moved away --: the window is closed, and they are gone as on closing it.
        /// </summary>
        public static void Abandon(GameSession session) => Drop(session, session.State.Workshop);

        private static void Drop(GameSession session, WorkshopHandler.Bench bench)
        {
            if (bench?.Recycle == null) return;
            if (bench.Recycle.Loot.Count > 0)
                Console.WriteLine($"[Recycling] {session?.State.CharacterName} closes a collection with " +
                                  $"{bench.Recycle.Loot.Count} stack(s) left in it: gone with the tax collector.");
            bench.Recycle.Loot.Clear();
            bench.Slots.Clear();
        }

        private static async Task KeepAsync(NetworkStream stream, int gid, int quantity)
        {
            if (quantity <= 0 || !await WorkshopHandler.GiveAsync(stream, gid, quantity)) return;
            await Write(stream, Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                InfoMessages.ItemGained, quantity.ToString(), gid.ToString()));
            await ItemUseHandler.WeighAsync(stream);
        }

        // ─── The pot ─────────────────────────────────────────────────────────────────────────

        /// <summary>mdw: the nuggets tab opened; the pot, to whoever may see it.</summary>
        public static async Task ShowPotAsync(NetworkStream stream)
        {
            long me = SessionContext.State.CharacterId;
            var alliance = AllianceStore.AllianceOf(me);
            if (alliance == null) return;
            if (!AllianceStore.HasRight(me, AllianceData.SeeNuggets) && !AllianceStore.HasRight(me, AllianceData.DistributeNuggets))
                return;
            await Write(stream, "mec", BuildPot(AllianceNuggets.PotOf(alliance.Id)));
        }

        /// <summary>mec: the pot.</summary>
        internal static byte[] BuildPot(long pot) => Pb.New().VarIfNotZero(1, Math.Min(pot, int.MaxValue)).Build();

        /// <summary>meb: the pot handed out, each share into a member's bank.</summary>
        public static async Task DistributeAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? meb = ConnectionProtocol.ReadPayload(payload, "meb");
            var me = SessionContext.State;
            var alliance = AllianceStore.AllianceOf(me.CharacterId);
            if (meb == null || alliance == null) return;
            if (!AllianceStore.HasRight(me.CharacterId, AllianceData.DistributeNuggets))
            {
                Console.WriteLine($"[Nuggets] {me.CharacterName} has not the right to hand out nuggets.");
                return;
            }

            var shares = new List<(long Character, long Amount)>();
            foreach (var entry in ProtoMessage.Parse(meb).Fields.Where(f => f.FieldNumber == 1 && f.WireType == 2))
            {
                var fields = ProtoMessage.Parse(entry.BytesValue).Fields;
                long character = fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0;
                long amount = fields.FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 0)?.VarIntValue ?? 0;
                if (character <= 0 || amount <= 0 || amount > int.MaxValue) continue;
                if (AllianceStore.AllianceOf(character)?.Id != alliance.Id) continue;
                shares.Add((character, amount));
            }

            long total = shares.Sum(s => s.Amount);
            if (total <= 0 || !AllianceNuggets.TryTake(alliance.Id, total))
            {
                await Write(stream, "mec", BuildPot(AllianceNuggets.PotOf(alliance.Id)));
                return;
            }

            foreach (var (character, amount) in shares)
            {
                long account = DatabaseManager.AccountIdOfCharacter(character);
                if (!await Bank.AddItemAsync(account, Recycling.Nugget, (int)amount, null))
                {
                    AllianceNuggets.Add(alliance.Id, amount);
                    continue;
                }
                var receiver = SessionRegistry.FindByCharacter(character);
                if (receiver == null) continue;
                await receiver.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                    InfoMessages.Info, SharedInBank, receiver.State.CharacterName, amount.ToString(),
                    me.CharacterName, alliance.Name)));
            }

            await Write(stream, "mec", BuildPot(AllianceNuggets.PotOf(alliance.Id)));
            Console.WriteLine($"[Nuggets] {me.CharacterName} hands out {total} nugget(s) of «{alliance.Name}» " +
                              $"among {shares.Count} member(s).");
        }

        // ─── Helpers ─────────────────────────────────────────────────────────────────────────

        private static int SubAreaOf(long mapId) => MapManager.GetMapInfo(mapId)?.SubAreaId ?? 0;

        private static Task Write(NetworkStream stream, string opcode, byte[] message)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(opcode, message));
    }
}
