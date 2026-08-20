using Cpp2IL.Core.ISIL;
using LibCpp2IL;


namespace Jondo.Unity.Reversing;

/// <summary>
/// Los nombres de verdad de cada mensaje, sacados del propio cliente.
///
/// El ofuscador renombra las CLASES —el mensaje que se llamaba
/// <c>CharacterExperienceGainEvent</c> pasa a llamarse <c>kuf</c>— pero protobuf necesita el nombre
/// completo en tiempo de ejecución para empaquetar y desempaquetar <c>Any</c>, y ese nombre viaja
/// como cadena de texto. Las cadenas no se ofuscan: están en claro dentro de global-metadata.dat.
///
/// Medido en 3.6.10.10: <b>513</b> nombres del tipo
/// <c>Com.Ankama.Dofus.Server.Game.Protocol.Character.CharacterExperienceGainEvent</c>.
///
/// Lo que faltaba era atarlos a su clase. El código que genera protobuf registra los tipos y sus
/// nombres JUNTOS, en el mismo método: carga la cadena y toca el tipo. Así que se recorre el método
/// y se anotan las dos cosas EN ORDEN; cuando en un método hay tantos nombres como mensajes, la
/// pareja sale por posición.
///
/// Si esto funciona, se acaba el problema que motivó todo lo demás: los nombres salen del cliente,
/// completos, y se vuelven a sacar en cada parche sin adivinar nada y sin preguntarle a nadie.
/// </summary>
public static class Names
{
    /// <summary>Lo que un método toca, en el orden en que lo toca.</summary>
    /// <param name="Method">Dónde se ha encontrado, para poder ir a mirarlo.</param>
    /// <param name="Texts">Los nombres completos que carga.</param>
    /// <param name="Types">Los mensajes del protocolo que menciona.</param>
    public sealed record Site(string Method, List<string> Texts, List<string> Types);

    /// <summary>El prefijo de los nombres de protobuf de Ankama.</summary>
    public const string Prefix = "Com.Ankama.Dofus.";

    /// <summary>
    /// Busca los sitios donde conviven los nombres y los tipos.
    ///
    /// Se barre el cliente ENTERO y no sólo el ensamblado del protocolo: quien registra los tipos
    /// puede ser una clase de arranque que viva en otra parte, y descartarla de antemano sería
    /// decidir la respuesta antes de mirar.
    /// </summary>
    public static List<Site> Sites(ClientReader client, Action<string>? report = null)
    {
        var messages = client.Messages().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        report?.Invoke($"{messages.Count:N0} mensajes en el protocolo; buscando quién los nombra…");

        // Antes de buscar quién carga las cadenas conviene saber si son cadenas siquiera. Los
        // nombres podrían estar en la tabla de TIPOS —un tipo sin ofuscar— y entonces no habría
        // nada que atar: se leerían directamente.
        var declared = client.App.Assemblies
            .SelectMany(a => a.Types)
            .Where(t => (t.Namespace ?? "").StartsWith("Com.Ankama", StringComparison.Ordinal))
            .ToList();

        report?.Invoke($"tipos declarados en Com.Ankama.*: {declared.Count:N0}");

        // La pista buena: los tipos ANIDADOS conservan su nombre real, y su declarante es la clase
        // de tres letras. Si eso se sostiene, la pareja sale sin adivinar nada.
        int anidados = 0;
        foreach (var type in client.Protocol.Types)
        {
            var dentro = type.NestedTypes;
            if (dentro == null || dentro.Count == 0) continue;

            foreach (var nested in dentro)
            {
                if (nested.Name is null or "Types") continue;
                if (anidados < 12)
                    report?.Invoke($"    {type.Name}  ->  anidado «{nested.Name}»");
                anidados++;
            }
        }
        report?.Invoke($"    tipos anidados con nombre: {anidados:N0}");

        var sites = new List<Site>();

        foreach (var method in client.AllMethods())
        {
            List<string>? texts = null;
            List<string>? types = null;

            // Sin Analyze() el ISIL viene vacío y no se encuentra nada. La primera versión no lo
            // llamaba y dio cero métodos en todo el cliente, que era la pista de que el fallo estaba
            // aquí y no en la hipótesis.
            try { method.Analyze(); } catch { continue; }

            foreach (var instruction in method.ConvertedIsil ?? [])
            {
                foreach (var operand in instruction.Operands)
                {
                    switch (operand.Data)
                    {
                        // Un tipo ya resuelto llega por su propio operando, sin pasar por dirección.
                        case IsilTypeMetadataUsageOperand usage
                            when usage.TypeAnalysisContext != null &&
                                 messages.Contains(usage.TypeAnalysisContext.Name):
                            (types ??= new List<string>()).Add(usage.TypeAnalysisContext.Name);
                            break;

                        case IsilImmediateOperand { Value: ulong address }:
                            Look(address, ref texts, ref types, messages);
                            break;

                        case IsilMemoryOperand memory when memory.Base == null && memory.Index == null:
                            Look((ulong)memory.Addend, ref texts, ref types, messages);
                            break;
                    }
                }
            }

            if (texts == null && types == null) continue;

            sites.Add(new Site(
                (method.DeclaringType?.Name ?? "?") + "." + method.Name,
                texts ?? new List<string>(),
                types ?? new List<string>()));
        }

        return sites.OrderByDescending(s => s.Texts.Count + s.Types.Count).ToList();
    }

    /// <summary>Qué hay en esa dirección: un nombre, un mensaje, o nada que interese.</summary>
    private static void Look(ulong address, ref List<string>? texts, ref List<string>? types,
                             HashSet<string> messages)
    {
        try
        {
            var usage = LibCpp2IlMain.GetAnyGlobalByAddress(address);
            if (usage is not { IsValid: true }) return;

            switch (usage.Type)
            {
                case MetadataUsageType.StringLiteral:
                    string? text = usage.AsLiteral();
                    if (text != null && text.StartsWith(Prefix, StringComparison.Ordinal))
                        (texts ??= new List<string>()).Add(text);
                    break;

                case MetadataUsageType.TypeInfo:
                case MetadataUsageType.Type:
                    string? name = usage.AsType()?.baseType?.Name;
                    if (name != null && messages.Contains(name))
                        (types ??= new List<string>()).Add(name);
                    break;
            }
        }
        catch { }
    }
}
