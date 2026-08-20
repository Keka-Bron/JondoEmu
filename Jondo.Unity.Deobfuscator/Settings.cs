using System.Security.Cryptography;
using System.Text;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Reversing;

namespace Jondo.Unity.Deobfuscator;

/// <summary>
/// Lo que el desofuscador recuerda entre una vez y la siguiente.
///
/// Vive en <c>%APPDATA%\Jondo\desofuscador.cfg</c>, en el mismo sitio y con el mismo formato de
/// <c>clave=valor</c> que las preferencias del lanzador: fuera de la carpeta del emulador, porque
/// son preferencias de quien lo usa y no datos del emulador, y en texto para poder abrirlo y
/// arreglarlo a mano si algo se tuerce.
///
/// ─── Las claves de la API ───────────────────────────────────────────────────────────────
///
/// Con dos excepciones. La primera: las claves NO van en claro. Se cifran con DPAPI atadas a la
/// cuenta de Windows, así que el fichero sólo se puede descifrar desde la sesión de quien las
/// escribió. No es una caja fuerte —quien pueda ejecutar código como tú puede leerlas— pero cierra
/// los dos accidentes que de verdad pasan: que se cuelen en una captura de pantalla y que se vayan
/// en un zip a otra máquina.
///
/// La segunda: se guarda UNA POR PROVEEDOR. Probar Gemini un rato y volver a Claude no puede
/// costar ir a buscar otra vez la clave de Claude; quien tenga tres, tiene las tres puestas.
/// </summary>
public sealed class Settings
{
    /// <summary>Dónde vive, al lado de las del lanzador.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jondo", "desofuscador.cfg");

    // ─── El modelo ──────────────────────────────────────────────────────────────────────

    /// <summary>Cuál de los atajos de <see cref="Provider"/> está elegido.</summary>
    public string ProviderName { get; set; } = Provider.All[0].Name;

    public string Url { get; set; } = Provider.All[0].Url;
    public string Model { get; set; } = Provider.All[0].Suggested;
    public Llm.Dialect Dialect { get; set; } = Provider.All[0].Dialect;

    /// <summary>Cuántas preguntas van a la vez. Cuatro es prudente con un proveedor de pago.</summary>
    public int AtOnce { get; set; } = 4;

    /// <summary>Las claves en claro y sólo en memoria, una por proveedor.</summary>
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>La clave del proveedor elegido.</summary>
    public string Key
    {
        get => _keys.GetValueOrDefault(ProviderName, "");
        set => _keys[ProviderName] = value;
    }

    /// <summary>El proveedor elegido, con su dialecto y su pista.</summary>
    public Provider Provider => Reversing.Provider.All
        .FirstOrDefault(p => p.Name.Equals(ProviderName, StringComparison.OrdinalIgnoreCase))
        ?? Reversing.Provider.All[^1];

    /// <summary>Deja puesto un proveedor, sin pisar lo que el usuario haya escrito a mano.</summary>
    public void Use(Provider provider)
    {
        ProviderName = provider.Name;
        Dialect = provider.Dialect;
        if (provider.Url.Length > 0) Url = provider.Url;
        if (provider.Suggested.Length > 0) Model = provider.Suggested;
    }

    // ─── Las rutas ──────────────────────────────────────────────────────────────────────

    /// <summary>La carpeta del cliente NUEVO, el que se quiere desofuscar.</summary>
    public string ClientFolder { get; set; } = "";

    /// <summary>El ensamblado del protocolo de la versión VIEJA, la que ya se conoce.</summary>
    public string OldProtocolDll { get; set; } = "";

    /// <summary>Por qué paso del asistente iba, para poder cerrar y volver.</summary>
    public int Step { get; set; }

    /// <summary>El idioma de la ventana, de los tres que habla el emulador.</summary>
    public Language Language { get; set; } = Language.Es;

    /// <summary>Cómo hay que llamar al modelo con lo que está puesto ahora.</summary>
    public Llm.Endpoint Endpoint() => new(Url, Model, Key, Dialect, AtOnce);

    // ─── Ir y volver del disco ──────────────────────────────────────────────────────────

