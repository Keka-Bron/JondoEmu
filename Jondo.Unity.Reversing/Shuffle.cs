namespace Jondo.Unity.Reversing;

/// <summary>
/// A fake version: the same protocol with the names rotated.
///
/// It is the only way of measuring the matcher BEFORE the patch arrives. Today's protocol
/// is taken, its names are shuffled as Ankama would, and the matcher is asked to
/// rebuild the correspondence. Since the correct answer is known whole, an exact
/// hit percentage comes out and, better still, the list of the ones it misses.
///
/// What it does NOT simulate: in a real patch there are new messages, messages that disappear and
/// fields that are added. Here only the names change, so this number is the matcher's
/// CEILING, not what it will give on patch day.
/// </summary>
public static class Shuffle
{
    /// <summary>Returns the protocol with the names changed, and the dictionary of truth.</summary>
    public static (Matcher.Model Shuffled, Dictionary<string, string> Truth) Rotate(
        Matcher.Model model, int seed = 7)
    {
        var random = new Random(seed);

        var names = model.Messages.Select(m => m.Name)
            .Concat(model.Enums.Select(e => e.Name))
            .ToList();

        // New three-letter names, in the same style as Ankama's and without repeating.
        var pool = new List<string>();
        for (char a = 'a'; a <= 'z' && pool.Count < names.Count * 2; a++)
            for (char b = 'a'; b <= 'z' && pool.Count < names.Count * 2; b++)
                for (char c = 'a'; c <= 'z' && pool.Count < names.Count * 2; c++)
                    pool.Add($"{a}{b}{c}");

        // Fisher-Yates shuffle, so that the new name keeps no relation with the
        // old one: if any were left, the matcher would get it right for the wrong reason.
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        var truth = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Count; i++) truth[names[i]] = pool[i];

        string Renamed(string type) => truth.TryGetValue(type, out string? now) ? now : type;

        var messages = model.Messages.Select(m => new ProtoWriter.Message(
            Renamed(m.Name),
            m.Fields.Select(f => new ProtoWriter.Field(f.Number, Renamed(f.Type), f.Name, f.Repeated)).ToList(),
            m.Doubtful)).ToList();

        var enums = model.Enums.Select(e => new ProtoWriter.Enumeration(
            Renamed(e.Name), e.Values)).ToList();

        return (new Matcher.Model(messages, enums), truth);
    }
}
