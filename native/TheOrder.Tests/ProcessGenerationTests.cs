using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Platform.Windows;
using Xunit;

namespace TheOrder.Tests;

public sealed class ProcessGenerationTests
{
    [Theory]
    [InlineData(99)]
    [InlineData(null)]
    public void HandleParentRelationMustMatchSnapshotEvenWithPlausibleCreation(int? actualParent)
    {
        var parent = new ProcessIdentity(50, 100, @"C:\install\launcher.exe", new("volume", "parent"));
        var child = parent with { Pid = 100, CreationFileTime = 150 };
        var platform = new WindowsGameProcesses(new WindowsFilesystemIdentity(), new SnapshotSource(new(200, [new(100, 50, "game.exe")])),
            pid => new Observer(new(pid == 50 ? parent : child, ProcessTrackingState.RunningConfirmed, ParentPid: actualParent)));
        var result = platform.Descendants([new(parent, ProcessTrackingState.RunningConfirmed)], 8192, 32);
        Assert.Empty(result.Candidates); Assert.False(result.Complete);
    }
    [Theory]
    [InlineData(150, 100, true)]
    [InlineData(201, 100, false)]
    [InlineData(150, 101, false)]
    [InlineData(99, 100, false)]
    public void ProductionEnumeratorBindsSnapshotAndBothGenerations(long childBirth, long openedParentBirth, bool accepted)
    {
        var parent = new ProcessIdentity(50, 100, @"C:\install\launcher.exe", new("volume", "parent"));
        var child = new ProcessIdentity(100, childBirth, @"C:\install\game.exe", new("volume", "child"));
        var source = new SnapshotSource(new(200, [new(100, 50, "game.exe")]));
        int opened = 0, disposed = 0;
        var platform = new WindowsGameProcesses(new WindowsFilesystemIdentity(), source, pid =>
        {
            opened++;
            return new Observer(new(pid == 50 ? parent with { CreationFileTime = openedParentBirth } : child, ProcessTrackingState.RunningConfirmed, HasWindow: true, ParentPid: pid == 100 ? 50 : 1), () => disposed++);
        });
        var result = platform.Descendants([new(parent, ProcessTrackingState.RunningConfirmed)], 8192, 32);
        Assert.Equal(accepted, result.Candidates.Length == 1);
        Assert.Equal(accepted, result.Complete);
        Assert.Equal(opened, disposed);
    }
    [Fact]
    public void EnumerationPermutationDoesNotChangeVerifiedChain()
    {
        var parent = new ProcessIdentity(50, 100, @"C:\install\launcher.exe", new("volume", "parent"));
        var helper = parent with { Pid = 60, CreationFileTime = 120 };
        var child = parent with { Pid = 70, CreationFileTime = 150 };
        ImmutableArray<ProcessSnapshotRow> rows = [new(60, 50, "helper.exe"), new(70, 60, "game.exe")];
        foreach (var order in new[] { rows, rows.Reverse().ToImmutableArray() })
        {
            var platform = new WindowsGameProcesses(new WindowsFilesystemIdentity(), new SnapshotSource(new(200, order)),
                pid => new Observer(new(pid == 50 ? parent : pid == 60 ? helper : child, ProcessTrackingState.RunningConfirmed, ParentPid: pid == 70 ? 60 : 50)));
            var result = platform.Descendants([new(parent, ProcessTrackingState.RunningConfirmed)], 8192, 32);
            Assert.True(result.Complete); Assert.Equal(new[] { 60, 70 }, result.Candidates.Select(c => c.Observation.Identity!.Pid).Order());
        }
    }
    private sealed class SnapshotSource(ProcessSnapshot snapshot) : IProcessSnapshotSource
    { public ProcessSnapshot Capture(int maximum) => snapshot; }
    private sealed class Observer(ProcessObservation value, Action? onDispose = null) : IObservedProcess
    { public int Pid => value.Identity!.Pid; public ProcessObservation Query() => value; public void Dispose() => onDispose?.Invoke(); }
}
