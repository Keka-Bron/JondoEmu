using System.Collections.Generic;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The frames of a house: its plaque, its sale, its codes.
    /// </summary>
    /// <remarks>
    /// Measured on the nine captures of "Casas/", OwnerAccount's house 522652 behind door 522653 of
    /// map 212601864, instance 22; frame numbers are positions in <c>hilo.tramas</c> of each file.
    /// The pandala capture ("Movimiento/ruta muy larga zonas pandala-sidimote-muchos
    /// interactivos", frames 3882-3890) is the only one where somebody knocks at a locked door
    /// that is not his.
    /// </remarks>
    public static class HouseProtocol
    {
        /// <summary>What a plaque says about one house.</summary>
        public sealed class Plaque
        {
            public int Instance { get; init; }

            /// <summary>The owner's account nickname and tag; empty for a house nobody owns.</summary>
            public string OwnerName { get; init; } = "";
            public string OwnerTag { get; init; } = "";

            /// <summary>The sale price; zero when not on sale.</summary>
            public long Price { get; init; }

            /// <summary>Whether it has an access code. See <see cref="Build"/>, f5.</summary>
            public bool Locked { get; init; }

            /// <summary>The model's rooms: f6 of the full plaque.</summary>
            public int Rooms { get; init; }

            /// <summary>The chests in every room (f10) and the workshops' skills (f11) of the full plaque.</summary>
            public int Chests { get; init; }
            public IReadOnlyList<long> Skills { get; init; } = System.Array.Empty<long>();

            /// <summary>
            /// The guild block of a house shared with a guild (f3), as it is to travel; null for
            /// none. See <see cref="Handlers.HouseHandler.GuildBlockOf"/>.
            /// </summary>
            public byte[]? GuildBlock { get; init; }

            public bool HasOwner => OwnerName.Length > 0;
        }

        /// <summary>
        /// A house's plaque (lnx). The short one travels on the map -- jss f9 and f7, izz -- and
        /// the full one (<paramref name="full"/>) when it is asked for or listed: izr and jaa.
        /// </summary>
        /// <remarks>
        /// <code>
        ///   f3  the guild it is shared with             optional
        ///   f4  1                                        every one of the 728 izz
        ///   f5  1                                        see below
        ///   f6  rooms                                    full only: 3, the Casa grande de Bonta's
        ///   f7  price                                    only while on sale
        ///   f8  { f1: nickname, f2: tag }                the owning account
        ///   f9  1                                        720 of 728
        ///   f10 chests, f11 [339, workshop skills]      full only: 2 and [339] in the captures
        ///   f12 instance
        /// </code>
        /// f5 is read as "has an access code": the one house in the captures that answered a knock
        /// with the code keypad (pandala, frames 3882-3884) carries it, and so do 582 of the 728
        /// plaques. INFERENCE, and not a clean one: OwnerAccount's own house has f5 before its code
        /// is changed ("desde dentro de casa salir a fuera", frame 13) and never after, even with a
        /// code set and at the next login (jaa of "Gremio/comprar una raid", 2 September).
        ///
        /// A house with no owner has never been captured. Without one, f4 and f9 -- which only ever
        /// come with an owner -- are left out and f8 goes empty, so the client has an account to
        /// read and no name in it. Not captured, but seen in the client: it shows such a house as
        /// abandoned. It travels on the street too, because the client drops the buyer's khr of a
        /// house its map never declared -- the window did not open until it did.
        /// </remarks>
        public static Pb Build(Plaque plaque, bool full, bool withGuild = true)
        {
            var lnx = Pb.New();
            if (withGuild && plaque.GuildBlock != null) lnx.Bytes(3, plaque.GuildBlock);
            if (plaque.HasOwner) lnx.Var(4, 1);
            if (plaque.Locked) lnx.Var(5, 1);
            if (full) lnx.VarIfNotZero(6, plaque.Rooms);
            lnx.VarIfNotZero(7, plaque.Price);
            if (plaque.HasOwner)
            {
                lnx.Msg(8, Pb.New().Str(1, plaque.OwnerName).Str(2, plaque.OwnerTag));
                lnx.Var(9, 1);
            }
            else
            {
                lnx.Msg(8, Pb.New());
            }
            if (full)
            {
                lnx.VarIfNotZero(10, plaque.Chests);
                if (plaque.Skills.Count > 0) lnx.Packed(11, plaque.Skills);
            }
            lnx.VarIfNotZero(12, plaque.Instance);
            return lnx;
        }

        /// <summary>
        /// A house changed (izz): { f1: plaque, f2: house, f3: packed doors }. Frames 14, 17 and 20
        /// of the sale, 21 and 24 of the withdrawal, 16 and 14 of the two code changes.
        /// </summary>
        public static byte[] BuildChanged(int houseId, IEnumerable<long> doors, Plaque plaque)
            => Pb.New().Msg(1, Build(plaque, false)).Var(2, houseId).Packed(3, doors).Build();

        /// <summary>
        /// A house's guild and rights (jak): { f1: instance, f2: guild block, f3: rights, f4: house }.
        /// The client's eqx finds the house in its list by f4 and f1, gives it the guild of f2 and
        /// hands f3 to the guild house window. Not captured; read off the client.
        /// </summary>
        public static byte[] BuildGuildRights(int houseId, int instance, byte[]? guild, int rights)
        {
            var jak = Pb.New().VarIfNotZero(1, instance);
            if (guild != null) jak.Bytes(2, guild);
            return jak.VarIfNotZero(3, rights).Var(4, houseId).Build();
        }

        /// <summary>
        /// The estate agency's page (izy): { f1: izw each, f2: page, f3: pages }; a house { f1:
        /// owner, f2: chests, f3 { f1: x, f2: y }, f4: owned, f5: price, f6: house, f7: owner's tag,
        /// f8: rooms, f9: locked, f11: model, f14: subarea }. See EstateAgencyHandler.
        /// </summary>
        public static byte[] BuildEstateList(IEnumerable<Handlers.EstateAgencyHandler.Listing> houses, int page, int pages,
                                             IReadOnlyDictionary<long, (string Name, string Tag)> owners)
        {
            var izy = Pb.New();
            foreach (var house in houses)
            {
                owners.TryGetValue(house.OwnerAccount, out var owner);
                izy.Msg(1, Pb.New()
                    .StrIfNotEmpty(1, owner.Name ?? "")
                    .VarIfNotZero(2, house.Chests)
                    .Msg(3, Pb.New().VarIfNotZero(1, house.X).VarIfNotZero(2, house.Y))
                    .VarIfNotZero(4, house.OwnerAccount > 0 ? 1 : 0)
                    .VarIfNotZero(5, house.Price)
                    .VarIfNotZero(6, house.HouseId)
                    .StrIfNotEmpty(7, owner.Tag ?? "")
                    .VarIfNotZero(8, house.Rooms)
                    .VarIfNotZero(9, house.Locked ? 1 : 0)
                    .VarIfNotZero(11, house.Model)
                    .VarIfNotZero(14, house.SubArea));
            }
            return izy.Var(2, page).Var(3, pages).Build();
        }

        /// <summary>The answer to izv (izr, response): { f1: full plaque }. Frame 5 of the sale.</summary>
        public static byte[] BuildInfo(Plaque plaque) => Pb.New().Msg(1, Build(plaque, true)).Build();

        /// <summary>One house of the account's list (jaa's lpx), with where its street is.</summary>
        public sealed class Owned
        {
            public int HouseId { get; init; }
            public int Model { get; init; }
            public long StreetMapId { get; init; }
            public int WorldX { get; init; }
            public int WorldY { get; init; }
            public int SubAreaId { get; init; }
            public Plaque Plaque { get; init; } = new Plaque();

            /// <summary>What the guild may do there, when it is shared: the guild's list carries it.</summary>
            public int GuildRights { get; init; }
        }

        /// <summary>Where a house of a list stands (lgl): { f1: street map, f2: x, f4: subarea, f5: y }.</summary>
        internal static Pb Where(Owned house)
            => Pb.New()
                .Var(1, house.StreetMapId)
                .VarIfNotZero(2, house.WorldX)
                .VarIfNotZero(4, house.SubAreaId)
                .VarIfNotZero(5, house.WorldY);

        /// <summary>
        /// One of the guild's houses (lpx, its oneof's f5): { f2: full plaque, f3: the guild's rights,
        /// f5: where } -- the only case the client's GuildHouses reads (lpx.cuzx), the rights enabling
        /// its «Ir» (bit 128). The capture's two: rights 505 and 217.
        /// </summary>
        private static Pb GuildLpx(Owned house)
            => Pb.New()
                .Var(2, house.HouseId)
                .Var(4, house.Model)
                .Msg(5, Pb.New()
                    .Msg(2, Build(house.Plaque, true, withGuild: false))
                    .VarIfNotZero(3, house.GuildRights)
                    .Msg(5, Where(house)));

        /// <summary>One of the guild's houses, shared or changed (jjp): { f1: lpx }.</summary>
        public static byte[] BuildGuildHouse(Owned house) => Pb.New().Msg(1, GuildLpx(house)).Build();

        /// <summary>
        /// A house that is not the guild's any more (jjt): { f3: instance, f4: house } -- the same jjt
        /// the sale capture sends when a house goes on sale (frame 15), which takes it from its guild.
        /// </summary>
        public static byte[] BuildGuildHouseGone(int houseId, int instance)
            => Pb.New().VarIfNotZero(3, instance).Var(4, houseId).Build();

        /// <summary>
        /// kind of the sale window (khr's f4): 1 in both windows captured. An enum of two values
        /// in the client; which purchasable 1 is is not known, and the same is used to buy.
        /// </summary>
        public const int SaleWindowKind = 1;

        /// <summary>
        /// The sale window (khr): { f1: buying, f2: house, f3: instance, f4: 1, f5: price }. Frame 3
        /// of the sale and of the withdrawal: f1 absent, the price the house is at, or its model's
        /// price -- 2,500,000 for the Casa grande de Bonta -- when it is not on sale.
        /// </summary>
        /// <remarks>
        /// <paramref name="buying"/> sets f1, the bool both captured windows leave out, for the
        /// window a buyer gets. INFERENCE: no capture buys a house, and f1 is the only field of
        /// the message that was not measured.
        /// </remarks>
        public static byte[] BuildSaleWindow(bool buying, int houseId, int instance, long price)
            => Pb.New()
                .VarIfNotZero(1, buying ? 1 : 0)
                .Var(2, houseId)
                .Var(3, instance)
                .Var(4, SaleWindowKind)
                .VarIfNotZero(5, price)
                .Build();

        /// <summary>
        /// jjt { f3: instance, f4: house }: the sale's frame 15, between taking the house off sale
        /// and putting it on at the new price. Only the sale sends it, not the withdrawal.
        /// </summary>
        public static byte[] BuildPutOnSale(int instance, int houseId)
            => Pb.New().Var(3, instance).Var(4, houseId).Build();

        /// <summary>
        /// izu { f2: nickname, f3: house, f5: tag, f6: price, f8: instance }: frame 19 of the sale
        /// (with its price) and 23 of the withdrawal (without).
        /// </summary>
        public static byte[] BuildSellingUpdate(string ownerName, string ownerTag, int houseId, long price, int instance)
            => Pb.New()
                .Str(2, ownerName ?? "")
                .Var(3, houseId)
                .Str(5, ownerTag ?? "")
                .VarIfNotZero(6, price)
                .Var(8, instance)
                .Build();

        /// <summary>
        /// kld's reason when the sale window closes: 3, frame 21 of the sale and 25 of the
        /// withdrawal.
        /// </summary>
        public const int SaleClosed = 3;

        /// <summary>
        /// kld's reason when a code keypad closes: 2, in the four captures that type one and in
        /// the pandala door.
        /// </summary>
        public const int CodeClosed = 2;

        /// <summary>
        /// The code keypad (kia): { f1: 1 to type a code to get in, f3: 8 }. Without f1 it is the
        /// owner's keypad to set one -- "cambiar codigo acceso", frame 6, and the chest's two --
        /// and with it the keypad of a locked door -- pandala, frame 3884. f3 is the length.
        /// </summary>
        public static byte[] BuildCodeKeypad(bool toGetIn)
            => Pb.New().VarIfNotZero(1, toGetIn ? 1 : 0).Var(3, Managers.HouseStore.CodeLength).Build();

        /// <summary>khu's f2 for a wrong code: 1, pandala frame 3890. Nothing when it went well.</summary>
        public const int WrongCode = 1;

        /// <summary>
        /// A code's outcome (khu): empty once a code is set -- the four keypads of "Casas/" -- and
        /// { f2: 1 } for a wrong one.
        /// </summary>
        public static byte[] BuildCodeResult(bool wrong) => Pb.New().VarIfNotZero(2, wrong ? WrongCode : 0).Build();

        /// <summary>
        /// An element redeclared (iwm): { f3: its declaration }, the same block as jss f11 -- the
        /// door with the skills it has now, frames 13 and 16 of the sale.
        /// </summary>
        public static byte[] BuildElementChanged(int elementId, int type, IEnumerable<(int Instance, int Skill)> skills)
        {
            var declaration = Pb.New().Var(1, 1);
            foreach (var (instance, skill) in skills)
                declaration.Msg(4, Pb.New().Var(1, instance).Var(2, skill));
            declaration.Var(5, elementId).Var(6, type);
            return Pb.New().Msg(3, declaration).Build();
        }
    }
}
