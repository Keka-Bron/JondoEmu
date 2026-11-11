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
    /// The «Conjuntos» window (CharacterPresets, PresetProtocol). Each button of the client's
    /// CharacterPresetUi goes out through the eql frame as one request, answered under its id
    /// (<see cref="AnswerAsync"/>):
    /// <code>
    ///   ifg            the character's own state            -> ifz { 1 build }
    ///   igp            a new build: 1 { 1 copy of a uuid | 2 empty | 3 the character's state },
    ///                  2 icon, 4 name                       -> igk { 1 { 1 build } }
    ///   ihf            3 uuid, 4 the new name               -> igc { }
    ///   iid            2 uuid, 4 the new icon               -> iir { 1 true }
    ///   igy            2 the spells, 3 uuid                 -> ija { 1 true }
    ///   iha            1 uuid, 2 { 1 characteristic, 2 points }  -> ijd { 1 sheet, 3 true }
    ///   ify            1 uuid, 3 { 1 slot, 2 item }         -> iic { 2 { 2 sheet, 3 looks }, 4 true }
    ///   ihs            2 uuid: «Guardar»                    -> iim { 2 true }
    ///   igo            1 uuid: the edits thrown away        -> ign { 1 true, 2 build }
    ///   iin            1 uuid: updated from what is worn    -> ihz { 1 true, 4 build }
    ///   iiy            1 uuid: deleted                      -> ihc { 1 true }
    ///   iio            1 uuid: put on, from the window or the item bar
    ///                  -> kub, hng per variant, itg, ivq per item, kub, ihq { 2 0 }
    ///   ihg            1 outfit uuid, 2 uuid: the outfit the build puts on
    ///                  -> ihu { 1 true, 2 build } as the request's answer
    /// </code>
    /// All measured in «Conjuntos/crear nuevo conjunto…» and «Conjuntos/eliminar conjunto» but
    /// igp's other two sources, iid and igo, which no capture presses: their shape and the hook
    /// each answer raises are the client's (eql::bbaj, CharacterPresetUi.OnPresetGfxUpdated; ign's
    /// handler does nothing).
    /// </summary>
    public static class PresetHandler
    {
        /// <summary>The longest name the window is let keep.</summary>
        private const int MaxNameLength = 64;

        private static long Me => SessionContext.State.CharacterId;

        private static Task SendAsync(NetworkStream stream, string op, byte[] body)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(op, body));

        /// <summary>
        /// The answer to one of the window's requests, under the id the request came with: the
        /// capture answers every one of them so (igc under ihf's 40, the ija under igy's 42 to 52),
        /// and the client finds what it asked by that id -- the icon and the build it was for, kept
        /// beside the iid it sent. Sent loose, the new icon never reached the build, nothing was
        /// left to save and «Guardar» stayed grey.
        /// </summary>
        private static Task AnswerAsync(NetworkStream stream, byte[]? request, string op, byte[] body)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(op, body, request == null ? -1 : ConnectionProtocol.RequestId(request)));

        private static List<ProtoField> Fields(byte[] payload, string op)
        {
            byte[]? body = ConnectionProtocol.ReadPayload(payload, op);
            return body == null ? new List<ProtoField>() : ProtoMessage.Parse(body).Fields;
        }

        private static string Text(List<ProtoField> fields, int number)
        {
            var field = fields.FirstOrDefault(f => f.FieldNumber == number && f.WireType == 2);
            return field == null ? "" : System.Text.Encoding.UTF8.GetString(field.BytesValue);
        }

        private static long Number(List<ProtoField> fields, int number)
            => fields.FirstOrDefault(f => f.FieldNumber == number && f.WireType == 0)?.VarIntValue ?? 0;

        private static DatabaseManager.DbCharacter? Character() => DatabaseManager.GetCharacterById(Me);

        // ─── The character's state ──────────────────────────────────────────────

        /// <summary>The character as he is, as a build: what he wears, his points, his bar.</summary>
        internal static CharacterPresets.Preset CurrentState()
        {
            var state = SessionContext.State;
            var preset = new CharacterPresets.Preset { Uuid = Guid.NewGuid().ToString(), Created = CharacterPresets.Now() };
            foreach (var item in Equipment.All)
                if (Equipment.IsWorn(item.Position)) preset.Items[item.Position] = item.Uid;

            preset.Points[ConnectionProtocol.Stat.Strength] = state.StatStrength;
            preset.Points[ConnectionProtocol.Stat.Vitality] = state.StatVitality;
            preset.Points[ConnectionProtocol.Stat.Wisdom] = state.StatWisdom;
            preset.Points[ConnectionProtocol.Stat.Chance] = state.StatChance;
            preset.Points[ConnectionProtocol.Stat.Agility] = state.StatAgility;
            preset.Points[ConnectionProtocol.Stat.Intelligence] = state.StatIntelligence;

            preset.Spells = SpellsOf(FightSpellLayout.Current(state.Breed, state.CharacterLevel, SessionContext.Current.AccountId));
            return preset;
        }

        /// <summary>A bar as the build's spell map: every spell known, with the slots it sits in.</summary>
        private static SortedDictionary<int, List<int>> SpellsOf(FightSpellLayout.Layout layout)
        {
            var spells = new SortedDictionary<int, List<int>>();
            foreach (var (spell, _) in layout.Spells) spells[spell] = new List<int>();
            foreach (var (slot, spell) in layout.Bar)
            {
                if (!spells.TryGetValue(spell, out var slots)) spells[spell] = slots = new List<int>();
                slots.Add(slot);
            }
            return spells;
        }

        /// <summary>ifg: the window's first card.</summary>
        public static async Task PlayerStateAsync(NetworkStream stream, byte[]? request = null)
        {
            var character = Character();
            if (character == null) return;
            await AnswerAsync(stream, request, "ifz", Pb.New().Bytes(1,
                PresetProtocol.BuildPreset(character, CurrentState(), playerState: true)).Build());
        }

        /// <summary>The builds, on entering the world (ihb). Empty for a character with none, as a new one gets it.</summary>
        public static async Task SendListAsync(NetworkStream stream)
        {
            var character = Character();
            if (character == null) return;
            await SendAsync(stream, "ihb", PresetProtocol.BuildList(character, CharacterPresets.Of(character.Id)));
        }

        // ─── Creating, saving, deleting ─────────────────────────────────────────

        /// <summary>igp: a new build, empty, a copy of another or of the character as he is.</summary>
        public static async Task CreateAsync(NetworkStream stream, byte[] payload)
        {
            var fields = Fields(payload, "igp");
            var character = Character();
            if (character == null) return;

            CharacterPresets.Preset preset;
            var source = fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 2);
            var kind = source == null ? null : ProtoMessage.Parse(source.BytesValue).Fields.FirstOrDefault(f => f.WireType == 2);
            switch (kind?.FieldNumber ?? 2)
            {
                case 1:
                    // A copy of one of his: ifn { 1 uuid }.
                    string original = Text(ProtoMessage.Parse(kind!.BytesValue).Fields, 1);
                    var copied = CharacterPresets.Find(Me, original);
                    if (copied == null) return;
                    preset = copied.Copy();
                    break;
                case 3:
                    preset = CurrentState();
                    break;
                default:
                    // Empty: nothing worn, no point spent, the spells he knows on a bar of their own.
                    var state = SessionContext.State;
                    preset = new CharacterPresets.Preset
                    {
                        Spells = SpellsOf(FightSpellLayout.Build(
                            SpellTable.KnownFor(state.Breed, state.CharacterLevel, SpellChoices.Chosen,
                                                LearnedSpells.For(Me, SessionContext.Current.AccountId)),
                            new Dictionary<int, int>())),
                    };
                    break;
            }

            preset.Icon = (int)Number(fields, 2);
            preset.Name = NameOrDefault(Text(fields, 4));
            var created = CharacterPresets.Create(Me, preset);
            if (created == null)
            {
                Console.WriteLine($"[Presets] {SessionContext.State.CharacterName} could not make another build.");
                return;
            }

            await AnswerAsync(stream, payload, "igk", Pb.New().Msg(1, Pb.New().Bytes(1,
                PresetProtocol.BuildPreset(character, created))).Build());
            Console.WriteLine($"[Presets] {SessionContext.State.CharacterName} made «{created.Name}» ({created.Uuid}).");
        }

        private static string NameOrDefault(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength);
            return name.Length == 0 ? "?" : name;
        }

        /// <summary>ihs: «Guardar», the draft becomes the build.</summary>
        public static async Task SaveAsync(NetworkStream stream, byte[] payload)
        {
            string uuid = Text(Fields(payload, "ihs"), 2);
            bool saved = CharacterPresets.Save(Me, uuid);
            await AnswerAsync(stream, payload, "iim", Pb.New().VarIfNotZero(2, saved ? 1 : 0).Build());
        }

        /// <summary>igo: the window's edits thrown away; ign carries the build as it was.</summary>
        public static async Task DiscardAsync(NetworkStream stream, byte[] payload)
        {
            string uuid = Text(Fields(payload, "igo"), 1);
            CharacterPresets.Discard(Me, uuid);
            var character = Character();
            var saved = CharacterPresets.Find(Me, uuid);
            if (character == null || saved == null) return;
            await AnswerAsync(stream, payload, "ign", Pb.New().Var(1, 1).Bytes(2, PresetProtocol.BuildPreset(character, saved)).Build());
        }

        /// <summary>iin: the build becomes what the character wears now, with his points and bar.</summary>
        public static async Task UpdateAsync(NetworkStream stream, byte[] payload)
        {
            string uuid = Text(Fields(payload, "iin"), 1);
            var character = Character();
            var saved = CharacterPresets.Find(Me, uuid);
            if (character == null || saved == null) return;

            var now = CurrentState();
            now.Uuid = saved.Uuid;
            now.Name = saved.Name;
            now.Icon = saved.Icon;
            now.Position = saved.Position;
            if (!CharacterPresets.Replace(Me, now)) return;
            await AnswerAsync(stream, payload, "ihz", Pb.New().Var(1, 1).Bytes(4, PresetProtocol.BuildPreset(character, now)).Build());
        }

        /// <summary>iiy: a build deleted.</summary>
        public static async Task DeleteAsync(NetworkStream stream, byte[] payload)
        {
            string uuid = Text(Fields(payload, "iiy"), 1);
            bool deleted = CharacterPresets.Delete(Me, uuid);
            await AnswerAsync(stream, payload, "ihc", Pb.New().VarIfNotZero(1, deleted ? 1 : 0).Build());
        }

        // ─── Editing ────────────────────────────────────────────────────────────

        /// <summary>ihf: the name, on the draft.</summary>
        public static async Task RenameAsync(NetworkStream stream, byte[] payload)
        {
            var fields = Fields(payload, "ihf");
            var draft = CharacterPresets.ForEdit(Me, Text(fields, 3));
            if (draft == null) return;
            draft.Name = NameOrDefault(Text(fields, 4));
            await AnswerAsync(stream, payload, "igc", Array.Empty<byte>());
        }

        /// <summary>iid: the icon, on the draft.</summary>
        public static async Task IconAsync(NetworkStream stream, byte[] payload)
        {
            var fields = Fields(payload, "iid");
            var draft = CharacterPresets.ForEdit(Me, Text(fields, 2));
            if (draft != null) draft.Icon = (int)Number(fields, 4);
            // Refused, the client puts the selector back on the icon the build has.
            await AnswerAsync(stream, payload, "iir", Pb.New().VarIfNotZero(1, draft != null ? 1 : 0).Build());
        }

        /// <summary>igy: the spells and their slots, the whole map each time.</summary>
        public static async Task SpellsAsync(NetworkStream stream, byte[] payload)
        {
            var fields = Fields(payload, "igy");
            var draft = CharacterPresets.ForEdit(Me, Text(fields, 3));
            var spells = fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
            if (draft == null || spells == null) return;
            draft.Spells = PresetProtocol.ReadSpells(spells.BytesValue);
            await AnswerAsync(stream, payload, "ija", Pb.New().Var(1, 1).Build());
        }

        /// <summary>
        /// iha: the points spent, all six each time. Refused when they cost more than the
        /// character's level gives, by the same tiers as the characteristics window (StatsHandler).
        /// </summary>
        public static async Task PointsAsync(NetworkStream stream, byte[] payload)
        {
            var fields = Fields(payload, "iha");
            var draft = CharacterPresets.ForEdit(Me, Text(fields, 1));
            var character = Character();
            if (draft == null || character == null) return;

            var wanted = new SortedDictionary<int, int>();
            foreach (var entry in fields.Where(f => f.FieldNumber == 2 && f.WireType == 2))
            {
                var pair = ProtoMessage.Parse(entry.BytesValue).Fields;
                int stat = (int)Number(pair, 1), points = (int)Number(pair, 2);
                if (PresetProtocol.Primaries.Contains(stat) && points >= 0) wanted[stat] = points;
            }

            bool fits = Fits(wanted, SessionContext.State.CharacterLevel);
            if (fits) draft.Points = wanted;
            var sheet = PresetProtocol.Sheet(character.Level, draft.Points,
                                             Equipment.BonusesOf(PresetProtocol.Placed(draft)));
            await AnswerAsync(stream, payload, "ijd", Pb.New().Msg(1, sheet).VarIfNotZero(3, fits ? 1 : 0).Build());
        }

        /// <summary>Whether a distribution costs no more than the level's capital.</summary>
        internal static bool Fits(IReadOnlyDictionary<int, int> points, int level)
        {
            int Of(int stat) => points.TryGetValue(stat, out int value) ? value : 0;
            int cost = StatsHandler.ComputeDistributionCost(
                Of(ConnectionProtocol.Stat.Strength), Of(ConnectionProtocol.Stat.Intelligence),
                Of(ConnectionProtocol.Stat.Chance), Of(ConnectionProtocol.Stat.Agility),
                Of(ConnectionProtocol.Stat.Vitality), Of(ConnectionProtocol.Stat.Wisdom));
            return cost >= 0 && cost <= StatsHandler.TotalCapitalForLevel(level);
        }

        /// <summary>
        /// ify: an item into a slot of the build, from the character's own bag or body; an item
        /// sits in one slot only. No item (uid 0) empties the slot.
        /// </summary>
        public static async Task PlaceItemAsync(NetworkStream stream, byte[] payload)
        {
            var fields = Fields(payload, "ify");
            var draft = CharacterPresets.ForEdit(Me, Text(fields, 1));
            var where = fields.FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 2);
            var character = Character();
            if (draft == null || where == null || character == null) return;

            var slot = ProtoMessage.Parse(where.BytesValue).Fields;
            int position = (int)Number(slot, 1);
            long uid = Number(slot, 2);

            bool placed = Equipment.IsWorn(position);
            if (placed && uid == 0)
            {
                draft.Items.Remove(position);
            }
            else if (placed)
            {
                var item = Equipment.ByUid(uid);
                placed = item != null && !Equipment.IsTaxCollectorPiece(item.Template);
                if (placed)
                {
                    foreach (int other in draft.Items.Where(p => p.Value == uid).Select(p => p.Key).ToList())
                        draft.Items.Remove(other);
                    draft.Items[position] = uid;
                }
            }

            var worn = PresetProtocol.Placed(draft);
            var body = Pb.New()
                .Msg(2, PresetProtocol.Sheet(character.Level, draft.Points, Equipment.BonusesOf(worn)))
                .Bytes(3, PresetProtocol.Looks(character, worn));
            await AnswerAsync(stream, payload, "iic", Pb.New().Msg(2, body).VarIfNotZero(4, placed ? 1 : 0).Build());
        }

        /// <summary>
        /// ihg: the outfit a build puts on, picked from the outfits list (OutfitChoiceListUi,
        /// action dfj); an empty one unlinks it. eql::bbag keeps the pair under the request's id,
        /// and eqq::bbfd finds it again by that id on ihu { 1: done, 2: the build }, so ihu is
        /// the request's answer. INFERRED from the client: no capture links an outfit.
        /// </summary>
        public static async Task OutfitAsync(NetworkStream stream, byte[] payload)
        {
            var fields = Fields(payload, "ihg");
            string outfit = Text(fields, 1), uuid = Text(fields, 2);
            var character = Character();

            CharacterPresets.Preset? preset = null;
            if (character != null && (outfit.Length == 0 || Outfits.Of(character.Id).Exists(o => o.Uuid == outfit)))
                preset = CharacterPresets.SetOutfit(Me, uuid, outfit);

            var ihu = Pb.New();
            if (character != null && preset != null) ihu.Var(1, 1).Bytes(2, PresetProtocol.BuildPreset(character, preset));
            await AnswerAsync(stream, payload, "ihu", ihu.Build());
        }

        // ─── Putting a build on ─────────────────────────────────────────────────

        /// <summary>
        /// iio: the build put on -- its points if the level affords them, its spells and bar, its
        /// items --, in the capture's order: kub, the variants (hng), the bar (itg), each item
        /// (ivq...), the sheet again and ihq. Not in a fight.
        /// </summary>
        public static async Task EquipAsync(NetworkStream stream, byte[] payload, long accountId)
        {
            string uuid = Text(Fields(payload, "iio"), 1);
            var preset = CharacterPresets.Find(Me, uuid);
            var state = SessionContext.State;
            if (preset == null) return;
            if (state.IsInFight)
            {
                Console.WriteLine($"[Presets] {state.CharacterName} cannot put a build on in a fight.");
                return;
            }

            // The points.
            if (preset.Points.Count > 0 && Fits(preset.Points, state.CharacterLevel))
            {
                int Of(int stat) => preset.Points.TryGetValue(stat, out int value) ? value : 0;
                state.StatStrength = Of(ConnectionProtocol.Stat.Strength);
                state.StatVitality = Of(ConnectionProtocol.Stat.Vitality);
                state.StatWisdom = Of(ConnectionProtocol.Stat.Wisdom);
                state.StatChance = Of(ConnectionProtocol.Stat.Chance);
                state.StatAgility = Of(ConnectionProtocol.Stat.Agility);
                state.StatIntelligence = Of(ConnectionProtocol.Stat.Intelligence);
                state.CharacterRemainingPoints = StatsHandler.TotalCapitalForLevel(state.CharacterLevel)
                    - StatsHandler.ComputeDistributionCost(state.StatStrength, state.StatIntelligence, state.StatChance,
                                                           state.StatAgility, state.StatVitality, state.StatWisdom);
                DatabaseManager.SaveCurrentCharacter();
            }
            await SendAsync(stream, Op.Kub, ConnectionProtocol.BuildCharacteristics());

            // The spells: each pair's half the build carries, then its bar.
            int changed = 0;
            foreach (int spell in preset.Spells.Keys)
            {
                var pair = SpellTable.PairOf(spell);
                if (pair == null) continue;
                int carried = SpellChoices.Chosen.TryGetValue(pair.Id, out int chosen) ? chosen : pair.Base;
                int grade = SpellTable.GradeFor(spell, state.CharacterLevel);
                if (carried == spell || grade == 0 || !SpellChoices.Choose(spell)) continue;
                await SendAsync(stream, Op.Hng, ConnectionProtocol.BuildSpellSwapped(spell, grade));
                changed++;
            }
            foreach (int slot in SpellChoices.Bar.Keys.ToList()) SpellChoices.PutInBar(slot, 0);
            foreach (var (spell, slots) in preset.Spells)
            {
                if (spell == Network.FightProtocol.HechizoCuerpoACuerpo) continue;
                foreach (int slot in slots) SpellChoices.PutInBar(slot, spell);
            }
            await ShortcutBarMessages.Live.SendSpellAsync(SessionContext.Current);

            // The items: off what the build does not wear, on what it does.
            var wanted = preset.Items.Where(p => Equipment.ByUid(p.Value) != null)
                                     .ToDictionary(p => p.Value, p => p.Key);
            int moved = 0;
            foreach (var item in Equipment.All.Where(i => Equipment.IsWorn(i.Position) && !wanted.ContainsKey(i.Uid)).ToList())
                if (await EquipmentHandler.MoveItemAsync(stream, item.Uid, EquipmentHandler.Bag, accountId, sheet: false)) moved++;
            foreach (var (uid, position) in wanted)
            {
                var item = Equipment.ByUid(uid);
                if (item == null || item.Position == position) continue;
                if (await EquipmentHandler.MoveItemAsync(stream, uid, position, accountId, sheet: false)) moved++;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, StatsHandler.BuildKrbPacket(state.CharacterRemainingPoints));
            await SendAsync(stream, Op.Kub, ConnectionProtocol.BuildCharacteristics());

            // The outfit the build puts on, if it has one and it is not the one worn.
            var character = Character();
            if (character != null && preset.Outfit.Length > 0 && preset.Outfit != Outfits.EquippedOf(character.Id)
                && Outfits.Equip(character, preset.Outfit, gid => CosmeticCollection.Has(accountId, gid)))
            {
                state.IsWardrobeDraftLoaded = false;
                await WardrobeHandler.AnnounceAsync(stream, accountId);
                await SlotLookHandler.RedrawAsync(stream);
            }

            await AnswerAsync(stream, payload, "ihq", PresetProtocol.BuildEquipped());
            Console.WriteLine($"[Presets] {state.CharacterName} put on «{preset.Name}»: {changed} variant(s), {moved} item move(s).");
        }
    }
}
