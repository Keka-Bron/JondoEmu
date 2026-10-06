namespace Jondo.Unity.Reversing;

/// <summary>
/// When two message names are the same name.
///
/// It looks like nonsense and it is the piece that decides whether a measurement is worth anything. The expected name is
/// a proposal —Ankama does not publish the Unity protocol's— so requiring the exact string
/// would measure aim in a synonyms contest: <c>MapComplementaryInformationsDataMessage</c>
/// and <c>MapComplementaryInformationsMessage</c> are the same message written by two people.
///
/// ─── Three bars that fell ───────────────────────────────────────────────────────────────
///
/// They are written down because the mistake is tempting and I have made it three times in a row:
///
///   forgiving one word from three on           «AppearanceSlotSetRequest» passed as
///                                              «AppearanceSlotSetResult»
///   the short one fitting whole in the long    «TitleSelect» passed as «TitleSelectRequest»
///   ...and forgiving only what is not «role»   «AuthenticationTicket» passed as
///                                              «AuthenticationTicketAccepted»
///
/// The third is the instructive one: forgiving what is left over forces knowing which words are filler, and
/// that list has to be extended by hand every time an «Accepted», an «End» or a «Storage» shows up.
/// With equality there is no list to maintain.
///
/// What remains: the SAME words, not one more, forgiving the order, the plurals and the final
/// «Message». It rejects legitimate synonyms —Teleport versus Zaap— and therefore measures low.
/// In a measurement, falling short shows and overshooting does not.
/// </summary>
public static class Naming
{
    /// <summary>Whether two names designate the same message.</summary>
    public static bool Same(string one, string other)
    {
        var a = Words(one);
        var b = Words(other);
        return a.Count > 0 && b.Count > 0 && a.SetEquals(b);
    }

    /// <summary>
    /// The words of a PascalCase name, lowercase, without plurals and without the «Message».
    ///
    /// The loose «s» is also removed, which appears when someone writes «Informations» splitting
    /// badly, and the final one of each word is trimmed: «Informations» and «Information» are the same
    /// word in two different hands.
    /// </summary>
    public static HashSet<string> Words(string name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (char c in name)
        {
            if (char.IsUpper(c) && current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
            current.Append(char.ToLowerInvariant(c));
        }
        if (current.Length > 0) words.Add(current.ToString());

        words.RemoveAll(w => w is "message" or "s");
        return words.Select(w => w.TrimEnd('s')).ToHashSet(StringComparer.Ordinal);
    }
}
