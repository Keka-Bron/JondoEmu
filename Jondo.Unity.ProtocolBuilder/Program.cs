using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Jondo.Unity.Reversing;

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

static Matcher.Model Leer(string assembly) => ProtoWriter.Model(assembly);

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

    // El porcentaje sobre los dos mil mensajes es una curiosidad. El número que decide si el
    // emulador arranca el día del parche es otro: de los que el emulador usa de verdad, cuántos
    // sobreviven. Son los mensajes grandes y con vecindad, así que la cifra no se parece a la otra.
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
/// Qué región del fichero de metadatos es cada cosa, y si el bloque de restos cae dentro de alguna.
/// </summary>
static int Cabecera(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Uso: cabecera <carpeta del cliente> [posición a localizar]");
        return 1;
    }

    using var client = new ClientReader(args[1]);

    // Dónde empieza el bloque con los nombres reales, medido con grep sobre el fichero.
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

    // Y el orden: los mensajes del protocolo, tal y como los enumera la tabla de tipos.
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
/// Los nombres de verdad, sacados del cliente. La sonda de <see cref="Names"/>.
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

    // El caso que lo resolvería todo: un método con UN nombre y UN mensaje es una pareja directa.
    var parejas = sitios.Where(s => s.Texts.Count == 1 && s.Types.Count == 1).ToList();
    Console.WriteLine();
    Console.WriteLine($"  métodos con exactamente un nombre y un mensaje: {parejas.Count:N0}");
    foreach (var pareja in parejas.Take(8))
        Console.WriteLine($"    {pareja.Types[0]}  =  {pareja.Texts[0]}");

    return 0;
}

