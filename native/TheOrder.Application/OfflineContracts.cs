using System.Collections.Immutable;

namespace TheOrder.Application;

public enum ClientOutcome { Success, Committed, CommittedWithWarning, ValidationFailure, Conflict, Cancelled, IoFailure, ReadOnly, LoadFailure, CandidateStale }
public sealed record ClientResult(ClientOutcome Outcome, string Message)
{
    public bool IsCommitted => Outcome is ClientOutcome.Committed or ClientOutcome.CommittedWithWarning;
}
public sealed record LibraryCapability(bool Loaded, bool CanEdit, string Message);
public sealed record LibraryQuery(string Text = "", string View = "all", string Status = "all", bool Favorites = false, string Category = "", string Tag = "", string MinRating = "", string Sort = "added");
public sealed record GameSummary(string Id, string Name, string Status, double? Rating, bool Favorite, string Labels, int InstallationCount, long PlaySeconds)
{
    public string? CoverKey { get; init; }
    public bool HasLocalCover { get; init; }
    public string StatusText => StatusNames.Display(Status);
    public string RatingText => Rating is null ? "未评分" : $"{Rating:0.#} 分";
    public string FavoriteText => Favorite ? "♥ 收藏" : "";
    public string Subtitle => $"{StatusText} · {RatingText} · {InstallationCount} 个安装";
    public override string ToString() => Name;
}
public static class StatusNames
{
    public static string Display(string value) => value switch { "planned" => "未开始", "playing" => "在玩", "completed" => "已完成", "paused" => "搁置", "dropped" => "放弃", _ => "全部状态" };
}
public sealed record LibraryQueryResult(ImmutableArray<GameSummary> Items, int Total, ImmutableArray<string> Categories, ImmutableArray<string> Tags);
public sealed record PersonalInput(string Name, string Status, string Rating, bool Favorite, string Notes, string Categories, string Tags);
public sealed record SavedFilter(string Id, string Name, LibraryQuery Query);
public sealed record ClientPreferences(NativeTheme Theme = NativeTheme.System, string Layout = "grid", string Sort = "added", ImmutableArray<SavedFilter> SavedFilters = default)
{
    public ImmutableArray<SavedFilter> Filters => SavedFilters.IsDefault ? [] : SavedFilters;
}
public interface IUiPreferencesStore
{
    Task<ClientPreferences> LoadAsync(CancellationToken cancellation);
    Task SaveAsync(ClientPreferences preferences, CancellationToken cancellation);
}
public interface IClientTextRules
{
    string NormalizeSearch(string value);
    ImmutableArray<string> QueryTerms(string value);
    ImmutableArray<string> SplitLabels(string value);
    int CompareNames(string a, string b);
    int CompareIds(string a, string b);
}
public interface ITransactionalLibraryFactory { Task<TransactionalLibrary> OpenAsync(CancellationToken cancellation); }
public interface IOfflineLibraryClient : IAsyncDisposable
{
    LibraryCapability Capability { get; }
    Task<ClientResult> LoadAsync(CancellationToken cancellation);
    Task<LibraryQueryResult> QueryAsync(LibraryQuery query, CancellationToken cancellation);
    Task<GameDetails> DetailsAsync(string id, CancellationToken cancellation);
    Task<ClientResult> SaveAsync(GameDetails details, PersonalInput input, CancellationToken cancellation);
    Task<ClientResult> BatchAsync(ImmutableArray<string> ids, BulkPersonalPatch patch, CancellationToken cancellation);
}
