using System.Collections.Immutable;
using TheOrder.Domain;

namespace TheOrder.Application;

// A reverse edge is evidence about this exact pair, never a title-based identity.
public sealed record RelatedSemanticEvidence(string SourceSubjectId, string SubjectId, string ReverseRelation);
public interface IRelatedSemanticReader
{
    Task<ImmutableArray<RelatedSemanticEvidence>> LoadAsync(string sourceSubjectId,
        ImmutableArray<BangumiRelationItem> items, CancellationToken cancellation);
}

// Display-only bridge proof from an explicit KunGal claim and a Bangumi reference.
// These identities do not participate in library matching or personal-data merges.
public sealed record RelatedGameIdentity(string CatalogId, string KunGalGalgameId, string BangumiSubjectId);
public interface IRelatedGameIdentityReader
{
    Task<ImmutableArray<RelatedGameIdentity>> LoadRelatedIdentitiesAsync(ImmutableArray<CatalogEntityRef> games, CancellationToken cancellation);
}

public sealed record RelatedWorkCard(string Key, string Provider, string Id, BangumiRelationKind Kind,
    string Title, string RawRelation, string Label, string GroupName, int Priority,
    string? CoverUrl, string Url, CatalogEntityRef? Catalog, BangumiRelationItem? Bangumi)
{
    public string Evidence { get; init; } = "";
    public string? LinkedBangumiSubjectId { get; init; }
}

public static class RelatedWorksProjection
{
    private sealed record Classification(string Label, string Group, int Priority, string Compatibility = "");

    public static ImmutableArray<RelatedWorkCard> Build(IEnumerable<CatalogEntityRef> catalogRows,
        IEnumerable<BangumiRelationItem> bangumiRows, IEnumerable<RelatedSemanticEvidence>? semanticEvidence = null,
        IEnumerable<RelatedGameIdentity>? gameIdentities = null)
    {
        var evidence = (semanticEvidence ?? []).Where(e =>
            BangumiRelationIdentityResolver.ValidSubjectId(e.SourceSubjectId) && BangumiRelationIdentityResolver.ValidSubjectId(e.SubjectId))
            .GroupBy(e => e.SubjectId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Distinct().ToArray(), StringComparer.Ordinal);
        var result = new List<RelatedWorkCard>();
        foreach (var group in catalogRows.Where(CatalogRelatedWorks.Eligible).GroupBy(row => row.Id, StringComparer.Ordinal))
        {
            var rows = group.ToArray(); var first = rows[0];
            var sourceEdges = rows.SelectMany(row => (row.RelationEvidence ?? []).Append(new(row.Relation ?? "", row.Subtitle ?? ""))).Distinct().ToArray();
            var relation = sourceEdges.Select(edge => edge.Type).Distinct(StringComparer.Ordinal).ToArray();
            var c = Combine(sourceEdges.Select(edge => CatalogClassification(edge.Type, edge.Phrase)));
            result.Add(new("nextmoe:" + first.Id, "NextMoe · KunGal", first.Id, BangumiRelationKind.Game, first.Name,
                string.Join(" / ", relation.Where(s => s.Length > 0)), c.Label, c.Group, c.Priority, first.ImageUrl,
                "https://www.kungal.com/galgame/" + first.KunGalGalgameId, first, null)
            { Evidence = "NextMoe 关系：" + string.Join("；", sourceEdges.Select(edge => $"{(edge.Phrase.Length > 0 ? edge.Phrase : "未标注")} [{(edge.Type.Length > 0 ? edge.Type : "未标注")}]").Distinct(StringComparer.Ordinal))
                + "。目录编号 " + first.Id + "；KunGal 页面 " + first.KunGalGalgameId + "。" });
        }
        foreach (var group in bangumiRows.GroupBy(row => row.Id, StringComparer.Ordinal))
        {
            var rows = group.ToArray(); var first = rows[0];
            var raw = rows.Select(row => row.Relation).Distinct(StringComparer.Ordinal).ToArray();
            var uniqueKind = rows.Select(row => row.Kind).Distinct().Count() == 1;
            var c = !uniqueKind ? Unknown("媒体类型待核对")
                : Combine(rows.Select(row => BangumiClassification(row.Kind, row.Relation)));
            var note = "Bangumi 关系：" + string.Join(" / ", raw.Select(s => s.Length > 0 ? s : "未标注")) + "。条目 " + first.Id + "。";
            if (evidence.TryGetValue(first.Id, out var reverse))
            {
                note += " 反向关系：" + string.Join("；", reverse.Select(e => $"{e.SubjectId} → {e.SourceSubjectId}：{e.ReverseRelation}")) + "。";
                // Only a consistent exact reverse edge can refine the coarse 动画 label.
                // 游戏 does not prove a direct adaptation, and never receives that label.
                var reverseRelations = reverse.Select(e => e.ReverseRelation).Distinct(StringComparer.Ordinal).ToArray();
                if (uniqueKind && first.Kind == BangumiRelationKind.Anime && raw.Length == 1 && raw[0] == "动画"
                    && reverse.Select(e => e.SourceSubjectId).Distinct(StringComparer.Ordinal).Count() == 1)
                {
                    if (reverseRelations.Length == 1 && reverseRelations[0] == "角色出演")
                        c = new("角色出演 · 跨作品关联", "跨作品关联", 60);
                    else if (reverseRelations.Length == 1 && reverseRelations[0] == "游戏")
                        c = new("动画关联", "动画作品", 25);
                    else if (reverseRelations.Length > 1) c = Unknown("关系待核对");
                }
            }
            result.Add(new("bangumi:" + first.Id, "Bangumi", first.Id, first.Kind, first.Title,
                string.Join(" / ", raw), c.Label, c.Group, c.Priority, first.CoverUrl, first.Url, null, first) { Evidence = note });
        }
        PreferKunGal(result, gameIdentities ?? []);
        // Stable input order for equally specific facts; no title-based ranking or merging.
        return [..result.OrderBy(card => card.Priority)];
    }

