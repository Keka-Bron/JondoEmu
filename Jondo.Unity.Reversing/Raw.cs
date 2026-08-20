using System.Buffers.Binary;
using System.Text;

namespace Jondo.Unity.Reversing;

/// <summary>
/// global-metadata.dat leído a pelo, sin pasar por LibCpp2IL.
///
/// Se llega aquí después de tres intentos de preguntárselo a la biblioteca —campos, propiedades,
/// <c>GetDefaultValue</c>— y de que los tres devolvieran cero sobre 71.190 entradas. Cuando la
/// respuesta es cero en TODAS, el fallo no está en los datos: está en cómo se pregunta.
///
/// Aquí no hay nada que adivinar. Los desplazamientos los da la cabecera, y los tamaños confirman la
/// estructura sin margen de duda:
///
///   fieldDefaultValues       631.476 / 12 = 52.623  ← justo las entradas que dice la cabecera
///   parameterDefaultValues   222.804 / 12 = 18.567  ← ídem
///
/// Doce bytes por entrada y tres enteros dentro. Con eso se puede recorrer la tabla a mano.
/// </summary>
public static class Raw
{
    /// <summary>Una entrada de las tablas de valores por defecto.</summary>
    /// <param name="Owner">El campo o el parámetro al que pertenece el valor.</param>
    /// <param name="Data">Dónde está el valor, relativo a la zona de datos.</param>
    public readonly record struct Entry(int Owner, int TypeIndex, int Data);

    /// <summary>Lee una tabla de valores por defecto: tres enteros por entrada.</summary>
    public static List<Entry> Defaults(byte[] file, long offset, long size)
    {
        var entries = new List<Entry>((int)(size / 12));
        for (long at = offset; at + 12 <= offset + size; at += 12)
        {
            entries.Add(new Entry(
                BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)at)),
                BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)at + 4)),
                BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)at + 8))));
        }
        return entries;
    }

    /// <summary>
    /// El valor guardado en esa posición, si es una cadena.
    ///
    /// IL2CPP guarda las cadenas con la longitud delante como entero comprimido: si el primer byte
    /// es menor que 0x80 la longitud es ese byte y ya está, y si no ocupa dos o cuatro. Los nombres
    /// del protocolo miden entre 40 y 130 caracteres, así que casi todos caen en el caso de dos
    /// bytes; se contemplan los tres por no dejar el caso raro fuera.
    /// </summary>
    public static string? Text(byte[] file, long at)
    {
        if (at < 0 || at >= file.Length) return null;

        // La longitud es un int32, no un entero comprimido.
        //
        // La primera versión lo leyó como comprimido y las cadenas salían con basura delante:
        // «\0\0\0</col» en vez de «</color>». Es lo que pasa al tomar por longitud el primer byte
        // de un int32 pequeño y empezar a leer tres bytes antes de tiempo. Se vio porque forcé la
        // sonda a enseñar cadenas cualesquiera en vez de sólo las que buscaba; con el filtro puesto
        // el fallo habría pasado por «aquí no hay nada».
        if (at + 4 > file.Length) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)at));
        at += 4;

        if (length <= 0 || length > 4096 || at + length > file.Length) return null;
        return Encoding.UTF8.GetString(file, (int)at, length);
    }

    /// <summary>Una cadena de la tabla de nombres, que van terminadas en cero.</summary>
    public static string Name(byte[] file, long offset, int index)
    {
        long at = offset + index;
        if (at < 0 || at >= file.Length) return "";

        long end = at;
        while (end < file.Length && file[end] != 0) end++;
        return Encoding.UTF8.GetString(file, (int)at, (int)(end - at));
    }

    /// <summary>El índice de nombre de un campo, de la tabla de campos (tres enteros por entrada).</summary>
    public static int FieldNameIndex(byte[] file, long fieldsOffset, int fieldIndex)
    {
        long at = fieldsOffset + (long)fieldIndex * 12;
        if (at < 0 || at + 4 > file.Length) return -1;
        return BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)at));
    }
}
