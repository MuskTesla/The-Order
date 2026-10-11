using System.Collections.Immutable;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

public sealed record ProcessSnapshotRow(int Pid, int Parent, string Name);
public sealed record ProcessSnapshot(long CaptureStartedFileTime, ImmutableArray<ProcessSnapshotRow> Rows);
public interface IProcessSnapshotSource { ProcessSnapshot? Capture(int maximum); }

// Shared by the native enumerator and deterministic generation-race tests.
internal static class ProcessSnapshotGeneration
{
    internal static ProcessTreeObservation Bind(ProcessSnapshot snapshot, ImmutableArray<ProcessObservation> ancestors,
        int maximumCandidates, Func<int, IObservedProcess?> open)
    {
        var known = ancestors.Where(a => a.Identity is not null).ToDictionary(a => a.Identity!.Pid);
        var candidates = ImmutableArray.CreateBuilder<ProcessTreeCandidate>();
        bool complete = true, added;
        do
        {
            added = false;
            foreach (var row in snapshot.Rows)
            {
                if (known.ContainsKey(row.Pid) || !known.TryGetValue(row.Parent, out var parent)) continue;
                using var childHandle = open(row.Pid);
                var child = childHandle?.Query();
                if (child?.Identity is not { } identity || identity.CreationFileTime > snapshot.CaptureStartedFileTime || child.ParentPid != row.Parent)
                { complete = false; continue; }
                // Re-open the parent generation too; the snapshot's integer alone is not ancestry.
                // An exited parent's retained-handle observation supplies its closed
                // lifetime interval. Windows may no longer allow opening its PID.
                using var parentHandle = parent.State == ProcessTrackingState.Exited && parent.ExitFileTime is not null ? null : open(row.Parent);
                var actualParent = parentHandle is null && parent.State == ProcessTrackingState.Exited ? parent : parentHandle?.Query();
                if (actualParent is null || actualParent.Identity != parent.Identity || actualParent.State == ProcessTrackingState.Unknown)
                { complete = false; continue; }
                var exit = parent.ExitFileTime ?? actualParent.ExitFileTime;
                if (identity.CreationFileTime < parent.Identity!.CreationFileTime || exit is { } end && identity.CreationFileTime > end)
                { complete = false; continue; }
                if (known.Count >= maximumCandidates) return new(candidates.ToImmutable(), false);
                known.Add(row.Pid, child); candidates.Add(new(row.Parent, child)); added = true;
            }
        } while (added);
        return new(candidates.ToImmutable(), complete);
    }
}
