using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TheOrder.Domain;
using TheOrder.Application;
using TheOrder.Presentation;
namespace TheOrder.WinUI.Views;
public sealed class CatalogContentView : StackPanel,IImageHost
{
    public IImagePipeline Images {get;set;}=null!;
    public IRemoteCoverRegistry? Registry {get;set;}
    public Func<CatalogEntityRef,Task>? Browse {get;set;}
    public Func<string,Task>? OpenLink {get;set;}
    public UIElement? Sources { get; set; }
    public bool ShowRelatedWorks { get; set; } = true;
    private readonly List<(string Kind,CoverPresenter View)> artwork=[];
    internal object ImageEvidence=>artwork.Select(a=>new{a.Kind,a.View.SourceStatus,a.View.HasImage,width=a.View.ActualWidth,height=a.View.ActualHeight}).ToArray();
    // Prime the exact display buckets before binding cards. Originals were fetched by import;
    // reopening at another DPI decodes only local bytes, without painting a loading placeholder.
    public async Task PrepareImagesAsync(WorkMetadata work,double scale,CancellationToken token)
    {
        var rows=(work.Presentation?.Characters.Select(c=>(c.Entity.ImageUrl,122d,146d))??work.Characters.Select(c=>(c.ImageUrl,122d,146d)))
            .Concat((ShowRelatedWorks ? CatalogRelatedWorks.Visible(work.Presentation) : []).Select(w=>(w.ImageUrl,146d,196d)))
            .Where(r=>r.ImageUrl is {Length:>0}).DistinctBy(r=>r.ImageUrl).Take(164);
        foreach(var (url,width,height) in rows)
        {
            token.ThrowIfCancellationRequested();var key=Registry?.Register(url,work.FetchedAt);if(key is null)continue;
            await Images.LoadAsync(key,ThumbnailSizing.Edge(width,height,scale),token);
        }
    }
    public CatalogContentView(){Spacing=22;}
    private static TextBlock Text(string text,double size=15)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};
    private static TextBlock Caption(string text,double size=14)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,HorizontalAlignment=HorizontalAlignment.Stretch};
    private static StackPanel Section(string title)=>new(){Spacing=10,Children={new TextBlock{Text=title,FontSize=22,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold}}};
    private HyperlinkButton Link(CatalogEntityRef entity){var b=new HyperlinkButton{Content=entity.Name,FontSize=15,Padding=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left};Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b,entity.Name+UiText.Get("，资料详情"));b.IsEnabled=TheOrder.Infrastructure.Online.RemoteWorkDto.ValidId(entity.Id);b.Click+=async(_,_)=>{if(Browse is not null)await Browse(entity);};return b;}
    private Border TagChip(string name, CatalogEntityRef? entity) => CatalogTagChip.Create(name, entity, Browse);
    private MarkdownView Markdown(string text)=>new(){Text=text,Images=Images,Registry=Registry,OpenLink=OpenLink};
    public void SetWork(WorkMetadata? work,string? personalName=null,bool collapseFacts=false)
    {
        var fallback = work is null ? null : CatalogLanguage.Fallback(work);
        if (work is not null) work = CatalogLanguage.Project(work);
        Children.Clear();artwork.Clear();if(work is null){Children.Add(Text(UiText.Get("尚未关联作品。可在更多操作中重新识别。")));return;}
        var p=work.Presentation;
        if (!collapseFacts)
        {
        var facts=Section(UiText.Get("作品信息"));var names=new[]{work.Title,work.OriginalTitle}.Where(n=>!string.IsNullOrWhiteSpace(n)&&n!=personalName).Distinct().ToArray();if(names.Length>0)facts.Children.Add(Text(string.Join(" / ",names)));
        if(p is {Companies.IsEmpty:false}) {var row=new TagFlowPanel();foreach(var company in p.Companies)row.Children.Add(Link(company));facts.Children.Add(row);}else if(!work.Developers.IsEmpty)facts.Children.Add(Text(UiText.Get("制作 · ")+string.Join(" / ",work.Developers)));
        if(!string.IsNullOrWhiteSpace(work.ReleaseDate))facts.Children.Add(Text(UiText.Get("发行 · ")+work.ReleaseDate));
        if(!work.Ratings.IsEmpty)facts.Children.Add(Text(UiText.Get("本版本评分 · ")+string.Join(" / ",work.Ratings.Select(r=>$"{SiteName(r.Source)} {r.Score:0.#}/{r.Scale:0.#}"+(r.Votes is { } votes?UiText.Format($"（{votes:0} 人）"):"")))));
        if(p is not null){foreach(var (name,items) in new[]{(UiText.Get("引擎"),p.Engines),(UiText.Get("系列"),p.Series)})if(!items.IsEmpty){var row=new TagFlowPanel();row.Children.Add(Text(name+" · "));foreach(var entity in items)row.Children.Add(Link(entity));facts.Children.Add(row);}}
        // The hero already contains these facts. Preserve the series link, which is not in the hero.
        Children.Add(facts);
        }
        else if (p is { Series.IsEmpty: false }) { var series = new TagFlowPanel(); foreach (var entity in p.Series) series.Children.Add(Link(entity)); Children.Add(series); }
        var intro=Section(UiText.Get("简介"));intro.Children.Add(Markdown(string.IsNullOrWhiteSpace(work.Description)?UiText.Get("暂无本版本简介。"):work.Description));Children.Add(intro);
        if (fallback is not null) Children.Add(Text(fallback, 12));
        if (!work.Tags.IsEmpty)
        {
            var tags = Section(UiText.Get("标签"));
            var flow = new TagFlowPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
            void AddTags(int count)
            {
                flow.Children.Clear();
                foreach (var name in work.Tags.Take(count))
                    flow.Children.Add(TagChip(name, p?.Tags.FirstOrDefault(tag => tag.Name == name)));
            }
            AddTags(16);
            tags.Children.Add(flow);
            if (work.Tags.Length > 16)
            {
                var more = new HyperlinkButton { Content = UiText.Format($"全部 {work.Tags.Length} 个标签"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
                bool all = false;
                more.Click += (_, _) => { all = !all; AddTags(all ? 200 : 16); more.Content = all ? UiText.Get("收起标签") : UiText.Format($"全部 {work.Tags.Length} 个标签"); };
                tags.Children.Add(more);
            }
            Children.Add(tags);
        }
        var characters=p?.Characters??[..work.Characters.Select(c=>new CatalogCharacter(new(CatalogEntityKind.Character,c.Id,c.Name,ImageUrl:c.ImageUrl),c.OriginalName,c.Role,"none",[..c.VoiceActors.Select(n=>new CatalogEntityRef(CatalogEntityKind.CreditName,"",n))]))];
        if(!characters.IsEmpty){var section=Section(UiText.Get("角色"));var safe=characters.Where(c=>c.Spoiler is "" or "none").ToArray();section.Children.Add(CharacterGrid(safe,work.FetchedAt));var hidden=characters.Where(c=>c.Spoiler is not ("" or "none")).ToArray();if(hidden.Length>0){var expander=new Expander{Header=UiText.Format($"剧透角色（{hidden.Length}）"),HorizontalAlignment=HorizontalAlignment.Stretch};expander.Expanding+=(_,_)=>{expander.Content??=CharacterGrid(hidden,work.FetchedAt);};section.Children.Add(expander);}Children.Add(section);}
        if (p is { Credits.IsEmpty: false })
        {
            var credits = new CreditColumnsPanel();
            foreach (var group in p.Credits.Where(g => !CatalogStaffRoles.IsVoiceActing(g.Role, g.RoleKey)))
            {
                var row = new StackPanel { Spacing = 8 };
                var role = Text(CatalogStaffRoles.Name(group.Role, group.RoleKey), 13);
                role.Opacity = .7; role.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                row.Children.Add(role);
                var flow = new TagFlowPanel { HorizontalSpacing = 8, VerticalSpacing = 4 };
                foreach (var person in group.People.Take(80)) flow.Children.Add(Link(person));
                row.Children.Add(flow);
                if (group.People.Length > 80)
                {
                    var remaining = new Expander { Header = UiText.Format($"另外 {group.People.Length - 80} 人"), HorizontalAlignment = HorizontalAlignment.Stretch };
                    remaining.Expanding += (_, _) => { if (remaining.Content is not null) return; var more = new TagFlowPanel(); foreach (var person in group.People.Skip(80)) more.Children.Add(Link(person)); remaining.Content = more; };
                    row.Children.Add(remaining);
                }
                credits.Children.Add(new Border { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CatalogCreditCardStyle"], Child = row });
            }
            if (credits.Children.Count > 0) Children.Add(new Expander { Header = UiText.Get("制作人员"), Content = credits, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        if (Sources is not null) Children.Add(Sources);
        if(ShowRelatedWorks && p is {RelatedWorks.IsEmpty:false}){var related=Section(UiText.Get("相关作品"));related.Children.Add(RelatedGrid(p.RelatedWorks,work.FetchedAt));Children.Add(related);}
    }
    private TagFlowPanel RelatedGrid(IEnumerable<CatalogEntityRef> works,string revision)
    {
        var flow=new TagFlowPanel {HorizontalSpacing=16,VerticalSpacing=18};
        foreach(var entity in works.Where(CatalogRelatedWorks.Eligible).Take(64))
        {
            var card=new StackPanel {Width=146,Spacing=6};
            var picture=new CoverPresenter {Pipeline=Images,SourceKey=Registry?.Register(entity.ImageUrl,revision)??ImageSourceKeys.Remote(entity.ImageUrl,revision),ImageCornerRadius=6};artwork.Add(("related",picture));
            card.Children.Add(new Border {Width=146,Height=196,CornerRadius=new CornerRadius(6),Child=picture});
            card.Children.Add(Caption(entity.Name));
            if(entity.Subtitle is {Length:>0})card.Children.Add(Caption(entity.Subtitle,12));
            var button=new Button {Content=card,Padding=new Thickness(0),BorderThickness=new Thickness(0),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),HorizontalContentAlignment=HorizontalAlignment.Stretch};
            button.IsEnabled=TheOrder.Infrastructure.Online.RemoteWorkDto.ValidId(entity.Id);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,entity.Name+(entity.Subtitle is {Length:>0} relation?"，"+relation:""));
            button.Click+=async(_,_)=>{if(Browse is not null)await Browse(entity);};
            flow.Children.Add(button);
        }
        return flow;
    }
    private StackPanel CharacterGrid(IEnumerable<CatalogCharacter> characters,string revision)
    {
        var result=new StackPanel {Spacing=12};
        var illustrated=new TagFlowPanel();
        var compact=new TagFlowPanel {HorizontalSpacing=16,VerticalSpacing=12};
        foreach(var c in characters.Take(100))
        {
            if(c.Entity.ImageUrl is not {Length:>0})
            {
                // Missing source artwork has no image frame or placeholder. Keep
                // these entries together so illustrated cards do not reserve height.
                var name=Caption(c.Entity.Name);name.TextWrapping=TextWrapping.NoWrap;name.TextTrimming=TextTrimming.CharacterEllipsis;name.MaxLines=1;
                var button=new Button {Content=name,Padding=new Thickness(0),MinHeight=0,BorderThickness=new Thickness(0),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch};
                button.IsEnabled=TheOrder.Infrastructure.Online.RemoteWorkDto.ValidId(c.Entity.Id);
                button.Click+=async(_,_)=>{if(Browse is not null)await Browse(c.Entity);};ToolTipService.SetToolTip(button,c.Entity.Name);
                var voices=CompactVoices(c);
                compact.Children.Add(new StackPanel {Width=168,Spacing=4,Children={button,voices}});
                continue;
            }
            var card=new StackPanel {Width=122,Spacing=4,Margin=new Thickness(0,0,12,16)};
            var art=new StackPanel {Spacing=6};
            var portrait=new CoverPresenter {Pipeline=Images,SourceKey=Registry?.Register(c.Entity.ImageUrl,revision)??ImageSourceKeys.Remote(c.Entity.ImageUrl,revision),PreserveEntireImage=true,ImageCornerRadius=6};
            artwork.Add(("character",portrait));
            art.Children.Add(new Border {Width=122,Height=146,CornerRadius=new CornerRadius(6),Child=portrait});art.Children.Add(Caption(c.Entity.Name));
            var illustratedButton=new Button {Content=art,Padding=new Thickness(0),BorderThickness=new Thickness(0),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),HorizontalContentAlignment=HorizontalAlignment.Stretch};
            illustratedButton.IsEnabled=TheOrder.Infrastructure.Online.RemoteWorkDto.ValidId(c.Entity.Id);illustratedButton.Click+=async(_,_)=>{if(Browse is not null)await Browse(c.Entity);};ToolTipService.SetToolTip(illustratedButton,c.OriginalName);card.Children.Add(illustratedButton);
            if(c.Voices.IsEmpty)card.Children.Add(Caption(UiText.Get("未收录声优"),12));else foreach(var voice in c.Voices.Take(3)){if(voice.Id.Length>0){var link=Link(voice);link.Content=Caption("CV "+voice.Name,12);link.FontSize=12;link.Padding=new Thickness(0);link.HorizontalAlignment=HorizontalAlignment.Stretch;link.HorizontalContentAlignment=HorizontalAlignment.Center;card.Children.Add(link);}else card.Children.Add(Caption("CV "+voice.Name,12));}
            illustrated.Children.Add(card);
        }
        if(illustrated.Children.Count>0)result.Children.Add(illustrated);
        if(compact.Children.Count>0)result.Children.Add(compact);
        return result;
    }
    private TextBlock CompactVoices(CatalogCharacter character)
    {
        var line=Caption(character.Voices.IsEmpty?UiText.Get("未收录声优"):"",12);
        line.TextWrapping=TextWrapping.NoWrap;line.TextTrimming=TextTrimming.CharacterEllipsis;line.MaxLines=1;
        if(character.Voices.IsEmpty)return line;
        line.Inlines.Clear();
        line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run {Text="CV "});
        var first=true;
        foreach(var voice in character.Voices.Take(16))
        {
            if(!first)line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run {Text=" / "});
            first=false;
            if(TheOrder.Infrastructure.Online.RemoteWorkDto.ValidId(voice.Id))
            {
                var link=new Microsoft.UI.Xaml.Documents.Hyperlink {UnderlineStyle=Microsoft.UI.Xaml.Documents.UnderlineStyle.None};link.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run {Text=voice.Name});
                link.Click+=async(_,_)=>{if(Browse is not null)await Browse(voice);};line.Inlines.Add(link);
            }
            else line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run {Text=voice.Name});
        }
        ToolTipService.SetToolTip(line,"CV "+string.Join(" / ",character.Voices.Select(v=>v.Name)));
        return line;
    }
    public static string SiteName(string site)=>site switch{"curated" or "kungal" or "nextmoe"=>"KunGal","howlongtobeat"=>"HowLongToBeat","bangumi"=>"Bangumi","vndb"=>"VNDB","erogamescape"=>"ErogameScape","dlsite"=>"DLsite",_=>site};
}
