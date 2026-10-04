using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed class IsolatedLibraryFactory(ILibraryCodec codec, NativeConfiguration configuration) : ITransactionalLibraryFactory
{
    public Task<TransactionalLibrary> OpenAsync(CancellationToken cancellation) => Task.Run(() =>
    {
        var persistence = new IsolatedLibraryPersistence(configuration.DataDirectory!);
        var library = TransactionalLibrary.Open(codec, persistence);
        if (cancellation.IsCancellationRequested) { library.Dispose(); cancellation.ThrowIfCancellationRequested(); }
        return library;
    }, cancellation);
}
