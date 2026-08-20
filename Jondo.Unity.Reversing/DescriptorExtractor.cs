using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Jondo.Unity.Reversing;

/// <summary>
/// El descriptor del protocolo, sacado del propio cliente.
///
/// Esto es el cimiento de todo lo demás. Sin descriptor completo no hay huellas, y sin huellas no
/// hay forma de emparejar los mensajes de una versión con los de la siguiente cuando Ankama les
/// rota los nombres de tres letras.
///
/// No hace falta desofuscar nada ni arrancar el juego: protobuf necesita el descriptor para
/// funcionar, así que el cliente lo lleva dentro. Está en global-metadata.dat, que es donde IL2CPP
/// guarda los literales, y está EN CRUDO: los bytes del FileDescriptorProto tal cual, no en base64
/// como los deja el generador de C# de escritorio. Se reconoce porque el campo 1 de un descriptor
/// es el nombre del fichero y ahí se leen en claro «com.ankama.dofus.proto», «network.proto».
///
/// ─── Por qué se comprueba con una ida y vuelta ──────────────────────────────────────────
///
/// Un lector de protobuf es MUY permisivo: casi cualquier montón de bytes se deja parsear sin
/// quejarse, dejando lo que no entiende en campos desconocidos. Así que "ha parseado" no prueba
/// nada. Lo que sí prueba es que al volver a serializarlo salgan los MISMOS bytes: eso sólo pasa
/// si cada byte de la entrada cayó en un campo que el descriptor conoce, y es lo que decide dónde
/// termina el bloque, que es el dato que no viene escrito en ninguna parte.
/// </summary>
public static class DescriptorExtractor
{
    public sealed record Blob(int Offset, int Length, FileDescriptorProto File);

    /// <summary>Los descriptores que hay dentro del fichero de metadatos del cliente.</summary>
    public static List<Blob> FindIn(string metadataPath)
    {
        byte[] data = File.ReadAllBytes(metadataPath);
        var blobs = new List<Blob>();
        int lastEnd = 0;

        foreach (int start in Starts(data))
        {
            if (start < lastEnd) continue;   // ya iba dentro del anterior

            var blob = ReadAt(data, start);
            if (blob == null) continue;

            blobs.Add(blob);
            lastEnd = blob.Offset + blob.Length;
        }

        return blobs;
    }

    /// <summary>
    /// Dónde puede empezar uno: en el 0x0A del campo 1, seguido de la longitud del nombre y de un
    /// nombre que acabe en «.proto». Se busca por el final —el «.proto»— y se retrocede, que es
    /// mucho más rápido que probar en cada byte del fichero.
    /// </summary>
    private static IEnumerable<int> Starts(byte[] data)
    {
        byte[] needle = ".proto"u8.ToArray();

        for (int i = 0; i + needle.Length <= data.Length; i++)
        {
            if (data[i] != needle[0]) continue;
            if (!data.AsSpan(i, needle.Length).SequenceEqual(needle)) continue;

            // El nombre puede tener cualquier longitud, así que se prueba a retroceder hasta un
            // encabezado 0x0A <longitud> que cuadre exactamente con lo que hay hasta el «.proto».
            int end = i + needle.Length;
            for (int nameLength = needle.Length; nameLength <= 120; nameLength++)
            {
                int header = end - nameLength - 2;      // 0x0A + un byte de longitud
                if (header < 0) break;
                if (data[header] == 0x0A && data[header + 1] == nameLength) yield return header;
            }
        }
    }

    /// <summary>
    /// Lee un descriptor completo desde ahí, si lo hay.
    ///
    /// La longitud no viene dada: se recorre campo a campo mientras lo que se lee tenga sentido
    /// para un FileDescriptorProto, y cada vez que el trozo leído hasta ese punto sobrevive a la
    /// ida y vuelta se anota como el mejor final conocido. Se devuelve el más largo que cuadre.
    /// </summary>
    private static Blob? ReadAt(byte[] data, int start)
    {
        FileDescriptorProto? best = null;
        int bestLength = 0;
        int at = start;

        while (at < data.Length)
        {
            if (!TryField(data, ref at)) break;

            int length = at - start;
            if (length < 24) continue;

            var file = TryParse(data.AsSpan(start, length).ToArray());
            if (file == null) continue;

            best = file;
            bestLength = length;
        }

        return best == null ? null : new Blob(start, bestLength, best);
    }

    /// <summary>Un campo de nivel superior: avanza el cursor si es plausible.</summary>
    private static bool TryField(byte[] data, ref int at)
    {
        if (!TryVarint(data, ref at, out ulong key)) return false;

        int field = (int)(key >> 3);
        int wire = (int)(key & 7);

        // Un FileDescriptorProto llega al campo 12. Más allá es que ya nos salimos del bloque.
        if (field is < 1 or > 12) return false;

        switch (wire)
        {
            case 0: return TryVarint(data, ref at, out _);
            case 2:
                if (!TryVarint(data, ref at, out ulong len)) return false;
                if (len > int.MaxValue || at + (int)len > data.Length) return false;
                at += (int)len;
                return true;
            default: return false;    // un descriptor no usa ni 32 ni 64 bits fijos
        }
    }

    private static bool TryVarint(byte[] data, ref int at, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (at < data.Length && shift <= 63)
        {
            byte b = data[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    /// <summary>Descodifica y comprueba. Devuelve null a la mínima duda.</summary>
    public static FileDescriptorProto? TryParse(byte[] raw)
    {
        try
        {
            var file = FileDescriptorProto.Parser.ParseFrom(raw);
            if (!file.ToByteArray().AsSpan().SequenceEqual(raw)) return null;
            if (string.IsNullOrEmpty(file.Name)) return null;
            if (file.MessageType.Count == 0 && file.EnumType.Count == 0) return null;
            return file;
        }
        catch (InvalidProtocolBufferException) { return null; }
    }

    /// <summary>Junta lo encontrado en un solo FileDescriptorSet, que es lo que se guarda.</summary>
    public static FileDescriptorSet AsSet(IEnumerable<Blob> blobs)
    {
        var set = new FileDescriptorSet();
        foreach (var blob in blobs) set.File.Add(blob.File);
        return set;
    }
}