    private static void PreferKunGal(List<RelatedWorkCard> cards, IEnumerable<RelatedGameIdentity> identities)
    {
        var proofs = identities.Distinct().Where(p =>
            BangumiRelationIdentityResolver.ValidSubjectId(p.CatalogId) &&
            BangumiRelationIdentityResolver.ValidSubjectId(p.KunGalGalgameId) &&
            BangumiRelationIdentityResolver.ValidSubjectId(p.BangumiSubjectId)).ToArray();
        foreach (var proof in proofs)
        {
            // Ambiguous mappings remain visible. A shared name or numeric ID is not proof.
            if (proofs.Count(p => p.CatalogId == proof.CatalogId) != 1 ||
                proofs.Count(p => p.BangumiSubjectId == proof.BangumiSubjectId) != 1) continue;
            var preferred = cards.SingleOrDefault(c => c.Catalog?.Id == proof.CatalogId && c.Kind == BangumiRelationKind.Game);
            var bridge = cards.SingleOrDefault(c => c.Bangumi?.Id == proof.BangumiSubjectId && c.Kind == BangumiRelationKind.Game);
            if (preferred?.Catalog?.KunGalGalgameId != proof.KunGalGalgameId || bridge is null ||
                bridge.Label == "媒体类型待核对") continue;
            var index = cards.IndexOf(preferred);
            cards[index] = preferred with
            {
                LinkedBangumiSubjectId = bridge.Id,
                Evidence = preferred.Evidence + $" 同作品编号关联：NextMoe {proof.CatalogId} / KunGal {proof.KunGalGalgameId} → Bangumi {proof.BangumiSubjectId}。" + bridge.Evidence
            };
            cards.Remove(bridge);
        }
    }

