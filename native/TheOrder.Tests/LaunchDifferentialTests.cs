using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using Xunit;

namespace TheOrder.Tests;

[Collection("Launch processes")]
public sealed class LaunchDifferentialTests
{
    private static JsonDocument Oracle() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "launch-oracle.json")));
    [Fact]
    public void ProductionElectronOracleSourceHashesRemainCurrent()
    {
        using var doc = Oracle(); var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "package.json"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var p in doc.RootElement.GetProperty("hashes").EnumerateObject())
            Assert.Equal(p.Value.GetString(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(root.FullName, p.Name)).Replace("\r\n", "\n")))).ToLowerInvariant());
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    [Trait("Category", "LaunchDifferential")]
    public async Task PreferredSelectionArgumentsCwdSingleActiveAndNoKillMatchActualElectron(int index)
    {
        using var doc = Oracle(); var spec = doc.RootElement.GetProperty("cases")[index];
        await using var f = new LaunchFixture(); f.Set("game");
        var call = spec.GetProperty("call");
        var arguments = call.ValueKind == JsonValueKind.Null ? ImmutableArray<string>.Empty : call.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToImmutableArray();
        var count = spec.GetProperty("count").GetInt32(); var preferred = spec.GetProperty("preferred");
        var installs = Enumerable.Range(0, count).Select(i => f.Library.Entry.Installations[0] with { Id = i.ToString(), Arguments = arguments }).ToImmutableArray();
        f.Library.Entry = f.Library.Entry with { Installations = installs, Preferred = preferred.ValueKind == JsonValueKind.Null ? null : preferred.GetInt32().ToString() };
        var selected = LaunchSelection.Select(f.Library.Entry);
        Assert.Equal(spec.GetProperty("accepted").GetBoolean(), selected is not null);
        if (selected is null) return;
        Assert.Equal(call.GetProperty("index").GetInt32().ToString(), selected.Id);
        var p = await f.Service.PrepareAsync("g", null); await f.Service.ConfirmPositionAsync(p.Snapshot!);
        var platform = new ControlledPlatform(); await using var service = new GameLaunchService(f.Library, f.Identity, platform, f.Store);
        await service.LaunchAsync("g");
        Assert.Equal(arguments, platform.Snapshot!.Installation.Arguments);
        Assert.Equal(call.GetProperty("cwdIsExecutableDirectory").GetBoolean(), LaunchSelection.WorkingDirectory(selected) == Path.GetDirectoryName(selected.Executable));
        Assert.Equal(spec.GetProperty("duplicateRejected").GetBoolean(), (await service.LaunchAsync("g")).Outcome == LaunchOutcome.AlreadyRunning);
        await service.DisposeAsync(); Assert.True(spec.GetProperty("noKill").GetBoolean());
        Assert.Equal(1, platform.Starts); // Actual Windows no-kill is exercised by LaunchTests.
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)]
    [Trait("Category", "LaunchDifferential")]
    public async Task ContinuationOracleDocumentsSameDecisionOrStrongerNativeEvidence(int index)
    {
        using var doc = Oracle(); var spec = doc.RootElement.GetProperty("continuations")[index];
        await using var f = new LaunchFixture(); f.Set("game"); await f.Approve();
        var name = spec.GetProperty("name").GetString();
        // Platform's retained-handle parent/time checks filter unrelated, known and old
        // processes. Actual Windows checks are independently exercised in LaunchTests.
        var platform = new ControlledPlatform { RootExited = true, ChildName = name is "unique-child" or "multiple" ? "game.exe" : null, ChildWindow = true, Multiple = name == "multiple" };
        await using var service = new GameLaunchService(f.Library, f.Identity, platform, f.Store, new() { PollInterval = TimeSpan.FromMilliseconds(10), HandoffWindow = TimeSpan.FromMilliseconds(100), CandidateSettle = TimeSpan.FromMilliseconds(20) });
        var started = await service.LaunchAsync("g"); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (service.Status.Tracking != ProcessTrackingState.RunningLimited && service.Status.Current?.Pid == started.Initial?.Pid) await Task.Delay(10, deadline.Token);
        bool handoff = service.Status.Current?.Pid != started.Initial?.Pid;
        Assert.Equal(spec.GetProperty("nativeExpected").GetString() == "handoff", handoff);
        bool oldHandoff = spec.GetProperty("selectedPid").ValueKind != JsonValueKind.Null;
        Assert.Equal(spec.GetProperty("intentionalImprovement").GetBoolean(), oldHandoff != handoff);
    }
}
