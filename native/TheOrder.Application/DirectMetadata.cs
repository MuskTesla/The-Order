using System.Collections.Immutable;
using System.Text.RegularExpressions;
using TheOrder.Domain;

namespace TheOrder.Application;

public interface IDirectMetadataSource
{ Task<WorkMetadata> FetchAsync(string source, string id, bool allowAdult, CancellationToken cancellation); }
public static class DirectMetadataIdentity
{
    public static string Normalize(string source, string input)
    {
        var value = input.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Port != 443 || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new InvalidDataException("请输入作品编号或官方作品链接。");
            value = source switch
            {
                "bangumi" when uri.Host is "bgm.tv" or "bangumi.tv" or "chii.in" && Regex.IsMatch(uri.AbsolutePath, "^/subject/[1-9][0-9]{0,9}/?$") => uri.AbsolutePath.Split('/')[2],
                "vndb" when uri.Host == "vndb.org" && Regex.IsMatch(uri.AbsolutePath, "^/v[1-9][0-9]{0,9}/?$") => uri.AbsolutePath.Trim('/'),
                _ => throw new InvalidDataException("作品链接与选择的资料源不一致。")
            };
        }
        if (source == "vndb" && Regex.IsMatch(value, "^[1-9][0-9]{0,9}$")) value = "v" + value;
        if (!(source == "bangumi" && Regex.IsMatch(value, "^[1-9][0-9]{0,9}$") || source == "vndb" && Regex.IsMatch(value, "^v[1-9][0-9]{0,9}$"))) throw new InvalidDataException("作品编号无效。");
        return value;
    }
}
public sealed class MetadataSourceClient(OfflineLibraryClient library, IDirectMetadataSource source)
{
    public Task<WorkMetadata> PreviewAsync(string provider, string id, bool allowAdult, CancellationToken cancellation)
        => source.FetchAsync(provider, DirectMetadataIdentity.Normalize(provider, id), allowAdult, cancellation);
    public Task<ClientResult> ApplyAsync(LibraryExtrasDetails details, string provider, string id, WorkMetadata preview,
        ImmutableArray<string> fields, bool fillOnly, CancellationToken cancellation)
    {
        var normalized = DirectMetadataIdentity.Normalize(provider, id);
        if (preview.References.Count(r => r.Source == provider && r.Id == normalized) != 1 || preview.Id != provider + ":" + normalized)
            return Task.FromResult(new ClientResult(ClientOutcome.ValidationFailure, "预览作品与选定资料源不一致，请重新读取。"));
        return LibraryExtrasClient.Result(() => library.Required().ApplySourceAsync(details.Token, new(provider, normalized, preview, fields, fillOnly), cancellation));
    }
    public Task<ClientResult> RemoveAsync(LibraryExtrasDetails details, string provider, CancellationToken cancellation)
        => LibraryExtrasClient.Result(() => library.Required().RemoveSourceAsync(details.Token, provider, cancellation));
}
