namespace Jondo.Unity.Server;

/// <summary>Exactly six plain RGB choices. A null slot inherits its breed/sex default.</summary>
public readonly record struct CharacterColors
{
    public int? Color1 { get; }
    public int? Color2 { get; }
    public int? Color3 { get; }
    public int? Color4 { get; }
    public int? Color5 { get; }
    public int? Color6 { get; }

    public CharacterColors(int? color1, int? color2, int? color3, int? color4, int? color5, int? color6)
    {
        Color1 = Validate(color1);
        Color2 = Validate(color2);
        Color3 = Validate(color3);
        Color4 = Validate(color4);
        Color5 = Validate(color5);
        Color6 = Validate(color6);
    }

    public int? this[int slot] => slot switch
    {
        1 => Color1, 2 => Color2, 3 => Color3, 4 => Color4, 5 => Color5, 6 => Color6,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    /// <summary>Creation/recolouring input is positional RGB, with -1 for inheritance.</summary>
    public static CharacterColors FromRgb(IReadOnlyList<long> values)
    {
        if (values.Count > 6) throw new ArgumentException("A character has exactly six colour slots.", nameof(values));
        int? Read(int index) => index >= values.Count || values[index] == -1 ? null
            : Validate(checked((int)values[index]));
        return new(Read(0), Read(1), Read(2), Read(3), Read(4), Read(5));
    }

    /// <summary>Compatibility input for saved outfits and the old look migration only.</summary>
    public static CharacterColors FromIndexed(IReadOnlyList<long> values)
    {
        if (values.Count > 6) throw new ArgumentException("A character has exactly six colour slots.", nameof(values));
        if (values.All(value => value == -1 || value is >= 0 and <= 0xFFFFFF)) return FromRgb(values);
        var colors = new int?[6];
        foreach (long value in values)
        {
            if (value == -1) continue;
            long slot = value >> 24;
            if (slot is < 1 or > 6) throw new ArgumentException($"Invalid character colour slot {slot}.", nameof(values));
            int index = (int)slot - 1;
            if (colors[index].HasValue) throw new ArgumentException($"Duplicate character colour slot {slot}.", nameof(values));
            colors[index] = (int)(value & 0xFFFFFF);
        }
        return new(colors[0], colors[1], colors[2], colors[3], colors[4], colors[5]);
    }

    /// <summary>Resolve each slot independently; a missing catalogue default is black.</summary>
    public int[] Resolve(IReadOnlyList<long>? defaults)
    {
        var colors = new int[6];
        for (int index = 0; index < 6; index++)
            colors[index] = this[index + 1] ?? (defaults != null && index < defaults.Count
                ? Validate(checked((int)defaults[index]))!.Value : 0);
        return colors;
    }

    /// <summary>The look boundary adds the slot index; storage never contains it.</summary>
    public List<long> ToIndexed(IReadOnlyList<long>? defaults)
        => Resolve(defaults).Select((rgb, index) => ((long)(index + 1) << 24) | (uint)rgb).ToList();

    private static int? Validate(int? value)
    {
        if (value is < 0 or > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(value), "A colour must be plain RGB.");
        return value;
    }
}
