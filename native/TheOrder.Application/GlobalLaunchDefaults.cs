using TheOrder.Domain;

namespace TheOrder.Application;

public sealed record GlobalLaunchDefaults(bool LocaleEmulatorEnabled = false, string? LocaleEmulatorPath = null,
    bool MagpieEnabled = false, string? MagpiePath = null, bool BackgroundMuteEnabled = false);
public sealed record GlobalLaunchDefaultsRead(GlobalLaunchDefaults Value, string? Warning = null, bool ReadOnly = false);
public interface IGlobalLaunchDefaultsStore
{
    Task<GlobalLaunchDefaultsRead> LoadAsync(CancellationToken cancellation = default);
    Task SaveAsync(GlobalLaunchDefaults value, CancellationToken cancellation = default);
}

// Global intent is read at preparation, never materialized into each installation.
// Admission is serialized against changing that intent to close the last-await race.
public sealed class LaunchPolicy
{
    private readonly object gate = new();
    private sealed record State(GlobalLaunchDefaults Value, long Revision, bool Available);
    private State state = new(new(), 0, true);
    public GlobalLaunchDefaults Current => Volatile.Read(ref state).Value;
    public long Revision => Volatile.Read(ref state).Revision;
    public void Apply(GlobalLaunchDefaults defaults, bool usable = true)
    { Validate(defaults); lock (gate) { var before = state; Volatile.Write(ref state, new(defaults, before.Revision + 1, usable)); } }
    public (Installation Installation, long Revision) Resolve(Installation source)
    {
        lock (gate)
        {
            var (value, revision, available) = state;
            if (source.Compatibility?.Auxiliaries is not { } a) return (source, revision);
            if (!available) throw new LaunchValidationException(LaunchOutcome.LaunchFailed, "全局启动设置暂不可读取。请到设置中检查后重试，未启动游戏。");
            var c = source.Compatibility;
            string? Tool(LaunchOverride mode, bool enabled, string? legacy, string? global, string name)
            {
                if (!Enabled(mode, enabled)) return null;
                var path = mode == LaunchOverride.Enabled && legacy is not null ? legacy : global;
                if (string.IsNullOrWhiteSpace(path)) throw new LaunchValidationException(LaunchOutcome.ExecutableMissing, $"已启用 {name}，但尚未设置工具路径。请到“游戏启动与辅助工具”中选择程序。");
                return path;
            }
            var effective = c with {
                Auxiliaries = null,
                LocaleEmulator = Tool(a.LocaleEmulator, value.LocaleEmulatorEnabled, c.LocaleEmulator, value.LocaleEmulatorPath, "Locale Emulator"),
                ScalingTool = Tool(a.Magpie, value.MagpieEnabled, c.ScalingTool, value.MagpiePath, "Magpie"),
                MuteInBackground = Enabled(a.BackgroundMute, value.BackgroundMuteEnabled)
            };
            return (source with { Compatibility = effective == new LaunchCompatibility() ? null : effective }, revision);
        }
    }
    public bool BackgroundMute(Installation source)
    {
        // UI sampling never waits on a slow Steam/LE creation admission.
        var current = Volatile.Read(ref state);
        return source.Compatibility?.Auxiliaries is { } a ? current.Available && Enabled(a.BackgroundMute, current.Value.BackgroundMuteEnabled) : source.Compatibility?.MuteInBackground == true;
    }
    public T Admit<T>(long preparedRevision, Func<T> create)
    {
        lock (gate)
        {
            if (state.Revision != preparedRevision) throw new LaunchValidationException(LaunchOutcome.StaleInstallation, "全局启动设置已变化，请重新点击启动以使用最新设置。");
            return create();
        }
    }
    public static bool Enabled(LaunchOverride mode, bool global) => mode switch {
        LaunchOverride.Inherit => global, LaunchOverride.Enabled => true, LaunchOverride.Disabled => false,
        _ => throw new InvalidDataException("游戏启动覆盖选项无效。") };
    public static void Validate(GlobalLaunchDefaults d)
    {
        ArgumentNullException.ThrowIfNull(d);
        foreach (var (path, name) in new[] { (d.LocaleEmulatorPath, "LEProc.exe"), (d.MagpiePath, "Magpie.exe") })
        {
            LibraryExtras.Path(path);
            if (path is not null && !Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"请选择已安装的 {name}。");
        }
    }
    public static LaunchAuxiliaries LegacyOverrides(LaunchCompatibility? c) => new(
        c?.LocaleEmulator is null ? LaunchOverride.Disabled : LaunchOverride.Enabled,
        c?.ScalingTool is null ? LaunchOverride.Disabled : LaunchOverride.Enabled,
        c?.MuteInBackground == true ? LaunchOverride.Enabled : LaunchOverride.Disabled);
}
