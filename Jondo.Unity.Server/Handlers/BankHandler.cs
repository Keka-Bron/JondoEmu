using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The bank at the counter: the banker's offer, the fee, the window, items and kamas in and
    /// out, and closing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on "Interactivos varios/entrar en banco bonta-abrir cofre gremio-usarlo-abrir
    /// cofre personal del banco.pcapng", frame numbers being positions in <c>hilo.tramas</c>:
    ///
    ///   72  C iov { f1: 3, f2: map, f3: banker }        talk to the banker
    ///   73  S ioc { f4: map, f5: banker }
    ///   74  S ios { f1: greeting, f2: bank reply { f3 { f1: 196 } }, f2: other, f3: "fee" }
    ///   79  C ioy { f1: bank reply }                     "Quiero consultar mi cofre."
    ///   80  S kld { f1: 1 }                              the dialogue closes
    ///   81  S lqn { f2: 20, f4: "1397" }                 "Has pagado 1397 kamas ..."
    ///   82  S ivf { f1: 66138278 }                       the purse, fee paid
    ///   83  S kci { f1: 2147483647, f3: 16 }             the bank's window
    ///   84  S iwb { f1 (x1397): the stacks }             what is inside
    ///   88  C kla                                         close
    ///   89  S khd { f3: 11 }
    ///
    /// The bank opens from the banker's reply, not from an interactive: see <see cref="Bankers"/>.
    /// </para>
    /// <para>
    /// Moving items is kcr, the message every storage window uses. That visit only opens the bank
    /// and closes it; the kcr are measured on the guild chest opened a minute earlier in the same
    /// bank, same window family (kci/iwb), frames 48-51 and 61-64:
    ///
    ///   C kcr { f1: 1,  f2: 534451715 }    in:  S itd (537272712), ium 534451715, iun
    ///   C kcr { f1: -1, f2: 537272712 }    out: S iua (537283702), itc 537272712, iun
    ///
    /// <b>The sign of f1 is the direction</b> and its size how many. The bin in front of that bank
    /// settles it ("abrir papelera frente a banco bonta y sacar cosas", frames 31-47): four kcr
    /// { f1: -1 } on one stack of four take ONE each time -- itd leaves 3, then 2, then 1, and the
    /// fourth sends itc -- while the bag's stack grows by ivj 2, 3, 4. So -1 is "one out", not "the
    /// whole stack". What arrives on an identical stack is ivj on that stack; what leaves part of a
    /// stack is the stack sent again with what remains (itd on the storage's side, ivj on the
    /// bag's, the latter by symmetry: no partial deposit was captured).
    /// </para>
    /// <para>
    /// Kamas: no capture moves kamas in any storage. kee { f1: kamas } is the message the client
    /// uses to put kamas in an exchange (the trade captures, see <see cref="TradeHandler"/>), and
    /// here it is read as kcr is: positive in, negative out. That is an inference; so is the
    /// answer, the purse (ivf) and the bank's content again with its new total in f2 (see
    /// <see cref="BankProtocol.BuildContent"/>), because the message the client may have for the
    /// bank's total alone was never seen on the wire.
    /// </para>
    /// </remarks>
    public static class BankHandler
    {
        /// <summary>
        /// Whether the bank is open for this session. The map is kept rather than a flag, so a
        /// window left open by a jump somewhere else stops taking items the moment the map changes.
        /// </summary>
        public static bool IsOpen
            => SessionContext.State.BankMapId != 0 && SessionContext.State.BankMapId == SessionContext.State.MapId;

        /// <summary>The effect the bank reply carries in the ios, frame 74.</summary>
        public static readonly IReadOnlyList<long> ConsultReplyEffects = new long[] { BankProtocol.ConsultEffect };

        // ─── The banker's offer ─────────────────────────────────────────────────

        /// <summary>
        /// The replies a banker offers: the bank's in front, then whatever else he was going to
        /// say, without repeating any.
        /// </summary>
        /// <remarks>
        /// In front because that is where frame 74 has it: 63535, the bank, then 63536. With no
        /// written conversation the Bontarian banker's template only yields its last reply, 63536,
        /// so the capture's two come out exactly.
        /// </remarks>
        public static long[] WithTheBankReply(Bankers.Banker banker, long[] replies)
        {
            var all = new List<long>((replies?.Length ?? 0) + 1) { banker.Consult };
            if (replies != null)
            {
                foreach (long reply in replies)
                {
                    if (!all.Contains(reply)) all.Add(reply);
                }
            }
            return all.ToArray();
        }

        /// <summary>The numbers the banker's greeting needs: the fee this account would pay now.</summary>
        public static string[] GreetingParameters(long accountId)
            => new[] { Bank.FeeOf(accountId).ToString(CultureInfo.InvariantCulture) };

        // ─── Opening ────────────────────────────────────────────────────────────

        /// <summary>
        /// The bank reply was chosen: the dialogue closes, the fee is paid and the bank opens.
        /// </summary>
        /// <remarks>
        /// Frames 80 to 84 in that order. Two cases the capture does not have:
        ///
        ///   a bank with nothing in it costs nothing, and then the lqn and the ivf are not sent --
        ///   there is nothing paid to tell and the purse has not changed;
        ///   a player short of the fee gets the client's own sentence for it (see
        ///   <see cref="BankProtocol.FeeUnpaid"/>) and no window.
        /// </remarks>
        public static async Task OpenAsync(NetworkStream stream)
        {
            await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(ConnectionProtocol.NpcDialogCloseReason));

            long account = SessionContext.Current.AccountId;
            long character = SessionContext.State.CharacterId;

            var opening = await Bank.OpenAsync(account, character, SessionContext.State.Kamas);
            if (opening == null) return;

            if (!opening.Paid)
            {
                await SendAsync(stream, Op.Lqn, BankProtocol.BuildFeeUnpaid(opening.Fee));
                Console.WriteLine($"[Bank] {character} cannot pay the {opening.Fee} kamas of the bank.");
                return;
            }

            if (opening.Fee > 0)
            {
                SessionContext.State.Kamas = opening.CharacterKamas;
                await SendAsync(stream, Op.Lqn, BankProtocol.BuildFeePaid(opening.Fee));
                await SendAsync(stream, Op.Ivf, ConnectionProtocol.BuildKamas(opening.CharacterKamas));
            }

            SessionContext.State.BankMapId = SessionContext.State.MapId;
            await SendAsync(stream, Op.Kci, BankProtocol.BuildOpened());
            await SendAsync(stream, Op.Iwb, BankProtocol.BuildContent(opening.Items, opening.Kamas));

            Console.WriteLine($"[Bank] Opened for account {account}: {opening.Items.Count} stack(s), " +
                              $"{opening.Kamas} kamas, fee {opening.Fee}.");
        }

        /// <summary>Close (kla): khd { f3: 11 }, frames 88-89, the same as any storage.</summary>
        public static async Task CloseAsync(NetworkStream stream)
        {
            SessionContext.State.BankMapId = 0;
            await SendAsync(stream, Op.Khd, ConnectionProtocol.BuildStorageClosed());
        }

        // ─── Items ──────────────────────────────────────────────────────────────

        /// <summary>
        /// kcr with the bank open. False when the bank is not open, so the next window in line
        /// gets it.
        /// </summary>
        public static async Task<bool> MoveAsync(NetworkStream stream, byte[] payload)
        {
            if (!IsOpen) return false;

            byte[]? kcr = ConnectionProtocol.ReadPayload(payload, Op.Kcr);
            if (kcr == null) return true;

            long quantity = 0, uid = 0;
            foreach (var field in ProtoMessage.Parse(kcr).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) quantity = (int)field.VarIntValue;   // int32: -1 is ten bytes of ones
                else if (field.FieldNumber == 2) uid = field.VarIntValue;
            }
            if (uid == 0 || quantity == 0) return true;

            long account = SessionContext.Current.AccountId;
            long character = SessionContext.State.CharacterId;

            if (quantity > 0)
            {
                var deposit = await Bank.DepositAsync(account, character, uid, (int)Math.Min(quantity, int.MaxValue));
                if (deposit == null)
                {
                    Console.WriteLine($"[Bank] {uid} x{quantity} could not go into the bank.");
                    return true;
                }

                Equipment.Remove(deposit.FromUid, deposit.Moved);
                RefreshLegacyInventory(character);

                await SendAsync(stream, Op.Itd, ConnectionProtocol.BuildItemArrived(1, deposit.InBank));
                if (deposit.LeftInBag == 0)
                    await SendAsync(stream, Op.Ium, ConnectionProtocol.BuildItemGone(deposit.FromUid));
                else
                    await SendAsync(stream, Op.Ivj, ConnectionProtocol.BuildItemQuantity(deposit.FromUid, deposit.LeftInBag));
            }
            else
            {
                var withdrawal = await Bank.WithdrawAsync(account, character, uid, (int)Math.Min(-quantity, int.MaxValue));
                if (withdrawal == null)
                {
                    Console.WriteLine($"[Bank] {uid} x{-quantity} could not come out of the bank.");
                    return true;
                }

                Equipment.Add(withdrawal.InBag.Uid, withdrawal.InBag.Gid, withdrawal.Moved, Equipment.Bag,
                              withdrawal.InBag.Effects);
                RefreshLegacyInventory(character);

                if (withdrawal.Merged)
                    await SendAsync(stream, Op.Ivj, ConnectionProtocol.BuildItemQuantity(withdrawal.InBag.Uid, withdrawal.InBag.Quantity));
                else
                    await SendAsync(stream, Op.Iua, ConnectionProtocol.BuildItemArrived(3, withdrawal.InBag));

                if (withdrawal.LeftInBank == null)
                    await SendAsync(stream, Op.Itc, ConnectionProtocol.BuildItemGone(withdrawal.FromUid));
                else
                    await SendAsync(stream, Op.Itd, ConnectionProtocol.BuildItemArrived(1, withdrawal.LeftInBank));
            }

            await SendAsync(stream, Op.Iun, ConnectionProtocol.BuildPods(0, 1000 + 5L * SessionContext.State.TotalStrength));

            // Another character of this account at a counter of his own sees the same bank.
            await RefreshWindowsAsync(account, SessionContext.Current.Id);
            return true;
        }

        /// <summary>
        /// The legacy inventory list, which some readers still use, read again from the database:
        /// what NpcHandler does after paying in tokens and FightHandler after the loot.
        /// </summary>
        private static void RefreshLegacyInventory(long characterId)
        {
            try
            {
                GameState.SetInventory(DatabaseManager.LoadInventory(characterId));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bank] The inventory list could not be read again: {ex.Message}");
            }
        }

        // ─── Kamas ──────────────────────────────────────────────────────────────

        /// <summary>
        /// kee with the bank open: kamas in (positive) or out (negative). False when the bank is
        /// not open. Inference throughout; see the remarks on the class.
        /// </summary>
        public static async Task<bool> KamasAsync(NetworkStream stream, byte[] payload)
        {
            if (!IsOpen) return false;

            byte[]? kee = ConnectionProtocol.ReadPayload(payload, Op.Kee);
            if (kee == null) return true;

            long amount = 0;
            foreach (var field in ProtoMessage.Parse(kee).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) amount = field.VarIntValue;
            }
            if (amount == 0) return true;

            long account = SessionContext.Current.AccountId;
            var moved = await Bank.MoveKamasAsync(account, SessionContext.State.CharacterId,
                                                  SessionContext.State.Kamas, amount);
            if (moved == null) return true;

            SessionContext.State.Kamas = moved.Value.Character;
            await SendAsync(stream, Op.Ivf, ConnectionProtocol.BuildKamas(moved.Value.Character));

            // The bank's new total goes to every open window of the account, this one included,
            // in the same turn as a credit from outside would: see RefreshWindowsAsync.
            if (SessionRegistry.TryGet(SessionContext.Current.Id, out _))
                await RefreshWindowsAsync(account, null);
            else
                await SendAsync(stream, Op.Iwb, BankProtocol.BuildContent(Bank.ItemsOf(account), Bank.KamasOf(account)));

            Console.WriteLine($"[Bank] {moved.Value.Moved:+#;-#;0} kamas for account {account}: " +
                              $"{moved.Value.Bank} in the bank, {moved.Value.Character} in the purse.");
            return true;
        }

        // ─── The other windows ──────────────────────────────────────────────────

        /// <summary>
        /// Sends the bank's content again to every window of that account that has it open, but
        /// the one given.
        /// </summary>
        /// <remarks>
        /// What the marketplace's credit to an absent seller, or one character's move seen from
        /// another of the same account, amounts to on the screen. The whole content and not a
        /// piece of it because the frame for the bank's total alone has not been seen; see the
        /// remarks on the class.
        /// </remarks>
        public static async Task RefreshWindowsAsync(long accountId, Guid? except)
        {
            if (accountId <= 0) return;

            var windows = new List<GameSession>();
            foreach (var session in SessionRegistry.InWorld())
            {
                if (session.AccountId != accountId) continue;
                if (except.HasValue && session.Id == except.Value) continue;

                var state = session.State;
                if (state.BankMapId == 0 || state.BankMapId != state.MapId) continue;
                windows.Add(session);
            }
            if (windows.Count == 0) return;

            // One refresh at a time per account, reading the base INSIDE the turn: two credits
            // landing together would otherwise be able to deliver the older total last.
            var turn = _refreshTurns.GetOrAdd(accountId, _ => new System.Threading.SemaphoreSlim(1, 1));
            await turn.WaitAsync().ConfigureAwait(false);
            try
            {
                byte[] content = ConnectionProtocol.Push(Op.Iwb,
                    BankProtocol.BuildContent(Bank.ItemsOf(accountId), Bank.KamasOf(accountId)));
                foreach (var session in windows)
                {
                    try
                    {
                        await session.SendAsync(content).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Bank] The bank window of {session.CharacterId} could not be told: {ex.Message}");
                    }
                }
            }
            finally
            {
                turn.Release();
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, System.Threading.SemaphoreSlim> _refreshTurns = new();

        // ─── Wire ───────────────────────────────────────────────────────────────

        /// <summary>
        /// To this session's client. With no stream -- a test, or anything run outside a
        /// connection -- it goes through the session, which drops it when there is no socket.
        /// </summary>
        private static Task SendAsync(NetworkStream? stream, string opcode, byte[] body)
            => stream != null
                ? Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(opcode, body))
                : SessionContext.Current.SendAsync(ConnectionProtocol.Push(opcode, body));
    }
}
