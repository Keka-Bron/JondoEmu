using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Cómo viaja el valor de un efecto de objeto dentro del ivx.
    ///
    /// No es un número suelto en un campo fijo. Cada entrada de efecto lleva el id en f11 y el
    /// valor en UNO de varios campos, y ese campo NO es un hueco: es el que dice de qué tipo es el
    /// efecto, igual que un `oneof`. Sacado del inventario de la captura real, 609 objetos:
    ///
    ///   f4: número          un varint suelto            "+400 vitalidad"
    ///   f5 { f1, f2 }       un rango, máximo y mínimo   "10 a 1 de daños neutrales"
    ///   f6 { f1, f2, f3 }   valor, diceNum y diceSide   el hechizo de un dofus, un título...
    ///   f1: cadena          "Fabricado por: ..."
    ///   f2 { f1..f5 }       una fecha
    ///   nada                "Ligado a una cuenta"
    ///
    /// Escribir un varint en f5 o en f6 es un error de tipo de alambre, no un valor distinto: el
    /// cliente busca ahí un submensaje, no lo encuentra, y se queda sin los parámetros. Eso es lo
    /// que dejaba las armas sin daños y lo que sacaba `{spellNoLvl,,}` en los dofus en vez del
    /// nombre del hechizo — con su propio Player.log diciéndolo con todas las letras:
    ///
    ///   ERROR [Hyperlink] Error while trying to convert an hyperlink of type spellNoLvl,
    ///   parameters , and text .
    ///
    /// La tabla aprendida de la captura (item_effect_fields.json, 121 efectos) manda siempre que
    /// tenga entrada. Para el resto se decide con la propia tabla Effects, y la regla se comprobó
    /// contra los 670 efectos del inventario capturado.
    /// </summary>
    public static class EffectFields
    {
        /// <summary>Qué campo usa cada efecto, aprendido de la captura. Manda sobre la regla.</summary>
        private static readonly Dictionary<int, int> _fields = new Dictionary<int, int>();

        /// <summary>Category y UseDice de cada efecto, que es con lo que se decide el resto.</summary>
        private static readonly Dictionary<int, (int Category, bool UseDice)> _kind =
            new Dictionary<int, (int, bool)>();

        public static int Count => _fields.Count;

        /// <summary>Los efectos de daño de arma, que son los que viajan como rango.</summary>
        private const int WeaponDamageCategory = 2;

        /// <summary>El efecto no se manda: lo suyo es un texto o una fecha que aquí no existe.</summary>
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
        /// Cómo debe viajar esta instancia del efecto.
        ///
        /// Se le pasan los tres números tal y como los declara el objeto — el valor fijo, y el par
        /// de dados — y devuelve el campo y lo que va dentro de él. Un efecto de tirada llega aquí
        /// ya resuelto, con el número en <paramref name="value"/> y los dados a cero: elegir qué
        /// punto del rango le toca a un objeto es cosa de quien lo fabrica, no del protocolo.
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
                    // Una cadena o una fecha: "Fabricado por", "Intercambiable el". Las pone el
                    // servidor sobre el objeto ya fabricado, y aquí no se fabrica nada. Mandarlas
                    // vacías deja al cliente pintando la etiqueta sin nada detrás, así que no van.
                    case AsString:
                    case AsDate: return (Skip, 0, 0, 0);
                    default: return (NoValue, 0, 0, 0);
                }
            }

            if (value == 0 && diceNum == 0 && diceSide == 0) return (NoValue, 0, 0, 0);

            // Daño de arma con rango de verdad. Los de esta categoría que traen un solo número
            // —empujar, atraer, quitar PM— sí viajan como número suelto, y así se ven bien.
            if (kind.Category == WeaponDamageCategory && diceSide != 0 && diceSide != diceNum)
            {
                return (AsRange, diceSide, diceNum, 0);
            }

            // Un efecto compuesto: el que nombra un hechizo, un oficio, un título. Los de tirada
            // no entran aquí — su par de números es el rango del que ya se sacó el valor.
            int nonZero = (value != 0 ? 1 : 0) + (diceNum != 0 ? 1 : 0) + (diceSide != 0 ? 1 : 0);
            if (!kind.UseDice && nonZero > 1) return (AsDice, value, diceNum, diceSide);

            return (AsNumber, OnlyNonZero(value, diceNum, diceSide), 0, 0);
        }

        private static long OnlyNonZero(long value, long diceNum, long diceSide)
            => value != 0 ? value : (diceNum != 0 ? diceNum : diceSide);

        /// <summary>
        /// Lo que este efecto suma a la ficha. Solo los que viajan como número suelto o como rango
        /// cuentan: los compuestos nombran cosas, no mueven características.
        /// </summary>
        public static long SheetValue(int effect, long value, long diceNum, long diceSide)
        {
            var (field, v1, _, _) = Shape(effect, value, diceNum, diceSide);
            return field == AsNumber || field == AsRange ? v1 : 0;
        }
    }
}
