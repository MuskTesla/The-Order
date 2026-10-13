namespace TheOrder.Application;

public static class AppUpdateShutdown
{
    public static async Task<bool> RunAsync(Func<Task<bool>> resolveEdits, Func<Task<IAppUpdateHandoff?>> prepare,
        Func<Task<bool>> saveSessions, Func<Task> drain, Action close)
    {
        if (!await resolveEdits()) return false;
        using var handoff = await prepare();
        if (!await saveSessions()) return false;
        await drain();
        handoff?.Commit();
        close();
        return true;
    }
}
