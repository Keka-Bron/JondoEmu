using Cpp2IL.Core.ISIL;
using LibCpp2IL;


namespace Jondo.Unity.Reversing;

/// <summary>
/// The real names of each message, taken from the client itself.
///
/// The obfuscator renames the CLASSES —the message that was called
/// <c>CharacterExperienceGainEvent</c> becomes <c>kuf</c>— but protobuf needs the full name
/// at run time to pack and unpack <c>Any</c>, and that name travels
/// as a text string. Strings are not obfuscated: they are in the clear inside global-metadata.dat.
///
/// Measured in 3.6.10.10: <b>513</b> names of the kind
/// <c>Com.Ankama.Dofus.Server.Game.Protocol.Character.CharacterExperienceGainEvent</c>.
///
/// What was missing was tying them to their class. The code protobuf generates registers the types and their
/// names TOGETHER, in the same method: it loads the string and touches the type. So the method is walked
/// and both things are noted IN ORDER; when a method has as many names as messages, the
/// pair comes out by position.
///
/// If this works, the problem that motivated everything else is over: the names come from the client,
/// complete, and are taken again on every patch without guessing anything and without asking anyone.
/// </summary>
public static class Names
{
    /// <summary>What a method touches, in the order it touches it.</summary>
    /// <param name="Method">Where it was found, to be able to go and look at it.</param>
    /// <param name="Texts">The full names it loads.</param>
    /// <param name="Types">The protocol messages it mentions.</param>
    public sealed record Site(string Method, List<string> Texts, List<string> Types);

    /// <summary>The prefix of Ankama's protobuf names.</summary>
    public const string Prefix = "Com.Ankama.Dofus.";

    /// <summary>
    /// Looks for the places where the names and the types live together.
    ///
    /// The WHOLE client is swept and not only the protocol assembly: whoever registers the types
    /// can be a startup class living somewhere else, and ruling it out beforehand would be
    /// deciding the answer before looking.
    /// </summary>
    public static List<Site> Sites(ClientReader client, Action<string>? report = null)
    {
        var messages = client.Messages().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        report?.Invoke($"{messages.Count:N0} mensajes en el protocolo; buscando quién los nombra…");

        // Before looking for who loads the strings it is worth knowing whether they are strings at all. The
        // names could be in the TYPES table —an unobfuscated type— and then there would be
        // nothing to tie: they would be read directly.
        var declared = client.App.Assemblies
            .SelectMany(a => a.Types)
            .Where(t => (t.Namespace ?? "").StartsWith("Com.Ankama", StringComparison.Ordinal))
            .ToList();

        report?.Invoke($"tipos declarados en Com.Ankama.*: {declared.Count:N0}");

        // The good clue: NESTED types keep their real name, and their declaring type is the
        // three-letter class. If that holds, the pair comes out without guessing anything.
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

            // Without Analyze() the ISIL comes empty and nothing is found. The first version did not
            // call it and gave zero methods in the whole client, which was the clue that the bug was
            // here and not in the hypothesis.
            try { method.Analyze(); } catch { continue; }

            foreach (var instruction in method.ConvertedIsil ?? [])
            {
                foreach (var operand in instruction.Operands)
                {
                    switch (operand.Data)
                    {
                        // An already resolved type arrives through its own operand, without going through an address.
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

    /// <summary>What is at that address: a name, a message, or nothing of interest.</summary>
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
