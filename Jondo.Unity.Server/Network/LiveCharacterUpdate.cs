using System;
using System.Text.Json;

namespace Jondo.Unity.Server.Network
{
    /// <summary>The validated, side-effect-free part of POST /api/personaje.</summary>
    public sealed class LiveCharacterUpdate
    {
        public string Character { get; private init; } = "";
        public long? Vitality { get; private init; }
        public long? Wisdom { get; private init; }
        public long? Strength { get; private init; }
        public long? Intelligence { get; private init; }
        public long? Chance { get; private init; }
        public long? Agility { get; private init; }
        public long? Kamas { get; private init; }
        public long? Level { get; private init; }
        public long? MapId { get; private init; }
        public long? Cell { get; private init; }
        public long? ItemGid { get; private init; }
        public long? Quantity { get; private init; }
        public long? MountGid { get; private init; }

        public bool HasChanges => Vitality.HasValue || Wisdom.HasValue || Strength.HasValue
            || Intelligence.HasValue || Chance.HasValue || Agility.HasValue || Kamas.HasValue
            || Level.HasValue || MapId.HasValue || ItemGid.HasValue || MountGid.HasValue;

        public static bool TryParse(string json, out LiveCharacterUpdate? update, out string error)
        {
            update = null;
            error = "";
            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "cuerpo-invalido";
                    return false;
                }

                JsonElement root = document.RootElement;
                string character = root.TryGetProperty("personaje", out var name)
                                   && name.ValueKind == JsonValueKind.String
                    ? name.GetString() ?? ""
                    : "";

                if (!Read(root, "vitalidad", out long? vitality, out error)
                    || !Read(root, "sabiduria", out long? wisdom, out error)
                    || !Read(root, "fuerza", out long? strength, out error)
                    || !Read(root, "inteligencia", out long? intelligence, out error)
                    || !Read(root, "suerte", out long? chance, out error)
                    || !Read(root, "agilidad", out long? agility, out error)
                    || !Read(root, "kamas", out long? kamas, out error)
                    || !Read(root, "nivel", out long? level, out error)
                    || !Read(root, "mapa", out long? mapId, out error)
                    || !Read(root, "celda", out long? cell, out error)
                    || !Read(root, "objeto", out long? item, out error)
                    || !Read(root, "cantidad", out long? quantity, out error)
                    || !Read(root, "montura", out long? mount, out error))
                    return false;

                if (cell.HasValue && !mapId.HasValue)
                {
                    error = "celda-sin-mapa";
                    return false;
                }
                if (quantity.HasValue && !item.HasValue)
                {
                    error = "cantidad-sin-objeto";
                    return false;
                }
                if (item.HasValue && (item <= 0 || item > int.MaxValue
                    || (quantity ?? 1) <= 0 || (quantity ?? 1) > 1_000_000))
                {
                    error = "objeto-invalido";
                    return false;
                }
                if (mount.HasValue && (mount <= 0 || mount > int.MaxValue))
                {
                    error = "montura-invalida";
                    return false;
                }
                if (mapId.HasValue && mapId <= 0)
                {
                    error = "mapa-invalido";
                    return false;
                }
                // El nivel era el unico identificador numerico sin comprobar: llegaba a
                // SetLevelAsync, que hace Math.Clamp(1, techo), asi que un "nivel": -5 o un
                // "nivel": 99999999 salian con HTTP 200 y un nivel distinto del pedido, sin que
                // nadie supiera que se habia recortado. Los demas campos ya dicen que no.
                if (level.HasValue && (level <= 0 || level > int.MaxValue))
                {
                    error = "nivel-invalido";
                    return false;
                }

                update = new LiveCharacterUpdate
                {
                    Character = character.Trim(),
                    Vitality = vitality,
                    Wisdom = wisdom,
                    Strength = strength,
                    Intelligence = intelligence,
                    Chance = chance,
                    Agility = agility,
                    Kamas = kamas,
                    Level = level,
                    MapId = mapId,
                    Cell = cell,
                    ItemGid = item,
                    Quantity = quantity,
                    MountGid = mount,
                };
                return true;
            }
            catch (JsonException)
            {
                error = "cuerpo-invalido";
                return false;
            }
        }

        private static bool Read(JsonElement root, string name, out long? value, out string error)
        {
            value = null;
            error = "";
            if (!root.TryGetProperty(name, out var property)) return true;
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long number))
            {
                value = number;
                return true;
            }

            error = "campo-invalido-" + name;
            return false;
        }
    }
}
