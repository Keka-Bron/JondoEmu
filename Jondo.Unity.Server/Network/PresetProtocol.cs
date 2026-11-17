using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The «Conjuntos» window's messages (CharacterPresets), measured in «Conjuntos/crear nuevo
    /// conjunto…» and «Conjuntos/eliminar conjunto». A build travels as
    /// <code>
    ///   ihr { 1 looks { 1 equipment look, 3 appearance look }, 2 icon,
    ///         3 sheet { repeated 1 { 1 characteristic, 2 { 1 scrolls, 2 points, 3 from the
    ///                   characteristics, 4 base, 6 equipment } } },
    ///         4 name, 5 repeated { 1 slot, 2 { 1 template, 2 uid, 3 effects } }, 6 ISO date,
    ///         7 { repeated 2 { 1 spell, 2 { 1 packed bar slots } } }, 10 uuid }
    /// </code>
    /// The sheet's columns are the kub's split in two: what the kub calls base (f4.f2) is here
    /// either the points spent in the six that take them (f2), what other characteristics give
    /// (f3: a tenth of wisdom for dodging and withdrawing, five pods a point of strength...) or a
    /// fixed base (f4). The three maps write their key even at zero, as protobuf's maps do: the
    /// amulet's slot, life and the weapon's spell all travel with an explicit 0.
    /// </summary>
    public static class PresetProtocol
    {
        /// <summary>The six characteristics points are spent in.</summary>
        internal static readonly int[] Primaries =
        {
            Stat.Strength, Stat.Vitality, Stat.Wisdom,
            Stat.Chance, Stat.Agility, Stat.Intelligence,
        };

        /// <summary>A build as the window draws it.</summary>
        public static byte[] BuildPreset(DbCharacter character, CharacterPresets.Preset preset,
                                         bool playerState = false)
        {
            var placed = Placed(preset);
            var bonuses = playerState ? Equipment.Bonuses() : Equipment.BonusesOf(placed);

            var pb = Pb.New().Bytes(1, Looks(character, placed));
            if (!playerState) pb.VarIfNotZero(2, preset.Icon);
            pb.Msg(3, Sheet(character.Level, preset.Points, bonuses));
            pb.Str(4, playerState ? CharacterPresets.PlayerStateName : preset.Name);
            foreach (var (position, item) in placed)
                pb.Msg(5, Pb.New().Var(1, position).Msg(2, ItemObject(item)));
            pb.Str(6, preset.Created);
            pb.Msg(7, Spells(preset.Spells));
            // The character's own card names the outfit he has on; a build, the one it puts on --
            // the captured builds carry none, as nobody had picked one.
            if (playerState) pb.Str(9, Outfits.EquippedOf(character.Id));
            else if (preset.Outfit.Length > 0 && Outfits.Of(character.Id).Exists(o => o.Uuid == preset.Outfit))
                pb.Str(9, preset.Outfit);
            pb.Str(10, preset.Uuid);
            return pb.Build();
        }

        /// <summary>The build's items that the character still has, in their build slots.</summary>
        internal static List<(int Position, Equipment.Item Item)> Placed(CharacterPresets.Preset preset)
        {
            var placed = new List<(int, Equipment.Item)>();
            foreach (var (position, uid) in preset.Items)
            {
                var item = Equipment.ByUid(uid);
                if (item != null && Equipment.IsWorn(position)) placed.Add((position, item));
            }
            return placed;
        }

        /// <summary>igs { 1 the equipment look, 3 the appearance look }.</summary>
        internal static byte[] Looks(DbCharacter character, IEnumerable<(int Position, Equipment.Item Item)> placed)
        {
            var (equipment, appearance) = CharacterLookBuilder.PresetLooks(character,
                placed.Select(p => (p.Position, p.Item.Template)).ToList());
            return Pb.New().Bytes(1, equipment).Bytes(3, appearance).Build();
        }

        /// <summary>
        /// The sheet (lhk): scrolls, the build's points and its equipment over the character's
        /// base. What other characteristics give is worked out over the three together.
        /// </summary>
        internal static Pb Sheet(int level, IReadOnlyDictionary<int, int> points, IReadOnlyDictionary<int, long> equipment)
        {
            var snapshot = PresetMessages.Live.Characteristics(SessionContext.Current, level, points, equipment);
            var sheet = Pb.New();
            foreach (var (id, value) in snapshot.CharacteristicDetailsById)
            {
                var columns = Pb.New().VarIfNotZero(1, value.FromInitialBonus).VarIfNotZero(2, value.FromAllocated)
                    .VarIfNotZero(3, value.FromBonus).VarIfNotZero(4, value.FromBase).VarIfNotZero(6, value.FromEquipment);
                if (value.Limit is { } limit) columns.Var(5, limit);
                sheet.Msg(1, Pb.New().Var(1, id).Msg(2, columns));
            }
            return sheet;
        }

        /// <summary>llm { 1 template, 2 uid, 3 effects }, the effects as the inventory carries them.</summary>
        internal static Pb ItemObject(Equipment.Item item)
        {
            var pb = Pb.New().Var(1, item.Template).Var(2, item.Uid);
            foreach (var effect in item.Effects)
            {
                var entry = ConnectionProtocol.EffectEntry(effect);
                if (entry != null) pb.Msg(3, entry);
            }
            return pb;
        }

        /// <summary>ihi { repeated 2 { 1 spell, 2 { 1 packed slots } } }: a spell off the bar carries none.</summary>
        internal static Pb Spells(IReadOnlyDictionary<int, List<int>> spells)
        {
            var pb = Pb.New();
            foreach (var (spell, slots) in spells)
            {
                var where = Pb.New();
                if (slots.Count > 0) where.Packed(1, slots.Select(s => (long)s));
                pb.Msg(2, Pb.New().Var(1, spell).Msg(2, where));
            }
            return pb;
        }

        /// <summary>ihi's spells, read back: what igy carries and the window has rearranged.</summary>
        internal static SortedDictionary<int, List<int>> ReadSpells(byte[] ihi)
        {
            var spells = new SortedDictionary<int, List<int>>();
            foreach (var entry in ProtoMessage.Parse(ihi).Fields)
            {
                if (entry.FieldNumber != 2 || entry.WireType != 2) continue;
                int spell = 0;
                var slots = new List<int>();
                foreach (var f in ProtoMessage.Parse(entry.BytesValue).Fields)
                {
                    if (f.FieldNumber == 1 && f.WireType == 0) spell = (int)f.VarIntValue;
                    else if (f.FieldNumber == 2 && f.WireType == 2)
                        slots.AddRange(Handlers.BankHandler.PackedOrSingle(
                            ProtoMessage.Parse(f.BytesValue).Fields, 1).Select(s => (int)s));
                }
                spells[spell] = slots.Distinct().OrderBy(s => s).ToList();
            }
            return spells;
        }

        /// <summary>ihq { 2 0 }: «it is on». The zero is written, as the capture writes it.</summary>
        public static byte[] BuildEquipped() => new byte[] { 0x10, 0x00 };
    }
}
