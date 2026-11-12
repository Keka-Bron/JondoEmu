using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The character bodies of the client's BodiesDataRoot (datos/bodies.json, written by
    /// tools/extract_bodies.py): what the appearance window's bodies tab offers.
    /// </summary>
    /// <remarks>
    /// A body is the FIRST skin of the look, the breed's base one: the Sacrier's male «base» body is
    /// skin 110, the creation capture's [110, 2172]. Each breed and sex has a «base» and a «retro»
    /// body with availableAtCreation -- which AppearanceBodiesTab, like the faces tab, takes with
    /// nothing unlocked -- and a payable one besides.
    /// </remarks>
    public static class BodyTable
    {
        private sealed class Body
        {
            public int Skin { get; init; }
            public int Breed { get; init; }
            public int Gender { get; init; }
            public bool Creation { get; init; }
        }

        private static readonly Lazy<Dictionary<int, Body>> _bodies = new(Load);

        private static Dictionary<int, Body> Load()
        {
            var bodies = new Dictionary<int, Body>();
            string path = Paths.BodiesJson;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Bodies] {Path.GetFileName(path)} is missing; nobody can change body. " +
                                  "Generate it with tools/extract_bodies.py.");
                return bodies;
            }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in doc.RootElement.GetProperty("bodies").EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int id)) continue;
                    var v = entry.Value;
                    bodies[id] = new Body
                    {
                        Skin = v.GetProperty("skin").GetInt32(),
                        Breed = v.GetProperty("breed").GetInt32(),
                        Gender = v.GetProperty("gender").GetInt32(),
                        Creation = v.GetProperty("creation").GetInt32() != 0,
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bodies] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
            return bodies;
        }

        /// <summary>Whether that breed and sex may wear that body with nothing unlocked.</summary>
        public static bool OfferedFreely(int bodyId, int breedId, int sex)
            => _bodies.Value.TryGetValue(bodyId, out var body) && body.Creation
               && body.Breed == breedId && body.Gender == (sex == 1 ? 1 : 0);

        /// <summary>
        /// The body a breed and sex wear with nothing picked: the creation one whose skin is the
        /// breed look's first (the Sacrier's male 26, skin 110); 0 if none is.
        /// </summary>
        public static int BaseOf(int breedId, int sex)
        {
            var skins = BreedLookCatalog.Get(breedId, sex)?.Skins;
            if (skins == null || skins.Count == 0) return 0;
            foreach (var (id, body) in _bodies.Value)
                if (body.Creation && body.Skin == skins[0] && body.Breed == breedId && body.Gender == (sex == 1 ? 1 : 0))
                    return id;
            return 0;
        }

        /// <summary>The skin of a body of that breed and sex; 0 for none, or one of another breed.</summary>
        public static int SkinFor(int bodyId, int breedId, int sex)
            => bodyId > 0 && _bodies.Value.TryGetValue(bodyId, out var body)
               && body.Breed == breedId && body.Gender == (sex == 1 ? 1 : 0) ? body.Skin : 0;
    }
}
