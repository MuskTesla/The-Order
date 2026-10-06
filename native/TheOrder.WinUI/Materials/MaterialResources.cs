namespace TheOrder.WinUI.Materials;

// Native users are released before dependencies, including partial construction.
// Failed Close calls remain retained and visible for the final retry.
internal sealed class MaterialResources : IDisposable
{
    private readonly List<IDisposable> objects = [];
    private bool closed;
    internal int Remaining => objects.Count;
    internal int CleanupFailures { get; private set; }
    internal T Own<T>(T resource) where T : IDisposable { if (closed) { resource.Dispose(); throw new ObjectDisposedException(nameof(MaterialResources)); } objects.Add(resource); return resource; }
    public void Dispose()
    {
        closed = true;
        for (var i = objects.Count - 1; i >= 0; i--)
            try { objects[i].Dispose(); objects.RemoveAt(i); }
            catch (Exception) { CleanupFailures++; }
    }
}
