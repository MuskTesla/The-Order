using TheOrder.Domain;

namespace TheOrder.Application;

public sealed record RecognitionLifecycle(Guid Epoch, Guid Generation, long Revision, long Request);
public sealed record RecognitionEntrySnapshot(LibraryEntry Entry, RecognitionLifecycle Lifecycle);
public interface IRecognitionLibrarySource
{
    Task<RecognitionEntrySnapshot> CaptureRecognitionAsync(string id, CancellationToken cancellation);
    Task<bool> IsRecognitionCurrentAsync(RecognitionEntrySnapshot snapshot, CancellationToken cancellation);
}

public sealed partial class TransactionalLibrary : IRecognitionLibrarySource
{
    public Task<RecognitionEntrySnapshot> CaptureRecognitionAsync(string id, CancellationToken cancellation = default)
        => Enqueue(() => Task.FromResult(CaptureRecognition(id)), cancellation);

    private RecognitionEntrySnapshot CaptureRecognition(string id)
    {
        var entry = Find(current.Entries, id);
        var life = lifecycles[id];
        return new(entry, new(epoch, life.Generation, life.Revision, life.Request));
    }
    private bool RecognitionCurrent(RecognitionEntrySnapshot snapshot) => snapshot.Lifecycle.Epoch == epoch
        && lifecycles.TryGetValue(snapshot.Entry.Id, out var life)
        && life.Generation == snapshot.Lifecycle.Generation && life.Revision == snapshot.Lifecycle.Revision
        && life.Request == snapshot.Lifecycle.Request;

    public Task<bool> IsRecognitionCurrentAsync(RecognitionEntrySnapshot snapshot, CancellationToken cancellation = default)
        => Enqueue(() => Task.FromResult(RecognitionCurrent(snapshot)), cancellation);

    // The review application layer alone calls this. Validation and token issuance share the
    // transaction queue, eliminating the check-then-BeginRequest race. Recognition never receives it.
    public Task<RequestToken> BeginRecognitionCommitAsync(RecognitionEntrySnapshot snapshot, CancellationToken cancellation = default)
        => Enqueue(() =>
        {
            if (!RecognitionCurrent(snapshot)) throw new StaleLibraryRequestException();
            var id = snapshot.Entry.Id; var life = lifecycles[id];
            UnregisterRequest(life.Request);
            life = life with { Request = ++request, Revision = ++revision, Pending = true };
            lifecycles[id] = life;
            var token = new RequestToken(epoch, id, life.Generation, life.Request, life.Revision);
            if (cancellation.CanBeCanceled)
                requestCancellations[token.Request] = cancellation.Register(() =>
                {
                    lock (admission) { if (!closing) _ = FinishRequestAsync(token); }
                });
            return Task.FromResult(token);
        }, cancellation);
}
