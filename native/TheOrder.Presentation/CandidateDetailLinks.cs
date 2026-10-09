using TheOrder.Domain;

namespace TheOrder.Presentation;

public sealed record CandidateDetailLink(string Title, string Url);

public static class CandidateDetailLinks
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "www.kungal.com", "kungal.com", "vndb.org", "bgm.tv", "bangumi.tv", "chii.in",
        "store.steampowered.com", "erogamescape.dyndns.org", "howlongtobeat.com", "www.dlsite.com"
    };

    public static bool Allowed(string? value) => value is { Length: > 0 and <= 8192 }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && Hosts.Contains(uri.IdnHost);

    public static IReadOnlyList<CandidateDetailLink> From(WorkMetadata work)
    {
        var links = new List<CandidateDetailLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string title, string? url)
        {
            if (Allowed(url) && seen.Add(url!)) links.Add(new(title, url!));
        }
        Add("打开作品页面", work.WebUrl);
        foreach (var reference in work.References.Take(32))
            Add(reference.Source switch { "vndb" => "在 VNDB 查看", "bangumi" => "在 Bangumi 查看",
                "steam" => "在 Steam 查看", "erogamescape" => "在 ErogameScape 查看",
                "dlsite" => "在 DLsite 查看", _ => "打开来源页面" }, reference.Url);
        return links;
    }
}
