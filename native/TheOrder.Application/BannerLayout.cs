namespace TheOrder.Application;

public readonly record struct BannerFrame(double Width, double Height, bool Docked);

public static class BannerLayout
{
    // Fill the content width and derive height from the source's own aspect.
    // Resolution affects selection and decode quality, never the page's alignment.
    public static BannerFrame Plan(double availableWidth, int sourceWidth, int sourceHeight,
        double plateWidth, double plateHeight, double occlusion = 0)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return new(0, 0, true);
        if (sourceWidth <= 0 || sourceHeight <= 0) return new(availableWidth, 0, true);
        double aspect = sourceWidth / (double)sourceHeight;
        double width = availableWidth;
        double height = width / aspect;
        double margin = availableWidth < 560 ? 8 : 28;
        // The measured, compact information panel gets the full scene height.
        // Saliency chooses its side, never forces it below a perfectly usable CG.
        // Dock only after the caller has tried a wider/denser overlay.
        bool docked = plateWidth + margin * 2 > width + .5 || plateHeight + margin * 2 > height + .5;
        return new(width, height, docked);
    }
}
