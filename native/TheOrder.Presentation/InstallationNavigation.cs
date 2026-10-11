namespace TheOrder.Presentation;

// UI-thread navigation intent. Loading installation data never reserves the user's next destination.
public sealed class InstallationNavigation(FilesViewModel files)
{
    private long generation;
    public void Invalidate() => generation++;
    public async Task OpenAsync(string id, Func<Task<bool>> resolveUnsaved, Func<Task> navigate)
    {
        var intent = ++generation;
        if (!await resolveUnsaved() || intent != generation) return;
        await files.LoadInstallationsAsync(id);
        if (intent == generation) await navigate();
    }
}
