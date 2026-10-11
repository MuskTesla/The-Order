using Markdig;
using Markdig.Syntax;
namespace TheOrder.Presentation;
public static class MarkdownContent
{
    public const int MaximumCharacters=200000;
    private static readonly MarkdownPipeline Pipeline=new MarkdownPipelineBuilder().UsePipeTables().UseTaskLists().UseAutoLinks().UseEmphasisExtras().DisableHtml().Build();
    public static MarkdownDocument Parse(string text)=>Markdown.Parse(text.Length<=MaximumCharacters?text:text[..MaximumCharacters],Pipeline);
    public static string? ResolveLink(string? value)
    {
        if (AllowedLink(value)) return value;
        if (value is {Length:>0 and <=8192} && value.StartsWith("/galgame/",StringComparison.Ordinal))
        {
            var absolute="https://www.kungal.com"+value;
            if (KunGalNavigation.TryEntity(absolute,out _)) return absolute;
        }
        return null;
    }
    public static bool AllowedLink(string? value)=>value is {Length:>0 and <=8192}&&Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "https" or "http"&&uri.UserInfo.Length==0&&uri.IsDefaultPort&&uri.Host.Length>0&&uri.Host!="localhost"&&!System.Net.IPAddress.TryParse(uri.Host,out _);
}
