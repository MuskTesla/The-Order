using TheOrder.Application;
using Xunit;

namespace TheOrder.Tests;

public sealed class ArtworkReadabilityTests
{
    [Fact] public void NightArtworkKeepsLightTextRatherThanAFlatLightPanel()
    {
        var image = Image(32, 28, 48, 90);
        Assert.True(ArtworkReadability.PreferLightText(image, 0, 0, 1, 1));
    }

    [Fact] public void BrightArtworkKeepsDarkText()
    {
        var image = Image(32, 225, 235, 245);
        Assert.False(ArtworkReadability.PreferLightText(image, 0, 0, 1, 1));
    }

    [Fact] public void TitleRegionWinsOverTheRestOfTheCg()
    {
        var image = Image(64, 235, 235, 235);
        for (int y = 32; y < 64; y++) for (int x = 0; x < 32; x++)
        {
            int at = (y * 64 + x) * 4;
            image.Bgra[at] = 52; image.Bgra[at + 1] = 30; image.Bgra[at + 2] = 20;
        }
        var original = image.Bgra.ToArray();
        Assert.False(ArtworkReadability.PreferLightText(image, .5, 0, .5, .5));
        Assert.True(ArtworkReadability.PreferLightText(image, 0, .5, .5, .5));
        Assert.Equal(original, image.Bgra);
    }

    [Fact] public void PartlyOutsideTitleRegionIsClampedWithoutReadingOutsideTheImage()
    {
        Assert.True(ArtworkReadability.PreferLightText(Image(1, 0, 0, 0), -1, -1, 3, 3));
    }

    [Fact] public void InvalidRegionAndPixelsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ArtworkReadability.PreferLightText(Image(1, 0, 0, 0), 0, 0, 0, 1));
        Assert.Throws<ArgumentException>(() => ArtworkReadability.PreferLightText(Image(1, 0, 0, 0), double.NaN, 0, 1, 1));
        Assert.Throws<ArgumentException>(() => ArtworkReadability.PreferLightText(new(2, 2, new byte[4]), 0, 0, 1, 1));
    }

    private static RasterPixels Image(int size, byte r, byte g, byte b)
    {
        var pixels = new byte[size * size * 4];
        for (int at = 0; at < pixels.Length; at += 4) { pixels[at] = b; pixels[at + 1] = g; pixels[at + 2] = r; pixels[at + 3] = 255; }
        return new(size, size, pixels);
    }
}
