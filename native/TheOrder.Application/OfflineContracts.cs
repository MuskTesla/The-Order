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
    // Read-only projection of the stored LibraryEntry.LastPlayedAt; never inferred from other fields.
    public string? LastPlayedAt { get; init; }
    public bool HasPlayHistory { get; init; }
    public string LastPlayedText => LastPlayedNames.Display(LastPlayedAt, HasPlayHistory, DateTimeOffset.Now);
    public override string ToString() => Name;
}
public static class LastPlayedNames
{
    // 从未游玩: no stored time and no history. 时间未知: history exists but the time is missing or unreadable.
    public static string Display(string? value, bool hasHistory, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value)) return hasHistory ? "时间未知" : "从未游玩";
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at)) return "时间未知";
        var local = at.ToOffset(now.Offset); var days = (now.Date - local.Date).Days;
        return days switch { < 0 => local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), 0 => "今天 " + local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), 1 => "昨天", < 7 => $"{days} 天前",
            _ => local.ToString(local.Year == now.Year ? "M月d日" : "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) };
    }
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