/// <summary>
/// La capa Op: un nombre por opcode, generado del cliente y de las anclas.
///
/// Es el último eslabón que faltaba. Sin esto el mapeo se queda en un fichero bonito: aplicarlo
/// significa editar a mano cientos de literales de tres letras repartidos por el emulador.
/// </summary>
static int Capa(string[] args)
{
    if (args.Length < 4)
    {
        Console.WriteLine("Uso: capa <cliente o dll> <anclas.tsv> <carpeta del emulador> [cliente anterior]");
        Console.WriteLine("Ej.: capa \"..\\Cliente 3.6.10.10\" datos/anclas_3.6.10.10.tsv . \"..\\clientes\\Cliente 3.6.4.3\"");
        return 1;
    }

    // El protocolo son DOS ensamblados —el del juego y el de la conexión— y los opcodes del
    // emulador salen de los dos. Con uno solo, 37 mensajes de conexión parecerían no existir y el
    // barrido los daría por basura.
    var ahora = Messages(args[1]);
    Console.WriteLine($"{ahora.Count:N0} mensajes en el protocolo de {Mapper.VersionOf(args[1])}");

    // Los de la versión anterior sirven para distinguir un resto de un error. Sin ellos, un opcode
    // que ya no existe se confundiría con «esto no era un opcode», que son cosas muy distintas.
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
        // Esto es un hallazgo, no un aviso de forma: son opcodes que el emulador usa y que en esta
        // versión del cliente NO EXISTEN. No pueden casar con nada; el código que los usa está
        // muerto y nadie lo sabía.
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

    // Sin --aplicar sólo se enseña lo que cambiaría. Tocar cuarenta ficheros del emulador no es
    // algo que deba pasar por escribir una orden de más.
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

/// <summary>Los nombres de mensaje de los dos ensamblados del protocolo.</summary>
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
/// La cadena recorrida, parche a parche, contra el salto directo.
///
/// Es el experimento entero: mide cada salto por separado, compone la cadena, y pone el resultado
/// al lado del salto de un tirón para que se vea si encadenar sirve de algo o no.
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

    // Lo que decide si el emparejador vale: cuántos empareja MAL cuando se sabe la respuesta. Un
    // fallo no se nota al mirar el resultado y envenena todo lo que venga detrás. Sólo cuentan los
    // saltos sin rotación, que son los únicos donde hay respuesta que saber.
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

    // Y ahora lo que se quería saber: la cadena entera contra el salto de un tirón.
    string primero = clientes[0], ultimo = clientes[^1];
    Console.WriteLine();
    Console.WriteLine($"  {Mapper.VersionOf(Path.GetFileName(primero))} → {Mapper.VersionOf(Path.GetFileName(ultimo))}:");

    var directo = Relay.Direct(primero, ultimo, _ => { });
    Console.WriteLine($"    de un tirón : {directo.Count,6:N0}");
    Console.WriteLine($"    por la cadena: {salida.Chain.Count,5:N0}");

    if (args.Length > 2 && File.Exists(args[2]))
    {
        // El porcentaje sobre los dos mil mensajes es una curiosidad. El número que decide si el
        // emulador arranca el día del parche es cuántos de los que usa de verdad sobreviven.
        // El barrido del emulador saca más opcodes de los que son: hay falsos positivos que no
        // corresponden a ningún mensaje del protocolo. Como denominador hay que usar los que sí
        // existen en la versión nueva, o el porcentaje sale rebajado por comparar contra opcodes
        // que no podía acertar nadie.
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
/// Los clientes de en medio, traídos de la CDN de Ankama.
///
/// El salto de 3.6.4.3 a 3.6.10.10 sale al 11,3% porque hay seis parches por medio. Con los
/// clientes intermedios el salto se parte en saltos de uno, y de cada uno sólo hacen falta tres
/// ficheros —el binario, los metadatos y el reproductor— unos 130 MB de los 12 GB que ocupa la
/// instalación. El resto no se pide siquiera.
/// </summary>
static async Task<int> Bajar(string[] args)
{
    // Lo que necesita ClientReader para abrir un cliente, y nada más.
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

        // Un cliente ya bajado no se vuelve a pedir. La cadena se hace en varias sesiones y no
        // tiene sentido gastar otros 130 MB por reanudarla.
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
/// El mapeo de una versión a la siguiente, que es lo que hace la ventana con su botón.
///
/// Está aquí además de en la ventana porque es la misma llamada, y porque un mapeo que se puede
/// lanzar desde un guión se puede meter en un proceso automático el día del parche.
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

    // Cuántos candidatos tiene cada duda es lo que dice si el modelo lo va a tener fácil o
    // imposible. Elegir entre dos es casi gratis; elegir entre quince es lo que de verdad cuesta.
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

/// <summary>Los opcodes que el emulador usa de verdad, sin los falsos positivos del barrido.</summary>
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

/// <summary>
/// El código del cliente, indexado por mensaje.
///
/// Es la etapa 3: dejar de mirar la forma del mensaje y empezar a mirar quién lo usa. Tarda medio
/// minuto y deja un fichero que las etapas siguientes leen sin volver a abrir el cliente.
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

/// <summary>Lo que hace falta para armar expedientes, cargado una vez.</summary>
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
/// El expediente de un mensaje, escrito para que lo lea alguien.
///
/// Antes de gastarse un céntimo en preguntarle al modelo conviene mirar un expediente y decidir si
/// uno mismo sabría contestarlo. Si no, el problema no es el modelo.
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

    // A ciegas se le tapa al expediente lo que ya se sabe de SU mensaje, y sólo de ése. Es la única
    // manera de medir honradamente cuánta señal lleva: con el ancla dentro, la respuesta está en la
    // pregunta.
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
/// Puntúa una tabla de propuestas contra lo que está medido.
///
/// Da igual quién las haya escrito —el modelo, una persona, otro programa—: si hay un nombre
/// medido para ese mensaje, se puede decir si acierta. Es lo que separa una tubería de un generador
/// de nombres bonitos.
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

        // Callarse no es fallar, pero tampoco es gratis: si el porcentaje se calcula sólo sobre las
        // que se mojan, una tubería que conteste una sola pregunta y acierte marca un 100 %. Van
        // contadas aparte y se imprimen al lado.
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
/// La etapa 4: el expediente delante del modelo, y la respuesta a una tabla.
///
/// Con <c>--evaluar</c> no barre el protocolo entero: coge los mensajes de los que YA se sabe el
/// nombre, le tapa al modelo justo ése —el resto de anclas se quedan, porque en el barrido de
/// verdad también estarán— y compara. Es la única forma de saber si lo que sale sirve, y sale
/// barato: noventa y nueve preguntas.
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

    // A quién se le pregunta. Evaluando, sólo a los que tienen nombre conocido; si no, a todo el
    // que tenga algo que contar, porque preguntar por un mensaje sin evidencia es pagar por un
    // «no lo sé» que ya sabíamos.
    var cola = evaluando
        ? modelo.Messages.Where(m => anclas.TryGetValue(m.Name, out var a) && a.Name.Length > 0)
                         .Select(m => m.Name).ToList()
        : modelo.Messages.Where(m => indice.TryGetValue(m.Name, out var e) &&
                                     (e.Context.Count > 0 || e.Strings.Count > 0))
                         .Select(m => m.Name).ToList();
    cola = cola.Take(limite).ToList();

    // La caché vive al lado de la salida a propósito: dos experimentos con tablas distintas no
    // deben compartirla. El precio es que cambiar de sitio la salida deja atrás lo ya pagado, así
    // que se dice dónde está en vez de que se descubra al ver la factura.
    string cache = Path.Combine(Path.GetDirectoryName(salida) is { Length: > 0 } d ? d : ".",
                                "respuestas");
    using var llm = new Llm(cache);

    // Uno fuera cada vez: al preguntar por un mensaje se le tapa SÓLO ése, en el expediente y en los
    // ejemplos. Tapar los noventa y nueve a la vez mediría una tubería que no es la que va a correr,
    // porque el día del barrido de verdad los ejemplos estarán todos.
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

    // Una tanda estéril no machaca la buena. Escribir sin mirar convierte cualquier barrido que no
    // conteste nada —la clave caducada, un «--limite 0», unas anclas de otra versión donde no casa
    // ni un opcode— en un borrado silencioso: la tabla de ayer se queda en la línea de cabecera y
    // el proceso se va diciendo que todo ha ido bien.
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

/// <summary>Las anclas menos la del mensaje que se está preguntando, cuando se evalúa.</summary>
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

/// <summary>Las tablas de valores por defecto, leídas del fichero a pelo.</summary>
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
        // El cruce definitivo: cada posición donde empieza un nombre real, contra cada índice de la
        // tabla. Sin decodificar cadenas ni suponer formatos: sólo números.
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
