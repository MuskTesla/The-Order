using System.Text.Json;
using TheOrder.Application;
using TheOrder.Platform.Windows;
using Xunit;
using Xunit.Abstractions;

namespace TheOrder.Tests;

[Collection("Launch processes")]
public sealed class ProcessScopeObservationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1u, 1u, 0, 0u, 2u, false)] // unseen child born and gone between reads
    [InlineData(1u, 1u, 1, 1u, 2u, false)] // same active count, different generation
    [InlineData(1u, 1u, 0, 0u, 1u, false)] // exit during membership read
    [InlineData(1u, 1u, 0, 1u, 1u, false)] // inconsistent membership
    [InlineData(1u, 1u, 1, 1u, 1u, true)]
    [InlineData(0u, 2u, 0, 0u, 2u, true)]
    public void NativeScopeRequiresStableAccountingAroundMembership(uint beforeActive, uint beforeTotal,
        int count, uint afterActive, uint afterTotal, bool complete)
    {
        var result = WindowsProcessLifetimeScope.Reconcile(beforeActive, beforeTotal, count == 0 ? [] : [42], afterActive, afterTotal);
        Assert.Equal(complete, result.Complete);
        Assert.Equal(afterTotal, result.TotalProcesses);
        Assert.Equal(complete ? count : 0, result.ActivePids.Length);
    }

    [Fact]
    public async Task RetainedParentGenerationRemainsQueryableAfterExit()
    {
        await using var f = new LaunchFixture(); f.Set("launcher");
        using var parent = f.Platform.Start((await f.Service.PrepareAsync("g", null)).Snapshot!, default);
        using var scope = parent.RetainLifetimeScope()!;
        await f.Report("run-game");
        bool seen = false;
        for (int i = 0; i < 12; i++)
        {
            var p = parent.Query(); var tree = f.Platform.Descendants([p], 8192, 32);
            using var reopened = f.Platform.Attach(p.Identity!);
            var members = scope.Query();
            var names = members.ActivePids.Select(pid => { try { using var process = System.Diagnostics.Process.GetProcessById(pid); return process.ProcessName; } catch (ArgumentException) { return "exited"; } }).ToArray();
            output.WriteLine(JsonSerializer.Serialize(new { i, parent = p, reopened = reopened?.Query(), tree, scope = members, names }));
            if (p.State == ProcessTrackingState.Exited) { seen = true; Assert.NotEmpty(tree.Candidates); }
            await Task.Delay(150);
        }
        Assert.True(seen);
    }
}
