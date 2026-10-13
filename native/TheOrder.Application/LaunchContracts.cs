using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

public enum LaunchOutcome
{
    Ready, PositionReviewRequired, Starting, Started, AlreadyLaunching, AlreadyRunning,
    StartedButTrackingLimited, InstallationMissing, ExecutableMissing, WorkingDirectoryInvalid,
    AccessDenied, LaunchFailed, StaleInstallation, Cancelled, Closed
}
public enum ProcessTrackingState { None, Attaching, RunningConfirmed, RunningLimited, ObservingHandoff, Exited, Unknown, Detached }
public enum ProcessEventKind { ProcessStarted, ProcessTrackingAttached, ProcessHandoff, ProcessExited, TrackingLimited, Detached }
public sealed record GameLaunchIdentity(Guid Epoch, Guid Incarnation, string GameId);
public sealed record LaunchEntry(GameLaunchIdentity Identity, ImmutableArray<Installation> Installations, string? Preferred);
public sealed record LaunchSnapshot(Guid OperationId, long Generation, GameLaunchIdentity Game, Installation Installation,
    string? Preferred, PathObservation Folder, PathObservation Executable, PathObservation WorkingDirectory, bool ExplicitWorkingDirectory)
{ public PathObservation? LocaleTool { get; init; } public PathObservation? ScalingTool { get; init; } }
public sealed record LaunchPosition(string GameId, string InstallationId, string Folder, string Executable, string WorkingDirectory,
    PathObservation FolderObservation, PathObservation ExecutableObservation, PathObservation WorkingObservation)
{ public LaunchCompatibility? Compatibility { get; init; } public string? SteamAppId { get; init; } public PathObservation? LocaleTool { get; init; } public PathObservation? ScalingTool { get; init; } }
public sealed record LaunchPreparation(LaunchOutcome Outcome, LaunchSnapshot? Snapshot, string Message);
public sealed record ProcessIdentity(int Pid, long CreationFileTime, string ImagePath, FilesystemIdentity? ImageIdentity);
public sealed record ProcessObservation(ProcessIdentity? Identity, ProcessTrackingState State, long? ExitFileTime = null, bool HasWindow = false, int? ParentPid = null);
public sealed record ProcessTreeCandidate(int ParentPid, ProcessObservation Observation);
public sealed record ProcessTreeObservation(ImmutableArray<ProcessTreeCandidate> Candidates, bool Complete);
public sealed record ProcessEvent(ProcessEventKind Kind, Guid OperationId, ProcessIdentity? Previous, ProcessIdentity? Current, DateTimeOffset ObservedAtUtc,
    DateTimeOffset? OccurredAtUtc = null, DateTimeOffset? ExitOccurredAtUtc = null);
