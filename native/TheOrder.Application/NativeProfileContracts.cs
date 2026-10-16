namespace TheOrder.Application;

public enum NativeProfileKind { Development, Test, Production, Beta }
// Optional provenance for an explicitly restored authoritative main. Existing persistence
// implementations keep the Phase 2 default (Main); restored history retains backup semantics.
public interface ILibraryRecoveryProvenance { LibraryRecovery MainRecovery { get; } }
public sealed class NativeProfileException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

// One application-owned lifetime. Component adapters borrow it; only the owner releases the lease.
public interface INativeProfileStore
{
    string Root { get; }
    void ValidateAccess();
    byte[]? ReadComponent(string name);
    void WriteComponent(string name, ReadOnlyMemory<byte> bytes);
    ILibraryPersistence CreateLibraryPersistence();
}
