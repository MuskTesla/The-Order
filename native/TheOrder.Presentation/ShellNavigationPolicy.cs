namespace TheOrder.Presentation;

public static class ShellNavigationPolicy
{
    public static string? ParentGameId(WorkspacePage page, string? selectedId, string? installationGameId) =>
        page == WorkspacePage.Installations ? installationGameId : selectedId;
    public static bool CanGoBack(WorkspacePage page, bool browsingDiscovery = false) => page switch
    {
        WorkspacePage.Library or WorkspacePage.Settings => false,
        WorkspacePage.Online => browsingDiscovery,
        _ => true
    };
    public static WorkspacePage? Parent(WorkspacePage page) => page switch
    {
        WorkspacePage.Details or WorkspacePage.Import or WorkspacePage.Recognition => WorkspacePage.Library,
        WorkspacePage.Installations => WorkspacePage.Details,
        _ => null
    };
}
