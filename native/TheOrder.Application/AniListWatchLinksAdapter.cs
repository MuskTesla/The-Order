namespace TheOrder.Application;

public static class AniListWatchLinksAdapter
{
    public static WatchLink[] From(AnimeWatchIdentity identity, AniListDetails? details) => details?.Media.Kind != AniListMediaKind.Anime || identity.AniListId != details.Media.Id ? []
        : details.WatchLinks.Select(x => new WatchLink(identity, x.Site, x.Url, WatchLinkKind.Playback, [], WatchLinkVerification.ProviderListed,
            [new("AniList", details.Media.Url, "原站标为 STREAMING；未核验当前地区与播放权限。")], x.Notes)).ToArray();
}
