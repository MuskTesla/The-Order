using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class WindowsLibrarySourcesWatcher(LibrarySourcesService sources) : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> watchers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> failed = new();
    public void Refresh()
    {
        var active = sources.Sources.Where(x => x.Watch).ToArray();
        foreach (var id in failed.Keys) if (failed.TryRemove(id, out _) && watchers.Remove(id, out var watcher)) watcher.Dispose();
        foreach (var id in watchers.Keys.Where(id => !active.Any(x => x.Id == id)).ToArray()) { watchers[id].Dispose(); watchers.Remove(id); }
        foreach (var root in active.Where(x => !watchers.ContainsKey(x.Id)))
        {
            try
            {
                var watch = new FileSystemWatcher(root.Root.StoredPath) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite, InternalBufferSize = 16384 };
                watch.Created += (_, _) => sources.Changed(root.Id); watch.Deleted += (_, _) => sources.Changed(root.Id);
                watch.Renamed += (_, _) => sources.Changed(root.Id); watch.Changed += (_, _) => sources.Changed(root.Id);
                watch.Error += (_, _) => { failed[root.Id] = 0; sources.Changed(root.Id); };
                watch.EnableRaisingEvents = true; watchers.Add(root.Id, watch);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { sources.Changed(root.Id); }
        }
    }
    public void Dispose() { foreach (var watcher in watchers.Values) watcher.Dispose(); watchers.Clear(); }
}
