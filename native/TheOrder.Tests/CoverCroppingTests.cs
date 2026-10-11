using TheOrder.Application;
using Xunit;

namespace TheOrder.Tests;

public sealed class CoverCroppingTests
{
    [Fact] public void PosterWithLowLetteringRetainsBottomDetail()
    {
        var pixels = Image(128, 192, (x, y) => y >= 171 && x is > 12 and < 116 && (x / 4 + y / 4) % 2 == 0);
        var before = pixels.Bgra.ToArray(); var crop = CoverCropping.Plan(pixels, .75);
        Assert.True(crop.Y > 17); Assert.Equal(192, crop.Y + crop.Height, 1);
        Assert.Equal(before, pixels.Bgra);
    }
    [Fact] public void TopDetailWinsWhenBottomHasNoImportantContent()
    {
        var pixels = Image(128, 192, (x, y) => y < 20 && x is > 12 and < 116 && (x / 4 + y / 4) % 2 == 0);
        Assert.InRange(CoverCropping.Plan(pixels, .75).Y, 0, 3);
    }
    [Fact] public void LandscapeCropKeepsAnOffCenterSubject()
    {
        var pixels = Image(320, 160, (x, y) => x is > 8 and < 75 && y is > 16 and < 145 && (x / 4 + y / 4) % 2 == 0);
        Assert.InRange(CoverCropping.Plan(pixels, .75).X, 0, 8);
    }
    [Theory][InlineData(.35)][InlineData(.75)][InlineData(1)][InlineData(1.8)][InlineData(8)]
    public void SourceWindowIsBoundedAndHasExactlyTheRequestedAspect(double aspect)
    {
        var crop = CoverCropping.Plan(Image(128, 192, (_, _) => false), aspect);
        Assert.InRange(crop.X, 0, 128 - crop.Width + .001); Assert.InRange(crop.Y, 0, 192 - crop.Height + .001);
        Assert.Equal(aspect, crop.Width / crop.Height, 8);
        Assert.True(Math.Abs(crop.Width - 128) < .001 || Math.Abs(crop.Height - 192) < .001);
    }
    [Fact] public void MatchingAspectUsesAllSourcePixels()
        => Assert.Equal(new CoverViewport(0, 0, 128, 192), CoverCropping.Plan(Image(128, 192, (_, _) => false), 2d / 3));
    private static RasterPixels Image(int width, int height, Func<int, int, bool> detail)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        { int i = (y * width + x) * 4; byte color = detail(x, y) ? (byte)255 : (byte)40; bytes[i] = bytes[i + 1] = bytes[i + 2] = color; bytes[i + 3] = 255; }
        return new(width, height, bytes);
    }
}
