using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TheOrder.Application;
using Xunit;

namespace TheOrder.Tests;

// Test-only handshake and lease audit. All identity, tree and scope facts come
// unchanged from the real Windows platform; this never manufactures a handoff.
internal sealed class LaunchFixtureObservation(IGameProcessPlatform inner, string reports) : IGameProcessPlatform
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<object> timeline = [];
    private ProcessLifetimeObservation? membership;
    private string? installationFolder;
    private int rootPid, observers, scopes;
    private bool parentObserved, childObserved;
    private bool incompleteRecorded, parentExitRecorded;
    public int OpenObservers => Volatile.Read(ref observers);
    public int OpenScopes => Volatile.Read(ref scopes);
    public object[] Timeline { get { lock (timeline) return [.. timeline]; } }
    public ProcessLifetimeObservation? Membership => membership;
    public void Requested() => Record("T0-request", null);
    public void Completed(LaunchStatus status) => Record("T5-test-completed", status);
    private void Record(string stage, object? fact) { lock (timeline) { if (timeline.Count < 16) timeline.Add(new { stage, milliseconds = clock.Elapsed.TotalMilliseconds, fact }); } }
    public void ProcessChanged(ProcessEvent fact)
    {
        if (fact.Kind == ProcessEventKind.ProcessHandoff) Record("T4-handoff-confirmed", fact);
        else if (fact.Kind == ProcessEventKind.Detached) Record("T5-observer-disposed", fact);
    }
    public IObservedProcess Start(LaunchSnapshot snapshot, CancellationToken cancellation)
    {
        var process = inner.Start(snapshot, cancellation); rootPid = process.Pid; installationFolder = snapshot.Folder.FinalPath;
        Record("T1-process-created", new { pid = process.Pid });
        return Wrap(process);
    }
    private IObservedProcess Wrap(IObservedProcess process) { Interlocked.Increment(ref observers); return new Observed(process, this); }
    public IObservedProcess? Attach(ProcessIdentity identity) { var process = inner.Attach(identity); Record("attachment", new { identity, success = process is not null }); return process is null ? null : Wrap(process); }
    public ImmutableArray<ProcessIdentity> FindExisting(PathObservation exe, int maximum) => inner.FindExisting(exe, maximum);
    public ProcessPresence RevalidateExternal(ProcessIdentity identity) => inner.RevalidateExternal(identity);
    public ProcessTreeObservation Descendants(ImmutableArray<ProcessObservation> ancestors, int maximum, int candidates)
    {
        var tree = inner.Descendants(ancestors, maximum, candidates);
        if (!tree.Complete && !incompleteRecorded) { incompleteRecorded = true; Record("incomplete-tree", new { ancestors, tree }); }
        // Require complete real kernel membership, already-attached generation
        // identities, and a live parent. A window alone is insufficient evidence.
        var scope = membership;
        if (!tree.Complete || scope is not { Complete: true } || !scope.ActivePids.All(pid => ancestors.Any(a => a.Identity?.Pid == pid))) return tree;
        var parent = ancestors.FirstOrDefault(a => a.Identity?.Pid == rootPid && a.State == ProcessTrackingState.RunningConfirmed);
        if (parent is null) return tree;
        if (!parentObserved)
        {
            parentObserved = true; Record("T2-parent-observed", new { parent, scope });
            File.WriteAllText(Path.Combine(reports, "run.parent-observed"), "identity bound by real observer");
        }
        var children = ancestors.Where(a => a.Identity is { } identity && identity.Pid != rootPid && a.State == ProcessTrackingState.RunningConfirmed
            && a.HasWindow && LaunchSelection.IsContinuationExecutable(Path.GetFileName(identity.ImagePath))
            && InstallationPreflightService.Within(identity.ImagePath, installationFolder!)).ToArray();
        if (!childObserved && children.Length == 1 && scope.ActiveProcesses == 2)
        {
            childObserved = true; Record("T3-child-observed", new { child = children[0], scope });
            File.WriteAllText(Path.Combine(reports, "run.child-observed"), "child identity and window consumed by real observer");
        }
        return tree;
    }
    private sealed class Observed(IObservedProcess inner, LaunchFixtureObservation owner) : IObservedProcess
    {
        private int disposed;
        public int Pid => inner.Pid;
        public long? StartTimestamp => inner.StartTimestamp;
        public ProcessObservation Query()
        {
            var value = inner.Query();
            if (Pid == owner.rootPid && value.State == ProcessTrackingState.Exited && !owner.parentExitRecorded) { owner.parentExitRecorded = true; owner.Record("parent-exited", value); }
            return value;
        }
        public IProcessLifetimeScope? RetainLifetimeScope()
        {
            var scope = inner.RetainLifetimeScope(); if (scope is null) return null;
            Interlocked.Increment(ref owner.scopes); return new Scope(scope, owner);
        }
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) { inner.Dispose(); Interlocked.Decrement(ref owner.observers); } }
    }
    private sealed class Scope(IProcessLifetimeScope inner, LaunchFixtureObservation owner) : IProcessLifetimeScope
    {
        private int disposed;
        public ProcessLifetimeObservation Query() { var value = inner.Query(); owner.membership = value; return value; }
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) { inner.Dispose(); Interlocked.Decrement(ref owner.scopes); } }
    }
}

