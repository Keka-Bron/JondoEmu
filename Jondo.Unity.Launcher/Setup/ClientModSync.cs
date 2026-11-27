using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Launcher.Setup;

internal static class ClientModSync
{
    internal static ClientModDeployment.Plan Prepare(string host, bool hasMelonLoader, bool requiresRedirectMod,
        Func<string, ClientModManifest, string, bool>? approve)
    {
        var server = new UriBuilder(Uri.UriSchemeHttp, host, Contract.Puerto).Uri;
        string cache = Path.Combine(UserDirectories.Data, "Jondo", "client-mods");
        var plan = ClientModDeployment.Prepare(server, Paths.Root, cache, LauncherPreferences.TrustedModManifest(host),
            hasMelonLoader, requiresRedirectMod, approve);
        if (plan.NewApproval.Length != 0) LauncherPreferences.TrustModManifest(host, plan.NewApproval);
        return plan;
    }

    internal static void Activate(ClientInstallation client, ClientModDeployment.Plan plan, System.Diagnostics.ProcessStartInfo start)
        => ClientModDeployment.Activate(client.Root, Paths.Root, plan, start,
            WineClient.Runs(client) ? WineClient.WindowsPath : null);
}
