using System.Collections.ObjectModel;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers;

/// <summary>Client-derived breed/sex defaults, published only after loading completes.</summary>
public static class BreedLookCatalog
{
    public sealed record BreedLook(int Bones, IReadOnlyList<long> Skins,
        IReadOnlyList<long> Scales, IReadOnlyList<long> Colors);

    // Lazy publication keeps concurrent first readers from seeing a partial catalogue.
    private static readonly Lazy<IReadOnlyDictionary<int, IReadOnlyDictionary<int, BreedLook>>> Looks = new(Load);

    public static BreedLook? Get(int breedId, int sex)
    {
        if (!Looks.Value.TryGetValue(breedId, out var bySex)) return null;
        return bySex.TryGetValue(sex == 1 ? 1 : 0, out var look) ? look : bySex.Values.FirstOrDefault();
    }

    private static IReadOnlyDictionary<int, IReadOnlyDictionary<int, BreedLook>> Load()
    {
        try
        {
            if (!File.Exists(Paths.BreedLooksJson))
            {
                Console.WriteLine($"[Looks] Cannot find {Path.GetFileName(Paths.BreedLooksJson)}. " +
                    "Run tools/extract_breed_looks.py to generate it.");
                return new ReadOnlyDictionary<int, IReadOnlyDictionary<int, BreedLook>>(new Dictionary<int, IReadOnlyDictionary<int, BreedLook>>());
            }
            using var document = JsonDocument.Parse(File.ReadAllText(Paths.BreedLooksJson));
            var looks = new Dictionary<int, IReadOnlyDictionary<int, BreedLook>>();
            foreach (var breed in document.RootElement.EnumerateObject())
            {
                if (!int.TryParse(breed.Name, out int breedId)) continue;
                var bySex = new Dictionary<int, BreedLook>();
                foreach (var sex in breed.Value.EnumerateObject())
                {
                    var colors = ReadList(sex.Value, "colors");
                    if (colors.Count != 6 || colors.Any(color => color is < 0 or > 0xFFFFFF))
                        throw new InvalidDataException($"Breed {breedId}/{sex.Name} must define six RGB defaults.");
                    int bones = sex.Value.TryGetProperty("bones", out var value) && value.TryGetInt32(out int number) ? number : 1;
                    bySex[sex.Name == "female" ? 1 : 0] = new(bones, ReadList(sex.Value, "skins"),
                        ReadList(sex.Value, "scales"), colors);
                }
                looks[breedId] = new ReadOnlyDictionary<int, BreedLook>(bySex);
            }
            Console.WriteLine($"[Looks] Loaded the base looks of {looks.Count} breeds.");
            return new ReadOnlyDictionary<int, IReadOnlyDictionary<int, BreedLook>>(looks);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Looks] Error loading the breed looks: {exception.Message}");
            return new ReadOnlyDictionary<int, IReadOnlyDictionary<int, BreedLook>>(new Dictionary<int, IReadOnlyDictionary<int, BreedLook>>());
        }
    }

    private static IReadOnlyList<long> ReadList(JsonElement value, string name)
        => Array.AsReadOnly(value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(item => item.GetInt64()).ToArray() : Array.Empty<long>());
}
