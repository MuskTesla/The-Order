using System.Runtime.InteropServices.WindowsRuntime;
using System.Buffers.Binary;
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
        if (encoded.Length is < 4 or > MaximumSourceBytes || edge is < 1 or > ArtworkSizing.MaximumEdge) throw new InvalidDataException(UiText.Get("图片大小超出范围。"));
        // Static PNG/JPEG/WebP only. WIC inspects dimensions before any pixel allocation.
        var bytes = encoded.ToArray();
        bool webp = bytes.Length >= 20 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
        bool png = bytes.Length >= 33 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (webp) ValidateStaticWebp(bytes);
        else if (png) ValidatePngContainer(bytes);
        else if (!(bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[^2] == 0xff && bytes[^1] == 0xd9))
            throw new InvalidDataException(UiText.Get("图片不是有效的 PNG/JPEG/WebP。"));
        using var stream = new InMemoryRandomAccessStream(); await stream.WriteAsync(bytes.AsBuffer()).AsTask(cancellation).ConfigureAwait(false); stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellation).ConfigureAwait(false);
        if (decoder.PixelWidth is < 1 or > 65535 || decoder.PixelHeight is < 1 or > 65535 || (ulong)decoder.PixelWidth * decoder.PixelHeight > 40_000_000 || decoder.FrameCount != 1)
            throw new InvalidDataException(UiText.Get("图片超过 4000 万像素或不是静态图片。"));
        double scale = Math.Min(1, (double)edge / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform { ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)), ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)), InterpolationMode = BitmapInterpolationMode.Fant };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (bitmap.PixelWidth > edge || bitmap.PixelHeight > edge) throw new InvalidDataException(UiText.Get("解码后的图片超过目标尺寸。"));
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)]; bitmap.CopyToBuffer(pixels.AsBuffer());
        return new(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }
    private static void ValidatePngContainer(ReadOnlySpan<byte> bytes)
    {
        // WIC can return a blank bitmap for an incomplete IDAT. Require the complete
        // bounded container before decoding/normalizing a personal photo; WIC still
        // validates compression/pixels. This is not a replacement PNG decoder.
        bool data = false;
        for (int offset = 8; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 12) throw new InvalidDataException(UiText.Get("PNG 块头不完整。"));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if ((ulong)length + 12 > (ulong)(bytes.Length - offset)) throw new InvalidDataException(UiText.Get("PNG 块长度无效。"));
            var kind = bytes.Slice(offset + 4, 4);
            if (offset == 8 && (!kind.SequenceEqual("IHDR"u8) || length != 13)) throw new InvalidDataException(UiText.Get("PNG 头无效。"));
            if (kind.SequenceEqual("IDAT"u8) && length > 0) data = true;
            if (kind.SequenceEqual("IEND"u8))
            {
                if (length != 0 || !data || offset + 12 != bytes.Length) throw new InvalidDataException(UiText.Get("PNG 结尾无效。"));
                return;
            }
            offset += 12 + (int)length;
        }
        throw new InvalidDataException(UiText.Get("PNG 容器不完整。"));
    }
    private static void ValidateStaticWebp(ReadOnlySpan<byte> bytes)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]) != bytes.Length - 8) throw new InvalidDataException(UiText.Get("WebP 容器长度无效。"));
        // Some installed WIC codecs expose only the first animation frame. Reject animation at
        // the container boundary as well as retaining the common decoder.FrameCount guard.
        for (int offset = 12; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 8) throw new InvalidDataException(UiText.Get("WebP 块头不完整。"));
            var kind = bytes.Slice(offset, 4);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            if ((ulong)length + (length & 1) > (ulong)(bytes.Length - offset - 8)) throw new InvalidDataException(UiText.Get("WebP 块长度无效。"));
            if (kind.SequenceEqual("ANIM"u8) || kind.SequenceEqual("ANMF"u8)) throw new InvalidDataException(UiText.Get("仅支持静态 WebP。"));
            if (kind.SequenceEqual("VP8X"u8))
            {
                var header = bytes.Slice(offset + 8, (int)length);
                if (length != 10 || (header[0] & 2) != 0) throw new InvalidDataException(UiText.Get("WebP 扩展头无效或包含动画。"));
                int width = 1 + header[4] + (header[5] << 8) + (header[6] << 16);
                int height = 1 + header[7] + (header[8] << 8) + (header[9] << 16);
                if (width > 65535 || height > 65535 || (long)width * height > 40_000_000) throw new InvalidDataException(UiText.Get("图片超过 4000 万像素或尺寸限制。"));
            }
            offset += 8 + (int)length + (int)(length & 1);
        }
    }
    public Task<LocalCover> NormalizePhotoAsync(string path, CancellationToken cancellation) => Task.Run(async () =>
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg")) throw new InvalidDataException(UiText.Get("请选择 PNG/JPEG 图片。"));
        // FileShare.Read pins the file against replacement/writes while reading; no unbounded ReadAllBytes.
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (source.Length is < 4 or > MaximumSourceBytes) throw new InvalidDataException(UiText.Get("照片超过 20 MiB 或无效。"));
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
        throw new InvalidDataException(UiText.Get("无法生成符合旧版 128 KiB 限制的个人封面。"));
    }, cancellation);
}
