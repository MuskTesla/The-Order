using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using TheOrder.Application;

namespace TheOrder.Infrastructure.Recognition;

// Loaded on demand on a worker. A cancelled/invalid load never publishes a partial index.
public sealed class TitleIndex(Func<Stream> open) : ITitleIndex
{
    private sealed record Loaded(ImmutableArray<TitleIndexRecord> Rows,Dictionary<string,int[]> Names, (string Key, string Value)[] Readme);
    private readonly SemaphoreSlim load = new(1,1);
    private Loaded? current;
    public int RecordCount=>current?.Rows.Length??0;
    public int TitleCount=>current?.Rows.Sum(r=>r.Names.Length)??0;
    public double LoadMilliseconds {get;private set;}
    public async Task<ImmutableArray<string>> FindReadmeTitlesAsync(ImmutableArray<string> headings, CancellationToken cancellation)
    {
        if (headings.Length > 300 || headings.Any(h => h.Length > 3000)) throw new InvalidDataException("说明标题查询超过上限。");
        if (headings.IsEmpty) return [];
        await FindAsync([], cancellation).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var result = new List<string>();
            foreach (var heading in headings)
            {
                cancellation.ThrowIfCancellationRequested();
                var key = string.Concat(TitleMatching.Lower(heading.Normalize(System.Text.NormalizationForm.FormKC)).EnumerateRunes().Where(r => System.Text.Rune.IsLetter(r) || System.Text.Rune.IsNumber(r)).Select(r => r.ToString()));
                var matches = new List<(string Key, string Value)>();
                foreach (var row in current!.Readme) { cancellation.ThrowIfCancellationRequested(); if (key.Contains(row.Key, StringComparison.Ordinal)) { matches.Add(row); if (matches.Count == 3) break; } }
                if (matches.Count > 0 && matches.All(m => matches[0].Key.Contains(m.Key, StringComparison.Ordinal))) result.Add(matches[0].Value);
            }
            return RecognitionIdentityBuilder.Names(result);
        }, cancellation).ConfigureAwait(false);
    }
    public async Task<ImmutableArray<IndexCandidate>> FindAsync(ImmutableArray<string> names,CancellationToken cancellation)
    {
        if(names.Length>40||names.Any(n=>n.Length>500))throw new InvalidDataException("索引查询超过上限。");
        await load.WaitAsync(cancellation).ConfigureAwait(false);
        try { if(current is null) current=await Task.Run(()=>Read(cancellation),cancellation).ConfigureAwait(false); }
        finally {load.Release();}
        var snapshot=current;
        return await Task.Run(()=> {
            var found=new SortedDictionary<int,string>();
            foreach(var name in names){cancellation.ThrowIfCancellationRequested();if(snapshot.Names.TryGetValue(TitleMatching.IndexKey(name),out var rows))foreach(var row in rows){if(!found.ContainsKey(row))found.Add(row,name);if(found.Count>=128)break;}if(found.Count>=128)break;}
            return found.Select(p=>new IndexCandidate(p.Key,snapshot.Rows[p.Key],p.Value,true)).ToImmutableArray();
        },cancellation).ConfigureAwait(false);
    }
    private Loaded Read(CancellationToken cancellation)
    {
        var watch=Stopwatch.StartNew();using var source=open();using var compressed=Bounded(source,8*1024*1024,cancellation);
        using var gzip=new GZipStream(compressed,CompressionMode.Decompress);using var expanded=Bounded(gzip,32*1024*1024,cancellation);
        using var document=JsonDocument.Parse(expanded,new(){MaxDepth=16});cancellation.ThrowIfCancellationRequested();
        var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("version",out var version)||version.ValueKind!=JsonValueKind.Number||!version.TryGetInt32(out int v)||v!=1||!root.TryGetProperty("records",out var records)||records.ValueKind!=JsonValueKind.Array||records.GetArrayLength()>150000)throw new InvalidDataException("本地标题索引格式不正确。");
        var result=ImmutableArray.CreateBuilder<TitleIndexRecord>();Dictionary<string,List<int>> lookup=new(StringComparer.Ordinal);
        foreach(var row in records.EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            if(row.ValueKind!=JsonValueKind.Object||!row.TryGetProperty("names",out var n)||!row.TryGetProperty("references",out var r)||n.ValueKind!=JsonValueKind.Array||r.ValueKind!=JsonValueKind.Array)continue;
            if(n.GetArrayLength()>100||r.GetArrayLength()>100||n.EnumerateArray().Any(x=>x.ValueKind!=JsonValueKind.String||x.GetString()!.Length>500)||r.EnumerateArray().Any(x=>x.ValueKind!=JsonValueKind.String||x.GetString()!.Length>100))throw new InvalidDataException("本地标题索引行超过上限。");
            var item=new TitleIndexRecord([..n.EnumerateArray().Select(x=>x.GetString()!)],[..r.EnumerateArray().Select(x=>x.GetString()!)]);int index=result.Count;result.Add(item);
            foreach(var key in item.Names.Select(TitleMatching.IndexKey).Where(x=>x.Length>0).Distinct(StringComparer.Ordinal)){if(!lookup.TryGetValue(key,out var bucket))lookup.Add(key,bucket=[]);bucket.Add(index);}
        }
        cancellation.ThrowIfCancellationRequested();
        var readme = result.SelectMany(r => r.Names).Select(v => (Key: TitleMatching.IndexKey(v), Value: v))
            .GroupBy(p => p.Key, StringComparer.Ordinal).Select(g => g.Last()).Where(p => p.Key.Length is >= 7 and <= 160).OrderByDescending(p => p.Key.Length).ToArray();
        cancellation.ThrowIfCancellationRequested(); LoadMilliseconds=watch.Elapsed.TotalMilliseconds;
        return new(result.ToImmutable(),lookup.ToDictionary(p=>p.Key,p=>p.Value.ToArray(),StringComparer.Ordinal),readme);
    }
    private static MemoryStream Bounded(Stream stream,int maximum,CancellationToken cancellation)
    {
        var result=new MemoryStream();try {var buffer=new byte[16384];while(true){cancellation.ThrowIfCancellationRequested();int n=stream.Read(buffer,0,Math.Min(buffer.Length,maximum+1-(int)result.Length));if(n==0)break;if(result.Length+n>maximum)throw new InvalidDataException("本地标题索引过大。");result.Write(buffer,0,n);}cancellation.ThrowIfCancellationRequested();result.Position=0;return result;}catch{result.Dispose();throw;}
    }
}
