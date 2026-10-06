using System.Security.Cryptography;
using System.Text;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Reversing;

namespace Jondo.Unity.Deobfuscator;

/// <summary>
/// What the deobfuscator remembers from one time to the next.
///
/// It lives in <c>%APPDATA%\Jondo\desofuscador.cfg</c>, in the same place and with the same
/// <c>key=value</c> format as the launcher's preferences: outside the emulator's folder, because
/// they are the user's preferences and not the emulator's data, and in text so it can be opened and
/// fixed by hand if something goes wrong.
///
/// ─── The API keys ───────────────────────────────────────────────────────────────────────
///
/// With two exceptions. The first: the keys do NOT go in the clear. They are encrypted with DPAPI tied to the
/// Windows account, so the file can only be decrypted from the session of whoever
/// wrote them. It is not a safe —whoever can run code as you can read them— but it closes
/// the two accidents that really happen: that they slip into a screenshot and that they leave
/// in a zip for another machine.
///
/// The second: ONE PER PROVIDER is kept. Trying Gemini for a while and going back to Claude cannot
/// cost going to fetch Claude's key again; whoever has three has all three set.
/// </summary>
public sealed class Settings
{
    /// <summary>Where it lives, next to the launcher's.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jondo", "desofuscador.cfg");

    // ─── The model ──────────────────────────────────────────────────────────────────────

    /// <summary>Which of the <see cref="Provider"/> shortcuts is chosen.</summary>
    public string ProviderName { get; set; } = Provider.All[0].Name;

    public string Url { get; set; } = Provider.All[0].Url;
    public string Model { get; set; } = Provider.All[0].Suggested;
    public Llm.Dialect Dialect { get; set; } = Provider.All[0].Dialect;

    /// <summary>How many questions go at once. Four is prudent with a paid provider.</summary>
    public int AtOnce { get; set; } = 4;

    /// <summary>The keys in the clear and only in memory, one per provider.</summary>
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The chosen provider's key.</summary>
    public string Key
    {
        get => _keys.GetValueOrDefault(ProviderName, "");
        set => _keys[ProviderName] = value;
    }

    /// <summary>The chosen provider, with its dialect and its hint.</summary>
    public Provider Provider => Reversing.Provider.All
        .FirstOrDefault(p => p.Name.Equals(ProviderName, StringComparison.OrdinalIgnoreCase))
        ?? Reversing.Provider.All[^1];

    /// <summary>Sets a provider, without overwriting what the user has typed by hand.</summary>
    public void Use(Provider provider)
    {
        ProviderName = provider.Name;
        Dialect = provider.Dialect;
        if (provider.Url.Length > 0) Url = provider.Url;
        if (provider.Suggested.Length > 0) Model = provider.Suggested;
    }

    // ─── The paths ──────────────────────────────────────────────────────────────────────

    /// <summary>The NEW client's folder, the one to be deobfuscated.</summary>
    public string ClientFolder { get; set; } = "";

    /// <summary>The protocol assembly of the OLD version, the one already known.</summary>
    public string OldProtocolDll { get; set; } = "";

    /// <summary>Which wizard step it was on, to be able to close and come back.</summary>
    public int Step { get; set; }

    /// <summary>The window's language, of the three the emulator speaks.</summary>
    public Language Language { get; set; } = Language.Es;

    /// <summary>How the model has to be called with what is set now.</summary>
    public Llm.Endpoint Endpoint() => new(Url, Model, Key, Dialect, AtOnce);

    // ─── To and from disk ───────────────────────────────────────────────────────────────

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

            // It is written alongside and moved on top: if it is cut halfway, what is lost is the
            // new and not what was already there, which includes the keys.
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
    /// A key, encrypted against the Windows account.
    ///
    /// If the encryption fails —which it should not, but it happens on system accounts and in some roaming
    /// profiles— it is stored EMPTY on purpose. Asking for it again is annoying; leaving it in the clear in a
    /// configuration file is worse.
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
