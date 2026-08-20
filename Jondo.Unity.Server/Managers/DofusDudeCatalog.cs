using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>Reader for the Unity reference wrapper used by dofusdude 3.6 exports.</summary>
    internal static class DofusDudeCatalog
    {
        public static IEnumerable<JsonElement> Rows(JsonDocument document)
        {
            if (!document.RootElement.TryGetProperty("references", out var references) ||
                !references.TryGetProperty("RefIds", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Format dofusdude invalide: references.RefIds absent.");

            foreach (var reference in rows.EnumerateArray())
            {
                if (reference.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object)
                    yield return data;
            }
        }

        public static int Int32(JsonElement row, string name, int fallback = 0)
        {
            if (!row.TryGetProperty(name, out var value)) return fallback;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out number)) return number;
            return fallback;
        }

        public static long Int64(JsonElement row, string name, long fallback = 0)
        {
            if (!row.TryGetProperty(name, out var value)) return fallback;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), out number)) return number;
            return fallback;
        }

        public static bool Boolean(JsonElement row, string name)
            => Int32(row, name) != 0;

        public static string Text(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        public static List<int> IntArray(JsonElement row, string name)
        {
            var result = new List<int>();
            if (!row.TryGetProperty(name, out var wrapper)) return result;
            JsonElement array = wrapper;
            if (wrapper.ValueKind == JsonValueKind.Object &&
                wrapper.TryGetProperty("Array", out var wrappedArray)) array = wrappedArray;
            if (array.ValueKind != JsonValueKind.Array) return result;

            foreach (var value in array.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                    result.Add(number);
            }
            return result;
        }
    }
}
