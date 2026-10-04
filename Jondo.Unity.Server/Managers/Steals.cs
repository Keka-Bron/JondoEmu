using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The steals: an effect that takes points of a characteristic off its target and hands the
    /// same points to its caster -- "#1 robo de agilidad" (268), "Roba #1 PM" (77), "Roba #1 de
    /// alcance" (320) -- and the two plain rows each one goes out as.
    /// </summary>
    /// <remarks>
    /// The real server never sends a steal as itself. Every one of the 26 steal rows of the
    /// class captures goes out as TWO rows with the steal's own uid, dice and duration: the
    /// characteristic's malus on the target and its bonus on the caster. Estafa's 268 (uid
    /// 371888) is "jxm 154 dice 100" on the enemy and "jxm 119 dice 100" on the Sram;
    /// Pillaje's 266 is 152 and 123, Estratagema's 269 155 and 126, Extorsión's 271 157 and
    /// 118, Drenaje Elemental's 266 152 and 123, and Desecación's 320 (uid 213583) 116 "-3
    /// alcance" and 117 "+3 alcance" in the Hipermago capture. Run as a characteristic, the
    /// steal went on the TARGET with a plus sign -- its description does not start with a
    /// minus -- and the enemy came out a hundred agility richer.
    ///
    /// Which pair a steal goes out as is read from the catalogue, not written here: the
    /// steal's characteristic -- the table's column, or, for the three that carry none, the
    /// last word of their description ("PA", "PM", "alcance") -- and the two ordinary boost
    /// rows of that characteristic, the one the catalogue marks with BonusType -1 and the one
    /// it marks with +1. The AP and MP pairs (168/111, 169/128) are an inference from the
    /// other five: no capture holds a 77 or an 84.
    /// </remarks>
    public static class Steals
    {
        /// <summary>A steal and what it goes out as.</summary>
        public readonly record struct Steal(int Characteristic, int MalusEffect, int BonusEffect);

        private static Dictionary<int, Steal>? _steals;
        private static readonly object _lock = new();

        /// <summary>The steal behind an effect, or null when the effect steals nothing.</summary>
        public static Steal? Of(int effectId)
        {
            Load();
            return _steals!.TryGetValue(effectId, out var steal) ? steal : null;
        }

        /// <summary>The characteristics the description of a table-less steal names by its last word.</summary>
        private static readonly Dictionary<string, int> ByLastWord = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PA"] = 1,
            ["PM"] = 23,
            ["alcance"] = 19,
        };

        private static void Load()
        {
            if (_steals != null) return;
            lock (_lock)
            {
                if (_steals != null) return;
                var found = new Dictionary<int, Steal>();
                try
                {
                    var rows = new List<(int Id, int Characteristic, int Category, int BonusType, int Boost, string Text)>();
                    using (var connection = new SqliteConnection(DatabaseManager.WorldConnectionString))
                    {
                        connection.Open();
                        using var command = connection.CreateCommand();
                        command.CommandText = "SELECT Id, Characteristic, Category, BonusType, Boost, Description FROM Effects;";
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            rows.Add((reader.GetInt32(0),
                                      reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                                      reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                                      reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                                      reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                                      reader.IsDBNull(5) ? "" : reader.GetString(5)));
                        }
                    }

                    // The plain boost rows of each characteristic: -1 the malus, +1 the bonus,
                    // outside the weapon's category. The lowest id when a characteristic has two.
                    var malus = new Dictionary<int, int>();
                    var bonus = new Dictionary<int, int>();
                    foreach (var row in rows)
                    {
                        if (row.Characteristic <= 0 || row.Boost != 1
                            || row.Category == Jondo.Unity.World.Combat.EffectSupport.WeaponCategory) continue;
                        if (row.BonusType == -1 && (!malus.TryGetValue(row.Characteristic, out int m) || row.Id < m))
                            malus[row.Characteristic] = row.Id;
                        if (row.BonusType == 1 && (!bonus.TryGetValue(row.Characteristic, out int b) || row.Id < b))
                            bonus[row.Characteristic] = row.Id;
                    }

                    foreach (var row in rows)
                    {
                        string text = row.Text.Trim();
                        bool isSteal = text.StartsWith("Roba ", StringComparison.Ordinal)
                                       || text.Contains(" robo de ", StringComparison.Ordinal);
                        if (!isSteal) continue;

                        // Life steals are blows, not steals of a characteristic: "de robo de agua".
                        if (text.Contains("robo de vida", StringComparison.Ordinal)) continue;
                        if (row.Id >= Jondo.Unity.World.Combat.EffectSupport.FirstDamage
                            && row.Id <= Jondo.Unity.World.Combat.EffectSupport.LastDamage) continue;
                        if (EffectEngine.EsRoboDeVida(row.Id)) continue;

                        int characteristic = row.Characteristic;
                        if (characteristic <= 0)
                        {
                            int space = text.LastIndexOf(' ');
                            string last = space >= 0 ? text.Substring(space + 1) : text;
                            if (!ByLastWord.TryGetValue(last, out characteristic)) continue;
                        }
                        if (!malus.TryGetValue(characteristic, out int malusEffect)
                            || !bonus.TryGetValue(characteristic, out int bonusEffect)) continue;
                        found[row.Id] = new Steal(characteristic, malusEffect, bonusEffect);
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Steals] Could not read the steals from the catalogue: {ex.Message}");
                }
                _steals = found;
            }
        }
    }
}
