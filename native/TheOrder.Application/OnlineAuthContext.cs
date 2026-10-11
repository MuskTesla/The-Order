namespace TheOrder.Application;

public readonly record struct OnlineAuthSnapshot(long Generation, CancellationToken Changed);
public interface IOnlineAuthContextSource { OnlineAuthContext AuthContext { get; } }

// Process-local identity only: never a secret, credential hash, or persistent user identifier.
public sealed class OnlineAuthContext
{
    private readonly object gate = new();
    private long generation;
    private int mutations;
    private CancellationTokenSource changed = new();
    private TaskCompletionSource ready = Completed();
    public long Generation { get { lock (gate) return generation; } }
    private static TaskCompletionSource Completed() { var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }
    internal void BeginMutation()
    {
        CancellationTokenSource old;
        lock (gate)
        {
            generation++; old = changed; changed = new();
            if (mutations++ == 0) ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.Cancel(); old.Dispose();
    }
    internal void EndMutation() { lock (gate) { if (--mutations == 0) ready.TrySetResult(); } }
    public async Task<OnlineAuthSnapshot> CaptureAsync(CancellationToken cancellation)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (mutations == 0) return new(generation, changed.Token);
                wait = ready.Task;
            }
            await wait.WaitAsync(cancellation).ConfigureAwait(false);
        }
    }
    public T Accept<T>(OnlineAuthSnapshot snapshot, Func<T> action)
    {
        lock (gate)
        {
            if (mutations != 0 || snapshot.Generation != generation) throw new OperationCanceledException(snapshot.Changed);
            snapshot.Changed.ThrowIfCancellationRequested();
            return action();
        }
    }
}

// All authenticated callers and settings share this adapter in the composition root.
// Intent invalidates immediately; durable credential writes execute in invocation order.
public sealed class AuthenticatedCredentials(ICredentialSettings settings, ICredentialProvider provider) : ICredentialSettings, ICredentialProvider, IOnlineAuthContextSource
{
    public OnlineAuthContext AuthContext { get; } = new();
    private readonly object queueGate = new();
    private Task tail = Task.CompletedTask;
    public Task<string?> ReadAsync(CancellationToken cancellation) => provider.ReadAsync(cancellation);
    public async Task<bool> HasCredentialAsync(CancellationToken cancellation)
    {
        var snapshot = await AuthContext.CaptureAsync(cancellation).ConfigureAwait(false);
        var result = await settings.HasCredentialAsync(cancellation).ConfigureAwait(false);
        return AuthContext.Accept(snapshot, () => { cancellation.ThrowIfCancellationRequested(); return result; });
    }
    public Task SaveAsync(string secret, CancellationToken cancellation) => Enqueue(() => settings.SaveAsync(secret, cancellation));
    public Task ClearAsync(CancellationToken cancellation) => Enqueue(() => settings.ClearAsync(cancellation));
    private Task Enqueue(Func<Task> mutation)
    {
        lock (queueGate)
        {
            AuthContext.BeginMutation();
            var previous = tail;
            return tail = Execute(previous, mutation);
        }
    }
    private async Task Execute(Task previous, Func<Task> mutation)
    {
        try { try { await previous.ConfigureAwait(false); } catch { /* A failed older intent must not discard its successor. */ } await mutation().ConfigureAwait(false); }
        finally { AuthContext.EndMutation(); }
    }
}
