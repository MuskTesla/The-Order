using System.Text.Json;
using TheOrder.Application;
using TheOrder.Infrastructure.Online;
using TheOrder.Platform.Windows;
namespace TheOrder.RecognitionLab;
public static partial class Live
{
    // Public artwork only, through the same bounded network and decoder as the app.
    // Does not open any library, credentials or profile and does not execute a game.
    public static async Task<int> ProbeArtwork(string file)
    {
        var urls=JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(file))!;
        if(urls.Length is <1 or >8) return 2;
        using var client=RemoteCoverSource.CreateClient(); var source=new RemoteCoverSource(client); var codec=new WindowsRasterCodec();
        bool good=true;
        foreach(var url in urls)
        {
            var key=source.Register(url,"probe");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                if(key is null) throw new InvalidDataException("public URL required");
                using var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                var bytes=await source.ReadAsync(key,timeout.Token);
                var pixels=bytes is null?null:await codec.DecodeAsync(bytes,256,timeout.Token);
                Console.WriteLine(JsonSerializer.Serialize(new {host=new Uri(url).Host,status=(int)response.StatusCode,type=response.Content.Headers.ContentType?.MediaType,source=bytes is not null,width=pixels?.Width,height=pixels?.Height}));
                good&=pixels is not null;
            }
            catch(Exception e) {Console.WriteLine(JsonSerializer.Serialize(new {host=new Uri(url).Host,error=e.GetType().Name,message=e.Message}));good=false;}
        }
        return good?0:1;
    }
}
