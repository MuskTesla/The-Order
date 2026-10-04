using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Infrastructure.Recognition;

// Stateless suggestion computation: eligibility/high confidence never grants write authority.
public sealed class EvidenceRanking : IRecognitionRanker
{
    private static string Compact(string s)=>TitleMatching.Compact(TitleMatching.Normalize(s));
    private static bool Program(string s)=>s is "product-name" or "file-description";
    private static bool Anchor(string s)=>s is "folder" or "custom-name" or "product-name" or "file-description" or "executable-name" or "manual-title" or "config-title";
    private static bool Proof(string s)=>Anchor(s)&&s!="custom-name";
    private static bool OfficialKind(string s)=>s is "official" or "original" or "latin";
    private static string Key(WorkReference r)=>r.Source=="vndb"&&TitleMatching.Rx(@"^v[1-9]\d*$").IsMatch(r.Id)||(r.Source is "bangumi" or "steam")&&TitleMatching.Rx(@"^[1-9]\d*$").IsMatch(r.Id)?r.Source+":"+r.Id:"";
    private static Dictionary<string,HashSet<string>> Grouped(IEnumerable<string> refs)=>refs.GroupBy(s=>s.Split(':')[0],StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToHashSet(StringComparer.Ordinal),StringComparer.Ordinal);
    private static bool SameCompany(string a,string b)
    {
        static string Brand(string s)=>TitleMatching.Compact(TitleMatching.Rx("株式会社|有限会社").Replace(TitleMatching.Rx(@"\b(?:copyright|corporation|company|co|ltd|limited|inc|llc|corp|\d{4})\b").Replace(TitleMatching.Normalize(s),""),""));
        var l=Brand(a);var r=Brand(b);return l.Length>=3&&r.Length>=3&&l==r;
    }
    private sealed record Ranked(RankedRecognitionCandidate Candidate,bool Eligible,bool FullCanonical,bool Physical,string[] Vndb);
    public ImmutableArray<RankedRecognitionCandidate> Rank(RecognitionEvidence evidence,IEnumerable<WorkMetadata> works,
        IReadOnlyDictionary<string,ImmutableArray<ReleaseIdentity>>? releases=null,CancellationToken cancellation=default)
    {
        if(evidence.Titles.Length>40||evidence.References.Length>40)throw new InvalidDataException("识别证据超过上限。");
        var trusted=evidence.References.Where(r=>r.Source!="sidecar").Select(r=>r.Value).Distinct(StringComparer.Ordinal).ToArray();
        var weak=evidence.References.Where(r=>r.Source=="sidecar").Select(r=>r.Value).Distinct(StringComparer.Ordinal).ToArray();
        var expected=Grouped(trusted);var local=evidence.Titles.Where(t=>t.Source!="sidecar").ToArray();
        var folders=local.Where(t=>t.Source is "folder" or "custom-name").ToArray();var programs=local.Where(t=>Program(t.Source)).ToArray();
        var resources=local.Where(t=>t.Source is "executable-name" or "archive-name" or "readme-title" or "manual-title" or "config-title").ToArray();
        var anchors=local.Where(t=>Anchor(t.Source)).ToArray();bool inputConflict=expected.Values.Any(v=>v.Count>1);
        // Last value for a duplicate ID, matching the existing Map replacement semantics.
        var unique=new Dictionary<string,WorkMetadata>(StringComparer.Ordinal);
        int inspected=0;
        foreach(var work in works){cancellation.ThrowIfCancellationRequested();if(++inspected>1024)throw new InvalidDataException("识别候选输入超过上限。");unique[work.Id]=work;if(unique.Count>128)throw new InvalidDataException("识别候选超过128项上限。");}
        List<Ranked> ranked=[];
        foreach(var work in unique.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            var refs=work.References.Select(Key).Where(r=>r.Length>0).ToHashSet(StringComparer.Ordinal);var actual=Grouped(refs);
            bool referenceConflict=expected.Any(pair=>actual.TryGetValue(pair.Key,out var values)&&!values.Overlaps(pair.Value));
            var provenance=work.TitleEvidence??[];
            bool Machine(string value){var p=provenance.Where(t=>Compact(t.Text)==Compact(value)).ToArray();return p.Length>0&&p.All(t=>t.IsMachine);}
            var primary=new[]{work.Title,work.OriginalTitle}.Where(value=>{var p=provenance.Where(t=>Compact(t.Text)==Compact(value)).ToArray();return p.Length==0||p.Any(t=>!t.IsMachine&&OfficialKind(t.Kind));}).ToArray();
            var official=provenance.Where(t=>!t.IsMachine&&OfficialKind(t.Kind)).SelectMany(t=>{var parts=TitleMatching.Rx(@"\s*[|｜]\s*").Split(t.Text).Where(s=>s.Length>0).ToArray();return parts.Length is >1 and <=5?new[]{t.Text}.Concat(parts):[t.Text];});
            var canonical=primary.Concat(official).Where(s=>s.Length>0).Distinct(StringComparer.Ordinal).ToArray();
            var all=canonical.Concat(work.Aliases.Where(n=>!Machine(n))).Distinct(StringComparer.Ordinal).ToArray();
            Dictionary<(string,string),TitleComparison> comparisons=[];
            TitleComparison Compare(string a,string b){cancellation.ThrowIfCancellationRequested();var key=(a,b);if(!comparisons.TryGetValue(key,out var c))comparisons[key]=c=TitleMatching.Compare(a,b);return c;}
            bool Exact(string a,string b){var c=Compare(a,b);return c.Exact&&c.Distinctive&&c.Version=="same"&&c.Conflict is null;}
            List<(string Source,string[] Refs)> bridges=[];
            foreach(var proof in evidence.IndexProofs.IsDefault?[]:evidence.IndexProofs)
            {
                if(!Proof(proof.Source)||Machine(proof.ObservedTitle)||!evidence.Titles.Any(t=>t.Source==proof.Source&&Exact(t.Value,proof.ObservedTitle))||!proof.RecordNames.Any(n=>Exact(n,proof.ObservedTitle))
                    ||!canonical.Any(n=>proof.RecordNames.Any(r=>Exact(n,r)))||Grouped(proof.References).Values.Any(v=>v.Count>1))continue;
                var hits=proof.References.Where(refs.Contains).ToArray();if(hits.Length>0)bridges.Add((proof.Source,hits));
            }
            var bridgeRefs=bridges.SelectMany(b=>b.Refs).Distinct(StringComparer.Ordinal).ToArray();
            bool directoryBridge=bridges.Any(b=>b.Source is "folder" or "product-name" or "file-description" or "executable-name");
            var matched=trusted.Where(refs.Contains).Concat(bridgeRefs).Distinct(StringComparer.Ordinal).ToArray();
            CandidateExplanation? Best(IEnumerable<LocalTitleEvidence> titles,IEnumerable<string> names)=>titles.SelectMany(t=>names.Select(n=>{var c=Compare(t.Value,n);return new CandidateExplanation(t.Source,t.Value,n,c.Score,c.Exact,c.Version,c.Conflict);})).OrderByDescending(c=>c.Similarity).FirstOrDefault();
            var folder=Best(folders,canonical);var pe=Best(programs,canonical);var resource=Best(resources,canonical);
            var folderAny=Best(folders,all);var peAny=Best(programs,all);var resourceAny=Best(resources,all);var installationAny=Best(anchors.Where(t=>t.Source is not ("manual-title" or "config-title")),all);
            bool Distinct(CandidateExplanation? c)=>c is not null&&Compare(c.ObservedTitle,c.MatchedTitle).Distinctive;
            bool full=new[]{folder,pe,resource}.Any(c=>c?.Exact==true&&Distinct(c));
            bool precise=resources.Any(t=>t.Source=="executable-name"&&TitleMatching.Rx(@"^[a-z]{4,}\d{2,}$",true).IsMatch(Compact(t.Value))&&canonical.Any(n=>Compact(n).StartsWith(Compact(t.Value),StringComparison.Ordinal)&&Compare(t.Value,n).Version!="conflict"));
            bool company=evidence.Companies.Any(c=>work.Developers.Any(d=>SameCompany(c,d)));
            bool titleConflict=local.Any(t=>primary.Any(n=>{var c=Compare(t.Value,n);return c.Version=="conflict"&&c.Score>=.12;}));
            bool latin=programs.Concat(folders).Any(t=>Compare(t.Value,t.Value).Distinctive&&TitleMatching.Rx(@"^[\x20-\x7e]+$").IsMatch(t.Value)&&primary.All(n=>TitleMatching.Rx(@"^[\x20-\x7e]+$").IsMatch(n))&&primary.Select(n=>Compare(t.Value,n).Score).DefaultIfEmpty(double.NegativeInfinity).Max()<.25);
            double bestScore=new[]{folder?.Similarity??0,pe?.Similarity??0,resource?.Similarity??0}.Max();
            double bestAny=new[]{folderAny?.Similarity??0,peAny?.Similarity??0,resourceAny?.Similarity??0}.Max();
            bool corroborates=bestAny>=.65||new[]{folderAny,peAny,resourceAny}.Any(c=>c?.Exact==true);
            bool physical=evidence.References.Any(r=>r.Source is "steam-file" or "steam-config")&&matched.Any(r=>evidence.References.Any(e=>e.Value==r&&e.Source is "steam-file" or "steam-config"));
            bool canonicalSupport=new[]{folder,pe,resource}.Any(c=>c?.Exact==true);
            bool anchor=(folderAny?.Similarity??0)>=.5||(peAny?.Similarity??0)>=.5||precise||directoryBridge;
            bool physicalCorroboration=physical&&((installationAny?.Similarity??0)>=.65||installationAny?.Exact==true);
            bool indexBridge=bridgeRefs.Length>0&&anchor;bool indexed=evidence.References.Any(r=>r.Source=="title-index"&&refs.Contains(r.Value))||indexBridge;
            var fullLocal=anchors.Where(t=>canonical.Any(n=>Exact(t.Value,n))).ToArray();
            var releaseRows=releases is not null&&releases.TryGetValue(work.Id,out var available)&&!available.IsDefault?available:[];
            bool confirmed=indexed&&anchor&&fullLocal.Length>0&&releaseRows.Any(r=>r.WorkId==work.Id&&new[]{"default","digital","physical"}.Contains(TitleMatching.Lower(r.Kind))&&fullLocal.Any(t=>new[]{r.Title,r.OriginalTitle}.Any(n=>Exact(t.Value,n))));
            double score=bestScore*.89;string reason=bestAny>0?"名称相近，继续核对作品、章节和版本":"缺少对应的本地作品线索";bool eligible=false;int level=1;
            if(physicalCorroboration){eligible=true;level=3;score=.99;reason="本地 Steam 编号与可读作品名称相互印证";}
            else if(confirmed){eligible=true;level=2;score=.985;reason="本地完整标题、索引编号与正式发行名称一致";}
            else if(indexBridge){eligible=true;level=full?1:2;score=.98;reason="同一索引记录连接本地名称、官方名称与作品编号";}
            else if(matched.Length>0&&corroborates&&(canonicalSupport||precise)&&anchor){eligible=true;score=.97+Math.Min(.02,matched.Length*.005);reason="本地"+(matched.Any(r=>r.StartsWith("steam:",StringComparison.Ordinal))?"编号与标题":"标题索引编号与完整资料")+"一致";}
            else if(trusted.Length==0&&full){bool ef=folder?.Exact==true&&Distinct(folder),ep=pe?.Exact==true&&Distinct(pe),er=resource?.Exact==true&&Distinct(resource)&&resource.Source=="readme-title";eligible=ef||ep&&(folder?.Similarity??0)>=.5||er&&(folder?.Similarity??0)>=.65;score=eligible?.95:.88;reason=ep?"程序内的完整作品名称与详情一致":ef?"完整官方标题唯一对应":"本地资料中的完整标题一致";}
            else if(trusted.Length==0&&precise&&(folderAny?.Similarity??0)>=.5){eligible=true;score=.955;reason="主程序名称中的作品编号与官方完整标题对应";}
            else {score=Math.Max(score,bestAny*.82);if(full)score=Math.Max(score,.93);if(weak.Any(refs.Contains)&&corroborates){score=Math.Max(score,.87);reason="旧识别编号与部分名称一致，仍需独立确认";}}
            if(company&&score>0)score=Math.Min(.99,score+.01);
            List<string> conflicts=[];if(titleConflict)conflicts.Add("章节或版本冲突");if(latin)conflicts.Add("本地名称与主标题矛盾");if(referenceConflict||inputConflict)conflicts.Add("独立本地编号存在冲突");if(evidence.AuxiliaryDirectory)conflicts.Add("补丁或备份目录");
            if(conflicts.Count>0){eligible=false;level=0;score=Math.Min(score,.6);reason=evidence.AuxiliaryDirectory?"位于补丁或备份目录，请选择实际游戏根目录":inputConflict||referenceConflict?"独立本地编号存在冲突":"本地程序或目录指向不同的作品、章节或版本";}
            ranked.Add(new(new(work,Math.Max(0,Math.Min(.99,score)),reason,false,conflicts.Count>0,[..conflicts],level,[..matched],[..new[]{folder,pe,resource,folderAny,peAny,resourceAny}.OfType<CandidateExplanation>().Distinct()]),eligible,full,physical,[..refs.Where(r=>r.StartsWith("vndb:",StringComparison.Ordinal))]));
        }
        // Remote catalog IDs are decimal; ordinal ordering is culture independent.
        ranked=ranked.OrderByDescending(r=>r.Eligible).ThenByDescending(r=>r.Eligible?r.Candidate.EvidenceLevel:0).ThenByDescending(r=>r.Candidate.Confidence).ThenBy(r=>r.Candidate.Work.Id,StringComparer.Ordinal).ToList();
        if(ranked.FirstOrDefault() is {Eligible:true} best)
        {
            var peers=ranked.Skip(1).Where(r=>r.Candidate.EvidenceLevel>=best.Candidate.EvidenceLevel).ToArray();
            var contender=peers.FirstOrDefault(r=>r.Eligible||r.Candidate.Confidence>=.85||r.Candidate.MatchedReferences.Length>0);
            double margin=contender is null?1:best.Candidate.Confidence-contender.Candidate.Confidence;
            bool split=best.Candidate.MatchedReferences.Length>0&&ranked.Count(r=>r.Candidate.MatchedReferences.Length>0)>1;
            bool same=!best.Physical&&best.FullCanonical&&peers.Any(r=>r.FullCanonical&&r.Candidate.Confidence>=.9);
            bool different=best.Candidate.EvidenceLevel==2&&best.Vndb.Length>0&&ranked.Skip(1).Any(r=>r.Vndb.Length>0&&!r.Vndb.Intersect(best.Vndb).Any()&&(r.FullCanonical||r.Eligible||r.Candidate.MatchedReferences.Length>0));
            ranked[0]=best with {Candidate=best.Candidate with {HighConfidence=!split&&!same&&!different&&margin>=.075,Reason=!split&&!same&&!different&&margin>=.075?best.Candidate.Reason:split||different?"来源编号同时支持多个作品，需要核对":"前两候选证据接近，需要补充线索"}};
        }
        cancellation.ThrowIfCancellationRequested();return [..ranked.Select(r=>r.Candidate)];
    }
}
