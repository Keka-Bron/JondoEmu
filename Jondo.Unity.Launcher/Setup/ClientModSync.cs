using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Launcher.Setup;

internal static class ClientModSync
{
    internal static ClientModDeployment.Plan? Prepare(string host, ClientInstallation client, bool enableNativeMods,
        Func<string, ClientModManifest, string, bool>? approve)
    {
        bool hasMelonLoader = client.HasMelonLoader;
        if (!client.IsWindows && (!enableNativeMods || !hasMelonLoader)) return null;

        var server = new UriBuilder(Uri.UriSchemeHttp, host, Contract.Puerto).Uri;
        string cache = Path.Combine(UserDirectories.Data, "Jondo", "client-mods");
        var plan = ClientModDeployment.Prepare(server, Paths.Root, cache, LauncherPreferences.TrustedModManifest(host),
            hasMelonLoader, approve);
        if (plan.NewApproval.Length != 0) LauncherPreferences.TrustModManifest(host, plan.NewApproval);
        return plan;
    }

    internal static void Activate(ClientInstallation client, ClientModDeployment.Plan plan, System.Diagnostics.ProcessStartInfo start)
        => ClientModDeployment.Activate(client.Root, Paths.Root, plan, start,
            WineClient.Runs(client) ? WineClient.WindowsPath : null);
}
