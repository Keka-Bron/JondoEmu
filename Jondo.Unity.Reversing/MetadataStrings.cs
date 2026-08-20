using System.Text;

namespace Jondo.Unity.Reversing;

/// <summary>
/// Las cadenas de texto que el cliente lleva dentro.
///
/// Un cliente de Unity compilado con IL2CPP guarda todos sus literales en un solo fichero,
/// global-metadata.dat, uno detrás de otro y en UTF-8. No hay que desofuscar nada para leerlos:
/// están en claro, y entre ellos está lo único que nos interesa aquí, el descriptor del protocolo
/// que protobuf necesita para trabajar en tiempo de ejecución.
///
/// El código que genera protobuf para C# guarda ese descriptor así:
///
///     byte[] descriptorData = Convert.FromBase64String(string.Concat("Cg...", "...", "..."));
///
/// O sea: base64, y partido en trozos cuando es largo. De ahí las dos cosas que hace esto —
/// encontrar los trozos y devolverlos EN ORDEN— porque volver a juntarlos es cosa del que los
/// decodifica.
/// </summary>
public static class MetadataStrings
{
    /// <summary>Un literal encontrado dentro del fichero, con dónde estaba.</summary>
    public readonly record struct Found(int Offset, string Text);

    /// <summary>
    /// Saca las cadenas que podrían ser base64, de al menos <paramref name="minimum"/> caracteres.
    ///
    /// No se acota por dónde empieza a propósito. El primer trozo de un descriptor empieza por
    /// "Cg" —el campo 1 de un FileDescriptorProto, que es su nombre— pero el segundo y el tercero
    /// empiezan por donde cayera el corte, así que filtrar por el principio dejaría fuera
    /// justamente las continuaciones.
    /// </summary>
    public static List<Found> Base64Like(string path, int minimum = 32)
    {
        byte[] data = File.ReadAllBytes(path);
        var found = new List<Found>();
        var run = new StringBuilder();
        int start = 0;

        for (int i = 0; i <= data.Length; i++)
        {
            byte b = i < data.Length ? data[i] : (byte)0;
            bool part = i < data.Length && IsBase64Char(b);

            if (part)
            {
                if (run.Length == 0) start = i;
                run.Append((char)b);
                continue;
            }

            if (run.Length >= minimum) found.Add(new Found(start, run.ToString()));
            run.Clear();
        }

        return found;
    }

    private static bool IsBase64Char(byte b)
        => (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') ||
           b == '+' || b == '/' || b == '=';
}
