using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Jondo.Unity.ProtocolBuilder;

// ─── El constructor de protocolos ───────────────────────────────────────────────────────
//
// Ankama rota los nombres de tres letras en cada parche: el jsd de hoy es otra cosa mañana. Las
// capturas que no se hagan ahora no se pueden hacer luego, y las que ya hay dejan de servir en
// cuanto cambie el cliente... salvo que se sepa traducir de una versión a la siguiente.
//
// Esto es lo primero de esa cadena: sacar del cliente su descriptor, que es lo que dice qué
// mensajes hay, cómo se llaman y qué campos lleva cada uno. Lo demás —huellas, emparejado entre
// versiones, traducción de capturas viejas— cuelga de tener esto completo.

if (args.Length == 0)
{
    Console.WriteLine("""
        protocolbuilder — el protocolo del cliente, sacado del propio cliente

          volcar <global-metadata.dat> [salida]

              Busca los descriptores dentro del cliente y los guarda juntos en un
              FileDescriptorSet. Sin arrancar el juego y sin conectarse a nada.

        Ejemplo:
          protocolbuilder volcar "C:\\Jondo 3.6.10.10\\Cliente 3.6.10.10\\Dofus_Data\\il2cpp_data\\Metadata\\global-metadata.dat"
        """);
    return 1;
}

switch (args[0])
{
    case "volcar": return Volcar(args);
    case "mirar": return Mirar(args);
    case "proto": return Proto(args);
    case "probar": return Probar(args);
    case "emparejar": return Emparejar(args);
    default:
        Console.WriteLine($"No sé qué es «{args[0]}».");
        return 1;
}

/// <summary>
/// Qué forma tienen las clases de los mensajes dentro del cliente.
///
/// Antes de emparejar dos versiones hay que saber de dónde se saca la forma de un mensaje. Esto
/// enseña un tipo por dentro —sus propiedades, sus constantes, sus campos— para decidirlo mirando
/// y no adivinando.
/// </summary>
static int Mirar(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Falta el ensamblado. Uso: mirar <dll> [nombre de tipo]");
        return 1;
    }

    using var reader = new AssemblyReader(args[1]);

    if (args.Length < 3)
    {
        var mensajes = reader.ProtocolMessages().OrderBy(t => t.Name).ToList();
        Console.WriteLine($"{reader.Assembly.GetName().Name}");
        Console.WriteLine($"  {reader.Types().Count():N0} tipos, de los cuales {mensajes.Count:N0} " +
                          "se llaman con tres letras minúsculas (los del cable).");
        Console.WriteLine();
        for (int i = 0; i < mensajes.Count; i += 18)
        {
            Console.WriteLine("  " + string.Join(" ", mensajes.Skip(i).Take(18).Select(t => t.Name)));
        }
        return 0;
    }

    var tipo = reader.Types().FirstOrDefault(t => t.Name == args[2]);
    if (tipo == null)
    {
        Console.WriteLine($"No hay ningún tipo «{args[2]}» ahí dentro.");
        return 1;
    }

    Console.WriteLine($"{tipo.FullName}   (base: {tipo.BaseType?.Name})");
    Console.WriteLine($"  interfaces: {string.Join(", ", tipo.GetInterfaces().Select(i => i.Name))}");
    Console.WriteLine();

    const BindingFlags todo = BindingFlags.Public | BindingFlags.NonPublic |
                              BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    Console.WriteLine("  ── constantes ────────────────────────────────");
    foreach (var f in tipo.GetFields(todo).Where(f => f.IsLiteral))
    {
        Console.WriteLine($"    {f.FieldType.Name,-10} {f.Name,-40} = {f.GetRawConstantValue()}");
    }

    Console.WriteLine("  ── campos ────────────────────────────────────");
    foreach (var f in tipo.GetFields(todo).Where(f => !f.IsLiteral).Take(40))
    {
        Console.WriteLine($"    {(f.IsStatic ? "static " : "")}{Corto(f.FieldType),-34} {f.Name}");
    }

    Console.WriteLine("  ── propiedades ───────────────────────────────");
    foreach (var p in tipo.GetProperties(todo).Take(60))
    {
        Console.WriteLine($"    {Corto(p.PropertyType),-34} {p.Name}");
    }

    return 0;
}

