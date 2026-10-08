using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Presentation;

public sealed record SessionPresentation(string Title, string State, string Duration, string History, bool CanPause, bool CanResume)
{
    public static SessionPresentation From(SessionEngineSnapshot? snapshot, LibrarySnapshot? library, bool canMutate)
    {
        var session = snapshot?.Session;
        var n = session?.Native;
        var entry = library?.Entries.FirstOrDefault(e => e.Id == n?.GameId);
        if (session is null || n is null) return new("当前没有游玩会话", "", "", "", false, false);
        var state = n.Lifecycle switch
        {
            SessionLifecycle.Completed => "已确认游戏退出",
            SessionLifecycle.Incomplete => "上次会话未能确认完整结束",
            SessionLifecycle.RecoveryPending => "会话已保存，等待下次恢复",
            _ when snapshot!.Error is not null => "记录保存或验证失败，计时已冻结",
            _ when n.UserPaused => "计时已暂停；游戏可能仍在运行",
            _ when snapshot!.Tracking == SessionTracking.CurrentProcessOnly => "追踪受限：仅观察当前进程",
            _ when snapshot!.Tracking != SessionTracking.LaunchScope => "追踪受限：当前进程状态未确认",
            _ when snapshot!.Timing == SessionTiming.Running => "正在游玩",
            _ => "正在核验游戏状态；暂未继续计时"
        };
        if (n.UserPaused && !n.IsTerminal && snapshot!.Tracking != SessionTracking.LaunchScope) state += " · 追踪受限";
        return new(entry?.Name ?? "当前游戏", state, "本次已确认 " + Format(session.Seconds),
            entry is null ? "" : $"历史累计 {Format(entry.TotalPlaySeconds)} · 游玩 {entry.PlayCount} 次",
            canMutate && !n.IsTerminal && !n.UserPaused && n.Lifecycle == SessionLifecycle.Open,
            canMutate && !n.IsTerminal && n.UserPaused && n.Lifecycle == SessionLifecycle.Open);
    }
    private static string Format(long seconds) => $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}";
}

public sealed class WindowLifetime
{
    public bool TrayAvailable { get; set; }
    public bool Hidden { get; private set; }
    public string? Error { get; private set; }
    public bool CloseToTray()
    {
        if (!TrayAvailable) { Error = "托盘不可用：关闭窗口会按正常流程退出 The Order，先处理草稿并保存游玩记录。"; return false; }
        Hidden = true; return true;
    }
    public void Show() => Hidden = false;
}
