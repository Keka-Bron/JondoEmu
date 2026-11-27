using Jondo.Unity.Launcher.Packs;

namespace Jondo.Unity.Launcher.Setup;

internal static class ClientModBuild
{
    public static async Task<int> PrepareAsync(string clientDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Preparing the client mod references requires Windows.");
            return 1;
        }

        string root = Path.GetFullPath(clientDirectory);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(50));
        try
        {
            string downloads = Path.Combine(root, ".jondo-build-downloads");
            Console.WriteLine($"Downloading the Dofus {ClientDownload.Version} Windows runtime to {root}.");
            var store = new ManifestStore(Http.Shared, downloads, ClientCdn.Windows);
            var manifest = await store.GetAsync(ClientDownload.Version, new ProgressMeter(null), cancel.Token);
            var runtime = manifest.Fragments().Single(fragment => fragment.Name == "win32_x64");
            await ClientCdn.DownloadAsync(Http.Shared, [runtime], root, null, cancel.Token);

            if (!MelonLoaderSetup.IsInstalled(root))
            {
                Console.WriteLine($"Installing MelonLoader {MelonLoaderSetup.Version}.");
                await MelonLoaderSetup.DownloadAsync(downloads, null, cancel.Token);
                MelonLoaderSetup.Unpack(downloads, root, null, cancel.Token);
            }

            if (!MelonLoaderSetup.IsGenerated(root))
                await MelonLoaderSetup.GenerateAsync(Path.Combine(root, ClientCdn.ExecutableFor(ClientCdn.Windows)),
                    AppContext.BaseDirectory, (_, _) => { }, cancel.Token);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            string log = Path.Combine(root, "MelonLoader", "Latest.log");
            if (File.Exists(log))
                foreach (string line in File.ReadLines(log).TakeLast(40))
                    Console.Error.WriteLine(line);
            return 1;
        }
    }
}
