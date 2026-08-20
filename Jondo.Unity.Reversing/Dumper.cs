using Cpp2IL.Core.OutputFormats;

namespace Jondo.Unity.Reversing;

/// <summary>
/// El ensamblado del protocolo, sacado de un cliente que sólo tiene el binario y los metadatos.
///
/// El cliente que uno instala trae ya <c>cpp2il_out</c> porque lo deja MelonLoader al arrancar. Un
/// cliente bajado de la CDN no: trae el binario en crudo. Esto hace el mismo paso que haría
/// MelonLoader, con la misma biblioteca y el mismo formato de salida.
///
/// ─── Por qué se escribe el .dll y no se lee el cliente directamente ─────────────────────
///
/// Cpp2IL ya tiene el protocolo en memoria en cuanto abre el cliente, así que sacar de ahí los
/// mensajes parece el atajo evidente. No se hace, y a propósito.
///
/// Todo lo medido hasta ahora —el techo del 68,3%, el salto del 11,3%, las 293 anclas— salió de
/// leer el .dll con MetadataLoadContext. Un segundo camino que lea los mismos datos por otro sitio
/// daría números que no se pueden comparar con esos, y comparar es justo para lo que existe esta
/// cadena. Si el número nuevo sale distinto quiero saber que es por los parches, no porque he
/// cambiado de lector a mitad del experimento.
///
/// ─── Se escriben todos, aunque sólo interese uno ────────────────────────────────────────
///
/// La primera versión escribía sólo <c>Ankama.Dofus.Protocol.Game.dll</c>, que es el único que se
/// lee. Daba cero mensajes. El lector abre el ensamblado con MetadataLoadContext, y para saber si
/// una clase implementa <c>IMessage</c> tiene que poder resolver <c>IMessage</c>, que vive en otro
/// ensamblado; con la carpeta vacía no resuelve nada y ninguna clase parece un mensaje.
///
/// Así que se escribe el volcado entero, que es lo que deja MelonLoader y con lo que están hechas
/// todas las medidas. Son 69 MB por versión —no los doscientos que parecían—, o sea medio giga por
/// la cadena de ocho. Barato para no tener que fiarse de dos caminos distintos.
/// </summary>
public static class Dumper
{
    /// <summary>Dónde deja MelonLoader el volcado, que es donde lo busca <see cref="Mapper.ProtocolDll"/>.</summary>
    public static string Where(string clientFolder)
        => Path.Combine(clientFolder, "MelonLoader", "Dependencies", "Il2CppAssemblyGenerator",
                        "Cpp2IL", "cpp2il_out");

    /// <summary>
    /// Deja el ensamblado del protocolo en su sitio y devuelve la ruta.
    ///
    /// Si ya está, no hace nada: abrir el cliente y reconstruir los ensamblados es medio minuto por
    /// versión, y la cadena se recorre más de una vez.
    /// </summary>
    public static string Protocol(string clientFolder, Action<string>? report = null)
    {
        const string wanted = "Ankama.Dofus.Protocol.Game";

        string folder = Where(clientFolder);
        string path = Path.Combine(folder, wanted + ".dll");
        if (File.Exists(path)) return path;

        report?.Invoke($"  {Path.GetFileName(clientFolder)}: abriendo el cliente…");
        using var client = new ClientReader(clientFolder);

        report?.Invoke($"  {Path.GetFileName(clientFolder)}: reconstruyendo los ensamblados…");
        var format = new AsmResolverDllOutputFormatDefault();
        var built = format.BuildAssemblies(client.App);

        Directory.CreateDirectory(folder);
        int written = 0, failed = 0;
        foreach (var assembly in built)
        {
            string name = assembly.Name?.ToString() ?? "";
            if (name.Length == 0) continue;
            try
            {
                assembly.ManifestModule!.Write(Path.Combine(folder, name + ".dll"));
                written++;
            }
            catch (Exception e)
            {
                // Un ensamblado del juego que no se deja escribir no arruina la versión: el que
                // importa es el del protocolo, y de ése sí se protesta. Pero se cuentan, porque si
                // fallan muchos el volcado no es comparable con el de MelonLoader.
                failed++;
                if (name == wanted)
                    throw new InvalidOperationException($"{clientFolder}: no se ha podido escribir {wanted}", e);
            }
        }

        if (!File.Exists(path))
            throw new InvalidOperationException($"en {clientFolder} no se ha reconstruido {wanted}");

        report?.Invoke($"  {Path.GetFileName(clientFolder)}: {written:N0} ensamblados escritos" +
                       (failed > 0 ? $", {failed:N0} fallidos" : ""));
        return path;
    }
}
