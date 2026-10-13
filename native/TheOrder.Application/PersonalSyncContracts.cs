using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

public enum PersonalSyncDirection { Off, Import, Upload, Bidirectional }
public enum PersonalSyncKind { Playtime, State, Folder, Rating }
public sealed record CloudPlaytime(string WorkId, long Minutes);
public sealed record CloudWorkState(string WorkId, string State, string? Completion, string UpdatedAt);
public sealed record CloudFolder(string Id, string Name, string OwnerId, int Items, string UpdatedAt);
public sealed record CloudRating(string WorkId, int Overall, string UpdatedAt);
public sealed record CloudPage<T>(ImmutableArray<T> Items, string? NextCursor, bool HasMore);
public sealed record PersonalWrite(string WorkId, long? Minutes = null, string? State = null, string? Completion = null);
public sealed record PersonalWriteResult(string WorkId, bool Success, int Status, string? Code);
public interface INextMoePersonalApi
{
    Task<CloudPage<CloudPlaytime>> PlaytimesAsync(string? cursor, CancellationToken cancellation);
    Task<CloudPage<CloudWorkState>> StatesAsync(string? cursor, CancellationToken cancellation);
    Task<CloudPage<CloudRating>> RatingsAsync(string? cursor, CancellationToken cancellation);
    Task<CloudPage<CloudFolder>> FoldersAsync(string? cursor, CancellationToken cancellation);
    Task<CloudPage<string>> FolderItemsAsync(string folderId, string? cursor, CancellationToken cancellation);
    Task<ImmutableArray<PersonalWriteResult>> WriteAsync(PersonalSyncKind kind, string? folderId, ImmutableArray<PersonalWrite> items, string idempotencyKey, CancellationToken cancellation);
    Task<CloudFolder> CreateFolderAsync(string name, string idempotencyKey, CancellationToken cancellation);
    Task DeleteFolderItemAsync(string folderId, string workId, CancellationToken cancellation);
}
public interface ICatalogSyncIdentities { Task<ImmutableArray<string>> ConfirmWorkIdsAsync(ImmutableArray<string> ids, CancellationToken cancellation); }
public sealed record PersonalSyncSettings(PersonalSyncDirection Playtime = PersonalSyncDirection.Off, PersonalSyncDirection State = PersonalSyncDirection.Off,
    PersonalSyncDirection Folders = PersonalSyncDirection.Off, bool ReadRatings = false, bool Automatic = false);
public sealed record SyncWorkCheckpoint(string EntryId, string WorkId, long LocalSeconds, long? CloudMinutes,
    long? AgreedMinutes, string LocalState, string? CloudState, string? Completion, string[] SessionIds, string UpdatedAt, long CarriedSeconds = 0);
// Imported minutes/ratings have an independent origin; they never masquerade as timestamped sessions.
public sealed record ImportedPersonalFacts(string WorkId, long? Minutes = null, int? Rating = null, string? State = null, string? Completion = null, string? FetchedAt = null);
public sealed record SyncFolderMapping(string FolderId, string LocalCollectionId, string Name, string[] Members, string[] CloudBaseline,
    string? Cursor = null, string? UpdatedAt = null, bool Authorized = false);
public sealed record PendingPersonalWrite(string Id, PersonalSyncKind Kind, string? FolderId, PersonalWrite[] Items, string CreatedAt, PersonalWrite[]? Before = null);
public sealed record PendingFolderCreation(string Id, string Name, string[] LocalMembers, string CreatedAt);
public sealed record AccountSyncState(string AccountId, string ClientId, PersonalSyncSettings Settings,
    SyncWorkCheckpoint[] Works, SyncFolderMapping[] Folders, ImportedPersonalFacts[] Imported, PendingPersonalWrite[] Pending,
    string? LastSuccess = null, string? PlaytimeCursor = null, string? StateCursor = null, string? RatingCursor = null, PendingFolderCreation[]? FolderCreations = null);
public interface IPersonalSyncStore
{
    Task<AccountSyncState?> LoadAsync(string accountId, string clientId, CancellationToken cancellation);
    Task SaveAsync(AccountSyncState state, CancellationToken cancellation);
}
public sealed record PersonalSyncRow(string Id, PersonalSyncKind Kind, string WorkId, string? EntryId,
    string Title, string LocalValue, string CloudValue, bool Conflict, string Explanation,
    PersonalWrite? Upload, ImportedPersonalFacts? Import, string? LocalStateToApply = null, string? FolderId = null, bool DeleteCloud = false, PersonalSyncChoice? Recommended = null, bool DeleteLocalMembership = false);
public sealed record PersonalSyncPreview(Guid Id, OnlineAuthSnapshot Auth, string AccountId, PersonalSyncSettings Settings,
    ImmutableArray<LibraryEntry> LocalSnapshot, ImmutableArray<PersonalSyncRow> Rows, ImmutableArray<CloudFolder> CloudFolders,
    int LocalCount, int CloudCount, DateTimeOffset CreatedAt);
public sealed record PersonalSyncResult(int Completed, int Failed, int Conflicts, string Message);
public enum PersonalSyncChoice { Skip, Import, Upload }
public interface IPersonalSyncLibrary
{
    Task<ImmutableArray<LibraryEntry>> SnapshotAsync(CancellationToken cancellation);
    Task ApplyStateAsync(LibraryEntry expected, string status, CancellationToken cancellation);
    Task<string> AddWishlistAsync(WorkMetadata metadata, CancellationToken cancellation);
}
