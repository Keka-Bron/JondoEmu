namespace Jondo.Unity.Reversing;

/// <summary>
/// Cuándo dos nombres de mensaje son el mismo nombre.
///
/// Parece una tontería y es la pieza que decide si una medición vale algo. El nombre esperado es
/// una propuesta —Ankama no publica los del protocolo Unity— así que exigir la cadena exacta
/// mediría la puntería en un concurso de sinónimos: <c>MapComplementaryInformationsDataMessage</c>
/// y <c>MapComplementaryInformationsMessage</c> son el mismo mensaje escrito por dos personas.
///
/// ─── Tres listones que se cayeron ───────────────────────────────────────────────────────
///
/// Van escritos porque el error es tentador y lo he cometido tres veces seguidas:
///
///   perdonar una palabra a partir de tres      «AppearanceSlotSetRequest» pasaba por
///                                              «AppearanceSlotSetResult»
///   que el corto quepa entero en el largo      «TitleSelect» pasaba por «TitleSelectRequest»
///   ...y perdonar sólo lo que no sea «papel»   «AuthenticationTicket» pasaba por
///                                              «AuthenticationTicketAccepted»
///
/// El tercero es el instructivo: perdonar lo que sobra obliga a saber qué palabras son relleno, y
/// esa lista hay que alargarla a mano cada vez que aparece un «Accepted», un «End» o un «Storage».
/// Con igualdad no hay lista que mantener.
///
/// Lo que queda: las MISMAS palabras, ni una más, perdonando el orden, los plurales y el «Message»
/// del final. Rechaza sinónimos legítimos —Teleport frente a Zaap— y por tanto mide por lo bajo.
/// En una medición, quedarse corto se nota y pasarse no.
/// </summary>
public static class Naming
{
    /// <summary>Si dos nombres designan el mismo mensaje.</summary>
    public static bool Same(string one, string other)
    {
        var a = Words(one);
        var b = Words(other);
        return a.Count > 0 && b.Count > 0 && a.SetEquals(b);
    }

    /// <summary>
    /// Las palabras de un nombre en PascalCase, en minúsculas, sin plurales y sin el «Message».
    ///
    /// Se quita también la «s» suelta, que aparece cuando alguien escribe «Informations» separando
    /// mal, y se recorta la del final de cada palabra: «Informations» y «Information» son la misma
    /// palabra en dos manos distintas.
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
