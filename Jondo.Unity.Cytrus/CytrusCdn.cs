namespace Jondo.Unity.Cytrus;

/// <summary>Where Ankama's CDN keeps manifests and bundles.</summary>
/// <remarks>
/// Old versions stay served: a manifest for a release that is no longer current still answers,
/// and so do its bundles. Bundles accept HTTP range requests.
/// </remarks>
public static class CytrusCdn
{
    public const string Host = "https://cytrus.cdn.ankama.com";

    /// <summary>
    /// The prefix of release names: the CDN calls client 3.6.10.11 "6.0_3.6.10.11". It is the
    /// Cytrus format version, not part of the game's version.
    /// </summary>
    public const string ReleasePrefix = "6.0_";

    /// <summary>The manifest of one release, for example <c>6.0_3.6.10.11</c>.</summary>
    public static string ManifestUrl(string game, string release, string platform, string releaseName)
        => Host + "/" + game + "/releases/" + release + "/" + platform + "/" + releaseName + ".manifest";

    /// <summary>A bundle, filed under the first two hex digits of its hash.</summary>
    public static string BundleUrl(string game, Sha1Hash bundle)
    {
        string hex = bundle.ToString();
        return Host + "/" + game + "/bundles/" + hex[..2] + "/" + hex;
    }
}
