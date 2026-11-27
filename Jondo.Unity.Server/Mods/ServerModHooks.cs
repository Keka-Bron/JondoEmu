namespace Jondo.Unity.Server.Mods;

internal static class ServerModHooks
{
    internal static T? Get<T>() where T : class
    {
        var snapshot = HookSet<T>.Snapshot;
        return snapshot.Length == 0 ? null : snapshot[0];
    }

    internal static IReadOnlyList<T> All<T>() where T : class => HookSet<T>.Snapshot;

    internal static Action Register<T>(T hook) where T : class
    {
        ArgumentNullException.ThrowIfNull(hook);
        return HookSet<T>.Add(hook);
    }

    private static class HookSet<T> where T : class
    {
        private static readonly object Gate = new();
        private static volatile T[] _entries = [];
        internal static T[] Snapshot => _entries;

        internal static Action Add(T hook)
        {
            lock (Gate) _entries = [.. _entries, hook];
            return () =>
            {
                lock (Gate)
                {
                    int index = Array.FindIndex(_entries, entry => ReferenceEquals(entry, hook));
                    if (index >= 0) _entries = [.. _entries[..index], .. _entries[(index + 1)..]];
                }
            };
        }
    }
}
