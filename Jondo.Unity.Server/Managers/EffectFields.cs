using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// How an item effect's value travels inside the ivx.
    ///
    /// It is not a loose number in a fixed field. Each effect entry carries the id in f11 and the value in
    /// ONE of several fields, and that field is NOT a slot: it is the one that says what type the effect
    /// is, like a `oneof`. Taken from the real capture's inventory, 609 items:
    ///
    ///   f4: number          a loose varint              "+400 vitality"
    ///   f5 { f1, f2 }       a range, maximum and minimum "10 to 1 neutral damage"
    ///   f6 { f1, f2, f3 }   value, diceNum and diceSide  a dofus's spell, a title...
    ///   f1: string          "Fabricado por: ..."
    ///   f2 { f1..f5 }       a date
    ///   nothing             "Ligado a una cuenta"
    ///
    /// Writing a varint in f5 or in f6 is a wire-type error, not a different value: the client looks for a
    /// submessage there, does not find it, and is left without the parameters. That is what left weapons
    /// without damage and what put `{spellNoLvl,,}` on the dofus instead of the spell's name -- with its own
    /// Player.log saying so in so many words:
    ///
    ///   ERROR [Hyperlink] Error while trying to convert an hyperlink of type spellNoLvl,
    ///   parameters , and text .
    ///
    /// The table learnt from the capture (item_effect_fields.json, 121 effects) always rules when it has an
    /// entry. For the rest it is decided with the Effects table itself, and the rule was checked against the
    /// 670 effects of the captured inventory.
    /// </summary>
    public static class EffectFields
    {
        /// <summary>Which field each effect uses, learnt from the capture. It rules over the rule.</summary>
        private static readonly Dictionary<int, int> _fields = new Dictionary<int, int>();

        /// <summary>Category and UseDice of each effect, which is what the rest is decided with.</summary>
        private static readonly Dictionary<int, (int Category, bool UseDice)> _kind =
            new Dictionary<int, (int, bool)>();

        public static int Count => _fields.Count;

        /// <summary>The weapon damage effects, which are the ones that travel as a range.</summary>
        private const int WeaponDamageCategory = 2;

        /// <summary>The effect is not sent: its thing is a text or a date that does not exist here.</summary>
        public const int Skip = -1;

        public const int NoValue = 0;
        public const int AsString = 1;
        public const int AsDate = 2;
        public const int AsNumber = 4;
        public const int AsRange = 5;
        public const int AsDice = 6;

        public static void Initialize()
        {
            _fields.Clear();
            _kind.Clear();

            string path = Paths.EffectFieldsJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[EffectFields] {Path.GetFileName(path)} no está; se decidirá " +
                                  "solo con la tabla Effects, que acierta pero no en todos.");
            }
            else
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    foreach (var entry in doc.RootElement.EnumerateObject())
                    {
                        if (int.TryParse(entry.Name, out int effect) && entry.Value.TryGetInt32(out int field))
                        {
                            _fields[effect] = field;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EffectFields] No se pudo leer {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Category, UseDice FROM Effects;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    _kind[reader.GetInt32(0)] = (reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                                                 !reader.IsDBNull(2) && reader.GetInt32(2) != 0);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EffectFields] No se pudo leer la tabla Effects: {ex.Message}");
            }

            Console.WriteLine($"[EffectFields] {_fields.Count} efectos con su forma aprendida de la " +
                              $"captura, {_kind.Count} clasificados por la tabla Effects.");
        }

        /// <summary>
        /// How this instance of the effect must travel.
        ///
        /// It is passed the three numbers as the item declares them -- the fixed value, and the pair of dice --
        /// and it returns the field and what goes inside it. A rolled effect arrives here already resolved, with
        /// the number in <paramref name="value"/> and the dice at zero: choosing which point of the range an item
        /// gets is the business of whoever makes it, not of the protocol.
        /// </summary>
        public static (int Field, long V1, long V2, long V3) Shape(int effect, long value, long diceNum, long diceSide)
        {
            _kind.TryGetValue(effect, out var kind);

            if (_fields.TryGetValue(effect, out int learned))
            {
                switch (learned)
                {
                    case AsRange: return (AsRange, diceSide != 0 ? diceSide : diceNum, diceNum, 0);
                    case AsDice: return (AsDice, value, diceNum, diceSide);
                    case AsNumber: return (AsNumber, OnlyNonZero(value, diceNum, diceSide), 0, 0);
                    // A string or a date: "Fabricado por", "Intercambiable el". The server puts them on the
                    // item once made, and nothing is made here. Sending them empty leaves the client
                    // drawing the label with nothing behind, so they do not go.
                    case AsString:
                    case AsDate: return (Skip, 0, 0, 0);
                    default: return (NoValue, 0, 0, 0);
                }
            }

            if (value == 0 && diceNum == 0 && diceSide == 0) return (NoValue, 0, 0, 0);

            // Weapon damage with a real range. The ones of this category that carry a single number
            // -- pushing, pulling, removing MP -- do travel as a loose number, and so look right.
            if (kind.Category == WeaponDamageCategory && diceSide != 0 && diceSide != diceNum)
            {
                return (AsRange, diceSide, diceNum, 0);
            }

            // A compound effect: the one naming a spell, a profession, a title. The rolled ones do
            // not come in here -- their pair of numbers is the range the value was already taken from.
            int nonZero = (value != 0 ? 1 : 0) + (diceNum != 0 ? 1 : 0) + (diceSide != 0 ? 1 : 0);
            if (!kind.UseDice && nonZero > 1) return (AsDice, value, diceNum, diceSide);

            return (AsNumber, OnlyNonZero(value, diceNum, diceSide), 0, 0);
        }

        private static long OnlyNonZero(long value, long diceNum, long diceSide)
            => value != 0 ? value : (diceNum != 0 ? diceNum : diceSide);

        /// <summary>
        /// What this effect adds to the sheet. Only those travelling as a loose number or as a range count:
        /// compound ones name things, they do not move characteristics.
        /// </summary>
        public static long SheetValue(int effect, long value, long diceNum, long diceSide)
        {
            var (field, v1, _, _) = Shape(effect, value, diceNum, diceSide);
            return field == AsNumber || field == AsRange ? v1 : 0;
        }
    }
}
