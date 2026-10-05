using System.Collections.Immutable;
using System.Globalization;
using TheOrder.Domain;

namespace TheOrder.Application;

public sealed class OfflineLibraryClient(ITransactionalLibraryFactory factory, IClientTextRules text) : IOfflineLibraryClient, IRecognitionLibrarySource
{
    private readonly SemaphoreSlim lifetime = new(1, 1);
    private TransactionalLibrary? library;
    private LibraryCapability capability = new(false, false, "尚未读取书架");
    private readonly object indexLock = new();
    private LibrarySnapshot? indexedSnapshot;
    private QueryIndex? index;
    private bool disposed;
    public LibraryCapability Capability => Volatile.Read(ref capability);
    public async Task<ClientResult> LoadAsync(CancellationToken cancellation)
    {
        await lifetime.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // UI refresh cannot replace the writer underneath the application-owned engine.
            if (library?.HasSessionEngine == true) return new(ClientOutcome.Success, "已从当前会话书架刷新。");
            if (library is not null) { await library.ShutdownAsync().ConfigureAwait(false); library = null; }
            Volatile.Write(ref capability, new(false, false, "正在加载…"));
            library = await factory.OpenAsync(cancellation).ConfigureAwait(false);
            var snapshot = library.Snapshot;
            Volatile.Write(ref capability, new(true, snapshot.Access == LibraryAccess.ValidWritable,
                snapshot.ReadOnlyReason is not null ? "资料可以查看，但当前容量条件下无法安全保存。已禁用编辑，原文件保持不变。" : (snapshot.Recovery == LibraryRecovery.Backup ? "已从有效备份读取。下一次明确保存将写回主库。" : "书架已加载，修改会保存在当前资料目录。")));
            return new(ClientOutcome.Success, snapshot.Recovery == LibraryRecovery.Backup ? Capability.Message : "");
        }
        catch (OperationCanceledException) { return new(ClientOutcome.Cancelled, "读取已取消。"); }
        catch (Exception e) when (e is IOException or InvalidDataException or DataIsolationException or UnauthorizedAccessException or NativeProfileException)
        {
            Volatile.Write(ref capability, new(false, false, "书架未能读取，原文件已保留。请检查隔离目录与资料完整性。"));
            return new(ClientOutcome.LoadFailure, Capability.Message);
        }
        finally { lifetime.Release(); }
    }
    internal TransactionalLibrary Required() => library ?? throw new InvalidOperationException("书架尚未加载。");
    public LibrarySnapshot? CurrentSnapshot => library?.Snapshot;
    public SessionEngine CreateSessionEngine(IFilesystemIdentity filesystem, IGameProcessPlatform platform, ILaunchPositionStore positions,
        ISessionClock clock, ISessionRecoveryTokens tokens, ISessionPowerEvents? power = null)
        => new(Required(), filesystem, platform, positions, clock, tokens, power);
    public Task<RecognitionEntrySnapshot> CaptureRecognitionAsync(string id, CancellationToken cancellation) => Required().CaptureRecognitionAsync(id, cancellation);
    public Task<bool> IsRecognitionCurrentAsync(RecognitionEntrySnapshot snapshot, CancellationToken cancellation) => Required().IsRecognitionCurrentAsync(snapshot, cancellation);
    public Task<ImmutableArray<string>> RecognitionTargetsAsync(ImmutableArray<string> selected, RecognitionMode mode, CancellationToken cancellation)
    {
        var snapshot = Required().Snapshot;
        return Task.Run(() => { cancellation.ThrowIfCancellationRequested(); var requested = selected.IsDefaultOrEmpty ? null : selected.ToHashSet(StringComparer.Ordinal);
            var ids = snapshot.Entries.Where(e => (requested is null || requested.Contains(e.Id)) && !e.Installations.IsEmpty && (mode == RecognitionMode.Reidentify || e.Metadata is null || string.IsNullOrWhiteSpace(e.Metadata.CoverUrl))).Select(e => e.Id).Take(RecognitionQueue.MaximumJobs + 1).ToImmutableArray();
            if (ids.Length > RecognitionQueue.MaximumJobs) throw new InvalidDataException("一次最多识别 1,000 个条目，请缩小选择范围。"); return ids;
        }, cancellation);
    }
    public Task<LibraryQueryResult> QueryAsync(LibraryQuery query, CancellationToken cancellation)
    {
        var snapshot = Required().Snapshot;
        return Task.Run(() =>
        {
            QueryIndex current;
            lock (indexLock)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!ReferenceEquals(indexedSnapshot, snapshot)) { index = new(snapshot.Entries, text, cancellation); indexedSnapshot = snapshot; }
                current = index!;
            }
            return current.Query(query, cancellation);
        }, cancellation);
    }
    public Task<GameDetails> DetailsAsync(string id, CancellationToken cancellation) => Required().DetailsAsync(id, cancellation);
    public async Task<ClientResult> SaveAsync(GameDetails details, PersonalInput input, CancellationToken cancellation)
    {
        try
        {
            // Parsing/normalization is part of the Application operation, never a View responsibility.
            var patch = await Task.Run(() =>
            {
                double? rating = null;
                if (input.Rating.Length > 0)
                {
                    if (!double.TryParse(input.Rating, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) || !double.IsFinite(parsed) || parsed is < 0 or > 10)
                        throw new InvalidDataException("个人评分须为 0 至 10，留空表示未评分。");
                    rating = parsed;
                }
                var old = details.Personal;
                return new PersonalPatch
                {
                    Name = new(input.Name != old.Name, input.Name), Status = new(input.Status != old.Status, input.Status), Rating = new(rating != old.Rating, rating),
                    Favorite = new(input.Favorite != old.Favorite, input.Favorite), Notes = new(input.Notes != old.Notes, input.Notes),
                    Categories = new(input.Categories != string.Join(", ", old.Categories), text.SplitLabels(input.Categories)),
                    Tags = new(input.Tags != string.Join(", ", old.Tags), text.SplitLabels(input.Tags))
                };
            }, cancellation).ConfigureAwait(false);
            return Present(await Required().SavePersonalAsync(details.Token, patch, cancellation).ConfigureAwait(false));
        }
        catch (Exception e) when (Expected(e)) { return Failure(e); }
    }
    public async Task<ClientResult> BatchAsync(ImmutableArray<string> ids, BulkPersonalPatch patch, CancellationToken cancellation)
    {
        try { return Present(await Required().UpdateBatchAsync(ids, patch, cancellation).ConfigureAwait(false)); }
        catch (Exception e) when (Expected(e)) { return Failure(e); }
    }
    internal static ClientResult Present<T>(CommitResult<T> receipt) => receipt.Warning is not null
        ? new(ClientOutcome.CommittedWithWarning, "修改已经保存，但存在附加警告：" + receipt.Warning + " 请勿重复提交。")
        : new(ClientOutcome.Committed, receipt.CancellationAfterCommit ? "修改已经保存。取消发生在提交开始之后。" : "修改已经保存。");
    internal static bool Expected(Exception e) => e is InvalidDataException or LibraryWriteLimitException or LibraryReadOnlyException or StaleLibraryRequestException or CandidateStaleException or OperationCanceledException or IOException or UnauthorizedAccessException or DataIsolationException;
    internal static ClientResult Failure(Exception e) => e switch
    {
        LibraryReadOnlyException => new(ClientOutcome.ReadOnly, e.Message),
        CandidateStaleException => new(ClientOutcome.CandidateStale, e.Message),
        StaleLibraryRequestException => new(ClientOutcome.Conflict, "资料已经变化，未保存此次修改。草稿已保留；请核对后取消草稿并重新打开。"),
        OperationCanceledException => new(ClientOutcome.Cancelled, "提交前已取消，修改没有保存。草稿已保留。"),
        InvalidDataException or LibraryWriteLimitException => new(ClientOutcome.ValidationFailure, e.Message),
        _ => new(ClientOutcome.IoFailure, "文件保存未完成，此次修改未提交。草稿已保留；检查文件占用、磁盘空间或访问权限后可手动重试。")
    };
    public async ValueTask DisposeAsync()
    {
        await lifetime.WaitAsync().ConfigureAwait(false);
        try { if (disposed) return; disposed = true; if (library is not null) await library.ShutdownAsync().ConfigureAwait(false); }
        finally { lifetime.Release(); }
    }

    private sealed class QueryIndex
    {
        private sealed record Item(GameSummary Summary, ImmutableArray<string> Fields, ImmutableArray<string> Categories, ImmutableArray<string> Tags,
            bool Unmatched, bool Unconfigured, string Added, string Recent);
        private readonly ImmutableArray<Item> items;
        private readonly ImmutableArray<string> categories, tags;
        private readonly IClientTextRules text;
        public QueryIndex(ImmutableArray<LibraryEntry> entries, IClientTextRules text, CancellationToken cancellation)
        {
            this.text = text;
            var built = ImmutableArray.CreateBuilder<Item>(entries.Length);
            var allCategories = new HashSet<string>(StringComparer.Ordinal); var allTags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                cancellation.ThrowIfCancellationRequested();
                var combinedTags = e.CustomTags.AddRange(e.Metadata?.Tags ?? []);
                var fields = new[] { e.Name, e.Metadata?.Title ?? "", e.Metadata?.OriginalTitle ?? "" }.Concat(e.Metadata?.Aliases ?? []).Concat(e.Metadata?.Developers ?? [])
                    .Concat(e.Categories).Concat(combinedTags).Concat(e.Installations.Select(i => i.Folder));
                var preferred = e.Installations.FirstOrDefault(i => i.Id == e.PreferredInstallationId) ?? (e.Installations.Length == 1 ? e.Installations[0] : null);
                built.Add(new(new GameSummary(e.Id, e.Name, e.Status, e.PersonalRating, e.Favorite, string.Join(" · ", e.Categories.Concat(e.CustomTags).Take(4)), e.Installations.Length, e.TotalPlaySeconds) { CoverKey = ImageSourceKeys.For(e), HasLocalCover = e.LocalCover is not null },
                    [.. fields.Select(text.NormalizeSearch)], e.Categories, combinedTags,
                    e.Installations.Length > 0 && string.IsNullOrEmpty(e.Metadata?.CoverUrl),
                    e.Installations.Length > 0 && (preferred?.Executable is null || !preferred.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)), e.AddedAt, e.LastPlayedAt ?? ""));
                allCategories.UnionWith(e.Categories); allTags.UnionWith(combinedTags);
            }
            items = built.MoveToImmutable();
            var comparer = Comparer<string>.Create(text.CompareNames);
            categories = [.. allCategories.Order(comparer)]; tags = [.. allTags.Order(comparer)];
        }
        public LibraryQueryResult Query(LibraryQuery query, CancellationToken cancellation)
        {
            var terms = text.QueryTerms(query.Text);
            double? minimum = query.MinRating.Length == 0 ? null : double.TryParse(query.MinRating, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : double.PositiveInfinity;
            var result = new List<Item>();
            foreach (var item in items)
            {
                cancellation.ThrowIfCancellationRequested(); var s = item.Summary;
                if (query.View switch { "local" => s.InstallationCount == 0, "wishlist" => s.InstallationCount != 0, "unmatched" => !item.Unmatched, "unconfigured" => !item.Unconfigured, _ => false }) continue;
                if (query.Status != "all" && query.Status != s.Status || query.Favorites && !s.Favorite || query.Category.Length > 0 && !item.Categories.Contains(query.Category)
                    || query.Tag.Length > 0 && !item.Tags.Contains(query.Tag) || minimum.HasValue && (!s.Rating.HasValue || s.Rating.Value < minimum.Value)) continue;
                if (terms.All(t => item.Fields.Any(f => f.Contains(t, StringComparison.Ordinal)))) result.Add(item);
            }
            result.Sort((a, b) =>
            {
                var difference = query.Sort switch
                {
                    "name" => text.CompareNames(a.Summary.Name, b.Summary.Name), "rating" => (b.Summary.Rating ?? -1).CompareTo(a.Summary.Rating ?? -1),
                    "playtime" => b.Summary.PlaySeconds.CompareTo(a.Summary.PlaySeconds), "recent" => string.CompareOrdinal(b.Recent, a.Recent), _ => string.CompareOrdinal(b.Added, a.Added)
                };
                if (difference == 0) difference = text.CompareNames(a.Summary.Name, b.Summary.Name);
                return difference != 0 ? difference : text.CompareIds(a.Summary.Id, b.Summary.Id);
            });
            cancellation.ThrowIfCancellationRequested();
            return new([.. result.Select(i => i.Summary)], items.Length, categories, tags);
        }
    }
}