    public static Settings Load()
    {
        var settings = new Settings();
        var values = Read();

        settings.ProviderName = values.GetValueOrDefault("proveedor", settings.ProviderName);
        settings.Url = values.GetValueOrDefault("url", settings.Url);
        settings.Model = values.GetValueOrDefault("modelo", settings.Model);
        settings.Dialect = values.GetValueOrDefault("dialecto", "").ToLowerInvariant() switch
        {
            "openai" => Llm.Dialect.OpenAi,
            "gemini" => Llm.Dialect.Gemini,
            "anthropic" => Llm.Dialect.Anthropic,
            _ => settings.Provider.Dialect,
        };
        settings.Language = values.GetValueOrDefault("idioma", "").ToLowerInvariant() switch
        {
            "en" => Language.En,
            "fr" => Language.Fr,
            _ => Language.Es,
        };
        settings.ClientFolder = values.GetValueOrDefault("cliente", "");
        settings.OldProtocolDll = values.GetValueOrDefault("protocolo.viejo", "");

        if (int.TryParse(values.GetValueOrDefault("a.la.vez"), out int atOnce) && atOnce > 0)
            settings.AtOnce = Math.Min(32, atOnce);
        if (int.TryParse(values.GetValueOrDefault("paso"), out int step) && step >= 0)
            settings.Step = step;

        foreach (var (name, guarded) in values)
        {
            if (!name.StartsWith("clave.", StringComparison.OrdinalIgnoreCase)) continue;
            string provider = name["clave.".Length..];
            string clear = Decipher(guarded);
            if (clear.Length > 0) settings._keys[provider] = clear;
        }

        return settings;
    }

    public void Save()
    {
        var lines = new List<string>
        {
            "# Preferencias del desofuscador de Jondo. Las claves van cifradas contra esta cuenta de",
            "# Windows: copiar el fichero a otra máquina las deja ilegibles, y eso es lo que se quiere.",
            "# Lo demás es texto y se puede tocar a mano.",
            "proveedor=" + ProviderName,
            "url=" + Url,
            "modelo=" + Model,
            "dialecto=" + Dialect switch
            {
                Llm.Dialect.OpenAi => "openai",
                Llm.Dialect.Gemini => "gemini",
                _ => "anthropic",
            },
            "a.la.vez=" + AtOnce,
            "cliente=" + ClientFolder,
            "protocolo.viejo=" + OldProtocolDll,
            "paso=" + Step,
            "idioma=" + LauncherTexts.Code(Language),
        };

        foreach (var (provider, clear) in _keys.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (clear.Length == 0) continue;
            string guarded = Cipher(clear);
            if (guarded.Length > 0) lines.Add($"clave.{provider}={guarded}");
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            // Se escribe al lado y se mueve encima: si se corta a mitad, lo que se pierde es lo
            // nuevo y no lo que ya había, que incluye las claves.
            string half = Path + ".escribiendo";
            File.WriteAllLines(half, lines);
            File.Move(half, Path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Dictionary<string, string> Read()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(Path)) return values;
            foreach (string line in File.ReadAllLines(Path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }
        catch (IOException) { }
        return values;
    }

    /// <summary>
    /// Una clave, cifrada contra la cuenta de Windows.
    ///
    /// Si el cifrado falla —que no debería, pero pasa en cuentas de sistema y en algunos perfiles
    /// itinerantes— se guarda VACÍA a propósito. Volver a pedirla molesta; dejarla en claro en un
    /// fichero de configuración es peor.
    /// </summary>
    private static string Cipher(string clear)
    {
        if (clear.Length == 0) return "";
        try
        {
            byte[] guarded = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(clear), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(guarded);
        }
        catch (CryptographicException) { return ""; }
        catch (PlatformNotSupportedException) { return ""; }
    }

    private static string Decipher(string guarded)
    {
        if (guarded.Length == 0) return "";
        try
        {
            byte[] clear = ProtectedData.Unprotect(
                Convert.FromBase64String(guarded), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (CryptographicException) { return ""; }
        catch (FormatException) { return ""; }
        catch (PlatformNotSupportedException) { return ""; }
    }
}