    private static Classification Combine(IEnumerable<Classification> source)
    {
        var values = source.Distinct().ToArray();
        if (values.Length == 1) return values[0];
        // A later work can also be an explicit side story or Fandisc. These are
        // compatible source assertions, not a reason to bury the work as unknown.
        // Opposite directions, unspecific phrases and crossovers stay conservative.
        if (values.All(value => value.Compatibility is "sequel-later" or "derived"))
            return new(string.Join(" / ", values.Select(value => value.Label).Distinct(StringComparer.Ordinal)),
                values.Any(value => value.Compatibility == "sequel-later") ? "前后作与衍生" : "衍生与外传",
                values.Max(value => value.Priority));
        return Unknown("多种关系 · 查看来源");
    }
    private static Classification Unknown(string label = "关系未细分") => new(label, "其他关联", 80);
    private static Classification CatalogClassification(string? type, string? phrase) => type switch
    {
        "adaptation_of" when phrase == "被改编为" => new("改编作品", "改编与续作", 0),
        "adaptation_of" when phrase == "改编自" => new("改编来源", "改编与续作", 5),
        "sequel_of" => new(phrase is { Length: > 0 } ? phrase : "前后作关联", "前后作关联", 40,
            phrase is "有续作" or "续作" or "续集" ? "sequel-later" : ""),
        "fandisc_of" => new(phrase is { Length: > 0 } ? phrase : "Fandisc 关联", "衍生与外传", 40,
            phrase is "有 Fandisc" or "有Fandisc" or "Fandisc" ? "derived" : ""),
        "side_story_of" => new(phrase is { Length: > 0 } ? phrase : "正篇 / 外传关联", "衍生与外传", 40,
            phrase is "有外传" or "外传" or "番外篇" ? "derived" : ""),
        "alternative_version" => new(phrase is { Length: > 0 } ? phrase : "不同版本", "其他版本", 45),
        "alternative_setting" => new(phrase is { Length: > 0 } ? phrase : "不同世界观", "跨作品关联", 60),
        "same_setting" or "same_world" => new(phrase is { Length: > 0 } ? phrase : "同世界观", "跨作品关联", 60),
        "same_series" => new(phrase is { Length: > 0 } ? phrase : "同系列", "同系列关联", 55),
        "shares_character" => new("角色出演 · 跨作品关联", "跨作品关联", 60),
        _ => Unknown(phrase is { Length: > 0 } ? phrase : "关系未细分")
    };
    private static Classification BangumiClassification(BangumiRelationKind kind, string relation) => relation switch
    {
        "续集" or "前传" => new(relation, "改编与续作", 5),
        "原声集" when kind == BangumiRelationKind.Music => new("原声音乐", "原声与主题音乐", 10),
        "片头曲" or "片尾曲" or "主题曲" or "插入歌" when kind == BangumiRelationKind.Music => new(relation, "原声与主题音乐", 20),
        "印象曲" when kind == BangumiRelationKind.Music => new(relation, "衍生音乐", 40),
        "广播剧" when kind == BangumiRelationKind.Music => new("广播剧", "广播剧与衍生音乐", 40),
        "外传" or "番外篇" or "总集篇" or "Fandisc" => new(relation, "衍生与外传", 40),
        "角色出演" => new("角色出演 · 跨作品关联", "跨作品关联", 60),
        "相同世界观" or "不同世界观" or "联动" => new(relation, "跨作品关联", 60),
        "不同演绎" => new(relation, "其他版本", 45),
        "画集" => new("画集", "书籍衍生", 40),
        "动画" when kind == BangumiRelationKind.Anime => new("动画关联 · 关系未细分", "关系待细分", 70),
        "书籍" or "游戏" => Unknown(relation + "关联 · 关系未细分"),
        "" => Unknown(),
        "其他" => Unknown("其他关联"),
        _ => Unknown(relation)
    };
}