public sealed record LaunchStatus(LaunchOutcome Outcome, ProcessTrackingState Tracking, LaunchSnapshot? Snapshot,
    ProcessIdentity? Initial, ProcessIdentity? Current, string Message, double? StartMilliseconds = null, double? AttachMilliseconds = null, double? HandoffMilliseconds = null,
    DateTimeOffset? ExitOccurredAtUtc = null)
{
    public bool ProcessCreated => Outcome is LaunchOutcome.Started or LaunchOutcome.StartedButTrackingLimited;
}
// A retained OS handle owns observation resources, never the lifetime of the game.
public interface IObservedProcess : IDisposable
{
    int Pid { get; }
    long? StartTimestamp => null;
    string? AuxiliaryNotice => null;
    ProcessObservation Query();
    // A separate owned lease survives stopped observation for bounded admission revalidation.
    IProcessLifetimeScope? RetainLifetimeScope() => null;
}
public sealed record ProcessLifetimeObservation(bool Complete, uint ActiveProcesses, uint TotalProcesses)
{
    public ImmutableArray<int> ActivePids { get; init; } = [];
}
public interface IProcessLifetimeScope : IDisposable
{
    ProcessLifetimeObservation Query();
}
public enum ProcessPresence { Inactive, Active, Unknown }
public interface IGameProcessPlatform
{
    // Runs final filesystem validation with pinned paths immediately before Process.Start.
    IObservedProcess Start(LaunchSnapshot snapshot, CancellationToken cancellation);
    IObservedProcess? Attach(ProcessIdentity identity);
    ProcessTreeObservation Descendants(ImmutableArray<ProcessObservation> ancestors, int maximumProcesses, int maximumCandidates);
    ImmutableArray<ProcessIdentity> FindExisting(PathObservation executable, int maximumProcesses);
    ProcessPresence RevalidateExternal(ProcessIdentity identity) => ProcessPresence.Unknown;
}
public interface ILaunchPositionStore
{
    Task<ImmutableArray<LaunchPosition>> ReadAsync(CancellationToken cancellation);
    Task WriteAsync(ImmutableArray<LaunchPosition> positions, CancellationToken cancellation);
}
public interface ILaunchLibrary
{
    Task<LaunchEntry> CaptureLaunchAsync(string id, CancellationToken cancellation);
    // The short synchronous creation step is serialized against delete/reimport/merge.
    Task<T> WithLaunchAdmissionAsync<T>(GameLaunchIdentity identity, string installationId, Func<T> create, CancellationToken cancellation);
}
public sealed class LaunchValidationException(LaunchOutcome outcome, string message) : Exception(message)
{ public LaunchOutcome Outcome { get; } = outcome; }

public static class LaunchSelection
{
    // Generic auxiliary names are negative evidence only; parent/creation/image/window
    // evidence is still required for every accepted continuation.
    public static bool IsContinuationExecutable(string name) => BoundedScanner.IsGameExecutable(name)
        && !System.Text.RegularExpressions.Regex.IsMatch(name, @"^(?:helper|launcher|config|settings|updater|crashreporter)(?:[._ -].*)?\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public static Installation? Select(LaunchEntry entry, string? explicitId = null) => explicitId is not null
        ? entry.Installations.FirstOrDefault(i => i.Id == explicitId)
        : entry.Installations.FirstOrDefault(i => i.Id == entry.Preferred) ?? (entry.Installations.Length == 1 ? entry.Installations[0] : null);
    public static string WorkingDirectory(Installation installation) => installation.WorkingDirectory ?? Path.GetDirectoryName(installation.Executable!)!;
    public static LaunchPosition Position(LaunchSnapshot snapshot) => new(snapshot.Game.GameId, snapshot.Installation.Id,
        snapshot.Installation.Folder, snapshot.Installation.Executable!, snapshot.WorkingDirectory.StoredPath,
        snapshot.Folder, snapshot.Executable, snapshot.WorkingDirectory)
        { Compatibility = snapshot.Installation.Compatibility, SteamAppId = snapshot.Installation.SteamAppId, LocaleTool = snapshot.LocaleTool, ScalingTool = snapshot.ScalingTool };
    public static bool Matches(LaunchPosition saved, LaunchSnapshot current) => saved.GameId == current.Game.GameId
        && saved.InstallationId == current.Installation.Id && saved.Folder == current.Installation.Folder
        && saved.Executable == current.Installation.Executable && saved.WorkingDirectory == current.WorkingDirectory.StoredPath
        && saved.Compatibility == current.Installation.Compatibility && saved.SteamAppId == current.Installation.SteamAppId
        && SameTool(saved.LocaleTool, current.LocaleTool) && SameTool(saved.ScalingTool, current.ScalingTool)
        && InstallationPreflightService.SameObservation(saved.FolderObservation, current.Folder)
        && InstallationPreflightService.SameObservation(saved.ExecutableObservation, current.Executable)
        && InstallationPreflightService.SameObservation(saved.WorkingObservation, current.WorkingDirectory);
    private static bool SameTool(PathObservation? a, PathObservation? b) => a is null && b is null || a is not null && b is not null && InstallationPreflightService.SameObservation(a, b);
}
