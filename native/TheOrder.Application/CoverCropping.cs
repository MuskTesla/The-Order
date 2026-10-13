using System.Runtime.CompilerServices;

namespace TheOrder.Application;

public sealed record CoverViewport(double X, double Y, double Width, double Height);

// Pick a window in the source image, then scale that window uniformly to fill
// the card. Analysis is bounded and shared by views of the same decoded image;
// it neither edits the original nor performs OCR or network work while scrolling.
public static class CoverCropping
{
    private sealed record Detail(double[] Rows, double[] Columns);
    private static readonly ConditionalWeakTable<RasterPixels, Detail> details = new();

    public static CoverViewport Plan(RasterPixels image, double aspect)
    {
        if (image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height * 4 != image.Bgra.LongLength)
            throw new ArgumentException("Invalid cover pixels.", nameof(image));
        if (!double.IsFinite(aspect) || aspect <= 0) throw new ArgumentOutOfRangeException(nameof(aspect));
        double width = Math.Min(image.Width, image.Height * aspect);
        double height = Math.Min(image.Height, image.Width / aspect);
        if (Math.Abs(width - image.Width) < .001 && Math.Abs(height - image.Height) < .001)
            return new(0, 0, image.Width, image.Height);
        var detail = details.GetValue(image, Analyze);
        bool vertical = height < image.Height;
        var lines = vertical ? detail.Rows : detail.Columns;
        double length = vertical ? image.Height : image.Width, visible = vertical ? height : width;
        double start = SelectWindow(lines, visible / length, vertical && image.Height > image.Width) * length;
        return vertical ? new(0, start, width, height) : new(start, 0, width, height);
    }

    private static Detail Analyze(RasterPixels image)
    {
        int width = Math.Min(128, image.Width), height = Math.Min(160, image.Height);
        var rows = new double[height]; var columns = new double[width];
        bool portrait = image.Height > image.Width;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int sx = Math.Min(image.Width - 1, (int)((x + .5) * image.Width / width));
            int sy = Math.Min(image.Height - 1, (int)((y + .5) * image.Height / height));
            int offset = (sy * image.Width + sx) * 4;
            double alpha = image.Bgra[offset + 3] / 255d;
            double energy = .002;
            int dx = Math.Max(1, image.Width / width), dy = Math.Max(1, image.Height / height);
            energy += Contrast(image, offset, sx - dx, sy) + Contrast(image, offset, sx + dx, sy)
                + Contrast(image, offset, sx, sy - dy) + Contrast(image, offset, sx, sy + dy);
            energy *= alpha;
            // Poster lettering commonly sits low in the image. Protect its
            // strokes more strongly, while still allowing a top-heavy image to
            // win when moving the crop down would cut important upper detail.
            double weight = !portrait ? 1 : y / (double)height >= .82 ? 2.7 : y / (double)height >= .6 ? 2 : 1.3;
            rows[y] += energy * weight; columns[x] += energy;
        }
        return new(rows, columns);
    }

    private static double Contrast(RasterPixels image, int offset, int x, int y)
    {
        int neighbor = (Math.Clamp(y, 0, image.Height - 1) * image.Width + Math.Clamp(x, 0, image.Width - 1)) * 4;
        double value = (Math.Abs(image.Bgra[offset] - image.Bgra[neighbor]) +
            Math.Abs(image.Bgra[offset + 1] - image.Bgra[neighbor + 1]) +
            Math.Abs(image.Bgra[offset + 2] - image.Bgra[neighbor + 2])) / 765d;
        return value * value;
    }

    private static double SelectWindow(double[] lines, double fraction, bool poster)
    {
        var prefix = new double[lines.Length + 1];
        for (int i = 0; i < lines.Length; i++) prefix[i + 1] = prefix[i] + lines[i];
        double total = prefix[^1], span = Math.Clamp(fraction, 0, 1) * lines.Length;
        double travel = lines.Length - span;
        if (travel <= .001 || total <= 0) return Math.Max(0, travel / 2 / lines.Length);
        double Sum(double at)
        {
            at = Math.Clamp(at, 0, lines.Length); int i = Math.Min((int)at, lines.Length - 1);
            return prefix[i] + (at - i) * lines[i];
        }
        double Cut(double at)
        {
            if (at <= .001 || at >= lines.Length - .001) return 0;
            return (Sum(at + 1.5) - Sum(at - 1.5)) / total;
        }
        double bestStart = travel / 2, best = double.PositiveInfinity;
        for (int i = 0; i <= 64; i++)
        {
            double start = travel * i / 64, end = start + span, t = i / 64d;
            double lost = 1 - (Sum(end) - Sum(start)) / total;
            double cost = lost + .28 * (Cut(start) + Cut(end)) + .018 * Math.Abs(t - .5);
            // Only a weak tie-breaker; detail loss always determines the crop.
            if (poster) cost += .002 * (1 - t);
            if (cost < best) { best = cost; bestStart = start; }
        }
        return bestStart / lines.Length;
    }
}
