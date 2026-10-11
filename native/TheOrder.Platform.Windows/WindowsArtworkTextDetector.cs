using System.Collections.Immutable;
using System.Security.Cryptography;
using Windows.AI.MachineLearning;
using TheOrder.Application;

namespace TheOrder.Platform.Windows;

// Local CPU detection only. No OCR strings, external process, credentials, downloads or uploads.
public sealed class WindowsArtworkTextDetector(string? modelPath = null) : IArtworkTextDetector
{
    public const string ModelSha256 = "03F550C6B406FDA8BF54BD8327815F6C7E2EDD98CEA02348C93D879254366587";
    private LearningModel? model;
    private LearningModelSession? session;
    private bool unavailable;
    public async Task<ArtworkTextDetection> AnalyzeAsync(RasterPixels pixels, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (unavailable) return new(false, [], "unavailable");
        try
        {
            return await Task.Run(async () => {
                if (pixels.Width < 1 || pixels.Height < 1 || Math.Max(pixels.Width, pixels.Height) > BannerSelection.TextAnalysisEdge || pixels.Bgra.Length != checked(pixels.Width * pixels.Height * 4))
                    throw new InvalidDataException("Invalid bounded artwork raster");
                if (session is null)
                {
                    var path = modelPath ?? Path.Combine(AppContext.BaseDirectory, "data", "ppocrv3-text-detector.onnx");
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        if (file.Length != 2423490 || Convert.ToHexString(SHA256.HashData(file)) != ModelSha256) throw new InvalidDataException("Artwork detector identity mismatch");
                    model = LearningModel.LoadFromFilePath(path);
                    session = new(model, new LearningModelDevice(LearningModelDeviceKind.Cpu));
                }
                cancellation.ThrowIfCancellationRequested();
                int width = (pixels.Width + 31) / 32 * 32, height = (pixels.Height + 31) / 32 * 32, plane = width * height;
                var values = new float[3 * plane];
                // OpenCV Zoo PP-OCRv3's published BGR normalization; padding does not stretch text.
                float[] means = [123.675f,116.28f,103.53f], scales = [255*.229f,255*.224f,255*.225f];
                for (int y=0;y<pixels.Height;y++) for(int x=0;x<pixels.Width;x++) for(int c=0;c<3;c++)
                    values[c*plane+y*width+x]=(pixels.Bgra[(y*pixels.Width+x)*4+c]-means[c])/scales[c];
                using var input = TensorFloat.CreateFromArray(new long[]{1,3,height,width},values);
                var binding = new LearningModelBinding(session);
                binding.Bind(model!.InputFeatures[0].Name,input);
                try
                {
                    // A bounded native CPU evaluation is drained before disposing its session.
                    var result = await session.EvaluateAsync(binding,"artwork-text");
                    using var output = (TensorFloat)result.Outputs[model.OutputFeatures[0].Name];
                    cancellation.ThrowIfCancellationRequested();
                    var map = output.GetAsVectorView();
                    if (map.Count != plane) throw new InvalidDataException("Unexpected text map dimensions");
                    return new ArtworkTextDetection(true, Regions(map,width,height,pixels.Width,pixels.Height), "local-ppocrv3");
                }
                finally { binding.Clear(); }
            },cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { unavailable = true; return new(false,[],"unavailable"); }
    }
    private static ImmutableArray<ArtworkTextRegion> Regions(IReadOnlyList<float> map,int width,int height,int imageWidth,int imageHeight)
    {
        var seen = new byte[map.Count]; var queue = new Queue<int>(); var regions = ImmutableArray.CreateBuilder<ArtworkTextRegion>();
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
    public ValueTask DisposeAsync() { session?.Dispose(); model?.Dispose(); session=null;model=null;return ValueTask.CompletedTask; }
}
