using System.Diagnostics;
using System.Text.Json;

namespace Jondo.Unity.Uplauncher;

public static class DofusLauncher
{
    public static LauncherConfig LoadConfig()
    {
        string path =
            Path.Combine(
                AppContext.BaseDirectory,
                "config.json"
            );

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "config.json introuvable.",
                path
            );
        }

        string json =
            File.ReadAllText(path);

        return
            JsonSerializer.Deserialize<LauncherConfig>(json)
            ??
            throw new InvalidOperationException(
                "Configuration invalide."
            );
    }

    public static Process StartDofus(
    LauncherConfig config)
    {
        if (!File.Exists(config.DofusPath))
        {
            throw new FileNotFoundException(
                "Dofus.exe introuvable.",
                config.DofusPath
            );
        }

        string clientDirectory =
            Path.GetDirectoryName(
                config.DofusPath
            )!;

        /*
         * Equivalent de :
         *
         * set "HASH=%RANDOM%%RANDOM%"
         *
         * Le .bat concatène deux RANDOM Windows.
         * RANDOM = 0..32767.
         */
        int random1 =
            Random.Shared.Next(
                0,
                32768
            );

        int random2 =
            Random.Shared.Next(
                0,
                32768
            );

        string zaapHash =
            $"{random1}{random2}";

        var startInfo =
            new ProcessStartInfo
            {
                FileName =
                    config.DofusPath,

                WorkingDirectory =
                    clientDirectory,

                UseShellExecute =
                    false
            };

        // ============================================================
        // VARIABLES D'ENVIRONNEMENT
        // Equivalent des SET du .bat
        // ============================================================

        startInfo.Environment["ZAAP_PORT"] =
            config.ZaapPort.ToString();

        startInfo.Environment["ZAAP_HASH"] =
            zaapHash;

        startInfo.Environment["ZAAP_GAME"] =
            config.ZaapGame;

        startInfo.Environment["ZAAP_RELEASE"] =
            config.ZaapRelease;

        startInfo.Environment["ZAAP_INSTANCE_ID"] =
            config.ZaapInstanceId;

        startInfo.Environment["ZAAP_CAN_AUTH"] =
            config.ZaapCanAuth;

        // ============================================================
        // ARGUMENTS
        // Equivalent EXACT de ton .bat
        // ============================================================

        startInfo.ArgumentList.Add(
            "-force-d3d11"
        );

        startInfo.ArgumentList.Add(
            "--port"
        );

        startInfo.ArgumentList.Add(
            config.ZaapPort.ToString()
        );

        startInfo.ArgumentList.Add(
            "--gameName"
        );

        startInfo.ArgumentList.Add(
            config.ZaapGame
        );

        startInfo.ArgumentList.Add(
            "--gameRelease"
        );

        startInfo.ArgumentList.Add(
            config.ZaapRelease
        );

        startInfo.ArgumentList.Add(
            "--instanceId"
        );

        startInfo.ArgumentList.Add(
            config.ZaapInstanceId
        );

        startInfo.ArgumentList.Add(
            "--hash"
        );

        startInfo.ArgumentList.Add(
            zaapHash
        );

        startInfo.ArgumentList.Add(
            "--canLogin"
        );

        startInfo.ArgumentList.Add(
            config.ZaapCanAuth
        );

        startInfo.ArgumentList.Add(
            "--langCode"
        );

        startInfo.ArgumentList.Add(
            config.LangCode
        );

        startInfo.ArgumentList.Add(
            "--autoConnectType"
        );

        startInfo.ArgumentList.Add(
            config.AutoConnectType.ToString()
        );

        startInfo.ArgumentList.Add(
            "--connectionPort"
        );

        startInfo.ArgumentList.Add(
            config.ConnectionPort.ToString()
        );

        // ============================================================
        // LOG
        // ============================================================

        Console.WriteLine(
            "============================================="
        );

        Console.WriteLine(
            " JONDO - Dofus 3 Local Emulator Launcher"
        );

        Console.WriteLine(
            $" Zaap port  : {config.ZaapPort}"
        );

        Console.WriteLine(
            " HAAPI port : 8888"
        );

        Console.WriteLine(
            $" Hash       : {zaapHash}"
        );

        Console.WriteLine(
            "============================================="
        );

        Console.WriteLine(
            $"ZAAP_PORT={config.ZaapPort}"
        );

        Console.WriteLine(
            $"ZAAP_HASH={zaapHash}"
        );

        Console.WriteLine(
            $"ZAAP_GAME={config.ZaapGame}"
        );

        Console.WriteLine(
            $"ZAAP_RELEASE={config.ZaapRelease}"
        );

        Console.WriteLine(
            $"ZAAP_INSTANCE_ID={config.ZaapInstanceId}"
        );

        Console.WriteLine(
            $"ZAAP_CAN_AUTH={config.ZaapCanAuth}"
        );

        Process? process =
            Process.Start(
                startInfo
            );

        if (process == null)
        {
            throw new InvalidOperationException(
                "Impossible de démarrer Dofus."
            );
        }

        return process;
    }
}