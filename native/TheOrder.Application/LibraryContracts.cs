using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

public enum LibraryReadStatus { Loaded, ConfigurationError, MissingFile, InvalidData, ReadError }
public sealed record LibraryReadResult(LibraryReadStatus Status, ImmutableArray<LibraryEntry> Entries, string? Error)
{
    public bool IsSuccess => Status == LibraryReadStatus.Loaded;
    public static LibraryReadResult Failure(LibraryReadStatus status, string message) => new(status, [], message);
}
public interface IReadOnlyLibrary
{
    Task<LibraryReadResult> LoadAsync(CancellationToken cancellationToken);
}
// The Windows adapter owns file access and validates isolation before opening any library.
public interface IIsolatedLibrarySource
{
    Stream OpenRead(string? explicitDirectory);
}
public sealed class DataIsolationException(string message) : Exception(message);
public sealed record NativeConfiguration(string? DataDirectory);
public enum NativeTheme { System, Light, Dark }
public interface INativeSettings
{
    NativeTheme Theme { get; set; }
}
// Phase 1 preferences are deliberately session-only. No legacy settings or secrets are read.
public sealed class SessionSettings : INativeSettings
{
    public NativeTheme Theme { get; set; } = NativeTheme.System;
}
