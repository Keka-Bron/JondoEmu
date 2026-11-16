using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The alliance prisms' world-list, crystal and alliance-fight frames.
    /// Map appearances use <see cref="MapActorMessages.Prism"/>.
    /// </summary>
    /// <remarks>
    /// Measured: the map actor of two prisms (a jss of «Chats/hablar por canal Comunidad», of
    /// «Alliance2», and one of «ruta muy larga con autopilotaje-2»), the world's list at world entry
    /// (ife) and a prism changed (ifa) in «Emotes/usar emoticonos». The rest -- one gone (iew),
    /// attacked (iex), its fight's end (iev) -- is read off fgv's handlers (bewt, bewo, bewp).
    /// </remarks>
    public static class PrismProtocol
    {
        /// <summary>The monster a prism is fought as: 3451, «Prisma de alianza».</summary>
        public const int Monster = 3451;

        /// <summary>
        /// The teleporter a prism's list is for (the hjj's root f4) and the tab its destinations go
        /// in (each one's f3, back in the hjc's f2): 0 is the zaap, 1 the zaapi, 3 the boat; the 2 in
        /// between is read as the prisms', the client's TeleporterTypeEnum order. An inference.
        /// </summary>
        public const int Teleporter = 2;
        public const int DestinationKind = 2;

        /// <summary>
        /// The crystals a prism may consume in its life: «Un prisma puede consumir solamente 4
        /// cristales sucesivos en su vida útil» (Translations 1149314).
        /// </summary>
        public const int MaxCrystals = 4;

        /// <summary>
        /// The storage kind (kci.f3) that opens the prism's crystal window: the client turns 18
        /// into its exchange 22 (gjp.bkme), which opens PrismModuleUi (fwy.bivq → bixl).
        /// </summary>
        public const int CrystalWindowKind = 18;

        /// <summary>A prism's fight ended (iev.f1): 0 not beaten (fgv.bewp: «ui.prism.notDefeated»), else weakened.</summary>
        public const int NotDefeated = 0;
        public const int Weakened = 1;

        // ─── The look ───────────────────────────────────────────────────────────

        /// <summary>
        /// A prism's look as a look string: the prism monster's bones, its symbol's prism skin, its
        /// first colour, and two colours of the emblem -- 9 its f3, 10 its f5 --.
        /// </summary>
        /// <remarks>
        /// Both prisms of the captures go {2211, colours 1=0xc2e5ee, 9, 10}: «Alliance2» (no f3, f5
        /// 0xb50104) with 9=0 and 10=0xb50104, and the other alliance (f3 0x1d1d1d, f5 0x3f3f3f)
        /// with those two. 2211 and 0xc2e5ee are the prism monster's own look. They also carry a
        /// skin, 2673 and 4793 for the symbols 104 and 434: no table of the client's says it, but
        /// they draw those symbols, and the rest of the set was matched by texture
        /// (<see cref="AllianceData.PrismSkinOfSymbol"/>). Without it the banner was blank.
        /// </remarks>
        public static string LookString(AllianceStore.Alliance alliance)
            => LookString(alliance, MobSpawnManager.GetMonsterData(Monster)?.Look);

        /// <summary>The same, from the prism monster's own look string.</summary>
        public static string LookString(AllianceStore.Alliance alliance, string monsterLook)
        {
            var variant = string.IsNullOrEmpty(monsterLook) ? null : Npcs.Variantes(monsterLook).FirstOrDefault();
            long bones = variant?.Bones ?? 0;
            long first = variant?.Colors.FirstOrDefault(c => (c >> 24) == 1) ?? 0;
            string colour1 = first == 0 ? "" : $"1={first & 0xFFFFFF},";
            int skin = alliance == null ? 0 : AllianceData.PrismSkinOfSymbol(alliance.EmblemSymbol);
            string skins = skin > 0 ? skin.ToString() : "";
            return $"{{{bones}|{skins}|{colour1}9={alliance?.EmblemBackground ?? 0},10={alliance?.EmblemSymbolRgb ?? 0}}}";
        }

        public static Pb Look(AllianceStore.Alliance alliance)
            => ConnectionProtocol.MonsterLook(LookString(alliance), hardcore: false);

        // ─── The prism ──────────────────────────────────────────────────────────

        /// <summary>
        /// What the actor and the lists say of a prism (ley): f2 when its state next changes, in
        /// milliseconds, f3 its state, f4 when it was placed, in seconds, f5 its alliance.
        /// </summary>
        /// <remarks>
        /// The client reads them so (ku.fhn, read in Ghidra): the conquests window writes the f4 as
        /// the creation date and, for a weakened or a pacified prism, the time left until the f2
        /// (AllianceConquestsUi.GetStateTimeLeft). Every prism of the captures' ife stands normal,
        /// and there the f2 is its placing, the f4 to the second: so it goes while normal.
        ///
        /// Its own alliance sees more (<paramref name="own"/>): the f1, an optional field the client
        /// takes for «mine», is the crystals it may still consume -- the crystal window's «{0} de 4
        /// cristales posibles», which takes none at 0 --, and the f6 (lno) the crystal it holds:
        /// { f2 the crystal, as an item of the bag (lec), f3 1 while it works }. With the f1 the
        /// client reads the f6's crystal unchecked (ku.fhn → me.ogk), so a prism with none still
        /// carries a lec, with an empty item: see <see cref="Crystal"/>. None of the 1,869 prisms of
        /// the captures is the player's own, so this side is all the client's code.
        /// </remarks>
        public static Pb State(PrismStore.Prism prism, AllianceStore.Alliance alliance, bool own = false)
        {
            var ley = Pb.New();
            if (own) ley.Var(1, Math.Max(0, MaxCrystals - prism.CrystalsUsed));
            ley.Var(2, prism.State == PrismStore.Normal || prism.NextChangeUtcMs <= 0 ? prism.PlacedUtcMs : prism.NextChangeUtcMs)
               .VarIfNotZero(3, prism.State)
               .Var(4, prism.PlacedUtcMs / 1000)
               .Msg(5, AllianceProtocol.Block(alliance));
            if (own) ley.Msg(6, Crystal(prism));
            return ley;
        }

        /// <summary>The crystal it holds (lno): { f2 the crystal (lec), f3 1 while it pacifies the prism }.</summary>
        /// <remarks>
        /// With no crystal the lec is still there, with an empty item (template 0): me.ogk throws on a
        /// missing lec or item, and the window's OnPrismAddOrUpdate leaves the slot empty for a
        /// template 0. Without it the owners' client threw on every prism of theirs without a crystal
        /// -- ifa on placing it, ife on entering the world --: no name over it, none on the minimap,
        /// and the map left half loaded, menus working and the character unable to move.
        /// </remarks>
        public static Pb Crystal(PrismStore.Prism prism)
        {
            var lno = Pb.New();
            if (prism.CrystalGid == 0) return lno.Msg(2, Pb.New().Msg(5, Pb.New()));
            var effects = DatabaseManager.TryGetItemTemplateEffects(prism.CrystalGid, out string json)
                ? Managers.Equipment.ParseEffects(json)
                : Array.Empty<Managers.Equipment.ItemEffect>();
            return lno.Msg(2, Pb.New().Msg(5, ConnectionProtocol.ItemBody(prism.CrystalGid, effects, 1, CrystalUid(prism))))
                      .VarIfNotZero(3, prism.HasActiveCrystal ? 1 : 0);
        }

        /// <summary>The crystal's uid in its slot: none of the bag's, so it is the prism's own id from far below.</summary>
        public static long CrystalUid(PrismStore.Prism prism) => 2_000_000_000L + prism.Id;

        /// <summary>
        /// A prism in the world's lists (lhm): f1 its map, f2 { f1 x, f2 y }, f3 its subarea, f4 its
        /// state, f5 its alliance's uuid. The 1,000-odd of the ife at world entry have this shape.
        /// </summary>
        public static Pb Information(PrismStore.Prism prism, AllianceStore.Alliance alliance, bool own = false)
        {
            var map = MapManager.GetMapInfo(prism.MapId);
            return Pb.New()
                .Var(1, prism.MapId)
                .Msg(2, Pb.New().VarIfNotZero(1, map?.PosX ?? 0).VarIfNotZero(2, map?.PosY ?? 0))
                .Var(3, prism.SubAreaId)
                .Msg(4, State(prism, alliance, own))
                .Str(5, alliance.Uuid);
        }

        /// <summary>One placed or changed (ifa): { f2 the prism }, as in «usar emoticonos».</summary>
        public static byte[] BuildChanged(Pb prism) => Pb.New().Msg(2, prism).Build();

        /// <summary>One gone (iew): { f1 its subarea }.</summary>
        public static byte[] BuildGone(int subAreaId) => Pb.New().Var(1, subAreaId).Build();

        /// <summary>One attacked (iex): { f1 the prism }; fgv.bewo writes «ui.prism.attacked» and its popup.</summary>
        public static byte[] BuildAttacked(Pb prism) => Pb.New().Msg(1, prism).Build();

        /// <summary>Its fight over (iev): { f1 how, f2 the prism }.</summary>
        public static byte[] BuildFightResult(int how, Pb prism) => Pb.New().VarIfNotZero(1, how).Msg(2, prism).Build();

        /// <summary>
        /// A prism as the first defender of its fight's line: named by its monster, «3451», which
        /// the client writes as «Prisma» (AllianceFightUi.GetFighterName), at its fight's level,
        /// with its look and the breed -2. It went nameless at level 1: «? (Niv.1)».
        /// </summary>
        public static Pb Fighter(PrismStore.Prism prism, AllianceStore.Alliance alliance)
            => TaxCollectorProtocol.Fighter(prism.ContextId, Monster.ToString(), TaxCollectorProtocol.LevelOf(Monster),
                                            Look(alliance).Build(), TaxCollectorProtocol.PrismListMarker);
    }
}