/// <summary>
/// El protocolo entero, reconstruido de las clases del cliente y escrito como .proto.
///
/// Es la mitad que faltaba: los números de campo. El descriptor serializado no está en el cliente,
/// pero las clases que genera protobuf llevan cada número en una constante, y el volcado que deja
/// Cpp2IL las conserva enteras.
/// </summary>
static int Proto(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Falta el ensamblado. Uso: proto <dll> [salida.proto]");
        return 1;
    }

    using var reader = new AssemblyReader(args[1]);
    var mensajes = ProtoWriter.Messages(reader);
    var enums = ProtoWriter.Enums(reader);

    int campos = mensajes.Sum(m => m.Fields.Count);
    int dudosos = mensajes.Count(m => m.Doubtful);

    Console.WriteLine($"{Path.GetFileName(args[1])}");
    Console.WriteLine($"  {mensajes.Count:N0} mensajes, {campos:N0} campos, {enums.Count:N0} enumerados");
    if (dudosos > 0) Console.WriteLine($"  {dudosos:N0} donde no cuadran las cuentas (van marcados).");

    string salida = args.Length > 2 ? args[2] : "protocolo.proto";
    File.WriteAllText(salida, ProtoWriter.Write(mensajes, enums, Path.GetFileName(args[1])));
    Console.WriteLine($"  escrito en {salida}");

    return 0;
}

static Matcher.Model Leer(string assembly)
{
    using var reader = new AssemblyReader(assembly);
    return new Matcher.Model(ProtoWriter.Messages(reader), ProtoWriter.Enums(reader));
}

/// <summary>
/// El techo del emparejador, medido contra sí mismo.
///
/// Se coge el protocolo de ahora, se le rotan los nombres como haría Ankama y se le pide que
/// reconstruya la correspondencia. La respuesta correcta se conoce entera, así que sale un
/// porcentaje exacto. No simula un parche de verdad —ahí también hay mensajes nuevos y campos
/// añadidos— pero dice cuánto se puede esperar como mucho.
/// </summary>
static int Probar(string[] args)
{
    if (args.Length < 2) { Console.WriteLine("Uso: probar <dll>"); return 1; }

    var uno = Leer(args[1]);
    var (otro, verdad) = Shuffle.Rotate(uno);

    var resultado = Matcher.Match(uno, otro);

    int bien = 0, mal = 0;
    foreach (var (from, to) in resultado.Pairs)
    {
        if (verdad.TryGetValue(from, out string? esperado) && esperado == to) bien++;
        else mal++;
    }

    Console.WriteLine($"  {uno.Messages.Count:N0} mensajes, con los nombres barajados");
    Console.WriteLine($"  emparejados bien : {bien:N0}  ({100.0 * bien / uno.Messages.Count:0.0} %)");
    Console.WriteLine($"  emparejados MAL  : {mal:N0}");
    Console.WriteLine($"  ambiguos         : {resultado.Ambiguous.Count:N0}");
    Console.WriteLine($"  sin pareja       : {resultado.Alone.Count:N0}");
    return 0;
}

