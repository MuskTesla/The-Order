using System.Collections.Immutable;
using System.Collections.ObjectModel;
using TheOrder.Application;

namespace TheOrder.Presentation;

public enum ImportStage { Idle, Scanning, Review, NoCandidates, Cancelled, Failed, Completed }
public sealed class ArgumentDraft(string value) : ObservableState
{
    private string text = value;
    public string Text { get => text; set { text = value; Changed(); } }
}
public sealed class InstallationDraft : ObservableState
{
    private string folder = "", executable = "", working = "";
    private bool preferred;
    public string Id { get; }
    public string Folder { get => folder; set { folder = value; Changed(); } }
    public string Executable { get => executable; set { executable = value; Changed(); } }
    public string WorkingDirectory { get => working; set { working = value; Changed(); } }
    public bool Preferred { get => preferred; set { preferred = value; Changed(); } }
    public ObservableCollection<ArgumentDraft> Arguments { get; } = [];
    public InstallationDraft(InstallationRow? row = null)
    {
        Id = row?.Installation.Id ?? Guid.NewGuid().ToString();
        Folder = row?.Installation.Folder ?? ""; Executable = row?.Installation.Executable ?? "";
        WorkingDirectory = row?.Installation.WorkingDirectory ?? ""; Preferred = row?.Preferred ?? false;
        foreach (var arg in row?.Installation.Arguments ?? []) Arguments.Add(new(arg));
    }
    public InstallationInput Input => new(Id, Folder, Executable.Length == 0 ? null : Executable, WorkingDirectory.Length == 0 ? null : WorkingDirectory, [.. Arguments.Select(a => a.Text)], Preferred);
}
public sealed class FilesViewModel(IOfflineFilesClient client, WorkspaceViewModel workspace) : ObservableState, IAsyncDisposable
{
    private CancellationTokenSource? scanCancellation;
    private Task? scanTask;
    private readonly List<Task> detailTasks = [];
    private readonly CancellationTokenSource detailsLifetime = new();
    private Task? mutation;
    private long detailGeneration;
    private long scanGeneration;
    private bool disposed;
    public ImportStage Stage { get; private set; }
    public string ScanMessage { get; private set; } = UiText.Get("选择游戏库目录进行有界扫描。扫描不会修改游戏文件。");
    public ScanProgress? Progress { get; private set; }
    public string? ScanRoot { get; private set; }
    public IReadOnlyList<ScanCandidate> Candidates { get; private set; } = Array.Empty<ScanCandidate>();
    public bool IsScanning => Stage == ImportStage.Scanning;
    public InstallationView? Installations { get; private set; }
    public string? GameId { get; private set; }
    public InstallationDraft? Draft { get; private set; }
    public bool HasDraft => Draft is not null;
    public string InstallationMessage { get; private set; } = "";
    public string LastPreflight { get; private set; } = "";
    public Func<ImmutableArray<string>, Task>? RecognizeImported { get; set; }
    public Task ScanAsync(string root)
    {
        if (disposed || IsScanning || !workspace.CanEdit) return Task.CompletedTask;
        scanCancellation?.Dispose(); scanCancellation = new();
        return scanTask = ScanCore(root, scanCancellation.Token);
    }
    private async Task ScanCore(string root, CancellationToken cancellation)
    {
        var generation = ++scanGeneration;
        ScanRoot = root; Progress = null;
        Stage = ImportStage.Scanning; Candidates = Array.Empty<ScanCandidate>(); workspace.SetExternalBusy(true);
        ScanMessage = UiText.Get("正在扫描…可以取消。"); NotifyScan();
        try
        {
            var result = await client.ScanAsync(root, new Progress<ScanProgress>(p => { if (!disposed && IsScanning && generation == scanGeneration) { Progress = p; ScanMessage = UiText.Format($"已检查 {p.Directories} 个目录 / {p.Entries} 项，发现 {p.Candidates} 个候选"); NotifyScan(); } }), cancellation);
            if (disposed) return;
            Stage = result.Status switch { ScanStatus.Completed => ImportStage.Review, ScanStatus.NoCandidates => ImportStage.NoCandidates, ScanStatus.Cancelled => ImportStage.Cancelled, _ => ImportStage.Failed };
            Candidates = Stage == ImportStage.Review ? result.Candidates : Array.Empty<ScanCandidate>();
            Progress = result.Progress; ScanMessage = result.Message + (result.Warnings.IsEmpty ? "" : UiText.Format($" 有 {result.Warnings.Length} 条读取提示，部分目录未能检查。"));
        }
        catch (OperationCanceledException) { Stage = ImportStage.Cancelled; ScanMessage = UiText.Get("扫描已取消，未导入任何候选。"); }
        catch (Exception) { Stage = ImportStage.Failed; ScanMessage = UiText.Get("扫描未完成，请检查目录权限后重试。"); }
        finally { workspace.SetExternalBusy(false); NotifyScan(); }
    }
    private void NotifyScan() { Changed(nameof(Stage)); Changed(nameof(ScanMessage)); Changed(nameof(Progress)); Changed(nameof(Candidates)); Changed(nameof(IsScanning)); }
    public void CancelScan() => scanCancellation?.Cancel();
    public Task RetryScanAsync() => ScanRoot is { } root ? ScanAsync(root) : Task.CompletedTask;
    private Task Track(Func<Task> action) => disposed ? Task.CompletedTask : mutation is { IsCompleted: false } ? mutation : mutation = action();
    public Task WaitForMutationAsync() => mutation ?? Task.CompletedTask;
    public Task ImportAsync(ImmutableArray<ScanCandidate> selected) => Track(() => ImportCore(selected));
    private async Task ImportCore(ImmutableArray<ScanCandidate> selected)
    {
        if (Stage != ImportStage.Review || selected.IsEmpty) return;
        var result = await workspace.ExecuteFileMutationAsync(token => client.ImportAsync(selected, token));
        ScanMessage = result.Message;
        if (result.IsCommitted) { Stage = ImportStage.Completed; Candidates = Array.Empty<ScanCandidate>(); }
        NotifyScan();
        if (result.IsCommitted && !result.ImportedGameIds.IsDefaultOrEmpty && !disposed && RecognizeImported is { } recognize)
        {
            try { await recognize(result.ImportedGameIds); }
            catch (Exception) { ScanMessage += UiText.Get(" 导入已保存，但自动识别未能启动，请在作品详情中重新识别，或在设置中补全缺失资料。"); NotifyScan(); }
        }
    }
    public Task LoadInstallationsAsync(string id)
    {
        if (disposed) return Task.CompletedTask;
        var generation = ++detailGeneration; GameId = id; Draft = null; Installations = null; NotifyInstallations();
        detailTasks.RemoveAll(t => t.IsCompleted);
        var task = LoadCore(id, generation); detailTasks.Add(task); return task;
    }
    private async Task LoadCore(string id, long generation)
    {
        try
        {
            var result = await client.InstallationsAsync(id, detailsLifetime.Token);
            if (disposed || generation != detailGeneration) return;
            Installations = result; InstallationMessage = UiText.Get("移除安装只解除书架关系，不删除游戏文件。");
        }
        catch (Exception) { if (generation == detailGeneration) InstallationMessage = UiText.Get("安装信息未能读取，请重新打开。"); }
        NotifyInstallations();
    }
    public void Edit(InstallationRow? row) { if (!workspace.CanEdit || Draft is not null) return; Draft = new(row); NotifyInstallations(); }
    public void CancelDraft() { Draft = null; NotifyInstallations(); }
    public Task SaveAsync() => Track(SaveCore);
    private async Task SaveCore()
    {
        if (Draft is null || Installations is null) return;
        var input = Draft.Input; var details = Installations.Details;
        var result = await workspace.ExecuteFileMutationAsync(token => client.SaveAsync(details, input, token));
        LastPreflight = result.Outcome.ToString(); InstallationMessage = result.Message;
        if (result.IsCommitted) { Draft = null; await RefreshAfterCommit(); }
        NotifyInstallations();
    }
    public Task RemoveAsync(InstallationRow row) => Track(() => RemoveCore(row));
    private async Task RemoveCore(InstallationRow row)
    {
        if (Installations is null || Draft is not null) return;
        var result = await workspace.ExecuteFileMutationAsync(token => client.RemoveAsync(Installations.Details, row.Installation.Id, token));
        InstallationMessage = result.Message; if (result.IsCommitted) await RefreshAfterCommit(); NotifyInstallations();
    }
    public Task PreferAsync(InstallationRow row) => Track(() => PreferCore(row));
    private async Task PreferCore(InstallationRow row)
    {
        if (Installations is null || Draft is not null) return;
        var result = await workspace.ExecuteFileMutationAsync(token => client.PreferAsync(Installations.Details, row.Installation.Id, token));
        InstallationMessage = result.Message; if (result.IsCommitted) await RefreshAfterCommit(); NotifyInstallations();
    }
    private async Task RefreshAfterCommit()
    {
        Installations = null; Draft = null;
        if (disposed) return;
        try { Installations = await client.InstallationsAsync(GameId!, CancellationToken.None); }
        catch (Exception) { InstallationMessage += UiText.Get(" 已保存，但安装界面刷新失败，请重新打开；不要重复提交。"); }
    }
    private void NotifyInstallations() { Changed(nameof(Installations)); Changed(nameof(Draft)); Changed(nameof(HasDraft)); Changed(nameof(InstallationMessage)); Changed(nameof(LastPreflight)); }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; scanCancellation?.Cancel(); detailsLifetime.Cancel();
        if (scanTask is not null) await scanTask; await Task.WhenAll(detailTasks); if (mutation is not null) await mutation;
        scanCancellation?.Dispose(); detailsLifetime.Dispose();
    }
}