internal sealed record NativeLaunchHandleSnapshot(int Process, int Job, int Section, int Thread, int Event)
{
    private static readonly Dictionary<uint, string> names = [];
    public static NativeLaunchHandleSnapshot Capture()
    {
        using var process = OpenProcess(0x440, false, Environment.ProcessId);
        Assert.False(process.IsInvalid);
        int size = 65536;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(process, 51, buffer, size, out var returned);
                if (status < 0) { size = Math.Max(size * 2, returned); continue; }
                var counts = new Dictionary<string, int>();
                long count = Marshal.ReadIntPtr(buffer).ToInt64();
                int stride = Marshal.SizeOf<Entry>();
                int header = 2 * IntPtr.Size;
                Assert.InRange(count, 0, (size - header) / stride);
                for (int i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<Entry>(IntPtr.Add(buffer, header + i * stride));
                    if (!names.TryGetValue(entry.Type, out var name))
                    {
                        var info = Marshal.AllocHGlobal(4096);
                        try
                        {
                            if (NtQueryObject(entry.Handle, 2, info, 4096, out _) < 0) continue;
                            var text = Marshal.PtrToStructure<Unicode>(info);
                            name = Marshal.PtrToStringUni(text.Buffer, text.Length / 2)!; names[entry.Type] = name;
                        }
                        finally { Marshal.FreeHGlobal(info); }
                    }
                    counts[name] = counts.GetValueOrDefault(name) + 1;
                }
                return new(counts.GetValueOrDefault("Process"), counts.GetValueOrDefault("Job"), counts.GetValueOrDefault("Section"), counts.GetValueOrDefault("Thread"), counts.GetValueOrDefault("Event"));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("Native handle snapshot did not complete; cannot assert resource release.");
    }
    public static void VerifyProbe()
    {
        var before = Capture();
        using (var process = OpenProcess(0x440, false, Environment.ProcessId)) { Assert.False(process.IsInvalid); Assert.Equal(before.Process + 1, Capture().Process); }
        using (var job = CreateJobObject(IntPtr.Zero, null)) { Assert.False(job.IsInvalid); Assert.Equal(before.Job + 1, Capture().Job); }
        using (var snapshot = CreateToolhelp32Snapshot(2, 0)) { Assert.False(snapshot.IsInvalid); Assert.Equal(before.Section + 1, Capture().Section); }
        var after = Capture(); Assert.Equal(before.Process, after.Process); Assert.Equal(before.Job, after.Job); Assert.Equal(before.Section, after.Section);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Entry { public IntPtr Handle, HandleCount, PointerCount; public uint Access, Type, Attributes, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct Unicode { public ushort Length, Maximum; public IntPtr Buffer; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int kind, IntPtr buffer, int size, out int returned);
    [DllImport("ntdll.dll")] private static extern int NtQueryObject(IntPtr handle, int kind, IntPtr buffer, int size, out int returned);
}