/// <summary>Empareja dos versiones de verdad, la vieja y la nueva.</summary>
static int Emparejar(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Uso: emparejar <dll version vieja> <dll version nueva> [salida.txt]");
        return 1;
    }

    var vieja = Leer(args[1]);
    var nueva = Leer(args[2]);

    Console.WriteLine($"  vieja: {vieja.Messages.Count:N0} mensajes, {vieja.Enums.Count:N0} enumerados");
    Console.WriteLine($"  nueva: {nueva.Messages.Count:N0} mensajes, {nueva.Enums.Count:N0} enumerados");

    var resultado = Matcher.Match(vieja, nueva);

    // Cuántos conservan el nombre: si Ankama no hubiera rotado nada, esto sería el 100% y el
    // emparejador no haría falta. Sirve para saber a qué se enfrenta uno de verdad.
    int iguales = resultado.Pairs.Count(p => p.Key == p.Value);

    Console.WriteLine();
    Console.WriteLine($"  emparejados : {resultado.Pairs.Count:N0} " +
                      $"({100.0 * resultado.Pairs.Count / vieja.Messages.Count:0.0} % de los viejos)");
    Console.WriteLine($"     de ellos, con el MISMO nombre en las dos: {iguales:N0}");
    Console.WriteLine($"     o sea que cambiaron de nombre: {resultado.Pairs.Count - iguales:N0}");
    Console.WriteLine($"  ambiguos    : {resultado.Ambiguous.Count:N0}   (más de un candidato con su forma)");
    Console.WriteLine($"  sin pareja  : {resultado.Alone.Count:N0}   (ninguno con su forma: nuevos o retirados)");

    if (args.Length > 3)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# viejo -> nuevo");
        foreach (var (from, to) in resultado.Pairs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"{from} -> {to}{(from == to ? "   (igual)" : "")}");
        }
        sb.AppendLine();
        sb.AppendLine("# sin pareja");
        foreach (string name in resultado.Alone) sb.AppendLine(name);
        sb.AppendLine();
        sb.AppendLine("# ambiguos");
        foreach (string name in resultado.Ambiguous) sb.AppendLine(name);
        File.WriteAllText(args[3], sb.ToString());
        Console.WriteLine($"  escrito en {args[3]}");
    }

    return 0;
}

static string Corto(Type t)
{
    string name = t.Name;
    if (!t.IsGenericType) return name;
    string args2 = string.Join(",", t.GetGenericArguments().Select(a => a.Name));
    return $"{name[..name.IndexOf('`')]}<{args2}>";
}

static int Volcar(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Falta la ruta del global-metadata.dat.");
        return 1;
    }

    string metadata = args[1];
    if (!File.Exists(metadata))
    {
        Console.WriteLine($"No está el fichero: {metadata}");
        return 1;
    }

    string salida = args.Length > 2
        ? args[2]
        : Path.Combine(Directory.GetCurrentDirectory(), "protocolo.desc");

    Console.WriteLine($"Leyendo {new FileInfo(metadata).Length / (1024 * 1024)} MB de metadatos...");
    var blobs = DescriptorExtractor.FindIn(metadata);
    if (blobs.Count == 0)
    {
        Console.WriteLine("  No hay ningún descriptor. O el cliente los guarda de otra forma, o");
        Console.WriteLine("  van partidos de una manera que esto no reconstruye.");
        return 2;
    }

    var set = DescriptorExtractor.AsSet(blobs);
    File.WriteAllBytes(salida, set.ToByteArray());

    int mensajes = 0, campos = 0, enums = 0;
    foreach (var file in set.File)
    {
        enums += file.EnumType.Count;
        foreach (var m in file.MessageType) Contar(m, ref mensajes, ref campos);
    }

    Console.WriteLine();
    Console.WriteLine($"  {blobs.Count} ficheros .proto");
    Console.WriteLine($"  {mensajes:N0} mensajes, {campos:N0} campos, {enums:N0} enumerados");
    Console.WriteLine($"  escrito en {salida}");
    Console.WriteLine();

    foreach (var blob in blobs.OrderByDescending(b => b.File.MessageType.Count).Take(12))
    {
        int propios = 0, suyos = 0;
        foreach (var m in blob.File.MessageType) Contar(m, ref propios, ref suyos);
        Console.WriteLine($"    {blob.File.Name,-52} {propios,5} mensajes   {blob.Length,7:N0} bytes");
    }
    if (blobs.Count > 12) Console.WriteLine($"    ... y {blobs.Count - 12} más");

    return 0;
}

static void Contar(DescriptorProto message, ref int mensajes, ref int campos)
{
    mensajes++;
    campos += message.Field.Count;
    foreach (var anidado in message.NestedType) Contar(anidado, ref mensajes, ref campos);
}
