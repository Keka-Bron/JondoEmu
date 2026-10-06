using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Jondo.Unity.Reversing;

// ─── The protocol builder ───────────────────────────────────────────────────────────────
//
// Ankama rotates the three-letter names on every patch: today's jsd is something else tomorrow. The
// captures not made now cannot be made later, and the ones there are stop being useful as
// soon as the client changes... unless one knows how to translate from one version to the next.
//
// This is the first link of that chain: taking the client's descriptor out of it, which is what says which
// messages there are, what they are called and which fields each one carries. The rest —fingerprints, matching between
// versions, translation of old captures— hangs on having this complete.

if (args.Length == 0)
{
    Console.WriteLine("""
        protocolbuilder — el protocolo del cliente, sacado del propio cliente

        Sacar la forma
          proto <dll del protocolo> [salida.proto]      mensajes, campos y números
          mirar <dll> [tipo]                            qué hay dentro de una clase
          volcar <global-metadata.dat> [salida]         el camino muerto del descriptor (§3.1)

        Emparejar dos versiones
          emparejar <dll vieja> <dll nueva> [salida]    quién es quién entre parches
          probar <dll> [opcodes del emulador.tsv]       el techo, con los nombres barajados

        Leer el código y bautizar
          indexar <carpeta del cliente> [salida.json] [saltos]
                                                        qué clase del cliente toca cada mensaje
          expediente <dll> <indice> <anclas> <mensaje|--todos|--medidos> [carpeta] [--ciego]
                                                        todo lo que se sabe de un mensaje, junto
          preguntar <dll> <indice> <anclas> [salida.tsv] [--evaluar] [--limite N]
                                                        el expediente delante del modelo
          evaluar <anclas.tsv> <propuestas.tsv>         cuánto acierta, contra lo medido

        Traer los clientes de en medio
          bajar --lista                                 qué versiones sirve todavía la CDN
          bajar <desde> <hasta> [carpeta]               los clientes de la cadena, sólo lo justo
          cadena <carpeta de clientes> [opcodes.tsv]    parche a parche, contra el salto directo

        Aplicar el mapeo al emulador
          capa <cliente> <anclas.tsv> <emulador> [viejo]  genera Op.cs con un nombre por opcode

        Ejemplo:
          protocolbuilder indexar "C:\Jondo 3.6.10.10\Cliente 3.6.10.10" datos/indice_3.6.10.10.json
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
    case "indexar": return Indexar(args);
    case "expediente": return Expediente(args);
    case "preguntar": return Preguntar(args).GetAwaiter().GetResult();
    case "evaluar": return Evaluar(args);
    case "mapear": return Mapear(args);
    case "bajar": return Bajar(args).GetAwaiter().GetResult();
    case "cadena": return Cadena(args);
    case "capa": return Capa(args);
    case "nombres": return Nombres(args);
    case "cabecera": return Cabecera(args);
    default:
        Console.WriteLine($"No sé qué es «{args[0]}».");
        return 1;
}

/// <summary>
/// What shape the message classes have inside the client.
///
/// Before matching two versions one has to know where a message's shape is taken from. This
/// shows a type from the inside —its properties, its constants, its fields— to decide it by looking
/// and not by guessing.
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
/// The whole protocol, rebuilt from the client's classes and written as .proto.
///
/// It is the half that was missing: the field numbers. The serialised descriptor is not in the client,
/// but the classes protobuf generates carry each number in a constant, and the dump Cpp2IL
/// leaves keeps them whole.
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

static Matcher.Model Leer(string assembly) => ProtoWriter.Model(assembly);

/// <summary>
/// The matcher's ceiling, measured against itself.
///
/// Today's protocol is taken, its names are rotated as Ankama would and it is asked to
/// rebuild the correspondence. The correct answer is known whole, so an exact
/// percentage comes out. It does not simulate a real patch —there there are also new messages and fields
/// added— but it says how much can be expected at most.
/// </summary>
static int Probar(string[] args)
{
    if (args.Length < 2) { Console.WriteLine("Uso: probar <dll> [opcodes del emulador.tsv]"); return 1; }

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

    // The percentage over the two thousand messages is a curiosity. The number that decides whether the
    // emulator starts on patch day is another: of the ones the emulator really uses, how many
    // survive. They are the big messages with a neighbourhood, so the figure does not resemble the other.
    if (args.Length > 2 && File.Exists(args[2]))
    {
        var suyos = Emulador(args[2]);
        int mios = suyos.Count(o => uno.Messages.Any(m => m.Name == o));
        int salvados = suyos.Count(o => resultado.Pairs.TryGetValue(o, out string? donde) &&
                                        verdad.GetValueOrDefault(o) == donde);
        Console.WriteLine();
        Console.WriteLine($"  de los {mios:N0} que usa el emulador y están en el protocolo: " +
                          $"{salvados:N0} ({(mios == 0 ? 0 : 100.0 * salvados / mios):0.0} %)");

        var perdidos = suyos.Where(o => uno.Messages.Any(m => m.Name == o) &&
                                        !resultado.Pairs.ContainsKey(o)).ToList();
        if (perdidos.Count > 0)
        {
            Console.WriteLine($"  se quedan sin pareja: {string.Join(" ", perdidos.Take(40))}" +
                              (perdidos.Count > 40 ? $" ...y {perdidos.Count - 40} más" : ""));
        }
    }
    return 0;
}

/// <summary>
/// Which region of the metadata file is which, and whether the block of remnants falls inside any.
/// </summary>
static int Cabecera(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Uso: cabecera <carpeta del cliente> [posición a localizar]");
        return 1;
    }

    using var client = new ClientReader(args[1]);

    // Where the block with the real names starts, measured with grep over the file.
    long buscada = args.Length > 2 && long.TryParse(args[2], out long p) ? p : 21_607_975;

    foreach (string miembro in Header.Members()) Console.WriteLine(miembro);
    Console.WriteLine();

    var campos = Header.Fields();
    Console.WriteLine($"versión de metadatos: {campos.GetValueOrDefault("version")}");
    Console.WriteLine();

    var regiones = Header.Regions();
    Console.WriteLine($"  {regiones.Count} regiones declaradas:");
    foreach (var region in regiones)
    {
        string marca = region.Holds(buscada) ? "  <<< AQUÍ CAE EL BLOQUE" : "";
        Console.WriteLine($"    {region.Name,-34} {region.Offset,12:N0} + {region.Size,11:N0}{marca}");
    }

    var dentro = regiones.Where(r => r.Holds(buscada)).ToList();
    Console.WriteLine();
    Console.WriteLine(dentro.Count == 0
        ? $"  La posición {buscada:N0} NO cae en ninguna región declarada: es un resto no referenciado."
        : $"  La posición {buscada:N0} cae en: {string.Join(", ", dentro.Select(r => r.Name))}");

    // And the order: the protocol's messages, as the types table enumerates them.
    Console.WriteLine();
    Crudo(args[1]);
    var unTipo = client.Protocol.Types.First(t => t.Fields.Count > 0);
    Console.WriteLine("   tipo " + unTipo.Name + ", campos " + unTipo.Fields.Count);
    var unCampo = unTipo.Fields[0];
    Console.WriteLine("   campo " + unCampo.Name + "  backing=" + (unCampo.BackingData?.GetType().Name ?? "null"));
    if (unCampo.BackingData != null) foreach (var kv in Header.Numbers(unCampo.BackingData)) Console.WriteLine("     bd." + kv.Key + " = " + kv.Value);
    var parejas = Header.Pairs(client, l => Console.WriteLine(l));
    foreach (var pareja in parejas.Take(12))
        Console.WriteLine("      " + pareja.Opcode + "  =  " + pareja.Real);

    var tipos = Header.Types();
    var mensajes = client.Messages().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
    var mios = tipos.Where(t => mensajes.Contains(t.Name)).ToList();

    Console.WriteLine();
    Console.WriteLine($"  {tipos.Count:N0} tipos en la tabla; {mios.Count:N0} son mensajes del protocolo");
    if (mios.Count > 0)
    {
        Console.WriteLine($"    del índice {mios[0].Index:N0} al {mios[^1].Index:N0}");
        Console.WriteLine($"    índices de nombre: del {mios.Min(t => t.NameIndex):N0} al {mios.Max(t => t.NameIndex):N0}");
        Console.WriteLine();
        Console.WriteLine("    los seis primeros, en orden de tabla:");
        foreach (var tipo in mios.Take(6))
            Console.WriteLine($"      #{tipo.Index,-7:N0} nombre@{tipo.NameIndex,-10:N0} {tipo.Name}");
    }

    return 0;
}

/// <summary>
/// The real names, taken from the client. The <see cref="Names"/> probe.
/// </summary>
static int Nombres(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Uso: nombres <carpeta del cliente>");
        return 1;
    }

    using var client = new ClientReader(args[1]);
    var sitios = Names.Sites(client, linea => Console.WriteLine(linea));

    Console.WriteLine();
    Console.WriteLine($"  {sitios.Count:N0} métodos tocan un nombre o un mensaje");
    Console.WriteLine($"    con nombres Y mensajes: {sitios.Count(s => s.Texts.Count > 0 && s.Types.Count > 0):N0}");
    Console.WriteLine($"    sólo nombres          : {sitios.Count(s => s.Texts.Count > 0 && s.Types.Count == 0):N0}");
    Console.WriteLine($"    sólo mensajes         : {sitios.Count(s => s.Texts.Count == 0 && s.Types.Count > 0):N0}");
    Console.WriteLine();
    Console.WriteLine("  los diez más cargados:");
    foreach (var sitio in sitios.Take(10))
    {
        Console.WriteLine($"    {sitio.Method,-50} {sitio.Texts.Count,4} nombres  {sitio.Types.Count,4} mensajes");
        if (sitio.Texts.Count > 0) Console.WriteLine($"      texto[0] : {sitio.Texts[0]}");
        if (sitio.Types.Count > 0) Console.WriteLine($"      mensaje[0]: {sitio.Types[0]}");
    }

    // The case that would solve everything: a method with ONE name and ONE message is a direct pair.
    var parejas = sitios.Where(s => s.Texts.Count == 1 && s.Types.Count == 1).ToList();
    Console.WriteLine();
    Console.WriteLine($"  métodos con exactamente un nombre y un mensaje: {parejas.Count:N0}");
    foreach (var pareja in parejas.Take(8))
        Console.WriteLine($"    {pareja.Types[0]}  =  {pareja.Texts[0]}");

    return 0;
}

/// <summary>
/// The Op layer: one name per opcode, generated from the client and from the anchors.
///
/// It is the last link that was missing. Without this the mapping stays a pretty file: applying it
/// means editing by hand hundreds of three-letter literals spread around the emulator.
/// </summary>
static int Capa(string[] args)
{
    if (args.Length < 4)
    {
        Console.WriteLine("Uso: capa <cliente o dll> <anclas.tsv> <carpeta del emulador> [cliente anterior]");
        Console.WriteLine("Ej.: capa \"..\\Cliente 3.6.10.10\" datos/anclas_3.6.10.10.tsv . \"..\\clientes\\Cliente 3.6.4.3\"");
        return 1;
    }

    // The protocol is TWO assemblies —the game one and the connection one— and the emulator's opcodes
    // come from both. With only one, 37 connection messages would seem not to exist and the
    // sweep would take them for garbage.
    var ahora = Messages(args[1]);
    Console.WriteLine($"{ahora.Count:N0} mensajes en el protocolo de {Mapper.VersionOf(args[1])}");

    // Those of the previous version serve to tell a remnant from an error. Without them, an opcode
    // that no longer exists would be confused with «this was not an opcode», which are very different things.
    var antes = args.Length > 4 ? Messages(args[4]) : new HashSet<string>(StringComparer.Ordinal);

    var anclas = Dossier.Anchors(args[2]);
    var ligados = Layer.Bound("datos", Mapper.VersionOf(args[1]));
    var barrido = Layer.Scan(args[3], ahora, antes, anclas, ligados);

    Console.WriteLine();
    Console.WriteLine($"  {barrido.Slots.Count:N0} opcodes de verdad, " +
                      $"{barrido.Slots.Sum(s => s.Uses):N0} usos en el código");
    Console.WriteLine($"    con nombre propio : {barrido.Slots.Count(s => s.Name.Length > 0):N0}");
    Console.WriteLine($"    sólo con opcode   : {barrido.Slots.Count(s => s.Name.Length == 0):N0}");

    if (barrido.Stale.Count > 0)
    {
        // This is a finding, not a formality: they are opcodes the emulator uses and that in this
        // client version DO NOT EXIST. They cannot match anything; the code using them is
        // dead and nobody knew.
        Console.WriteLine();
        Console.WriteLine($"  {barrido.Stale.Count:N0} literales son de una versión anterior y aquí ya no existen:");
        foreach (var trozo in barrido.Stale.Chunk(16))
            Console.WriteLine("    " + string.Join(" ", trozo));
    }

    if (barrido.Ignored.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  {barrido.Ignored.Count:N0} literales de tres letras que no son opcodes de nada, se dejan en paz:");
        Console.WriteLine("    " + string.Join(" ", barrido.Ignored));
    }

    string salida = Path.Combine(args[3], "Jondo.Unity.Protocol", "Op.cs");
    Console.WriteLine();
    Console.WriteLine($"  escrito en {Layer.Write(barrido, Mapper.VersionOf(args[1]), salida)}");

    // Without --aplicar only what would change is shown. Touching forty emulator files is not
    // something that should happen for typing one command too many.
    bool aplicar = args.Contains("--aplicar");
    var cambios = Layer.Apply(args[3], barrido, aplicar);

    Console.WriteLine();
    Console.WriteLine($"  {cambios.Count:N0} líneas en {cambios.Select(c => c.File).Distinct().Count():N0} ficheros" +
                      (aplicar ? " cambiadas" : " cambiarían (--aplicar para hacerlo)"));

    foreach (var cambio in cambios.Take(aplicar ? 0 : 6))
    {
        Console.WriteLine($"    {Path.GetFileName(cambio.File)}:{cambio.Line}");
        Console.WriteLine($"      - {Recorta(cambio.Before)}");
        Console.WriteLine($"      + {Recorta(cambio.After)}");
    }

    return 0;
}

static string Recorta(string linea) => linea.Length <= 96 ? linea : linea[..93] + "...";

/// <summary>The message names of the protocol's two assemblies.</summary>
static HashSet<string> Messages(string clientOrDll)
{
    var names = new HashSet<string>(StringComparer.Ordinal);

    foreach (string assembly in new[] { "Ankama.Dofus.Protocol.Game", "Ankama.Dofus.Protocol.Connection" })
    {
        string path = File.Exists(clientOrDll)
            ? Path.Combine(Path.GetDirectoryName(clientOrDll)!, assembly + ".dll")
            : Path.Combine(Path.GetDirectoryName(Mapper.ProtocolDll(clientOrDll))!, assembly + ".dll");

        if (!File.Exists(path)) continue;
        foreach (var message in ProtoWriter.Model(path).Messages) names.Add(message.Name);
    }

    if (names.Count == 0) throw new InvalidOperationException($"no he encontrado el protocolo en {clientOrDll}");
    return names;
}

/// <summary>
/// The chain walked, patch by patch, against the direct jump.
///
/// It is the whole experiment: it measures each jump separately, composes the chain, and puts the result
/// next to the one-go jump so that it shows whether chaining is of any use or not.
/// </summary>
static int Cadena(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Uso: cadena <carpeta de clientes> [opcodes del emulador.tsv]");
        return 1;
    }

    var clientes = Directory.GetDirectories(args[1])
        .Where(d => File.Exists(Path.Combine(d, "GameAssembly.dll")))
        .OrderBy(d => Mapper.VersionOf(Path.GetFileName(d)), Comparer<string>.Create(Cytrus.Compare))
        .ToList();

    if (clientes.Count < 2)
    {
        Console.WriteLine($"En {args[1]} hay {clientes.Count} cliente(s). Hacen falta al menos dos.");
        return 1;
    }

    Console.WriteLine($"{clientes.Count} versiones: " +
                      string.Join(" → ", clientes.Select(c => Mapper.VersionOf(Path.GetFileName(c)))));
    Console.WriteLine();

    var relay = new Relay();
    var salida = relay.Run(clientes, linea => Console.WriteLine(linea));

    Console.WriteLine();
    Console.WriteLine("  salto                nombres  formas  semillas  rota │ empareja   duda   solo │ acierta  FALLA  calla");
    Console.WriteLine("  ────────────────────────────────────────────────────┼───────────────────────┼─────────────────────");
    foreach (var salto in salida.Hops)
    {
        string juicio = salto.Rotated ? "  sí" : "  no";
        string medida = salto.Rotated
            ? "      —      —      —"
            : $" {salto.Right,7:N0} {salto.Wrong,6:N0} {salto.Unsure,6:N0}";
        Console.WriteLine(
            $"  {salto.From,-8}→{salto.To,-9} {salto.SameName,6:N0}  {salto.SameShape,6:N0}    {salto.Seeds,6:N0} {juicio} │" +
            $" {salto.Paired,8:N0} {salto.Doubtful,6:N0} {salto.Gone,6:N0} │{medida}");
    }

    // What decides whether the matcher is any good: how many it matches WRONGLY when the answer is known. A
    // failure is not noticed when looking at the result and poisons everything that comes after. Only the
    // jumps without rotation count, which are the only ones where there is an answer to know.
    var limpios = salida.Hops.Where(h => !h.Rotated).ToList();
    if (limpios.Count > 0)
    {
        int fallos = limpios.Sum(h => h.Wrong), aciertos = limpios.Sum(h => h.Right);
        int total = limpios.Sum(h => h.OldCount);
        Console.WriteLine();
        Console.WriteLine($"  En los {limpios.Count} saltos SIN rotación hay respuesta conocida, y el emparejador no la ve:");
        Console.WriteLine($"    de {total:N0} mensajes, {aciertos:N0} bien ({100.0 * aciertos / total:0.0}%) y {fallos:N0} MAL.");
    }

    var rotados = salida.Hops.Where(h => h.Rotated).ToList();
    if (rotados.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  En los {rotados.Count} saltos CON rotación no hay contra qué medir, pero sí se ve el precio:");
        foreach (var salto in rotados)
        {
            Console.WriteLine($"    {salto.From} → {salto.To}: empareja {salto.Paired:N0} de {salto.OldCount:N0} " +
                              $"({100.0 * salto.Paired / salto.OldCount:0.0}%), y conserva {salto.SameShape:N0} formas");
        }
    }

    // And now what was wanted: the whole chain against the one-go jump.
    string primero = clientes[0], ultimo = clientes[^1];
    Console.WriteLine();
    Console.WriteLine($"  {Mapper.VersionOf(Path.GetFileName(primero))} → {Mapper.VersionOf(Path.GetFileName(ultimo))}:");

    var directo = Relay.Direct(primero, ultimo, _ => { });
    Console.WriteLine($"    de un tirón : {directo.Count,6:N0}");
    Console.WriteLine($"    por la cadena: {salida.Chain.Count,5:N0}");

    if (args.Length > 2 && File.Exists(args[2]))
    {
        // The percentage over the two thousand messages is a curiosity. The number that decides whether the
        // emulator starts on patch day is how many of the ones it really uses survive.
        // The emulator sweep brings out more opcodes than there are: there are false positives that do not
        // correspond to any protocol message. As the denominator one has to use the ones that do
        // exist in the new version, or the percentage comes out lowered by comparing against opcodes
        // nobody could get right.
        var protocolo = ProtoWriter.Model(Dumper.Protocol(ultimo, _ => { }))
            .Messages.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var suyos = Emulador(args[2]).Where(protocolo.Contains).ToHashSet(StringComparer.Ordinal);

        int porCadena = salida.Chain.Values.Count(v => suyos.Contains(v));
        int porDirecto = directo.Values.Count(v => suyos.Contains(v));
        Console.WriteLine();
        Console.WriteLine($"  De los {suyos.Count:N0} opcodes que usa el emulador y están en el protocolo:");
        Console.WriteLine($"    de un tirón : {porDirecto,6:N0}   ({100.0 * porDirecto / suyos.Count:0.0}%)");
        Console.WriteLine($"    por la cadena: {porCadena,5:N0}   ({100.0 * porCadena / suyos.Count:0.0}%)");
    }

    return 0;
}

/// <summary>
/// The clients in between, fetched from Ankama's CDN.
///
/// The jump from 3.6.4.3 to 3.6.10.10 comes out at 11.3% because there are six patches in between. With the
/// intermediate clients the jump splits into jumps of one, and of each only three
/// files are needed —the binary, the metadata and the player— some 130 MB of the 12 GB the
/// installation takes. The rest is not even asked for.
/// </summary>
static async Task<int> Bajar(string[] args)
{
    // What ClientReader needs to open a client, and nothing more.
    string[] queremos =
    [
        "*GameAssembly.dll",
        "*global-metadata.dat",
        "*UnityPlayer.dll",
    ];

    string cache = Path.Combine("datos", "cytrus");
    using var cytrus = new Cytrus(cache);
    void Decir(string linea) => Console.WriteLine(linea);

    if (args.Length > 1 && args[1] == "--lista")
    {
        var todas = await cytrus.VersionsAsync();
        Console.WriteLine($"{todas.Count:N0} versiones en el archivo, de la más vieja a la más nueva:");
        foreach (string v in todas) Console.WriteLine("  " + Cytrus.Tail(v));
        return 0;
    }

    if (args.Length < 3)
    {
        Console.WriteLine("Uso: bajar <desde> <hasta> [carpeta]     ·     bajar --lista");
        Console.WriteLine("Ej.: bajar 3.6.4.3 3.6.10.10 clientes");
        return 1;
    }

    string carpeta = args.Length > 3 ? args[3] : "clientes";

    Console.WriteLine($"Cadena de {args[1]} a {args[2]}:");
    var cadena = await cytrus.ChainAsync(args[1], args[2], Decir);
    Console.WriteLine($"  {cadena.Count} eslabones: {string.Join(" → ", cadena.Select(Cytrus.Tail))}");
    Console.WriteLine();

    long total = 0;
    foreach (string version in cadena)
    {
        string corta = Cytrus.Tail(version);
        string destino = Path.Combine(carpeta, "Cliente " + corta);

        // An already downloaded client is not asked for again. The chain is done over several sessions and it
        // makes no sense to spend another 130 MB to resume it.
        if (File.Exists(Path.Combine(destino, "GameAssembly.dll")))
        {
            Console.WriteLine($"{corta}: ya está en {destino}");
            continue;
        }

        Console.WriteLine($"{corta}:");
        var traidos = await cytrus.FetchAsync(version, queremos, destino, Decir);
        total += traidos.Sum(t => t.Size);
    }

    Console.WriteLine();
    Console.WriteLine($"  {Cytrus.Human(total)} bajados en total, en {carpeta}");
    return 0;
}

/// <summary>
/// The mapping from one version to the next, which is what the window does with its button.
///
/// It is here as well as in the window because it is the same call, and because a mapping that can be
/// launched from a script can be put into an automatic process on patch day.
/// </summary>
static int Mapear(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Uso: mapear <cliente o dll viejo> <cliente o dll nuevo> [opcodes del emulador.tsv]");
        return 1;
    }

    var mapper = new Mapper();
    var suyos = args.Length > 3 && File.Exists(args[3])
        ? Emulador(args[3]).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>(StringComparer.Ordinal);

    mapper.Build(args[1], args[2], "datos", suyos, linea => Console.WriteLine("  " + linea));

    Console.WriteLine();
    Console.WriteLine($"  escrito en {mapper.Export("datos")}");

    var dudas = mapper.Doubts();
    Console.WriteLine($"  {dudas.Count:N0} dudas por las que merece la pena preguntar al modelo");

    // How many candidates each doubt has is what says whether the model will find it easy or
    // impossible. Choosing between two is almost free; choosing among fifteen is what really costs.
    Console.WriteLine($"    con un solo candidato: {dudas.Count(d => d.Candidates.Count == 1):N0}");
    Console.WriteLine($"    entre dos o tres     : {dudas.Count(d => d.Candidates.Count is 2 or 3):N0}");
    Console.WriteLine($"    entre cuatro o más   : {dudas.Count(d => d.Candidates.Count > 3):N0}");
    Console.WriteLine();
    foreach (var duda in dudas.Take(10))
    {
        string que = duda.Name.Length > 0 ? duda.Name : duda.Meaning;
        Console.WriteLine($"    {duda.Old} entre {string.Join(", ", duda.Candidates.Take(6))}   ({que})");
    }
    return 0;
}

/// <summary>The opcodes the emulator really uses, without the sweep's false positives.</summary>
static List<string> Emulador(string path)
{
    var opcodes = new List<string>();
    foreach (string linea in File.ReadLines(path).Skip(1))
    {
        string[] celdas = linea.Split('\t');
        if (celdas.Length < 2 || celdas[0].Length != 3) continue;
        if (celdas[1] == "descartado") continue;
        if (!opcodes.Contains(celdas[0])) opcodes.Add(celdas[0]);
    }
    return opcodes;
}

/// <summary>Matches two real versions, the old and the new.</summary>
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

    // How many keep their name: if Ankama had not rotated anything, this would be 100% and the
    // matcher would not be needed. It serves to know what one is really up against.
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

/// <summary>
/// The client's code, indexed by message.
///
/// It is stage 3: stop looking at the message's shape and start looking at who uses it. It takes half a
/// minute and leaves a file the following stages read without opening the client again.
/// </summary>
static int Indexar(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Uso: indexar <carpeta del cliente> [salida.json] [saltos]");
        return 1;
    }

    string salida = args.Length > 2 ? args[2] : "indice.json";
    int saltos = args.Length > 3 && int.TryParse(args[3], out int s) ? s : 2;

    var reloj = System.Diagnostics.Stopwatch.StartNew();
    using var cliente = new ClientReader(args[1]);
    Console.WriteLine($"{Path.GetFileName(args[1].TrimEnd('\\', '/'))}   Unity {cliente.Version}");
    Console.WriteLine($"  cargado en {reloj.Elapsed.TotalSeconds:0.0} s");

    var evidencia = CodeIndex.Build(cliente, saltos, Console.WriteLine);
    CodeIndex.Save(evidencia, salida);

    Console.WriteLine($"  {reloj.Elapsed.TotalSeconds:0.0} s en total, escrito en {salida}");
    return 0;
}

/// <summary>What is needed to put dossiers together, loaded once.</summary>
static (Matcher.Model Model, Dictionary<string, CodeIndex.Evidence> Index,
        Dictionary<string, Dossier.Anchor> Anchors, Dictionary<string, List<string>> Parents)
    Papeles(string dll, string indice, string anclas)
{
    var modelo = Leer(dll);
    var indexado = File.Exists(indice) ? CodeIndex.Load(indice) : new Dictionary<string, CodeIndex.Evidence>();
    var medido = Dossier.Anchors(anclas);

    Console.WriteLine($"  {modelo.Messages.Count:N0} mensajes, {indexado.Count:N0} indexados, " +
                      $"{medido.Count:N0} con algo medido");
    return (modelo, indexado, medido, Dossier.Parents(modelo));
}

/// <summary>
/// A message's dossier, written for someone to read.
///
/// Before spending a cent on asking the model it is worth looking at a dossier and deciding whether
/// one would know how to answer it oneself. If not, the problem is not the model.
/// </summary>
static int Expediente(string[] args)
{
    if (args.Length < 5)
    {
        Console.WriteLine("Uso: expediente <dll del protocolo> <indice.json> <anclas.tsv> <mensaje|--todos|--medidos> [carpeta] [--ciego]");
        return 1;
    }

    var (modelo, indice, anclas, padres) = Papeles(args[1], args[2], args[3]);
    string version = Path.GetFileNameWithoutExtension(args[2]).Replace("indice_", "");
    string que = args[4];

    // Blind, the dossier has hidden from it what is already known about ITS message, and only that one. It is the only
    // way of measuring honestly how much signal it carries: with the anchor inside, the answer is in the
    // question.
    bool ciego = args.Contains("--ciego");
    Dictionary<string, Dossier.Anchor> Vistas(string mensaje) => Tapando(anclas, ciego ? mensaje : null);

    if (!que.StartsWith("--"))
    {
        Console.WriteLine();
        Console.Write(Dossier.Build(que, modelo, indice.GetValueOrDefault(que), Vistas(que), padres, version));
        return 0;
    }

    var cuales = que == "--medidos"
        ? modelo.Messages.Where(m => anclas.TryGetValue(m.Name, out var a) && a.Name.Length > 0).ToList()
        : modelo.Messages;

    string carpeta = args.Length > 5 && !args[5].StartsWith("--") ? args[5] : "expedientes";
    Directory.CreateDirectory(carpeta);
    foreach (var mensaje in cuales)
    {
        File.WriteAllText(Path.Combine(carpeta, mensaje.Name + ".md"),
            Dossier.Build(mensaje.Name, modelo, indice.GetValueOrDefault(mensaje.Name),
                          Vistas(mensaje.Name), padres, version));
    }
    Console.WriteLine($"  {cuales.Count:N0} expedientes en {carpeta}{(ciego ? "   (a ciegas)" : "")}");
    return 0;
}

/// <summary>
/// Scores a table of proposals against what is measured.
///
/// It does not matter who wrote them —the model, a person, another program—: if there is a measured
/// name for that message, one can say whether it gets it right. It is what separates a pipeline from a generator
/// of pretty names.
/// </summary>
static int Evaluar(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Uso: evaluar <anclas.tsv> <propuestas.tsv>");
        return 1;
    }

    var anclas = Dossier.Anchors(args[1]);
    var porConfianza = new Dictionary<string, (int Bien, int Mal)>(StringComparer.OrdinalIgnoreCase);
    int bien = 0, mal = 0, sinMedir = 0, calladas = 0;
    var fallos = new List<string>();

    foreach (string linea in File.ReadLines(args[2]))
    {
        if (linea.Length == 0 || linea[0] == '#') continue;
        string[] celdas = linea.Split('\t');
        if (celdas.Length < 2) continue;

        // Keeping quiet is not failing, but it is not free either: if the percentage is computed only over the
        // ones that commit, a pipeline that answers a single question and gets it right scores 100 %. They are
        // counted apart and printed alongside.
        if (celdas[1].Length == 0) { calladas++; continue; }

        if (!anclas.TryGetValue(celdas[0], out var verdad) || verdad.Name.Length == 0) { sinMedir++; continue; }

        bool acierta = Naming.Same(celdas[1], verdad.Name);
        string confianza = celdas.Length > 2 ? celdas[2] : "(sin decir)";
        var cuenta = porConfianza.GetValueOrDefault(confianza);
        porConfianza[confianza] = acierta ? (cuenta.Bien + 1, cuenta.Mal) : (cuenta.Bien, cuenta.Mal + 1);

        if (acierta) bien++;
        else { mal++; fallos.Add($"    {celdas[0]}  dijo {celdas[1],-40} era {verdad.Name}   [{confianza}]"); }
    }

    int total = bien + mal;
    int preguntadas = total + calladas + sinMedir;
    Console.WriteLine($"  {preguntadas:N0} filas: {total:N0} contrastables, {calladas:N0} sin nombre, " +
                      $"{sinMedir:N0} sin nada con que compararlas");
    Console.WriteLine($"  acierto: {bien:N0} de {total:N0} ({(total == 0 ? 0 : 100.0 * bien / total):0.0} %)");
    if (calladas > 0)
    {
        Console.WriteLine($"  sobre todo lo preguntado: {bien:N0} de {total + calladas:N0} " +
                          $"({100.0 * bien / (total + calladas):0.0} %)");
    }
    Console.WriteLine();
    Console.WriteLine("  por confianza declarada:");
    foreach (var (confianza, cuenta) in porConfianza.OrderByDescending(p => p.Value.Bien + p.Value.Mal))
    {
        int suyas = cuenta.Bien + cuenta.Mal;
        Console.WriteLine($"    {confianza,-12} {cuenta.Bien,4} de {suyas,4}   " +
                          $"({100.0 * cuenta.Bien / suyas:0.0} %)");
    }

    if (fallos.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("  los que falla:");
        foreach (string f in fallos.Take(40)) Console.WriteLine(f);
        if (fallos.Count > 40) Console.WriteLine($"    ...y {fallos.Count - 40} más");
    }
    return 0;
}

/// <summary>
/// Stage 4: the dossier in front of the model, and the answer into a table.
///
/// With <c>--evaluar</c> it does not sweep the whole protocol: it takes the messages whose name is ALREADY
/// known, hides exactly that one from the model —the rest of the anchors stay, because in the real
/// sweep they will be there too— and compares. It is the only way of knowing whether what comes out is any good, and it comes
/// cheap: ninety-nine questions.
/// </summary>
static async Task<int> Preguntar(string[] args)
{
    if (args.Length < 4)
    {
        Console.WriteLine("Uso: preguntar <dll del protocolo> <indice.json> <anclas.tsv> [salida.tsv] [--evaluar] [--limite N]");
        return 1;
    }

    var (modelo, indice, anclas, padres) = Papeles(args[1], args[2], args[3]);
    string version = Path.GetFileNameWithoutExtension(args[2]).Replace("indice_", "");
    bool evaluando = args.Contains("--evaluar");
    string salida = args.Length > 4 && !args[4].StartsWith("--") ? args[4] : "propuestas.tsv";

    int limite = int.MaxValue;
    int donde = Array.IndexOf(args, "--limite");
    if (donde > 0 && donde + 1 < args.Length && int.TryParse(args[donde + 1], out int n)) limite = n;

    // Whom one asks. When evaluating, only those with a known name; if not, everyone
    // who has something to tell, because asking about a message without evidence is paying for an
    // «I do not know» we already knew.
    var cola = evaluando
        ? modelo.Messages.Where(m => anclas.TryGetValue(m.Name, out var a) && a.Name.Length > 0)
                         .Select(m => m.Name).ToList()
        : modelo.Messages.Where(m => indice.TryGetValue(m.Name, out var e) &&
                                     (e.Context.Count > 0 || e.Strings.Count > 0))
                         .Select(m => m.Name).ToList();
    cola = cola.Take(limite).ToList();

    // The cache lives next to the output on purpose: two experiments with different tables must not
    // share it. The price is that moving the output leaves behind what was already paid for, so
    // where it is gets said instead of being discovered on seeing the bill.
    string cache = Path.Combine(Path.GetDirectoryName(salida) is { Length: > 0 } d ? d : ".",
                                "respuestas");
    using var llm = new Llm(cache);

    // One out each time: on asking about a message ONLY that one is hidden, in the dossier and in the
    // examples. Hiding all ninety-nine at once would measure a pipeline that is not the one that will run,
    // because on the day of the real sweep the examples will all be there.
    string Instrucciones(string mensaje)
        => Llm.System(anclas.Values.Where(a => !evaluando || a.Opcode != mensaje));

    string Expedientar(string mensaje)
        => Dossier.Build(mensaje, modelo, indice.GetValueOrDefault(mensaje),
                         Tapando(anclas, evaluando ? mensaje : null), padres, version);

    Console.WriteLine($"  {cola.Count:N0} preguntas, modelo {llm.Model}" + (evaluando ? "   (evaluando)" : ""));
    int guardadas = llm.Cached(cola.Select(m => (Expedientar(m), Instrucciones(m))));
    Console.WriteLine($"  {guardadas:N0} ya contestadas de antes, en {cache}");

    if (!llm.Ready && guardadas < cola.Count)
    {
        Console.WriteLine("  No hay clave. Define JONDO_LLM_KEY o ANTHROPIC_API_KEY, o usa «expediente --todos»");
        Console.WriteLine("  para volcarlos y contestarlos por otro camino.");
        return 2;
    }

    var filas = new List<string>();
    int hechas = 0, mudas = 0, aciertos = 0, fallos = 0;

    foreach (string mensaje in cola)
    {
        string respuesta;
        try { respuesta = await llm.AskAsync(Expedientar(mensaje), Instrucciones(mensaje)); }
        catch (Exception e) { Console.WriteLine($"  {mensaje}: {e.Message}"); continue; }

        var propuesta = Llm.Read(respuesta);
        hechas++;
        if (propuesta?.Name is not { Length: > 0 }) { mudas++; continue; }

        filas.Add(string.Join('\t', mensaje, propuesta.Name, propuesta.Confidence ?? "", Plano(propuesta.Because)));

        if (evaluando && anclas.TryGetValue(mensaje, out var verdad))
        {
            bool bien = Naming.Same(propuesta.Name, verdad.Name);
            if (bien) aciertos++; else fallos++;
            Console.WriteLine($"  {(bien ? "si" : "NO")}  {mensaje}  {propuesta.Name,-42} " +
                              $"{(bien ? "" : "esperado " + verdad.Name)}   [{propuesta.Confidence}]");
        }
    }

    // A barren batch does not overwrite the good one. Writing without looking turns any sweep that does not
    // answer anything —an expired key, a «--limite 0», anchors from another version where not even
    // one opcode matches— into a silent deletion: yesterday's table is left at the header line and
    // the process leaves saying all went well.
    if (filas.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  ni una fila: se deja {salida} como estaba");
        return 3;
    }

    File.WriteAllLines(salida, filas.Prepend("# mensaje\tnombre\tconfianza\ten qué se basa"));
    Console.WriteLine();
    Console.WriteLine($"  {hechas:N0} contestadas, {mudas:N0} sin nombre, {filas.Count:N0} en la tabla");
    if (evaluando)
    {
        int total = aciertos + fallos;
        Console.WriteLine($"  acierto: {aciertos:N0} de {total:N0} " +
                          $"({(total == 0 ? 0 : 100.0 * aciertos / total):0.0} %)");
    }
    Console.WriteLine($"  escrito en {salida}");
    return 0;
}

/// <summary>The anchors minus that of the message being asked about, when evaluating.</summary>
static Dictionary<string, Dossier.Anchor> Tapando(Dictionary<string, Dossier.Anchor> anclas, string? oculto)
{
    if (oculto == null) return anclas;
    var copia = new Dictionary<string, Dossier.Anchor>(anclas, StringComparer.Ordinal);
    copia.Remove(oculto);
    return copia;
}

static string Plano(string? text)
    => (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

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

/// <summary>The default value tables, read raw from the file.</summary>
static void Crudo(string clientFolder)
{
    string path = Path.Combine(clientFolder, "Dofus_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
    byte[] file = File.ReadAllBytes(path);

    var regiones = Header.Regions().ToDictionary(r => r.Name, r => r);
    var datos = regiones["fieldAndParameterDefaultValueData"];
    var cadenas = regiones["string"];
    var campos = regiones["fields"];

    foreach (string tabla in new[] { "fieldDefaultValues", "parameterDefaultValues" })
    {
        var region = regiones[tabla];
        var entradas = Raw.Defaults(file, region.Offset, region.Size);

        long desde = 21607975 - datos.Offset, hasta = 23382050 - datos.Offset;
        int dentro = entradas.Count(e => e.Data >= desde && e.Data <= hasta);
        Console.WriteLine("   CRUDO " + tabla + ": indices que APUNTAN al bloque de nombres = " + dentro);
        Console.WriteLine("   CRUDO " + tabla + ": rango de indices " + entradas.Min(e => e.Data) + " .. " + entradas.Max(e => e.Data) + "  (bloque en " + desde + ".." + hasta + ")");
        // The definitive cross-check: each position where a real name starts, against each index of the
        // table. Without decoding strings or assuming formats: only numbers.
        var posiciones = new Dictionary<long, long>();
        int cuantos = 0;
        for (int i = 0; i + 10 < file.Length; i++)
        {
            if (file[i] != 'C' || file[i + 1] != 'o' || file[i + 2] != 'm' || file[i + 3] != '.' ||
                file[i + 4] != 'A' || file[i + 5] != 'n' || file[i + 6] != 'k') continue;
            cuantos++;
            for (int d = 1; d <= 5; d++) posiciones[i - d - datos.Offset] = i;
        }

        var tocan = entradas.Where(e => posiciones.ContainsKey(e.Data)).ToList();
        Console.WriteLine("   CRUCE " + tabla + ": " + cuantos + " nombres en el fichero, " +
                          tocan.Count + " entradas apuntan a uno");

        foreach (var e in tocan.Take(6))
        {
            long donde = posiciones[e.Data];
            string texto = Raw.Name(file, donde, 0);
            string nom = tabla.StartsWith("field", StringComparison.Ordinal)
                ? Raw.Name(file, cadenas.Offset, Raw.FieldNameIndex(file, campos.Offset, e.Owner)) : "(par)";
            Console.WriteLine("     campo «" + nom + "» -> " + texto[..Math.Min(90, texto.Length)]);
        }
        int aciertos = 0, muestra = 0, sueltas = 0;
        foreach (var entrada in entradas)
        {
            string? texto = Raw.Text(file, datos.Offset + entrada.Data);
            if (texto != null && sueltas < 5 && texto.Length > 3) { Console.WriteLine("   CRUDO ejemplo(" + tabla + "): " + texto.Substring(0, Math.Min(70, texto.Length))); sueltas++; }
            if (texto == null || !texto.StartsWith("Com.Ankama", StringComparison.Ordinal)) continue;

            aciertos++;
            if (muestra++ < 4)
            {
                string nombre = tabla.StartsWith("field", StringComparison.Ordinal)
                    ? Raw.Name(file, cadenas.Offset, Raw.FieldNameIndex(file, campos.Offset, entrada.Owner))
                    : "(parámetro " + entrada.Owner + ")";
                Console.WriteLine("   CRUDO " + tabla + ": campo «" + nombre + "» -> " + texto);
            }
        }
        Console.WriteLine("   CRUDO " + tabla + ": " + entradas.Count.ToString("N0") + " entradas, " +
                          aciertos.ToString("N0") + " con nombre real");
    }
}
