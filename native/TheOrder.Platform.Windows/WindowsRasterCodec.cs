using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using TheOrder.Application;
using TheOrder.Domain;

namespace TheOrder.Platform.Windows;

public sealed class WindowsRasterCodec : IRasterCodec
{
    public const int MaximumSourceBytes = 20 * 1024 * 1024;
    public async Task<RasterPixels> DecodeAsync(ReadOnlyMemory<byte> encoded, int edge, CancellationToken cancellation)
    {
        if (encoded.Length is < 4 or > MaximumSourceBytes || edge is < 1 or > 1600) throw new InvalidDataException("图片大小超出范围。");
        // Only PNG/JPEG. Header dimensions are inspected by WIC before requesting any pixel allocation.
        var bytes = encoded.ToArray();
        if (!(bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[^2] == 0xff && bytes[^1] == 0xd9) && !(bytes.Length >= 33 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })))
            throw new InvalidDataException("照片不是有效的 PNG/JPEG。");
        using var stream = new InMemoryRandomAccessStream(); await stream.WriteAsync(bytes.AsBuffer()).AsTask(cancellation).ConfigureAwait(false); stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellation).ConfigureAwait(false);
        if (decoder.PixelWidth is < 1 or > 65535 || decoder.PixelHeight is < 1 or > 65535 || (ulong)decoder.PixelWidth * decoder.PixelHeight > 40_000_000 || decoder.FrameCount != 1)
            throw new InvalidDataException("图片超过 4000 万像素或不是静态图片。");
        double scale = Math.Min(1, (double)edge / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform { ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)), ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)), InterpolationMode = BitmapInterpolationMode.Fant };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (bitmap.PixelWidth > edge || bitmap.PixelHeight > edge) throw new InvalidDataException("解码后的图片超过目标尺寸。");
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)]; bitmap.CopyToBuffer(pixels.AsBuffer());
        return new(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }
    public Task<LocalCover> NormalizePhotoAsync(string path, CancellationToken cancellation) => Task.Run(async () =>
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg")) throw new InvalidDataException("请选择 PNG/JPEG 图片。");
        // FileShare.Read pins the file against replacement/writes while reading; no unbounded ReadAllBytes.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (source.Length is < 4 or > MaximumSourceBytes) throw new InvalidDataException("照片超过 20 MiB 或无效。");
        var encoded = new byte[(int)source.Length]; await source.ReadExactlyAsync(encoded, cancellation).ConfigureAwait(false);
        var pixels = await DecodeAsync(encoded, 1600, cancellation).ConfigureAwait(false);
        // Flatten premultiplied transparency on white; create a fresh JPEG without source metadata.
        for (int offset = 0; offset < pixels.Bgra.Length; offset += 4)
        { int white = 255 - pixels.Bgra[offset + 3]; for (int channel = 0; channel < 3; channel++) pixels.Bgra[offset + channel] = (byte)Math.Min(255, pixels.Bgra[offset + channel] + white); pixels.Bgra[offset + 3] = 255; }
        for (int attempt = 0; attempt < 7; attempt++)
        {
            foreach (float quality in new[] { .88f, .80f, .72f })
            {
                using var output = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, new[] { new KeyValuePair<string, BitmapTypedValue>("ImageQuality", new(quality, global::Windows.Foundation.PropertyType.Single)) }).AsTask(cancellation).ConfigureAwait(false);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)pixels.Width, (uint)pixels.Height, 96, 96, pixels.Bgra);
                var scale = Math.Pow(.8, attempt); int width = Math.Max(1, (int)Math.Round(pixels.Width * scale)), height = Math.Max(1, (int)Math.Round(pixels.Height * scale));
                encoder.BitmapTransform.ScaledWidth = (uint)width; encoder.BitmapTransform.ScaledHeight = (uint)height;
                await encoder.FlushAsync().AsTask(cancellation).ConfigureAwait(false);
                if (output.Size > 128 * 1024) continue;
                var jpeg = new byte[(int)output.Size]; output.Seek(0); await output.ReadAsync(jpeg.AsBuffer(), (uint)jpeg.Length, InputStreamOptions.None).AsTask(cancellation).ConfigureAwait(false);
                return new LocalCover("data:image/jpeg;base64," + Convert.ToBase64String(jpeg), width, height);
            }
        }
        throw new InvalidDataException("无法生成符合旧版 128 KiB 限制的个人封面。");
    }, cancellation);
}
