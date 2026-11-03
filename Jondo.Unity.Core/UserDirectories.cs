namespace Jondo.Unity.Launcher;

/// <summary>Profile paths must be absolute even before the first launch creates their directories.</summary>
public static class UserDirectories
{
    public static string Configuration => Folder(Environment.SpecialFolder.ApplicationData);
    public static string Data => Folder(Environment.SpecialFolder.LocalApplicationData);

    private static string Folder(Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Cannot locate the user's profile directory; configure HOME/XDG_CONFIG_HOME/XDG_DATA_HOME.");
        return path;
    }
}
