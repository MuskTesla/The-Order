namespace TheOrder.Presentation;

// Container-local lifecycle, independent of the image cache and XAML implementation.
public sealed class ImageRequestSlot : IDisposable
{
    private CancellationTokenSource? cancellation;
    private long generation;
    private string? currentKey;
    public (long Generation, CancellationToken Cancellation) Begin(string key)
    { Clear(); currentKey = key; cancellation = new(); return (generation, cancellation.Token); }
    public bool IsCurrent(string key, long request) => currentKey == key && generation == request && cancellation?.IsCancellationRequested == false;
    public void Clear() { generation++; currentKey = null; cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; }
    public void Dispose() => Clear();
}
