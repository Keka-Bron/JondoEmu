using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jondo.Unity.Launcher.Data
{
    public sealed class BreedAppearanceInfo
    {
        public int BreedId { get; init; }
        public string Name { get; init; } = "";

        public BreedGenderAppearance Male { get; init; } = new();
        public BreedGenderAppearance Female { get; init; } = new();

        public int CreatureBonesId { get; init; }

        public BreedGenderAppearance GetGender(int sex)
        {
            return sex == 1
                ? Female
                : Male;
        }
    }

    public sealed class BreedGenderAppearance
    {
        /*
         * Exemple :
         * {1|3499||57}
         *
         * BonesId    = 1
         * BaseSkinId = 3499
         * Scale      = 57
         */
        public string RawLook { get; set; } = "";

        public int BonesId { get; set; }
        public int BaseSkinId { get; set; }
        public int Scale { get; set; }

        public int[] DefaultColors { get; set; } =
            Array.Empty<int>();

        public Dictionary<int, HeadAppearanceInfo> Heads { get; } =
            new();
    }

    public sealed class HeadAppearanceInfo
    {
        public int Id { get; init; }
        public string SkinsRaw { get; init; } = "";

        public int[] Skins { get; init; } =
            Array.Empty<int>();

        public string AssetId { get; init; } = "";

        public int Order { get; init; }

        public bool Payable { get; init; }

        public bool AvailableAtCreation { get; init; }

        public int NameId { get; init; }
    }

    public static class BreedAppearanceDatabase
    {
        private static readonly Dictionary<int, BreedAppearanceInfo>
            _breeds = new();

        private static bool _loaded;

        public static bool IsLoaded => _loaded;

        public static IReadOnlyDictionary<int, BreedAppearanceInfo>
            Breeds => _breeds;

        // ============================================================
        // LOAD
        // ============================================================

        public static void Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "breed_dump.txt introuvable.",
                    path
                );
            }

            _breeds.Clear();

            string[] lines =
                File.ReadAllLines(path);

            ParseBreeds(lines);
            ParseHeads(lines);

            _loaded = true;

            Console.WriteLine(
                $"[AppearanceDB] Loaded {_breeds.Count} breeds."
            );

            int headCount =
                _breeds.Values
                    .SelectMany(b =>
                        new[]
                        {
                            b.Male.Heads.Count,
                            b.Female.Heads.Count
                        })
                    .Sum();

            Console.WriteLine(
                $"[AppearanceDB] Loaded {headCount} heads."
            );
        }

        // ============================================================
        // PUBLIC ACCESS
        // ============================================================

        public static BreedAppearanceInfo GetBreed(
            int breedId)
        {
            EnsureLoaded();

            if (!_breeds.TryGetValue(
                    breedId,
                    out BreedAppearanceInfo? breed))
            {
                throw new KeyNotFoundException(
                    $"Breed {breedId} absent de l'AppearanceDB."
                );
            }

            return breed;
        }

        public static BreedGenderAppearance GetAppearance(
            int breedId,
            int sex)
        {
            return GetBreed(breedId)
                .GetGender(sex);
        }

        public static HeadAppearanceInfo GetHead(
            int breedId,
            int sex,
            int headId)
        {
            BreedGenderAppearance appearance =
                GetAppearance(
                    breedId,
                    sex
                );

            if (!appearance.Heads.TryGetValue(
                    headId,
                    out HeadAppearanceInfo? head))
            {
                throw new KeyNotFoundException(
                    $"Head {headId} introuvable pour " +
                    $"Breed={breedId}, Sex={sex}."
                );
            }

            return head;
        }

        public static bool TryGetHead(
            int breedId,
            int sex,
            int headId,
            out HeadAppearanceInfo? head)
        {
            head = null;

            if (!_loaded)
                return false;

            if (!_breeds.TryGetValue(
                    breedId,
                    out BreedAppearanceInfo? breed))
            {
                return false;
            }

            return breed
                .GetGender(sex)
                .Heads
                .TryGetValue(
                    headId,
                    out head
                );
        }

        // ============================================================
        // BREEDS
        // ============================================================

        private static void ParseBreeds(
            string[] lines)
        {
            int index = 0;

            while (index < lines.Length)
            {
                string line =
                    lines[index].Trim();

                if (!line.StartsWith(
                        "BreedId=",
                        StringComparison.Ordinal))
                {
                    index++;
                    continue;
                }

                int breedId =
                    ParseIntAfterEquals(line);

                string name = "";
                string maleLook = "";
                string femaleLook = "";
                int creatureBones = 0;

                int[] maleColors =
                    Array.Empty<int>();

                int[] femaleColors =
                    Array.Empty<int>();

                index++;

                while (index < lines.Length)
                {
                    line =
                        lines[index].Trim();

                    if (line.StartsWith(
                        "BreedId=",
                        StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (line.StartsWith(
                        "=== DOFUS",
                        StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (line.StartsWith("Name="))
                    {
                        name =
                            line.Substring(
                                "Name=".Length
                            );
                    }
                    else if (line.StartsWith("MaleLook="))
                    {
                        maleLook =
                            line.Substring(
                                "MaleLook=".Length
                            );
                    }
                    else if (line.StartsWith("FemaleLook="))
                    {
                        femaleLook =
                            line.Substring(
                                "FemaleLook=".Length
                            );
                    }
                    else if (line.StartsWith(
                        "CreatureBonesId="))
                    {
                        creatureBones =
                            ParseIntAfterEquals(
                                line
                            );
                    }
                    else if (line.StartsWith(
                        "MaleColors="))
                    {
                        maleColors =
                            ParseIntArray(
                                line.Substring(
                                    "MaleColors=".Length
                                )
                            );
                    }
                    else if (line.StartsWith(
                        "FemaleColors="))
                    {
                        femaleColors =
                            ParseIntArray(
                                line.Substring(
                                    "FemaleColors=".Length
                                )
                            );
                    }

                    index++;
                }

                var male =
                    ParseBreedLook(
                        maleLook
                    );

                male.DefaultColors =
                    maleColors;

                var female =
                    ParseBreedLook(
                        femaleLook
                    );

                female.DefaultColors =
                    femaleColors;

                _breeds[breedId] =
                    new BreedAppearanceInfo
                    {
                        BreedId = breedId,
                        Name = name,

                        Male = male,
                        Female = female,

                        CreatureBonesId =
                            creatureBones
                    };
            }
        }

        private static BreedGenderAppearance
            ParseBreedLook(string look)
        {
            /*
             * Format observé :
             *
             * {1|3499||57}
             */

            var result =
                new BreedGenderAppearance
                {
                    RawLook = look
                };

            if (string.IsNullOrWhiteSpace(
                    look))
            {
                return result;
            }

            Match match =
                Regex.Match(
                    look,
                    @"^\{(-?\d+)\|(-?\d+)\|\|(-?\d+)\}$"
                );

            if (!match.Success)
            {
                throw new FormatException(
                    $"Look Breed invalide : {look}"
                );
            }

            result.BonesId =
                int.Parse(
                    match.Groups[1].Value,
                    CultureInfo.InvariantCulture
                );

            result.BaseSkinId =
                int.Parse(
                    match.Groups[2].Value,
                    CultureInfo.InvariantCulture
                );

            result.Scale =
                int.Parse(
                    match.Groups[3].Value,
                    CultureInfo.InvariantCulture
                );

            return result;
        }

        // ============================================================
        // HEADS
        // ============================================================

        private static void ParseHeads(
            string[] lines)
        {
            foreach (string rawLine in lines)
            {
                string line =
                    rawLine.Trim();

                if (!line.StartsWith(
                        "Breed=",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                /*
                 * Exemple :
                 *
                 * Breed=18;
                 * BreedName=Ouginak;
                 * Gender=1;
                 * HeadId=591;
                 * Skins=5538;
                 * AssetId=181_11;
                 * Order=10;
                 * Payable=False;
                 * AvailableAtCreation=True;
                 * NameId=1177399
                 */

                Dictionary<string, string> values =
                    line.Split(
                            ';',
                            StringSplitOptions.RemoveEmptyEntries
                        )
                        .Select(part =>
                            part.Split(
                                '=',
                                2
                            ))
                        .Where(parts =>
                            parts.Length == 2)
                        .ToDictionary(
                            parts => parts[0],
                            parts => parts[1]
                        );

                if (!values.ContainsKey("Breed") ||
                    !values.ContainsKey("Gender") ||
                    !values.ContainsKey("HeadId"))
                {
                    continue;
                }

                int breedId =
                    ParseInt(
                        values["Breed"]
                    );

                int gender =
                    ParseInt(
                        values["Gender"]
                    );

                int headId =
                    ParseInt(
                        values["HeadId"]
                    );

                if (!_breeds.TryGetValue(
                        breedId,
                        out BreedAppearanceInfo? breed))
                {
                    continue;
                }

                string skinsRaw =
                    values.GetValueOrDefault(
                        "Skins",
                        ""
                    );

                /*
                 * Pour l'instant les dumps sont majoritairement
                 * un seul skin.
                 *
                 * On supporte quand même plusieurs IDs si le client
                 * en fournit plus tard.
                 */
                int[] skins =
                    ParseSkinList(
                        skinsRaw
                    );

                var head =
                    new HeadAppearanceInfo
                    {
                        Id = headId,

                        SkinsRaw =
                            skinsRaw,

                        Skins =
                            skins,

                        AssetId =
                            values.GetValueOrDefault(
                                "AssetId",
                                ""
                            ),

                        Order =
                            ParseOptionalInt(
                                values,
                                "Order"
                            ),

                        Payable =
                            ParseOptionalBool(
                                values,
                                "Payable"
                            ),

                        AvailableAtCreation =
                            ParseOptionalBool(
                                values,
                                "AvailableAtCreation"
                            ),

                        NameId =
                            ParseOptionalInt(
                                values,
                                "NameId"
                            )
                    };

                BreedGenderAppearance appearance =
                    breed.GetGender(
                        gender
                    );

                appearance.Heads[headId] =
                    head;
            }
        }

        // ============================================================
        // HELPERS
        // ============================================================

        private static void EnsureLoaded()
        {
            if (!_loaded)
            {
                throw new InvalidOperationException(
                    "BreedAppearanceDatabase.Load() " +
                    "n'a pas été appelé."
                );
            }
        }

        private static int ParseIntAfterEquals(
            string line)
        {
            int pos =
                line.IndexOf('=');

            if (pos < 0)
                return 0;

            return ParseInt(
                line.Substring(
                    pos + 1
                )
            );
        }

        private static int ParseInt(
            string text)
        {
            return int.Parse(
                text.Trim(),
                CultureInfo.InvariantCulture
            );
        }

        private static int[] ParseIntArray(
            string text)
        {
            if (string.IsNullOrWhiteSpace(
                    text))
            {
                return Array.Empty<int>();
            }

            return text.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries
                )
                .Select(x =>
                    ParseInt(x))
                .ToArray();
        }

        private static int[] ParseSkinList(
            string text)
        {
            if (string.IsNullOrWhiteSpace(
                    text))
            {
                return Array.Empty<int>();
            }

            /*
             * Support :
             *
             * 5538
             * 5538,5539
             * 5538|5539
             */
            return Regex
                .Matches(
                    text,
                    @"\d+"
                )
                .Select(m =>
                    int.Parse(
                        m.Value,
                        CultureInfo.InvariantCulture
                    ))
                .ToArray();
        }

        private static int ParseOptionalInt(
            Dictionary<string, string> values,
            string key)
        {
            if (!values.TryGetValue(
                    key,
                    out string? value))
            {
                return 0;
            }

            return int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int result)
                    ? result
                    : 0;
        }

        private static bool ParseOptionalBool(
            Dictionary<string, string> values,
            string key)
        {
            if (!values.TryGetValue(
                    key,
                    out string? value))
            {
                return false;
            }

            return bool.TryParse(
                value,
                out bool result
            ) && result;
        }
    }
}