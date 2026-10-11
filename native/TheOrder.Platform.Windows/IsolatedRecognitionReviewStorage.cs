using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class IsolatedRecognitionReviewStorage(NativeConfiguration configuration) : IRecognitionReviewStorage
{
    private const int Maximum = 16 * 1024 * 1024;
    private readonly SemaphoreSlim queue = new(1, 1);
    public Task<RecognitionReviewDocuments> ReadAsync(CancellationToken cancellation) => Run(root =>
    {
        if (configuration.Profile is { } profile) return new RecognitionReviewDocuments(profile.ReadComponent("native-recognition.json"), null);
        byte[]? Read(string name)
        {
            try
            {
                using var stream = IsolatedLibrarySource.OpenChecked(Path.Combine(root, name));
                if (stream.Length > Maximum) throw new InvalidDataException(UiText.Get("核对队列超过 16 MiB，原文件已保留。"));
                var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
            }
            catch (FileNotFoundException) { return null; }
        }
        var native = Read("native-recognition.json"); return new RecognitionReviewDocuments(native, native is null ? Read("metadata-matching.json") : null);
    }, cancellation);
    public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation) => Run(root =>
    {
        if (bytes.Length > Maximum) throw new InvalidDataException(UiText.Get("核对队列过大，未保存。"));
        if (configuration.Profile is { } profile) { profile.WriteComponent("native-recognition.json", bytes); return true; }
        var target = Path.Combine(root, "native-recognition.json"); var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        void Check() { try { using var file = IsolatedLibrarySource.OpenChecked(target); } catch (FileNotFoundException) { } }
        Check();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes.Span); stream.Flush(true); }
            Check(); cancellation.ThrowIfCancellationRequested(); File.Move(temporary, target, true); return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, cancellation);
    private async Task<T> Run<T>(Func<string, T> action, CancellationToken cancellation)
    {
        await queue.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var root = NativeProfileAccess.Validate(configuration); var handles = IsolatedLibraryPersistence.PinDirectoryChain(root);
                try { NativeProfileAccess.Validate(configuration); cancellation.ThrowIfCancellationRequested(); return action(root); }
                finally { foreach (var handle in handles) handle.Dispose(); }
            }, cancellation).ConfigureAwait(false);
        }
        finally { queue.Release(); }
    }
}
