using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Local CPU detection only. No OCR strings, external process, credentials, downloads or uploads.
public sealed class WindowsArtworkTextDetector(string? modelPath = null) : IArtworkTextDetector
{
    public const string ModelSha256 = "03F550C6B406FDA8BF54BD8327815F6C7E2EDD98CEA02348C93D879254366587";
    private InferenceSession? session;
    private readonly SemaphoreSlim evaluation = new(1, 1);
    private bool unavailable;
    private bool disposed;
    public async Task<ArtworkTextDetection> AnalyzeAsync(RasterPixels pixels, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        await evaluation.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (unavailable || disposed) return new(false, [], "unavailable");
            return await Task.Run(() => {
                if (pixels.Width < 1 || pixels.Height < 1 || Math.Max(pixels.Width, pixels.Height) > BannerSelection.TextAnalysisEdge || pixels.Bgra.Length != checked(pixels.Width * pixels.Height * 4))
                    throw new InvalidDataException("Invalid bounded artwork raster");
                if (session is null)
                {
                    var path = modelPath ?? Path.Combine(AppContext.BaseDirectory, "data", "ppocrv3-text-detector.onnx");
                    byte[] bytes;
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (file.Length != 2423490) throw new InvalidDataException("Artwork detector identity mismatch");
                        bytes = new byte[file.Length];
                        file.ReadExactly(bytes);
                    }
                    if (Convert.ToHexString(SHA256.HashData(bytes)) != ModelSha256)
                        throw new InvalidDataException("Artwork detector identity mismatch");
                    // CPU is the only provider. No WinML auto-provider downloads,
                    // GPU requirement or dependence on the OS's old ONNX version.
                    using var options = new SessionOptions
                    {
                        IntraOpNumThreads = 1, InterOpNumThreads = 1,
                        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL
                    };
                    session = new(bytes, options);
                }
                cancellation.ThrowIfCancellationRequested();
                int width = (pixels.Width + 31) / 32 * 32, height = (pixels.Height + 31) / 32 * 32, plane = width * height;
                var values = new float[3 * plane];
                // OpenCV Zoo PP-OCRv3's published BGR normalization; padding does not stretch text.
                float[] means = [123.675f,116.28f,103.53f], scales = [255*.229f,255*.224f,255*.225f];
                for (int y=0;y<pixels.Height;y++) for(int x=0;x<pixels.Width;x++) for(int c=0;c<3;c++)
                    values[c*plane+y*width+x]=(pixels.Bgra[(y*pixels.Width+x)*4+c]-means[c])/scales[c];
                using var input = OrtValue.CreateTensorValueFromMemory(values, new long[]{1,3,height,width});
                using var run = new RunOptions();
                cancellation.ThrowIfCancellationRequested();
                using var cancel = cancellation.Register(() => run.Terminate = true);
                // Run is drained before releasing its inputs, outputs or session.
                using var output = session.Run(run, new Dictionary<string, OrtValue> { [session.InputNames[0]] = input }, session.OutputNames);
                cancellation.ThrowIfCancellationRequested();
                var map = output[0].GetTensorDataAsSpan<float>();
                if (map.Length != plane) throw new InvalidDataException("Unexpected text map dimensions");
                return new ArtworkTextDetection(true, Regions(map,width,height,pixels.Width,pixels.Height), "local-ppocrv3");
            },cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
        catch { unavailable = true; return new(false,[],"unavailable"); }
        finally { evaluation.Release(); }
    }
    private static ImmutableArray<ArtworkTextRegion> Regions(ReadOnlySpan<float> map,int width,int height,int imageWidth,int imageHeight)
    {
        var seen = new byte[map.Length]; var queue = new Queue<int>(); var regions = ImmutableArray.CreateBuilder<ArtworkTextRegion>();
        for(int y=0;y<imageHeight;y++) for(int x=0;x<imageWidth;x++)
        {
            int p=y*width+x; if(seen[p]!=0||map[p]<.3)continue;
            queue.Enqueue(p);seen[p]=1;int xmin=x,xmax=x,ymin=y,ymax=y,count=0;double confidence=0;
            while(queue.TryDequeue(out var at))
            {
                int cx=at%width,cy=at/width;count++;confidence+=map[at];xmin=Math.Min(xmin,cx);xmax=Math.Max(xmax,cx);ymin=Math.Min(ymin,cy);ymax=Math.Max(ymax,cy);
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                {
                    int nx=cx+dx,ny=cy+dy;if(nx<0||ny<0||nx>=imageWidth||ny>=imageHeight)continue;
                    int next=ny*width+nx;if(seen[next]!=0||map[next]<.3)continue;seen[next]=1;queue.Enqueue(next);
                }
            }
            if(count>=12&&xmax-xmin>=5&&ymax-ymin>=3&&confidence/count>=.5)
            {
                regions.Add(new(xmin/(double)imageWidth,ymin/(double)imageHeight,(xmax-xmin+1d)/imageWidth,(ymax-ymin+1d)/imageHeight,confidence/count));
                if(regions.Count>=200)return regions.ToImmutable();
            }
        }
        return regions.ToImmutable();
    }
    public async ValueTask DisposeAsync()
    {
        await evaluation.WaitAsync().ConfigureAwait(false);
        try { disposed = true; session?.Dispose(); session = null; }
        finally { evaluation.Release(); }
    }
}
