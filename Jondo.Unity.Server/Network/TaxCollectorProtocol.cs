using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The tax collectors' frames and the alliance fights' list, the window where the members come
    /// to defend one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured: the map actor (a jss of «Busqueda grupo/busqueda automatica...», «Alliance2»'s
    /// tax collector on cell 352), the talk (ioc + ion) and the whole attack of «Gremio/atacar a
    /// recaudador y perder», and the lea of the 20 tax collectors of the hks in «Alianzas/ir a
    /// templo alianza». The rest is read off the client: the shapes with clientscan proto and the
    /// meaning of each field from the handlers of fgv (bewf-bexi) and the alliance fights'
    /// service ffc (bdjn, bdjs, bdjt, bdjo), in Ghidra.
    /// </para>
    /// <para>
    /// A tax collector is known by three numbers. Its uuid is what the client asks about (kbu,
    /// the menu's entity info 6) and what removes it (hlc). Its MAP is what the client keys it by:
    /// the kdc's handler stores the view under the lgl's f1. And its actor id is what the map and
    /// the fight call it (the kam's f3, the team's leader).
    /// </para>
    /// </remarks>
    public static class TaxCollectorProtocol
    {
        /// <summary>The monster a tax collector is fought as: 3859, «Recaudador», the f3 of its identity.</summary>
        public const int Monster = 3859;

        /// <summary>Its bones on a map and in a fight: 7312 in the jss, 7313 in the jxg of the attack.</summary>
        public const int MapBones = 7312;
        public const int FightBones = 7313;

        /// <summary>Its scale, the "s" (115) of both looks.</summary>
        public const int Scale = 115;

        /// <summary>
        /// What it can carry and its prospecting, the ion's f2 and f5: 1000 and 200 for the tax
        /// collector talked to in the capture, which is all a tax collector has before any equipment.
        /// </summary>
        public const int MaxPods = 1000;
        public const int Prospecting = 200;

        /// <summary>
        /// The breed a tax collector carries as a fighter of the alliance fights' list (lpc f7): -5,
        /// whose name is then read as its two name ids (ffc.bdjn) and whose slot shows the tax
        /// collector's icon (AllianceFightUi.BindCharacterSlot). It went as the level, which no one
        /// reads for that.
        /// </summary>
        public const int FighterListMarker = -5;

        /// <summary>
        /// A prism's breed in the same list: -2, the other one the slot treats as no player. Its
        /// name is its monster, «3451», which AllianceFightUi.GetFighterName writes as «Prisma».
        /// </summary>
        public const int PrismListMarker = -2;

        /// <summary>
        /// Why a tax collector could not be placed or touched (hla), as fgv.bewy words them: 0 an
        /// unknown error, 2 not yours, 3 no rights, 4 too many, 5 one already on the map, 6 too
        /// tired, 7 not here, 8 not rich enough.
        /// </summary>
        public const int ErrorUnknown = 0;
        public const int ErrorNotYours = 2;
        public const int ErrorNoRights = 3;
        public const int ErrorTooMany = 4;
        public const int ErrorAlreadyOnMap = 5;
        public const int ErrorTooTired = 6;
        public const int ErrorCantHireHere = 7;

        /// <summary>A tax collector's state (lby): collecting, waiting for help, fighting.</summary>
        public const int StateCollecting = 0;
        public const int StateWaitingForHelp = 1;
        public const int StateFighting = 2;

        /// <summary>The kind of an alliance fight (lci.f1, lcg): a tax collector's, a prism's.</summary>
        public const int FightOfTaxCollector = 0;
        public const int FightOfPrism = 1;

        /// <summary>
        /// The side of an alliance fight's fighter (mdx.f3, lbl): ffc.bdjs puts 0 among the
        /// attackers and 1 among the defenders, where the first one is the tax collector itself.
        /// </summary>
        public const int SideAttackers = 0;
        public const int SideDefenders = 1;

        /// <summary>
        /// An alliance fight's phase (lgp.f2): the UI runs a countdown to the lgp's f1 for phase 1
        /// (AllianceFightUi.PrepareOneFightData), and for no other.
        /// </summary>
        public const int PhaseWaiting = 1;
        public const int PhaseFighting = 2;

        // ─── The look ───────────────────────────────────────────────────────────

        /// <summary>
        /// A tax collector's look as a look string: its bones, its alliance's symbol as a skin, and
        /// two colours of the emblem -- 8 the emblem's f5, 7 its f3 --, at its scale.
        /// </summary>
        /// <remarks>
        /// Both looks of the captures: «Alliance2» (symbol 104, f5 11862276, no f3) dresses its tax
        /// collector with skin 1647 and colours 0x08b50104 and 0x07000000; «Alliance-3» (136,
        /// f3 1002575, f5 16630094) with 1762 and 0x08fdc14e, 0x070f4c4f. 1647 and 1762 are the
        /// skins of those two symbols in the client's EmblemSymbols.
        /// </remarks>
        public static string LookString(AllianceStore.Alliance alliance, int bones)
        {
            int skin = alliance == null ? 0 : AllianceData.SkinOfSymbol(alliance.EmblemSymbol);
            int symbolColour = alliance?.EmblemSymbolRgb ?? 0;
            int background = alliance?.EmblemBackground ?? 0;
            return $"{{{bones}|{(skin > 0 ? skin.ToString() : "")}|8={symbolColour},7={background}|{Scale}}}";
        }

        /// <summary>The look block (lhg) of a tax collector, never grown by Hardcore.</summary>
        public static Pb Look(AllianceStore.Alliance alliance, int bones)
            => ConnectionProtocol.MonsterLook(LookString(alliance, bones), hardcore: false);

        // ─── On the map ─────────────────────────────────────────────────────────

        /// <summary>
        /// The tax collector as an actor of its map (ljx): the kind 6 of the actor's f2.f1.
        ///
        ///   f1 { f1 cell, f2 direction }
        ///   f2 { f1 { f6 { f1 { f1 the alliance, f2 last name, f3 uuid, f4 who placed it, f5 first name } } }
        ///        f3 the look }
        ///   f3 its actor id
        ///
        /// Byte for byte «Alliance2»'s tax collector of the party search capture.
        /// </summary>
        /// <remarks>
        /// The last name goes first. gjc.bkiy, which writes this block back from the client's
        /// entity, puts entity info 10 in f2 and 9 in f5, and the tooltip (fnl.bhcj) looks 10 up
        /// among the TaxCollectorNames and 9 among the TaxCollectorFirstNames; the capture's
        /// f5 is 154, the last first name there is. With the two the other way round the first
        /// name was looked for among the 154 first names by a last name's id -- 241 of 253 --,
        /// found nothing, and the tax collector had no tooltip and no menu.
        /// </remarks>
        public static Pb Actor(TaxCollectorStore.TaxCollector tc, AllianceStore.Alliance alliance)
            => Pb.New()
                .Msg(1, Pb.New().Var(1, tc.CellId).Var(2, tc.Direction))
                .Msg(2, Pb.New()
                    .Msg(1, Pb.New().Msg(6, Pb.New().Msg(1, Pb.New()
                        .Msg(1, AllianceProtocol.Block(alliance))
                        .Var(2, tc.LastNameId)
                        .Str(3, tc.Uuid)
                        .Var(4, tc.OwnerId)
                        .Var(5, tc.FirstNameId))))
                    .Msg(3, Look(alliance, MapBones)))
                .Var(3, tc.ContextId);

        /// <summary>The actor drawn again or for the first time (jsn): { f1 the actor }.</summary>
        public static byte[] BuildShown(TaxCollectorStore.TaxCollector tc, AllianceStore.Alliance alliance)
            => Pb.New().Msg(1, Actor(tc, alliance)).Build();

        /// <summary>
        /// Where something stands (lgl): f1 the map, f2 and f5 its world coordinates, f4 its subarea.
        /// The handlers of hld and hjw read them so (fgv.beww, bewx): the subarea's name from f4 and
        /// "{x},{y}" from f2 and f5.
        /// </summary>
        public static Pb Position(long mapId, int subAreaId, int x, int y)
            => Pb.New().Var(1, mapId).VarIfNotZero(2, x).VarIfNotZero(4, subAreaId).VarIfNotZero(5, y);

        public static Pb Position(TaxCollectorStore.TaxCollector tc)
        {
            var map = MapManager.GetMapInfo(tc.MapId);
            return Position(tc.MapId, tc.SubAreaId, map?.PosX ?? 0, map?.PosY ?? 0);
        }

        /// <summary>What a tax collector is besides its place: what it carries, wears and casts.</summary>
        public sealed class Details
        {
            public int Pods { get; init; }
            public long Worth { get; init; }
            public IReadOnlyList<HavenBagStore.StoredItem> Equipment { get; init; } = Array.Empty<HavenBagStore.StoredItem>();
            public IReadOnlyDictionary<int, int> Spells { get; init; } = new Dictionary<int, int>();
            public IReadOnlyList<Pb> Sheet { get; init; } = Array.Empty<Pb>();
        }

        /// <summary>
        /// A tax collector as the alliance's lists carry it (lea): the sheet's f1 (mcx), the hjq of
        /// one placed, the hks of the tops, the consult (hkb) and the modification (kds).
        ///
        ///   f1 who placed it        f2 { f4 { f1 pods, f2 worth } }     f3 { f1 x, f2 y }
        ///   f4 first name           f5 what it wears (lec)              f6 his name
        ///   f7 its spells (lli)     f8 when, in seconds                 f9 its state
        ///   f10 last name           f11 its subarea                     f12 its uuid
        ///   f13 its look            f14 its alliance                    f15 its map
        ///   f16 its characteristics
        /// </summary>
        /// <remarks>
        /// Read off kx.nxi, which builds the client's view of one: the first name from f4, the last
        /// from f10, the subarea from f11, the pods and worth from the f2 of case 4. The twenty of
        /// the hks have this shape -- f1 the same character for the two tax collectors of
        /// «Attacker-Character», f6 his name, f2 {f4 {160, 622691}} --.
        /// </remarks>
        public static Pb Information(TaxCollectorStore.TaxCollector tc, AllianceStore.Alliance alliance, int state,
                                     Details details = null)
        {
            var map = MapManager.GetMapInfo(tc.MapId);
            var lea = Pb.New().Var(1, tc.OwnerId);
            if (details != null && (details.Pods != 0 || details.Worth != 0))
                lea.Msg(2, Pb.New().Msg(4, Pb.New().VarIfNotZero(1, details.Pods).VarIfNotZero(2, details.Worth)));
            lea.Msg(3, Pb.New().VarIfNotZero(1, map?.PosX ?? 0).VarIfNotZero(2, map?.PosY ?? 0))
               .Var(4, tc.FirstNameId);
            foreach (var item in details?.Equipment ?? Array.Empty<HavenBagStore.StoredItem>())
                lea.Msg(5, Worn(item));
            lea.StrIfNotEmpty(6, tc.OwnerName);
            foreach (var (slot, spellLevel) in details?.Spells ?? new Dictionary<int, int>())
                lea.Msg(7, Spell(slot, spellLevel));
            lea.Var(8, tc.PlacedUtcS)
               .VarIfNotZero(9, state)
               .Var(10, tc.LastNameId)
               .VarIfNotZero(11, tc.SubAreaId)
               .Str(12, tc.Uuid)
               .Msg(13, Look(alliance, MapBones))
               .Msg(14, AllianceProtocol.Block(alliance))
               .Var(15, tc.MapId);
            foreach (var entry in details?.Sheet ?? Array.Empty<Pb>())
                lea.Msg(16, entry);
            return lea;
        }

        // ─── What it wears and casts ────────────────────────────────────────────

        /// <summary>
        /// The seven kinds of item a tax collector wears, by the slot they go in (lec.f1): the
        /// item types of superType 69 -- tunic, plate, banner, daggers, saddlebags, chests and
        /// horseshoes. The hks measures two of them: chests (279) in 5 and saddlebags (280) in 4.
        /// </summary>
        public static readonly IReadOnlyDictionary<int, int> SlotOfType = new Dictionary<int, int>
        {
            [277] = 0, [274] = 1, [275] = 2, [276] = 3, [280] = 4, [279] = 5, [273] = 6,
        };

        /// <summary>
        /// One item it wears (lec): { f1 its slot, f5 the item }, the item as every item message
        /// carries it -- { f1 template, f2 effects, f3 quantity, f4 uid } --, which is also the
        /// shape of an item arriving in the bag. me.ogk reads the item from the f5.
        /// </summary>
        public static Pb Worn(HavenBagStore.StoredItem item)
        {
            int type = Forgemagic.TemplateOf(item.Gid)?.Type ?? 0;
            return Pb.New()
                .VarIfNotZero(1, SlotOfType.TryGetValue(type, out int slot) ? slot : 0)
                .Msg(5, ConnectionProtocol.ItemBody(item.Gid, Managers.Equipment.ParseEffects(item.Effects),
                                                    Math.Max(1, item.Quantity), item.Uid));
        }

        /// <summary>
        /// One spell (lli): { f2 the SpellLevels id, f3 its slot }. The capture's tax collectors
        /// carry six, 64128 in slot 0 (left out) to 64136 in slot 5. kv takes f2 and f3 as they are.
        /// </summary>
        public static Pb Spell(int slot, int spellLevel) => Pb.New().Var(2, spellLevel).VarIfNotZero(3, slot);

        /// <summary>
        /// One characteristic of its sheet (lns), as the kub carries the player's: { f1 id, f4 { f2
        /// base, f7 what its equipment adds } }, or f5 { f1, f5 } for those the client reads there.
        /// </summary>
        public static Pb Characteristic(int id, long baseValue, long bonus, int container)
        {
            var entry = Pb.New();
            if (id != 0) entry.Var(1, id);
            switch (container)
            {
                case 5: entry.Msg(5, Pb.New().Var(1, baseValue).VarIfNotZero(5, bonus)); break;
                case 2: entry.Msg(2, Pb.New().VarIfNotZero(2, baseValue + bonus)); break;
                default: entry.Msg(4, Pb.New().VarIfNotZero(2, baseValue).VarIfNotZero(7, bonus)); break;
            }
            return entry;
        }

        // ─── The tops ───────────────────────────────────────────────────────────

        /// <summary>
        /// The top tax collectors (hks): { f1 the world's, f3 the dungeons' }, each a whole lea. The
        /// capture's carry ten and ten and no f2. fvl.birh hands f3 and f1 to TopTaxCollectors.Init,
        /// whose rows show each one's alliance, worth and place, sorted by the client.
        /// </summary>
        public static byte[] BuildTops(IEnumerable<Pb> world, IEnumerable<Pb> dungeons)
        {
            var hks = Pb.New();
            foreach (var lea in world) hks.Msg(1, lea);
            foreach (var lea in dungeons) hks.Msg(3, lea);
            return hks.Build();
        }

        // ─── Consulting and modifying ───────────────────────────────────────────

        /// <summary>
        /// The tax collector to consult (hkb): { f2 its lea }. fgv.bexd keeps it and the window
        /// opens read-only (fvl.bipl → TaxCollectorEquipment.Init in consult mode).
        /// </summary>
        public static byte[] BuildConsult(Pb information) => Pb.New().Msg(2, information).Build();

        /// <summary>
        /// The tax collector to modify (kds): { f1 its lea }, an exchange. epd.banv keeps it and the
        /// window opens editable beside the bag (fvl.bipm → TaxCollectorEquipment.Init in edit mode).
        /// </summary>
        public static byte[] BuildModificationStarted(Pb information) => Pb.New().Msg(1, information).Build();

        /// <summary>
        /// What it wears changed (hkh): f3 put on or taken off, f4 the item (lec), f5 its uuid, f6 its
        /// sheet anew. fgv.bexe adds the item (kx.nxj) or takes off the one with its template (nxk).
        /// </summary>
        public static byte[] BuildEquipmentChanged(string uuid, bool wornNow, Pb item, IEnumerable<Pb> sheet)
        {
            var hkh = Pb.New().VarIfNotZero(3, wornNow ? 1 : 0).Msg(4, item).Str(5, uuid);
            foreach (var entry in sheet ?? Array.Empty<Pb>()) hkh.Msg(6, entry);
            return hkh.Build();
        }

        /// <summary>Its spells (hki): { f1 each one, f3 its uuid }; fgv.bexf hands them to the window.</summary>
        public static byte[] BuildSpells(string uuid, IReadOnlyDictionary<int, int> spells)
        {
            var hki = Pb.New();
            foreach (var (slot, spellLevel) in spells) hki.Msg(1, Spell(slot, spellLevel));
            return hki.Str(3, uuid).Build();
        }

        // ─── The alliance's default tax collector ───────────────────────────────

        /// <summary>
        /// The alliance's presets (hlb): { f1 { f1 the sheet, f2 the spells, f3 its id } }. fgv.bexh
        /// keeps them and the personalization tab shows the one it has selected, or the first.
        /// </summary>
        public static byte[] BuildPresets(string presetId, IReadOnlyDictionary<int, int> spells, IEnumerable<Pb> sheet)
        {
            var lfd = Pb.New();
            foreach (var entry in sheet ?? Array.Empty<Pb>()) lfd.Msg(1, entry);
            foreach (var (slot, spellLevel) in spells) lfd.Msg(2, Spell(slot, spellLevel));
            lfd.Str(3, presetId);
            return Pb.New().Msg(1, lfd).Build();
        }

        /// <summary>A preset's spells changed (hlh): { f1 its id, f2 the spells }.</summary>
        public static byte[] BuildPresetSpells(string presetId, IReadOnlyDictionary<int, int> spells)
        {
            var hlh = Pb.New().Str(1, presetId);
            foreach (var (slot, spellLevel) in spells) hlh.Msg(2, Spell(slot, spellLevel));
            return hlh.Build();
        }

        // ─── While its owner was away ───────────────────────────────────────────

        /// <summary>
        /// What happened to one's tax collectors while away (hkx): { f1 each one }, each { f1 who,
        /// f2 last name, f3 where, f4 first name, f5 his name, f6 how }. fgv.bexb writes
        /// «ui.guild.taxCollector(s)HarvestedWhileAbsence» for how 2, with a link to who, and
        /// «…DefeatedWhileAbsence» for how 3.
        /// </summary>
        public static byte[] BuildAbsences(IEnumerable<TaxCollectorStore.Absence> absences)
        {
            var hkx = Pb.New();
            foreach (var absence in absences)
            {
                var map = MapManager.GetMapInfo(absence.MapId);
                hkx.Msg(1, Pb.New()
                    .VarIfNotZero(1, absence.WhoId)
                    .Var(2, absence.LastNameId)
                    .Msg(3, Position(absence.MapId, absence.SubAreaId, map?.PosX ?? 0, map?.PosY ?? 0))
                    .Var(4, absence.FirstNameId)
                    .StrIfNotEmpty(5, absence.WhoName)
                    .Var(6, absence.Kind));
            }
            return hkx.Build();
        }

        // ─── Reading the requests ───────────────────────────────────────────────

        /// <summary>The varints and strings of a request's top level, by field.</summary>
        public static (Dictionary<int, long> Numbers, Dictionary<int, string> Texts, Dictionary<int, byte[]> Blocks) Read(byte[] request)
        {
            var numbers = new Dictionary<int, long>();
            var texts = new Dictionary<int, string>();
            var blocks = new Dictionary<int, byte[]>();
            if (request == null) return (numbers, texts, blocks);
            foreach (var field in ProtoMessage.Parse(request).Fields)
            {
                if (field.WireType == 0) numbers[field.FieldNumber] = field.VarIntValue;
                else if (field.WireType == 2)
                {
                    blocks[field.FieldNumber] = field.BytesValue;
                    texts[field.FieldNumber] = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                }
            }
            return (numbers, texts, blocks);
        }

        /// <summary>A spell block (lli) read back: its SpellLevels id and its slot.</summary>
        public static (int SpellLevel, int Slot) ReadSpell(byte[] lli)
        {
            var (numbers, _, _) = Read(lli);
            return ((int)numbers.GetValueOrDefault(2), (int)numbers.GetValueOrDefault(3));
        }

        /// <summary>One placed or changed (hjq): { f1 the lea }. fgv.bewu tells the chat who placed it.</summary>
        public static byte[] BuildAdded(Pb information) => Pb.New().Msg(1, information).Build();

        /// <summary>One gone from the alliance's list (hlc): { f1 its uuid }.</summary>
        public static byte[] BuildRemoved(string uuid) => Pb.New().Str(1, uuid).Build();

        /// <summary>
        /// One taken off by somebody (hjt): f2 his name, f3 the tax collector's uuid, f4 his id.
        /// fgv.bewz writes «ui.alliance.taxCollectorRemoved» with them.
        /// </summary>
        public static byte[] BuildRemovedBy(string uuid, long whoId, string whoName)
            => Pb.New().StrIfNotEmpty(2, whoName).Str(3, uuid).Var(4, whoId).Build();

        /// <summary>A tax collector's state changed (hkd): { f1 uuid, f2 state }.</summary>
        public static byte[] BuildState(string uuid, int state)
            => Pb.New().Str(1, uuid).VarIfNotZero(2, state).Build();

        /// <summary>A refusal (hla): { f1 why }.</summary>
        public static byte[] BuildError(int reason) => Pb.New().VarIfNotZero(1, reason).Build();

        // ─── Talking to it ──────────────────────────────────────────────────────

        /// <summary>
        /// What it says when talked to (ion), the four numbers of «Soy un recaudador de la alianza
        /// #1…» (Translations 1001356):
        ///
        ///   f1 the pods it carries (#6)     f2 the pods it can carry (#2)   f3 the alliance (#1, #4)
        ///   f4 how many tax collectors the alliance has (#5)      f5 its prospecting (#3)
        ///   f7 what what it carries is worth (#7)
        ///
        /// {429, 1000, «Alliance-3», 20, 200, 23836} in the capture. The pods it can carry and its
        /// prospecting are its own, with what its saddlebags and chests add.
        /// </summary>
        public static byte[] BuildDialog(AllianceStore.Alliance alliance, int pods, int taxCollectors, long worth,
                                         int maxPods = MaxPods, int prospecting = Prospecting)
            => Pb.New()
                .VarIfNotZero(1, pods)
                .Var(2, maxPods)
                .Msg(3, AllianceProtocol.Block(alliance))
                .VarIfNotZero(4, taxCollectors)
                .Var(5, prospecting)
                .VarIfNotZero(7, worth)
                .Build();

        // ─── What the alliance hears of a fight ─────────────────────────────────

        /// <summary>
        /// One of its tax collectors attacked (hld): f1 where, f2 its uuid, f3 and f5 its names.
        /// fgv.beww writes «ui.alliance.taxCollectorAttackedChat» and, with the option on, a popup
        /// whose «Unirse» opens the alliance's fights (fgv.bext).
        /// </summary>
        public static byte[] BuildAttacked(TaxCollectorStore.TaxCollector tc)
            => Pb.New()
                .Msg(1, Position(tc))
                .Str(2, tc.Uuid)
                .Var(3, tc.FirstNameId)
                .Var(5, tc.LastNameId)
                .Build();

        /// <summary>
        /// How its fight ended (hjw): f1 where, f4 whether it died, f5 and f8 its names.
        /// fgv.bewx writes «ui.alliance.taxCollectorSurvived» or «…Died».
        /// </summary>
        public static byte[] BuildFightResult(TaxCollectorStore.TaxCollector tc, bool died)
            => Pb.New()
                .Msg(1, Position(tc))
                .VarIfNotZero(4, died ? 1 : 0)
                .Var(5, tc.FirstNameId)
                .Var(8, tc.LastNameId)
                .Build();

        /// <summary>
        /// Somebody collected it (kdc): f1 where, f3 its uuid, f4 who collected, f5 the items
        /// ({ f1 item, f2 quantity } each) and f9 its look. fgv.bexi counts the items for
        /// «ui.alliance.taxCollector.collected».
        /// </summary>
        public static byte[] BuildCollected(TaxCollectorStore.TaxCollector tc, AllianceStore.Alliance alliance,
                                            string collector, IEnumerable<(int ItemId, int Quantity)> items)
        {
            var kdc = Pb.New().Msg(1, Position(tc)).Str(3, tc.Uuid).StrIfNotEmpty(4, collector);
            foreach (var (item, quantity) in items) kdc.Msg(5, Pb.New().Var(1, item).Var(2, quantity));
            return kdc.Msg(9, Look(alliance, MapBones)).Build();
        }

        // ─── The alliance fights' list ──────────────────────────────────────────

        /// <summary>
        /// Which alliance fight (lci): f1 its kind, f2 the map it is about, f4 the fight. The
        /// client keys the fight by f2 and draws f4 as a short (ffc.bdjo).
        /// </summary>
        public static Pb FightKey(int kind, long mapId, long fightId)
            => Pb.New().VarIfNotZero(1, kind).Var(2, mapId).Var(4, fightId);

        /// <summary>Its phase (lgp): f1 when it ends, in milliseconds, f2 which.</summary>
        public static Pb Phase(int phase, long endsMs)
            => Pb.New().VarIfNotZero(1, endsMs).VarIfNotZero(2, phase);

        /// <summary>
        /// A person in the list (lpg): { f1 { f2 name, f3 level, f4 { f6 look, f7 breed } }, f2 id },
        /// read off gk.mwi, which takes the name from f2, the level from f3 and the look and breed
        /// from the f4.
        /// </summary>
        /// <remarks>
        /// The f4 is one of the person's oneof (lpe: f4 lpc a fighter, f5 lon a member's details,
        /// f6 ldt a rank). It went as f6, a rank: gk.mwi read the absent fighter without a null
        /// check, threw, and the alliance window's «Attacks» tab stayed empty. The breed draws the
        /// slot's class head and names the class in its tooltip; without it the slot was blank.
        /// </remarks>
        public static Pb Fighter(long id, string name, int level, byte[] look, int breed)
            => Pb.New()
                .Msg(1, Pb.New()
                    .Str(2, name ?? "")
                    .Var(3, level)
                    .Msg(4, Pb.New().Bytes(6, look ?? Array.Empty<byte>()).VarIfNotZero(7, breed)))
                .Var(2, id);

        /// <summary>
        /// The tax collector in the list: its two name ids as its name, "z,31" for 35 and 109, its
        /// fight's level and the breed -5, which is how the client tells it from a person.
        /// </summary>
        /// <remarks>
        /// The client reads that name as two ids in base 36 split by a comma: ffc.bdjh is
        /// pu.pgp(name.Split(a.bb())[i]), with a.bb() "," and pu.pgp a base-36 reader, both seen in
        /// the running client («35,220» gave 113 and 2664). It went as "35 109", then "35,220": the
        /// first split gave one piece and threw IndexOutOfRangeException, the second looked up
        /// names 113 and 2664 that do not exist and threw NullReferenceException. Either way ffc
        /// dropped every mdx and mdu, and the attacks tab counted the fight but drew no line, so
        /// no defender could join.
        /// </remarks>
        public static Pb TaxCollectorFighter(TaxCollectorStore.TaxCollector tc, AllianceStore.Alliance alliance)
            => Fighter(tc.ContextId, NameIds(tc.FirstNameId, tc.LastNameId), LevelOf(Monster),
                       Look(alliance, MapBones).Build(), FighterListMarker);

        /// <summary>A tax collector's two name ids as the alliance fights' list names it: "z,31" for 35 and 109.</summary>
        public static string NameIds(int firstNameId, int lastNameId) => Base36(firstNameId) + "," + Base36(lastNameId);

        private static string Base36(long value)
        {
            if (value == 0) return "0";
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            bool negative = value < 0;
            ulong rest = negative ? (ulong)(-value) : (ulong)value;
            var text = new System.Text.StringBuilder();
            while (rest > 0) { text.Insert(0, digits[(int)(rest % 36)]); rest /= 36; }
            return negative ? "-" + text : text.ToString();
        }

        /// <summary>A monster's first grade's level, as its fight has it; 1 when it is not loaded.</summary>
        internal static int LevelOf(int monster)
        {
            var data = MobSpawnManager.GetMonsterData(monster);
            return data == null || data.Grades.Count == 0 ? 1 : data.Grades[0].Level;
        }

        /// <summary>
        /// One fight of the list (lgz): f1 the defenders, the tax collector first, f2 the attackers,
        /// f3 the phase, f4 which. bi.moj puts f1 where ffc.bdjn reads the tax collector from.
        /// </summary>
        public static Pb FightLine(Pb key, Pb phase, IEnumerable<Pb> defenders, IEnumerable<Pb> attackers)
        {
            var line = Pb.New();
            foreach (var defender in defenders) line.Msg(1, defender);
            foreach (var attacker in attackers) line.Msg(2, attacker);
            return line.Msg(3, phase).Msg(4, key);
        }

        /// <summary>The whole list (mdu), what the fights' tab gets on opening: { f1 each fight }.</summary>
        public static byte[] BuildFights(IEnumerable<Pb> lines)
        {
            var mdu = Pb.New();
            foreach (var line in lines) mdu.Msg(1, line);
            return mdu.Build();
        }

        /// <summary>A fight added to the list (meh): { f1 which, f2 its phase }.</summary>
        public static byte[] BuildFightAdded(Pb key, Pb phase) => Pb.New().Msg(1, key).Msg(2, phase).Build();

        /// <summary>A fight gone from the list (mdv): { f1 which }.</summary>
        public static byte[] BuildFightRemoved(Pb key) => Pb.New().Msg(1, key).Build();

        /// <summary>A fighter in (mdx): { f1 the fighter, f2 which fight, f3 his side }.</summary>
        public static byte[] BuildFighterAdded(Pb fighter, Pb key, int side)
            => Pb.New().Msg(1, fighter).Msg(2, key).VarIfNotZero(3, side).Build();

        /// <summary>A fighter out (meu): { f1 his id, f3 which fight } -- fgv.bewh reads the f3.</summary>
        public static byte[] BuildFighterRemoved(long fighter, Pb key) => Pb.New().Var(1, fighter).Msg(3, key).Build();

        /// <summary>A fight's phase changed (met): { f1 which, f3 the phase }.</summary>
        public static byte[] BuildPhase(Pb key, Pb phase) => Pb.New().Msg(1, key).Msg(3, phase).Build();

        /// <summary>
        /// The fight a request names (mer, mea, med): their f1 is the lci. Returns its map and its
        /// fight; zero for what is not there.
        /// </summary>
        public static (long MapId, long FightId) ReadFightKey(byte[] request)
        {
            long map = 0, fight = 0;
            if (request == null) return (0, 0);
            var lci = ProtoMessage.Parse(request).Fields.Find(f => f.FieldNumber == 1 && f.WireType == 2);
            if (lci == null) return (0, 0);
            foreach (var field in ProtoMessage.Parse(lci.BytesValue).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 2) map = field.VarIntValue;
                else if (field.FieldNumber == 4) fight = field.VarIntValue;
            }
            return (map, fight);
        }

        /// <summary>The uuid a request names in its f1 (kbu, hkt, hkp).</summary>
        public static string ReadUuid(byte[] request)
        {
            if (request == null) return "";
            var field = ProtoMessage.Parse(request).Fields.Find(f => f.FieldNumber == 1 && f.WireType == 2);
            return field == null ? "" : System.Text.Encoding.UTF8.GetString(field.BytesValue);
        }
    }
}
