using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using TheOrder.Platform.Windows;

namespace TheOrder.Performance;

public static partial class Program
{
    private static async Task SessionAppends(string output, string covers)
    {
        var results = new List<object>();
        foreach (int count in historyCounts)
        {
            string root = Path.Combine(output, "native-append-" + count);
            var original = Dataset(1, count, seed)[0];
            string installation = Id("append-install").ToString();
            var entry = original with { Installations = [new(installation, AppendPlatform.Folder, AppendPlatform.Executable, [], AppendPlatform.Folder)], PreferredInstallationId = installation };
            var input = Codec.Encode([entry], Date);
            using var owner = NativeProfileStore.Open(new(NativeProfileKind.Test, root), Codec);
            using (var persistence = owner.CreateLibraryPersistence()) await persistence.SaveAsync(owner.ReadComponent("library.json")!, input);
            await using var library = TransactionalLibrary.Open(Codec, owner.CreateLibraryPersistence());
            var clock = new AppendClock(); var platform = new AppendPlatform();
            long beforeCount = library.Snapshot.Entries[0].PlayCount, beforeSeconds = library.Snapshot.Entries[0].TotalPlaySeconds;
            int beforeSessions = library.Snapshot.Entries[0].Sessions.Length;
            var times = new List<double>(); var allocations = new List<long>(); var resources = new List<object>();
            await using (var engine = new SessionEngine(library, new AppendFilesystem(), platform, new AppendPositions(), clock, new AppendTokens(), autoPoll: false,
                launchOptions: new() { RunningPollInterval = TimeSpan.FromMilliseconds(5), PollInterval = TimeSpan.FromMilliseconds(5), HandoffWindow = TimeSpan.FromMilliseconds(50), CandidateSettle = TimeSpan.FromMilliseconds(10) }))
            {
                await engine.InitializeAsync();
                var preparation = await engine.PrepareAsync(entry.Id);
                Need(preparation.Snapshot is not null, "Append launch preparation failed");
                await engine.ConfirmPositionAsync(preparation.Snapshot!);
                for (int round = 0; round < 4; round++)
                {
                    clock.Advance(); long allocated = GC.GetTotalAllocatedBytes(); var watch = Stopwatch.StartNew();
                    var launch = await engine.LaunchAsync(entry.Id);
                    await engine.DrainAsync(); watch.Stop();
                    if (round > 0) { times.Add(watch.Elapsed.TotalMilliseconds); allocations.Add(GC.GetTotalAllocatedBytes() - allocated); }
                    Write(Path.Combine(output, "native-append-progress.json"), new { history = count, round, launch.Outcome, launch.Message, engineError = engine.Snapshot.Error,
                        beforeSessions, sessions = library.Snapshot.Entries[0].Sessions.Length, beforeCount, count = library.Snapshot.Entries[0].PlayCount,
                        beforeSeconds, seconds = library.Snapshot.Entries[0].TotalPlaySeconds, platform.Starts, platform.Handles });
                    Need(launch.ProcessCreated && library.Snapshot.Entries[0].Sessions.Length == beforeSessions + round + 1
                        && library.Snapshot.Entries[0].PlayCount == beforeCount + round + 1 && library.Snapshot.Entries[0].TotalPlaySeconds == beforeSeconds,
                        "Native append accounting failed");
                    // Cleanup is outside the measured append. Detach never claims a game exit.
                    await engine.StopObservingAsync(); platform.EndSyntheticProcess();
                    var cleanup = Stopwatch.StartNew();
                    while (platform.Handles != 0 && cleanup.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
                    Need(platform.Handles == 0, "Previous synthetic monitor did not drain");
                    await engine.DrainAsync();
                    Need(library.Snapshot.Entries[0].TotalPlaySeconds == beforeSeconds, "Detach changed playtime");
                    resources.Add(Resources());
                }
            }
            Need(platform.Handles == 0 && platform.Starts == 4, "Synthetic process ownership did not drain");
            var sorted = times.Order().ToArray();
            var measurement = new { name = "native-session-append", unit = "ms", warmups = 1, samples = times.Count, median = Percentile(sorted, .5), p95 = Percentile(sorted, .95), min = sorted[0], max = sorted[^1], elapsedMs = times, allocations };
            results.Add(new { history = count, nativeHistory = original.Sessions.Count(s => s.Native is not null), measurement, sourcePayloadSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)),
                owner.Manifest.ProfileId, owner.Manifest.StoreId, root, mainBytes = new FileInfo(Path.Combine(root, "library.json")).Length,
                accounting = new { appended = 4, countDelta = library.Snapshot.Entries[0].PlayCount - beforeCount, secondsDelta = library.Snapshot.Entries[0].TotalPlaySeconds - beforeSeconds, syntheticProcessStarts = platform.Starts, retainedSyntheticHandles = platform.Handles }, resources,
                note = "Actual SessionEngine admission/start and durable native append, synthetic filesystem/process/clock/token adapters; no OS process or real game launched. Detach cleanup excluded from append timing." });
            Write(Path.Combine(output, "native-session-append-results.json"), results);
            Console.WriteLine($"native append history={count} median={measurement.median:F1}ms");
        }
    }
    private sealed class AppendClock : ISessionClock
    {
        private long ticks, epoch;
        public void Advance() => ticks += NativeSession.TicksPerSecond;
        public void Discontinuity() => epoch++;
        public SessionClockStamp Read() => new(ticks, epoch, DateTimeOffset.Parse(Date).AddTicks(ticks));
    }
    private sealed class AppendFilesystem : IFilesystemIdentity
    {
        public PathPreflight Inspect(string path, bool requireDirectory = true) => new(PreflightStatus.Valid, new(path, path, path, new("synthetic-volume", path), requireDirectory, false, 0), "synthetic");
    }
    private sealed class AppendPositions : ILaunchPositionStore
    {
        private ImmutableArray<LaunchPosition> values = [];
        public Task<ImmutableArray<LaunchPosition>> ReadAsync(CancellationToken cancellation) => Task.FromResult(values);
        public Task WriteAsync(ImmutableArray<LaunchPosition> positions, CancellationToken cancellation) { values = positions; return Task.CompletedTask; }
    }
    private sealed class AppendTokens : ISessionRecoveryTokens
    {
        public uint? BootId => 7;
        public FilesystemIdentity ProfileIdentity => new("synthetic-profile", "append");
        public string Seal(SessionRecoveryIdentity identity) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(identity));
        public SessionRecoveryIdentity? Open(string value) => JsonSerializer.Deserialize<SessionRecoveryIdentity>(Convert.FromBase64String(value));
    }
    private sealed class AppendPlatform : IGameProcessPlatform
    {
        public const string Folder = @"D:\SyntheticAppend";
        public const string Executable = Folder + @"\fixture.exe";
        private sealed class State(ProcessIdentity identity) { public readonly ProcessIdentity Identity = identity; public bool Alive = true; }
        private State? current;
        public int Starts, Handles;
        public void EndSyntheticProcess() { if (current is not null) current.Alive = false; }
        public IObservedProcess Start(LaunchSnapshot snapshot, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Starts++;
            current = new(new(1000 + Starts, DateTimeOffset.Parse(Date).ToFileTime() + Starts, Executable, new("synthetic-volume", Executable)));
            return new Observation(this, current);
        }
        public IObservedProcess? Attach(ProcessIdentity identity) => current is { Alive: true } state && state.Identity == identity ? new Observation(this, state) : null;
        public ProcessTreeObservation Descendants(ImmutableArray<ProcessObservation> ancestors, int maximumProcesses, int maximumCandidates) => new([], true);
        public ImmutableArray<ProcessIdentity> FindExisting(PathObservation executable, int maximumProcesses) => [];
        public ProcessPresence RevalidateExternal(ProcessIdentity identity) => current is { Alive: true } state && state.Identity == identity ? ProcessPresence.Active : ProcessPresence.Inactive;
        private sealed class Observation : IObservedProcess
        {
            private readonly AppendPlatform owner; private readonly State state; private bool disposed;
            public Observation(AppendPlatform owner, State state) { this.owner = owner; this.state = state; Interlocked.Increment(ref owner.Handles); }
            public int Pid => state.Identity.Pid;
            public ProcessObservation Query() => new(state.Identity, state.Alive ? ProcessTrackingState.RunningConfirmed : ProcessTrackingState.Exited,
                state.Alive ? null : state.Identity.CreationFileTime + 10000, HasWindow: true);
            public IProcessLifetimeScope RetainLifetimeScope() => new Scope(state);
            public void Dispose() { if (!disposed) { disposed = true; Interlocked.Decrement(ref owner.Handles); } }
        }
        private sealed class Scope(State state) : IProcessLifetimeScope
        {
            public ProcessLifetimeObservation Query() => new(true, state.Alive ? 1u : 0u, 1) { ActivePids = state.Alive ? [state.Identity.Pid] : [] };
            public void Dispose() { }
        }
    }
}
