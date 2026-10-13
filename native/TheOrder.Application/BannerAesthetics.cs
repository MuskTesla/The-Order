namespace TheOrder.Application;

public sealed record BannerComposition(double Scene, double TextSpace, double Tone, double Resolution,
    double LeftOcclusion, double RightOcclusion, bool PreferRight)
{
    public double Score => Scene + TextSpace + Tone + Resolution;
}

// Landscape-specific heuristics, not a portrait crop score or a face recognizer.
// A small spatial map measures scene structure and possible title interference.
// OCR is a separate hard gate. No inference runs while scrolling or painting.
public static class BannerAesthetics
{
    public const string Algorithm = "landscape-composition-v1";
    private const int Columns = 96, Rows = 54;
    private sealed record Map(double[] Luma, double[] Detail, double[] Saliency, double Variance, double Clipped);

    public static BannerComposition Analyze(RasterPixels image, int sourceWidth, int sourceHeight)
    {
        var map = Read(image);
        var left = Occlusion(map, .03, .44, .60, .51);
        var right = Occlusion(map, .37, .44, .60, .51);
        // A wide scene can have quiet space on either side. Preserve the established
        // left placement on close scores; avoid moving the title for tiny differences.
        bool preferRight = right + .07 < left;
        var quiet = 1 - Math.Min(left, right);
        var detail = map.Detail.Average();
        var contrast = Math.Clamp(Math.Sqrt(map.Variance) / .16, 0, 1);
        var textured = Math.Clamp(detail / .045, 0, 1);
        var ordered = 1 - Math.Clamp((detail - .18) / .22, 0, 1);
        // Broad spatial contrast is useful for a scene; uniformly blank and uniformly
        // noisy frames receive less credit. Saturation and skin colour are not rewards.
        var scene = 25 * (.45 * contrast + .30 * textured + .25 * ordered);
        var tone = 20 * (.65 * contrast + .35 * (1 - Math.Clamp((map.Clipped - .38) / .58, 0, 1)));
        var resolution = 30 * Math.Clamp(Math.Min(sourceWidth / 1600d, sourceHeight / 500d), 0, 1);
        return new(scene, 25 * quiet, tone, resolution, left, right, preferRight);
    }

    public static (bool Right, double Cost) Placement(RasterPixels image, double width, double height)
    {
        var map = Read(image);
        var w = Math.Clamp(width, .05, 1); var h = Math.Clamp(height, .05, 1);
        var left = Occlusion(map, .03, Math.Max(0, .97 - h), w, h);
        var right = Occlusion(map, Math.Max(0, .97 - w), Math.Max(0, .97 - h), w, h);
        return right + .07 < left ? (true, right) : (false, left);
    }

    private static double Occlusion(Map map, double x, double y, double width, double height)
    {
        double saliency = 0, detail = 0; int count = 0;
        for (int row = 0; row < Rows; row++) for (int column = 0; column < Columns; column++)
        {
            double px = (column + .5) / Columns, py = (row + .5) / Rows;
            if (px < x || px > x + width || py < y || py > y + height) continue;
            int at = row * Columns + column; saliency += map.Saliency[at]; detail += map.Detail[at]; count++;
        }
        var total = map.Saliency.Sum();
        // The fraction of visual interest covered matters more than empty area size.
        return Math.Clamp(.72 * (total > .001 ? saliency / total : 1) + .28 * Math.Clamp(detail / Math.Max(1, count) / .14, 0, 1), 0, 1);
    }

    private static Map Read(RasterPixels image)
    {
        if (image.Width < 1 || image.Height < 1 || image.Bgra.LongLength != (long)image.Width * image.Height * 4)
            throw new ArgumentException("Invalid banner raster");
        var luma = new double[Columns * Rows]; var red = new double[luma.Length]; var blue = new double[luma.Length];
        double sum = 0, squares = 0, clipped = 0;
        // Average four samples per cell. This also makes the score stable across decode sizes.
        for (int y = 0; y < Rows; y++) for (int x = 0; x < Columns; x++)
        {
            double r = 0, g = 0, b = 0;
            for (int oy = 0; oy < 2; oy++) for (int ox = 0; ox < 2; ox++)
            {
                double dx = .25 + ox * .5, dy = .25 + oy * .5;
                int sx = Math.Min(image.Width - 1, (int)((x + dx) * image.Width / Columns));
                int sy = Math.Min(image.Height - 1, (int)((y + dy) * image.Height / Rows));
                int at = (sy * image.Width + sx) * 4; r += image.Bgra[at + 2] / 1020d; g += image.Bgra[at + 1] / 1020d; b += image.Bgra[at] / 1020d;
            }
            int p = y * Columns + x; luma[p] = .2126 * r + .7152 * g + .0722 * b; red[p] = r - g; blue[p] = b - g;
            sum += luma[p]; squares += luma[p] * luma[p]; if (luma[p] < .015 || luma[p] > .985) clipped++;
        }
        double mean = sum / luma.Length, mr = red.Average(), mb = blue.Average();
        var detail = new double[luma.Length]; var saliency = new double[luma.Length];
        for (int y = 0; y < Rows; y++) for (int x = 0; x < Columns; x++)
        {
            int at = y * Columns + x;
            detail[at] = (Math.Abs(luma[at] - luma[y * Columns + Math.Max(0, x - 1)]) +
                Math.Abs(luma[at] - luma[Math.Max(0, y - 1) * Columns + x]));
            saliency[at] = .02 + Math.Min(.45, Math.Abs(luma[at] - mean)) +
                .35 * (Math.Abs(red[at] - mr) + Math.Abs(blue[at] - mb)) + Math.Min(.25, detail[at]);
        }
        return new(luma, detail, saliency, Math.Max(0, squares / luma.Length - mean * mean), clipped / luma.Length);
    }

    // Only near-identical whole scenes, within the same verified work, qualify for
    // a resolution upgrade. This never synthesizes pixels or searches by title.
    public static bool SameScene(RasterPixels first, RasterPixels second)
    {
        if (Math.Abs(Math.Log(first.Width / (double)first.Height / (second.Width / (double)second.Height))) > .025) return false;
        double difference = 0; const int w = 32, h = 18;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int a = (Math.Min(first.Height - 1, (2 * y + 1) * first.Height / (2 * h)) * first.Width + Math.Min(first.Width - 1, (2 * x + 1) * first.Width / (2 * w))) * 4;
            int b = (Math.Min(second.Height - 1, (2 * y + 1) * second.Height / (2 * h)) * second.Width + Math.Min(second.Width - 1, (2 * x + 1) * second.Width / (2 * w))) * 4;
            for (int c = 0; c < 3; c++) difference += Math.Abs(first.Bgra[a + c] - second.Bgra[b + c]) / 255d;
        }
        return difference / (w * h * 3) < .032;
    }
}
