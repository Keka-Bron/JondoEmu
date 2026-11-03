using Jondo.Unity.Launcher.Security;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Reversing;

namespace Jondo.Unity.Deobfuscator;

/// <summary>
/// What the deobfuscator remembers from one time to the next.
///
/// It lives in the user's configuration directory, next to <c>Jondo/lanzador.cfg</c>, with the same
/// <c>key=value</c> format as the launcher's preferences: outside the emulator's folder, because
/// they are the user's preferences and not the emulator's data, and in text so it can be opened and
/// fixed by hand if something goes wrong.
///
/// ─── The API keys ───────────────────────────────────────────────────────────────────────
///
/// Keys use the desktop tools' shared secret store: DPAPI on Windows, and AES-GCM
/// with an owner-only key file on Linux. Provider keys are never written in clear text.
///
/// One key per provider is kept. Trying Gemini for a while and going back to Claude cannot
/// cost going to fetch Claude's key again; whoever has three has all three set.
/// </summary>
public sealed class Settings
{
    private readonly string _path;
    public Settings(string? preferencesFile = null) => _path = preferencesFile ?? Path;
    /// <summary>Where it lives, next to the launcher's.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Jondo.Unity.Launcher.UserDirectories.Configuration, "Jondo", "desofuscador.cfg");

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

    /// <summary>The window's language, of the three the emulator speaks.</summary>
    public Language Language { get; set; } = Language.Es;

    /// <summary>How the model has to be called with what is set now.</summary>
    public Llm.Endpoint Endpoint() => new(Url, Model, Key, Dialect, AtOnce);

    // ─── To and from disk ───────────────────────────────────────────────────────────────

    public static Settings Load(string? preferencesFile = null)
    {
        var settings = new Settings(preferencesFile);
        var values = Read(settings._path);

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
            "# Jondo deobfuscator preferences. Keys use DPAPI on Windows, AES-GCM with a private key file on Linux.",
            "# The rest is text and can be edited by hand.",
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
            "idioma=" + LauncherTexts.Code(Language),
        };

        foreach (var (provider, clear) in _keys.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (clear.Length == 0) continue;
            string guarded = SecretStore.Protect(clear);
            if (guarded.Length > 0) lines.Add($"clave.{provider}={guarded}");
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

            // It is written alongside and moved on top: if it is cut halfway, what is lost is the
            // new and not what was already there, which includes the keys.
            string half = _path + ".escribiendo";
            File.WriteAllLines(half, lines);
            File.Move(half, _path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return values;
            foreach (string line in File.ReadAllLines(path))
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

    private static string Decipher(string guarded)
    {
        if (guarded.Length == 0) return "";
        // Older Deobfuscator settings contain unprefixed DPAPI ciphertext, never plain Base64.
        return SecretStore.Unprotect(guarded.Contains(':') ? guarded : "dpapi:" + guarded);
    }
}
