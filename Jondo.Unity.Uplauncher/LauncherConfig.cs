namespace Jondo.Unity.Uplauncher;

public class LauncherConfig
{
    public string DofusPath { get; set; } = "";
    public int ZaapPort { get; set; } = 15881;
    public string ZaapGame { get; set; } = "dofus";
    public string ZaapRelease { get; set; } = "dofus3";
    public string ZaapInstanceId { get; set; } = "1";
    public string ZaapCanAuth { get; set; } = "true";
    public string LangCode { get; set; } = "fr";
    public int AutoConnectType { get; set; } = 1;
    public int ConnectionPort { get; set; } = 5555;
}