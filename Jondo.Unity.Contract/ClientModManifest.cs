using System;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jondo.Unity.Launcher;

public sealed record ClientModManifest([property: JsonPropertyName("mods")] ClientModEntry[] Mods)
{
    public const string Route = Contract.Prefijo + "mods";
    public const string BootstrapFileName = "JondoModLoader.dll";
    public const string LaunchManifestEnvironmentVariable = "JONDO_CLIENT_MOD_MANIFEST";
    public const int MaxEntries = 128;
    public const int MaxDllBytes = 128 * 1024 * 1024;
    public const int MaxManifestBytes = 128 * 1024;

    private static readonly Regex SafeDllName = new(
        @"\A(?!(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\.)[A-Za-z0-9_-][A-Za-z0-9_.-]{0,119}\.[dD][lL][lL]\z",
        RegexOptions.CultureInvariant);

    public static bool IsDllName(string? name) => name != null
        && !name.Equals(BootstrapFileName, StringComparison.OrdinalIgnoreCase)
        && SafeDllName.IsMatch(name);

    public static bool IsDllSize(long size) => size > 0 && size <= MaxDllBytes;
}

public sealed record ClientModEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);
