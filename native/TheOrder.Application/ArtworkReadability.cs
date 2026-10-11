namespace TheOrder.Application;

// Select the text polarity that needs less tint over the actual title area.
// A light app theme must not flatten a dark CG into a gray reading panel.
public static class ArtworkReadability
{
    public static bool PreferLightText(RasterPixels image, double left, double top, double width, double height)
    {
        if (image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height * 4 != image.Bgra.LongLength)
            throw new ArgumentException("Invalid artwork pixels.", nameof(image));
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0) throw new ArgumentException("Invalid title region.");
        double x0 = Math.Clamp(left, 0, 1), y0 = Math.Clamp(top, 0, 1);
        double x1 = Math.Clamp(left + width, x0, 1), y1 = Math.Clamp(top + height, y0, 1);
        double darkText = 0, lightText = 0;
        // 384 samples, regardless of image/window/DPI. No new pixel buffers.
        for (int y = 0; y < 16; y++) for (int x = 0; x < 24; x++)
        {
            int sx = Math.Min(image.Width - 1, (int)((x0 + (x + .5) / 24 * (x1 - x0)) * image.Width));
            int sy = Math.Min(image.Height - 1, (int)((y0 + (y + .5) / 16 * (y1 - y0)) * image.Height));
            int at = (sy * image.Width + sx) * 4;
            double b = image.Bgra[at] / 255d, g = image.Bgra[at + 1] / 255d, r = image.Bgra[at + 2] / 255d;
            darkText += RequiredTint(r, g, b, lift: true);
            lightText += RequiredTint(r, g, b, lift: false);
        }
        return lightText < darkText;
    }

    private static double RequiredTint(double r, double g, double b, bool lift)
    {
        // Match the existing scene scrim and shader's linear-light contrast
        // bounds. Choose the clearer of two readable treatments; don't lower
        // the contrast bound to make a flat panel appear more transparent.
        double Linear(double c) => Math.Pow(c, 2.2);
        double luminance = .2126 * Linear(r) + .7152 * Linear(g) + .0722 * Linear(b);
        double tint = Math.Pow((lift ? 250 : 32) / 255d, 2.2);
        double bound = lift ? .30 : .10;
        double needed = lift ? luminance < bound ? (bound - luminance) / Math.Max(tint - luminance, 1e-4) : 0
            : luminance > bound ? (luminance - bound) / Math.Max(luminance - tint, 1e-4) : 0;
        return Math.Max(lift ? .055 : .075, Math.Clamp(needed, 0, 1));
    }
}
