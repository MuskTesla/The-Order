using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TheOrder.Application;
using TheOrder.Domain;
using static TheOrder.Infrastructure.Online.RemoteWorkDto;
namespace TheOrder.Infrastructure.Online;
internal static class CatalogDisplayDto
{
    internal static JsonElement PreferredIntro(IEnumerable<JsonElement> source)=>source.Where(i=>Text(i,"value").Length>0)
        .OrderBy(i=>CatalogLanguage.LanguageRank(Text(i,"lang"), "zh-Hans"))
        .ThenBy(i=>Field(i,"is_machine").ValueKind==JsonValueKind.True?1:0)
        .ThenBy(i=>Text(i,"source") is "kungal" or "curated"?0:1).FirstOrDefault();
    internal static CatalogLocalizedText[] Introductions(JsonElement row) => Rows(row,"intros").Take(64)
        .Where(i=>Text(i,"value").Length is >0 and <=500000)
        .Select(i=>new CatalogLocalizedText(Text(i,"lang"),Text(i,"value"),Text(i,"source"),Field(i,"is_machine").ValueKind==JsonValueKind.True)).ToArray();
    internal static CatalogLocalizedText[] Names(JsonElement row)
    {
        var result = new List<CatalogLocalizedText>(); var localized=Field(row,"localized");
        if(localized.ValueKind==JsonValueKind.Object) foreach(var p in localized.EnumerateObject().Take(32))
            if(Text(p.Value,"value") is {Length:>0 and <=2000} name) result.Add(new(p.Name,name,IsMachine:Field(p.Value,"is_machine").ValueKind==JsonValueKind.True));
        foreach(var title in Rows(row,"titles").Take(128).Where(t=>Text(t,"title_kind")=="official"))
            if(Text(title,"title") is {Length:>0 and <=2000} name) result.Add(new(Text(title,"lang"),name,IsMachine:Field(title,"is_machine").ValueKind==JsonValueKind.True));
        // Work summaries use olang; entities use lang. Keep the actual original
        // separately from translated names and never manufacture a kanji reading.
        if(Text(row,"display_name") is {Length:>0 and <=2000} original)
            result.Add(new(Text(row,"lang") is {Length:>0} lang ? lang : Text(row,"olang"),original,"original"));
        if(Text(row,"latin") is {Length:>0 and <=2000} latin) result.Add(new("en",latin,"romanization"));
        return result.DistinctBy(n=>(n.Language,n.Value,n.Source)).Take(128).ToArray();
    }
    private static CatalogEntityRef Ref(JsonElement row,CatalogEntityKind kind,string? subtitle=null,string? image=null)=>new CatalogEntityRef(kind,Text(row,"id"),Localized(row),subtitle,image) { Names=Names(row) };
    private static ImmutableArray<CatalogEntityRef> Entities(JsonElement root,string block,CatalogEntityKind kind)=>
        [..Rows(root,block).Take(500).Where(r=>ValidId(Text(r,"id"))).Select(r=>Ref(r,kind)).DistinctBy(r=>r.Id)];
    internal static ImmutableArray<CatalogCharacter> Characters(JsonElement work,bool explicitContent)
    {
        var groups=CatalogRosterDto.Groups(work);
        return [..groups.Select(g=> {
            var best=g.OrderByDescending(c=>Image(Field(c,"image")) is not null?4:0).ThenByDescending(c=>Text(c,"roster_role") is "main" or "secondary" or "appears"?2:0).ThenByDescending(c=>Text(c,"identity").Length>0?1:0).First();
            var spoiler=g.Select(c=>Text(c,"spoiler")).OrderByDescending(s=>s switch{"major"=>2,"minor"=>1,_=>0}).First();
            var image=Field(best,"image");var portrait=explicitContent||Text(image,"sexual")!="explicit"?Image(image):null;
            var voices=CatalogRosterDto.Voices(g.OrderByDescending(c=>c.Equals(best)).SelectMany(c=>Rows(c,"voices"))).Select(v=>Ref(v,CatalogEntityKind.CreditName));
            return new CatalogCharacter(Ref(best,CatalogEntityKind.Character,image:portrait),Text(best,"display_name"),Text(best,"roster_role"),spoiler,[..voices]);
        })];
    }
    internal static CatalogPresentation Work(JsonElement work,bool explicitContent,string introSource)
    {
        var art=new List<CatalogArt>();
        void Art(JsonElement image,string kind,string? caption=null) { if(Image(image) is { } url&&Text(image,"sexual")!="explicit"&&Field(image,"width").ValueKind==JsonValueKind.Number&&Field(image,"height").ValueKind==JsonValueKind.Number&&Field(image,"width").TryGetInt32(out var w)&&Field(image,"height").TryGetInt32(out var h)&&w is >=640 and <=65535&&h is >=180 and <=65535&&w/(double)h>=1.35)art.Add(new(url,w,h,kind,caption)); }
        Art(Field(work,"banner"),"banner");foreach(var s in Rows(work,"screenshots").Take(200))Art(s,"screenshot",Text(s,"caption"));foreach(var c in Rows(work,"covers").Take(200))Art(c,"cover");
        var credits=Rows(work,"credits").Take(64).Select(g=>new CatalogCredit(Text(g,"role_name") is {Length:>0} n?n:Text(g,"role_key"),[..Rows(g,"credits").Take(500).Where(p=>ValidId(Text(p,"id"))).DistinctBy(p=>Text(p,"id")).Select(p=>Ref(p,CatalogEntityKind.CreditName))]) {RoleKey=Text(g,"role_key")}).Where(g=>!g.People.IsEmpty).ToImmutableArray();
        var tags=Rows(work,"tags").Take(200).Where(t=>Text(t,"tier")!="hidden"&&Text(t,"spoiler") is "" or "none"&&ValidId(Text(t,"id"))).Select(t=>Ref(t,CatalogEntityKind.Tag)).DistinctBy(t=>t.Id).ToImmutableArray();
        var related=Rows(work,"relations").Take(64).Select(r=>(Row:Field(r,"work"),Relation:Text(r,"phrase"),Type:Text(r,"relation_type")))
            .Where(r=>ValidId(Text(r.Row,"id"))&&KunGalGalgameId(r.Row) is not null)
            .Select(r=>Ref(r.Row,CatalogEntityKind.Work,r.Relation,Image(Field(r.Row,"cover"))) with {Relation=r.Type,KunGalGalgameId=KunGalGalgameId(r.Row)})
            .DistinctBy(r=>r.Id).ToImmutableArray();
        return new CatalogPresentation(introSource,[..art.DistinctBy(a=>a.Url).Take(64)],Entities(work,"companies",CatalogEntityKind.Company),Entities(work,"engines",CatalogEntityKind.Engine),Entities(work,"series",CatalogEntityKind.Series),tags,Characters(work,explicitContent),credits,related) { LanguageSchema=CatalogLanguage.DisplaySchema, RelatedSchema=CatalogRelatedWorks.Schema, Names=Names(work), Introductions=Introductions(work) };
    }
    internal static CatalogEntityDetail Entity(JsonElement row,CatalogEntityKind kind,string requestedId)
    {
        var expected = kind switch { CatalogEntityKind.Character => "character", CatalogEntityKind.Company => "company", CatalogEntityKind.CreditName => "credit_name", CatalogEntityKind.Person => "person", CatalogEntityKind.Tag => "tag", CatalogEntityKind.Engine => "engine", CatalogEntityKind.Series => "series", CatalogEntityKind.Trait => "trait", _ => "" };
        if(!ValidId(Text(row,"id"))||Text(row,"id")!=requestedId||row.ValueKind!=JsonValueKind.Object||Text(row,"object")!=expected)throw new OnlineException(OnlineFailure.InvalidResponse);
        var intro=PreferredIntro(Rows(row,"intros"));var description=Text(intro,"value");if(description.Length==0)description=Text(row,"description");
        var image=new[]{"image","figure","logo","photo"}.Select(k=>Image(Field(row,k))).FirstOrDefault(u=>u is not null);
        var related=new List<CatalogEntityRef>();foreach(var s in Rows(row,"siblings").Take(64))if(ValidId(Text(s,"id")))related.Add(Ref(s,CatalogEntityKind.CreditName));
        if(ValidId(Text(row,"person_id")))related.Add(new(CatalogEntityKind.Person,Text(row,"person_id"),UiText.Get("人物与其他名义")));
        if(kind == CatalogEntityKind.Character) foreach(var trait in Rows(row,"traits").Take(100)) { var value = ValidId(Text(trait,"id")) ? trait : Field(trait,"trait"); if(ValidId(Text(value,"id")) && Text(trait,"spoiler") is "" or "none") related.Add(Ref(value,CatalogEntityKind.Trait)); }
        if(kind == CatalogEntityKind.Trait) foreach(var item in Rows(row,"parents").Concat(Rows(row,"subtraits")).Take(200)) if(ValidId(Text(item,"id"))) related.Add(Ref(item,CatalogEntityKind.Trait));
        if(kind == CatalogEntityKind.Company) foreach(var relation in Rows(row,"relations").Take(64)) { var company=Field(relation,"company"); if(ValidId(Text(company,"id"))) related.Add(Ref(company,CatalogEntityKind.Company,Text(relation,"phrase"))); }
        var facts=new List<string>();foreach(var (key,label) in new[]{("company_kind","类型"),("release_date","日期"),("work_count","相关作品"),("height_cm","身高"),("weight_kg","体重"),("birth_year","出生年份"),("gender","性别"),("blood_type","血型")}){var field=Field(row,key);var value=field.ValueKind==JsonValueKind.Number?field.GetRawText():Text(row,key);if(value.Length>0)facts.Add(UiText.Get(label)+" · "+value);}
        var refs=Rows(row,"refs").Take(64).Select(r=>new WorkReference(Text(r,"source"),Text(r,"external_id"),ReferenceUrl(Text(r,"source"),Text(r,"external_id")))).ToImmutableArray();
        return new CatalogEntityDetail(Ref(row,kind,image:image),description,[..facts],[..related.DistinctBy(e=>(e.Kind,e.Id))],refs) { Introductions=Introductions(row) };
    }
}
