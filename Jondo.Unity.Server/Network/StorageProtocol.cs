using System.Collections.Generic;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The frames that open a storage window, one per kind of storage, and the guild chest's own.
    /// Moving items is the same in all of them and is built where it always was:
    /// <see cref="ConnectionProtocol.BuildItemArrived"/>, <see cref="ConnectionProtocol.BuildItemGone"/>,
    /// <see cref="ConnectionProtocol.BuildItemQuantity"/>.
    /// </summary>
    public static class StorageProtocol
    {
        // ─── kci: a storage opens ───────────────────────────────────────────────

        /// <summary>
        /// kci { f1: slots, f3: kind }. The kind is which storage the client is to draw, and each
        /// one is measured on its own capture:
        ///
        ///   house chest   f1 100, f3 4    "Casas/abrir cofre de la casa-mover items-cerrarlo", frame 8,
        ///                                  and the guild house's two chests, "Gremio/pocima hogar
        ///                                  a gremio", frames 62 and 92
        ///   bin           f1 100, f3 17   "abrir papelera frente a banco bonta y sacar cosas", frame 12
        ///   haven bag     f1 max, f3 19   "abrir cofre de mi merkasako-cambiar cosas...", frame 6
        ///   bank          f1 max, f3 16   see <see cref="BankProtocol.BuildOpened"/>
        ///
        /// f1 read as the slots is an inference; the numbers are the captures'.
        /// </summary>
        public static byte[] BuildOpened(int slots, int kind) => Pb.New().Var(1, slots).Var(3, kind).Build();

        public const int HouseChestKind = 4;
        public const int BinKind = 17;
        public const int HavenBagKind = 19;
        public const int ChestSlots = 100;

        public static byte[] BuildHouseChestOpened() => BuildOpened(ChestSlots, HouseChestKind);
        public static byte[] BuildBinOpened() => BuildOpened(ChestSlots, BinKind);
        public static byte[] BuildHavenBagOpened() => BuildOpened(int.MaxValue, HavenBagKind);

        // ─── The guild chest ────────────────────────────────────────────────────

        /// <summary>
        /// kbk { f1: 22, f2: tab, f3: 100 }: the guild chest's window, frame 29 of the bank capture,
        /// where kci would be for any other storage. f1 is the same enum as kci's kind, so 22 is
        /// the guild chest's; f2 the tab it opens on and f3 its slots, both inferences.
        /// </summary>
        public static byte[] BuildGuildChestOpened(int tab)
            => Pb.New().Var(1, Managers.GuildChests.WindowKind).Var(2, tab).Var(3, ChestSlots).Build();

        /// <summary>
        /// The guild chest's tabs (ivl), one f1 each: { f1: packed item types it takes, f2: tab,
        /// f3: right to take out, f4: right to put in, f5: 76, f6: right to look, f7: its name's
        /// key }. Frame 28 of the bank capture, frame 10 of "Gremio/muchas acciones en mi gremio
        /// como lider" for a guild with one tab.
        /// </summary>
        /// <remarks>
        /// Which right is which is the client's own table, GuildRightsDataRoot: for the first tab
        /// 8 "Consultar", 24 "Dejar", 23 "Retirar"; and f3/f4/f6 hold exactly those three in that
        /// order -- the second tab's 29/30/31 and the third's 34/33/32 (September) agree. f5 is 76
        /// in every tab of every capture; what it is is not known.
        /// </remarks>
        public static byte[] BuildGuildChestTabs(IEnumerable<Managers.GuildChests.Tab> tabs)
        {
            var ivl = Pb.New();
            foreach (var tab in tabs)
            {
                ivl.Msg(1, Pb.New()
                    .Packed(1, tab.ItemTypes)
                    .Var(2, tab.Index)
                    .Var(3, tab.WithdrawRight)
                    .Var(4, tab.DepositRight)
                    .Var(5, Managers.GuildChests.TabF5)
                    .Var(6, tab.ConsultRight)
                    .Str(7, tab.NameKey));
            }
            return ivl.Build();
        }

        /// <summary>
        /// Who is looking at the guild chest (jlo): f1 (repeated) their names. Frame 31, with the
        /// one character who had it open.
        /// </summary>
        public static byte[] BuildGuildChestViewers(IEnumerable<string> names)
        {
            var jlo = Pb.New();
            foreach (string name in names) jlo.Str(1, name ?? "");
            return jlo.Build();
        }

        /// <summary>jlq { f1: name }, frame 32, right behind jlo: the one who just opened it.</summary>
        public static byte[] BuildGuildChestViewer(string name) => Pb.New().Str(1, name ?? "").Build();
    }
}
