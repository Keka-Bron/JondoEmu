using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>A magus working on somebody else's item, the two of them in one exchange.</summary>
    public sealed class Commission
    {
        public long CrafterId { get; init; }
        public long CustomerId { get; init; }
        public long InviterId { get; init; }
        public int SkillId { get; init; }
        public bool Accepted { get; set; }

        /// <summary>What the customer offers: their stacks and how many of each, in arrival order.</summary>
        public List<(long Uid, int Quantity)> Offer { get; } = new List<(long, int)>();

        public long Payment { get; set; }
        public bool CustomerReady { get; set; }

        /// <summary>A rune went onto the customer's item: the payment is owed.</summary>
        public bool Worked { get; set; }

        public bool Ended { get; set; }

        /// <summary>One of the two at a time: both of them write here, each from their own connection.</summary>
        public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);

        public long Other(long me) => me == CrafterId ? CustomerId : CrafterId;
    }

    /// <summary>
    /// A commission: a magus maging a customer's item at a magus table.
    /// </summary>
    /// <remarks>
    /// ─── What is measured, and from which side ──────────────────────────────────────────────
    ///
    /// The magus' side, whole, in two sessions (Oficios/"envio invitacion a maguear traje-sombrero-
    /// recibo pago-meto runas" and "... zapatos-meto runas"); the customer's, up to accepting, in
    /// "recibo invitacion para que me magueen joya y rechazo y luego acepto":
    ///
    ///   invite   C kbl { f1: other, f2: skill, f3: 10 I am the magus / 11 I am the customer }
    ///            S kgu { f1: other, f2: my role, f3: who invited } + hlm, to each of the two
    ///            S kdv { f1: 3 } when the magus stands too far from the table
    ///   refuse   C kla   S khd { f3: 11 } + hlm
    ///   accept   C kgi   customer S iss (the magus' job) + kgw { magus' level, skill }
    ///                    magus    S keg { skill }
    ///   offer    the magus sees ked { item, f4: true } for what the customer lays down,
    ///            kcl { kamas } for the payment, kgt { f3: ready, f4: customer }
    ///   table    C kgd { f1: ±1, f4: uid }: S keo + kfb onto the table, kfs + ked back
    ///   runes    exactly the magus table's (WorkshopHandler), on the customer's item
    ///   close    C kla   magus S lqs + lqn 594 "Pago" + ivf (when paid), kcl {}, khd, ivx, hlm
    ///
    /// What the customer sees of the magus' work is not captured. It is sent as the mirror of
    /// the magus' side, with the "the other one did it" flag every one of those messages has
    /// (kfb f2, kfs f2, kex f1, ked f4) -- the flag the trade capture shows set on the side of
    /// whoever did NOT do it. The customer's payment is taken from kee, the kamas of a trade.
    ///
    /// The customer pays when the commission closes, if they were ready and a rune went onto
    /// their item: the closing of the second session pays the 5,000 exactly then.
    /// </remarks>
    public static class CommissionHandler
    {
        private static Task SendAsync(GameSession? session, string opcode, byte[] body)
            => session == null ? Task.CompletedTask : session.SendAsync(ConnectionProtocol.Push(opcode, body));

        /// <summary>Something done as another character: their inventory, their kamas, their database rows.</summary>
        internal static T As<T>(GameSession session, Func<T> action)
        {
            using (SessionContext.Push(session)) return action();
        }

        public static Commission? Of(GameSession session) => session.State.Commission;

        // ─── Invitation ─────────────────────────────────────────────────────────────────────

        /// <summary>kbl: an invitation, sent by the magus or by the customer.</summary>
        public static async Task InviteAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? kbl = ConnectionProtocol.ReadPayload(payload, Op.Kbl);
            if (kbl == null) return;
            long target = VarOf(kbl, 1);
            int skillId = (int)VarOf(kbl, 2);
            int role = (int)VarOf(kbl, 3);

            var me = SessionContext.Current;
            var other = SessionRegistry.FindByCharacter(target);
            if (other == null || !other.IsInWorld || target == me.CharacterId || other.MapId != me.MapId)
            {
                Console.WriteLine($"[Commission] {me.CharacterId} invites {target}, who is not on this map.");
                return;
            }
            if (!SkillManager.TryGet(skillId, out var skill) || !skill.IsForgemagus
                || (role != WorkshopProtocol.CrafterRole && role != WorkshopProtocol.CustomerRole))
            {
                Console.WriteLine($"[Commission] Skill {skillId} with role {role}: only the magus tables take commissions.");
                return;
            }
            if (me.State.Commission != null || other.State.Commission != null)
            {
                Console.WriteLine($"[Commission] {me.CharacterId} or {target} already has one.");
                return;
            }

            // The magus has to stand where the table is: a station on this map with that skill.
            bool atTable = InteractiveRegistry.OnMap(me.MapId)
                .Any(i => i.Actions.Any(a => a.Kind == InteractiveActionKind.Workshop && a.SkillId == skillId));
            if (!atTable)
            {
                await me.SendAsync(ConnectionProtocol.Push(Op.Kdv,
                    WorkshopProtocol.BuildCommissionImpossible(WorkshopProtocol.TooFar)));
                Console.WriteLine($"[Commission] No table with skill {skillId} on map {me.MapId}.");
                return;
            }

            bool iAmCrafter = role == WorkshopProtocol.CrafterRole;
            var commission = new Commission
            {
                CrafterId = iAmCrafter ? me.CharacterId : target,
                CustomerId = iAmCrafter ? target : me.CharacterId,
                InviterId = me.CharacterId,
                SkillId = skillId,
            };
            me.State.Commission = commission;
            other.State.Commission = commission;
            me.State.Workshop = null;

            int otherRole = iAmCrafter ? WorkshopProtocol.CustomerRole : WorkshopProtocol.CrafterRole;
            await me.SendAsync(ConnectionProtocol.Push(Op.Kgu, WorkshopProtocol.BuildCommissionRequested(target, role, me.CharacterId)));
            await me.SendAsync(ConnectionProtocol.Push(Op.Hlm));
            await other.SendAsync(ConnectionProtocol.Push(Op.Kgu, WorkshopProtocol.BuildCommissionRequested(me.CharacterId, otherRole, me.CharacterId)));
            await other.SendAsync(ConnectionProtocol.Push(Op.Hlm));
            Console.WriteLine($"[Commission] {me.CharacterId} invites {target} to skill {skillId}, as the " +
                              (iAmCrafter ? "magus." : "customer."));
        }

        /// <summary>kgi: the one invited accepts.</summary>
        /// <returns>False when there is no commission waiting for this character.</returns>
        public static async Task<bool> AcceptAsync(NetworkStream stream)
        {
            var me = SessionContext.Current;
            var commission = me.State.Commission;
            if (commission == null || commission.Accepted || commission.InviterId == me.CharacterId) return commission != null;

            var crafter = SessionRegistry.FindByCharacter(commission.CrafterId);
            var customer = SessionRegistry.FindByCharacter(commission.CustomerId);
            if (crafter == null || customer == null || !SkillManager.TryGet(commission.SkillId, out var skill))
            {
                await EndAsync(commission);
                return true;
            }

            commission.Accepted = true;
            crafter.State.Workshop = new WorkshopHandler.Bench
            {
                SkillId = commission.SkillId, Magus = true, Commission = commission,
            };

            int job = skill.ParentJobId;
            int level = crafter.State.JobLevel(job);
            long experience = crafter.State.Jobs.TryGetValue(job, out var progress) ? progress.Experience : 0;
            await SendAsync(customer, Op.Iss, WorkshopProtocol.BuildOtherJob(job, JobExperience.Next(level), level,
                JobExperience.Floor(level), experience, crafter.CharacterId));
            await SendAsync(customer, Op.Kgw, WorkshopProtocol.BuildCustomerOpened(level, commission.SkillId));
            await SendAsync(crafter, Op.Keg, WorkshopProtocol.BuildCrafterOpened(commission.SkillId));
            Console.WriteLine($"[Commission] {me.CharacterId} accepts: magus {crafter.CharacterId} (job {job} " +
                              $"level {level}), customer {customer.CharacterId}, skill {commission.SkillId}.");
            return true;
        }

        // ─── The customer ───────────────────────────────────────────────────────────────────

        /// <summary>kcr from the customer: a stack laid down on the offer, or taken back.</summary>
        /// <returns>False when this character is no customer of an open commission.</returns>
        public static async Task<bool> OfferAsync(NetworkStream stream, byte[] payload)
        {
            var me = SessionContext.Current;
            var commission = me.State.Commission;
            if (commission == null || !commission.Accepted || commission.CustomerId != me.CharacterId) return false;
            byte[]? kcr = ConnectionProtocol.ReadPayload(payload, Op.Kcr);
            if (kcr == null) return true;
            int delta = (int)VarOf(kcr, 1);
            long uid = VarOf(kcr, 2);

            await commission.Gate.WaitAsync();
            try
            {
                var crafter = SessionRegistry.FindByCharacter(commission.CrafterId);
                var bench = crafter?.State.Workshop;
                if (bench != null && (bench.Item == uid || bench.RuneSlot?.Uid == uid || bench.SignatureSlot?.Uid == uid))
                    return true;   // on the table: the magus decides

                var item = Equipment.ByUid(uid);
                int index = commission.Offer.FindIndex(o => o.Uid == uid);
                int had = index >= 0 ? commission.Offer[index].Quantity : 0;
                int now = item == null || item.Position != Equipment.Bag ? 0 : Math.Clamp(had + delta, 0, item.Quantity);
                if (now == had) return true;

                if (now == 0)
                {
                    commission.Offer.RemoveAt(index);
                    await me.SendAsync(ConnectionProtocol.Push(Op.Keo, WorkshopProtocol.BuildUnoffered(uid)));
                    await SendAsync(crafter, Op.Keo, WorkshopProtocol.BuildUnoffered(uid));
                }
                else
                {
                    if (index >= 0) commission.Offer[index] = (uid, now);
                    else commission.Offer.Add((uid, now));
                    await me.SendAsync(ConnectionProtocol.Push(Op.Ked, WorkshopProtocol.BuildOffered(item!.Template, item.Effects, now, uid, remote: false)));
                    await SendAsync(crafter, Op.Ked, WorkshopProtocol.BuildOffered(item.Template, item.Effects, now, uid, remote: true));
                }
                await NotReadyAsync(commission, me, crafter);
            }
            finally { commission.Gate.Release(); }
            return true;
        }

        /// <summary>kee from the customer: what they will pay.</summary>
        public static async Task<bool> PaymentAsync(NetworkStream stream, byte[] payload)
        {
            var me = SessionContext.Current;
            var commission = me.State.Commission;
            if (commission == null || !commission.Accepted || commission.CustomerId != me.CharacterId) return false;
            byte[]? kee = ConnectionProtocol.ReadPayload(payload, Op.Kee);
            if (kee == null) return true;

            await commission.Gate.WaitAsync();
            try
            {
                commission.Payment = Math.Clamp(VarOf(kee, 1), 0, me.State.Kamas);
                var crafter = SessionRegistry.FindByCharacter(commission.CrafterId);
                await NotReadyAsync(commission, me, crafter);
                byte[] kcl = ConnectionProtocol.Push(Op.Kcl, WorkshopProtocol.BuildPayment(commission.Payment));
                await me.SendAsync(kcl);
                await SendAsync(crafter, Op.Kcl, WorkshopProtocol.BuildPayment(commission.Payment));
            }
            finally { commission.Gate.Release(); }
            return true;
        }

        /// <summary>kep from the customer: ready, or not.</summary>
        public static async Task<bool> ReadyAsync(NetworkStream stream, byte[] payload)
        {
            var me = SessionContext.Current;
            var commission = me.State.Commission;
            if (commission == null || !commission.Accepted || commission.CustomerId != me.CharacterId) return false;
            byte[]? kep = ConnectionProtocol.ReadPayload(payload, Op.Kep);
            if (kep == null) return true;

            await commission.Gate.WaitAsync();
            try
            {
                commission.CustomerReady = VarOf(kep, 1) != 0;
                var crafter = SessionRegistry.FindByCharacter(commission.CrafterId);
                byte[] kgt = WorkshopProtocol.BuildReady(commission.CustomerReady, me.CharacterId);
                await me.SendAsync(ConnectionProtocol.Push(Op.Kgt, kgt));
                await SendAsync(crafter, Op.Kgt, kgt);
            }
            finally { commission.Gate.Release(); }
            return true;
        }

        private static async Task NotReadyAsync(Commission commission, GameSession customer, GameSession? crafter)
        {
            if (!commission.CustomerReady) return;
            commission.CustomerReady = false;
            byte[] kgt = WorkshopProtocol.BuildReady(false, customer.CharacterId);
            await customer.SendAsync(ConnectionProtocol.Push(Op.Kgt, kgt));
            await SendAsync(crafter, Op.Kgt, kgt);
        }

        // ─── The magus ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// kgd from the magus: one of the customer's stacks onto the table -- the item, a rune, a
        /// signature -- or back to the offer.
        /// </summary>
        public static async Task MoveAsync(NetworkStream stream, byte[] payload)
        {
            var me = SessionContext.Current;
            var commission = me.State.Commission;
            var bench = me.State.Workshop;
            byte[]? kgd = ConnectionProtocol.ReadPayload(payload, Op.Kgd);
            if (commission == null || !commission.Accepted || commission.CrafterId != me.CharacterId
                || bench?.Commission != commission || kgd == null) return;
            int delta = (int)VarOf(kgd, 1);
            long uid = VarOf(kgd, 4);

            await commission.Gate.WaitAsync();
            try
            {
                var customer = SessionRegistry.FindByCharacter(commission.CustomerId);
                if (customer == null) return;
                var item = As(customer, () => Equipment.ByUid(uid));
                if (item == null) return;

                if (delta > 0)
                {
                    int index = commission.Offer.FindIndex(o => o.Uid == uid);
                    if (index < 0) return;
                    int quantity = commission.Offer[index].Quantity;
                    var template = Forgemagic.TemplateOf(item.Template);

                    // What goes where: the signature to its slot, a rune to the rune's, and the
                    // item to be maged -- of one of the table's types -- onto the table itself.
                    if (item.Template == Forgemagic.SignatureRune)
                        bench.SignatureSlot = (uid, true);
                    else if (Forgemagic.RuneOf(item.Template) != null || Forgemagic.TranscendenceOf(item.Template) != null)
                        bench.RuneSlot = (uid, true);
                    else if (template != null && SkillManager.TryGet(commission.SkillId, out var skill)
                             && (skill.ModifiableItemTypeIds.Contains(template.Type) || me.State.ForgeGod)
                             && bench.Item == 0)
                        bench.Item = uid;
                    else return;

                    commission.Offer.RemoveAt(index);
                    await me.SendAsync(ConnectionProtocol.Push(Op.Keo, WorkshopProtocol.BuildUnoffered(uid)));
                    await me.SendAsync(ConnectionProtocol.Push(Op.Kfb,
                        WorkshopProtocol.BuildAdded(item.Template, item.Effects, quantity, uid, withFloat: false)));
                    await SendAsync(customer, Op.Keo, WorkshopProtocol.BuildUnoffered(uid));
                    await SendAsync(customer, Op.Kfb,
                        WorkshopProtocol.BuildAdded(item.Template, item.Effects, quantity, uid, withFloat: false, remote: true));
                    return;
                }

                // Back to the offer.
                if (bench.Item == uid) bench.Item = 0;
                else if (bench.RuneSlot?.Uid == uid && bench.RuneSlot.Value.Customers) bench.RuneSlot = null;
                else if (bench.SignatureSlot?.Uid == uid && bench.SignatureSlot.Value.Customers) bench.SignatureSlot = null;
                else return;

                int back = Math.Max(1, item.Quantity);
                commission.Offer.Add((uid, back));
                await me.SendAsync(ConnectionProtocol.Push(Op.Kfs, WorkshopProtocol.BuildRemoved(uid)));
                await me.SendAsync(ConnectionProtocol.Push(Op.Ked,
                    WorkshopProtocol.BuildOffered(item.Template, item.Effects, back, uid, remote: false)));
                await SendAsync(customer, Op.Kfs, WorkshopProtocol.BuildRemoved(uid, remote: true));
                await SendAsync(customer, Op.Ked,
                    WorkshopProtocol.BuildOffered(item.Template, item.Effects, back, uid, remote: true));
            }
            finally { commission.Gate.Release(); }
        }

        // ─── The end ────────────────────────────────────────────────────────────────────────

        /// <summary>kla from either of the two: the commission ends, paid if it is owed.</summary>
        /// <returns>False when this character has no commission.</returns>
        public static async Task<bool> CloseAsync()
        {
            var commission = SessionContext.Current.State.Commission;
            if (commission == null) return false;
            await EndAsync(commission);
            return true;
        }

        /// <summary>A character leaves -- the map, the game -- in the middle of one.</summary>
        public static async Task AbandonAsync(GameSession session)
        {
            var commission = session.State.Commission;
            if (commission != null) await EndAsync(commission);
        }

        /// <summary>
        /// Closes the commission for both: the payment if it is owed (the customer ready, a rune on
        /// their item), the windows, and each one's bag again.
        /// </summary>
        public static async Task EndAsync(Commission commission)
        {
            await commission.Gate.WaitAsync();
            try
            {
                if (commission.Ended) return;
                commission.Ended = true;

                var crafter = SessionRegistry.FindByCharacter(commission.CrafterId);
                var customer = SessionRegistry.FindByCharacter(commission.CustomerId);
                long paid = 0;
                if (commission.Accepted && commission.CustomerReady && commission.Worked && commission.Payment > 0
                    && crafter != null && customer != null && customer.State.Kamas >= commission.Payment)
                {
                    paid = commission.Payment;
                    As(customer, () => { customer.State.Kamas -= paid; DatabaseManager.SaveCurrentCharacter(); return 0; });
                    As(crafter, () => { crafter.State.Kamas += paid; DatabaseManager.SaveCurrentCharacter(); return 0; });
                }

                foreach (var session in new[] { crafter, customer })
                {
                    if (session == null) continue;
                    session.State.Commission = null;
                    if (session.State.Workshop?.Commission == commission) session.State.Workshop = null;
                }

                if (crafter != null)
                {
                    if (paid > 0)
                    {
                        await SendAsync(crafter, Op.Lqs, WorkshopProtocol.BuildPaymentLine(paid));
                        await SendAsync(crafter, Op.Lqn, WorkshopProtocol.BuildPaymentInfo(paid));
                        await SendAsync(crafter, Op.Ivf, ConnectionProtocol.BuildKamas(crafter.State.Kamas));
                    }
                    await CloseWindowAsync(crafter, commission);
                }
                if (customer != null)
                {
                    if (paid > 0) await SendAsync(customer, Op.Ivf, ConnectionProtocol.BuildKamas(customer.State.Kamas));
                    await CloseWindowAsync(customer, commission);
                }
                Console.WriteLine($"[Commission] Magus {commission.CrafterId} and customer {commission.CustomerId}: " +
                                  (commission.Accepted ? $"closed, {paid} kamas paid." : "invitation withdrawn."));
            }
            finally { commission.Gate.Release(); }
        }

        private static async Task CloseWindowAsync(GameSession session, Commission commission)
        {
            if (commission.Accepted) await SendAsync(session, Op.Kcl, WorkshopProtocol.BuildPayment(0));
            await SendAsync(session, Op.Khd, ConnectionProtocol.BuildShopClosed());
            if (commission.Accepted)
                await SendAsync(session, Op.Ivx, As(session, ConnectionProtocol.BuildInventory));
            await SendAsync(session, Op.Hlm, Array.Empty<byte>());
        }

        private static long VarOf(byte[] body, int field)
        {
            foreach (var f in ProtoMessage.Parse(body).Fields)
                if (f.FieldNumber == field && f.WireType == 0) return f.VarIntValue;
            return 0;
        }
    }
}
