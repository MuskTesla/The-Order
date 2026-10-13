using TheOrder.Application;
using Xunit;

namespace TheOrder.Tests;

public sealed class BannerAestheticsTests
{
    private static RasterPixels Scene(int width = 192, int height = 108, bool mirror = false, bool noise = false)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double u = (mirror ? width - 1 - x : x) / (double)width, v = y / (double)height;
            bool focus = u > .69 && v > .23 && v < .91;
            byte light = (byte)(focus ? ((x / 3 + y / 3) % 2 == 0 ? 220 : 110) : 35 + 15 * v);
            if (noise) light = (byte)((x + y) % 2 == 0 ? 250 : 5);
            int at = (y * width + x) * 4; bytes[at] = light; bytes[at + 1] = (byte)Math.Min(255, light + 10); bytes[at + 2] = light; bytes[at + 3] = 255;
        }
        return new(width, height, bytes);
    }
    [Fact] public void QuietTitleSpaceIsOppositeSceneInterestAndMirrorsWithIt()
    {
        var right = BannerAesthetics.Analyze(Scene(), 1920, 1080);
        var left = BannerAesthetics.Analyze(Scene(mirror:true), 1920, 1080);
        Assert.False(right.PreferRight); Assert.True(left.PreferRight);
        Assert.True(right.LeftOcclusion < right.RightOcclusion); Assert.True(left.RightOcclusion < left.LeftOcclusion);
        Assert.InRange(Math.Abs(right.Score - left.Score), 0, 2);
    }
    [Fact] public void ResolutionSaturatesAtDisplayNeedsAndDoesNotRewardMegapixelsForever()
    {
        var image = Scene();
        var low = BannerAesthetics.Analyze(image, 640, 360);
        var hd = BannerAesthetics.Analyze(image, 1920, 1080);
        var huge = BannerAesthetics.Analyze(image, 7680, 4320);
        Assert.True(hd.Score >= low.Score + 17); Assert.Equal(hd, huge);
        Assert.Equal(low.Scene, hd.Scene); Assert.Equal(low.TextSpace, hd.TextSpace);
    }
    [Fact] public void BannerDoesNotLosePointsJustBecauseItIsAnOfficialWideLandscape()
    {
        var standard = BannerAesthetics.Analyze(Scene(), 1920, 1080);
        var wide = BannerAesthetics.Analyze(Scene(192,62), 1920, 620);
        Assert.Equal(standard.Resolution, wide.Resolution);
        Assert.InRange(Math.Abs(standard.Score - wide.Score), 0, 7);
    }
    [Fact] public void WholeSceneMatchingDoesNotConfuseDistinctCompositions()
    {
        var original = Scene(); var copy = new RasterPixels(original.Width, original.Height, original.Bgra.ToArray());
        for (int i = 0; i < copy.Bgra.Length; i += 4) copy.Bgra[i]++;
        Assert.True(BannerAesthetics.SameScene(original, copy));
        Assert.False(BannerAesthetics.SameScene(original, Scene(mirror:true)));
        Assert.False(BannerAesthetics.SameScene(original, Scene(192,62)));
    }
    [Theory]
    [InlineData(1920,620,1000)]
    [InlineData(800,600,1000)]
    [InlineData(800,450,1360)]
    [InlineData(1280,720,440)]
    [InlineData(1080,1920,1100)]
    public void FrameFillsContentWidthAtTheNativeAspectEvenForLowResolutionSources(int sourceWidth,int sourceHeight,double available)
    {
        var frame = BannerLayout.Plan(available,sourceWidth,sourceHeight,620,260);
        Assert.Equal(sourceWidth / (double)sourceHeight, frame.Width / frame.Height, 6);
        Assert.Equal(available,frame.Width);
    }
    [Fact] public void OverlayIsPreservedDespiteBusySceneAndOnlyUnfittableTextMovesBelow()
    {
        var room = BannerLayout.Plan(1100,1920,1080,620,250,.30);
        Assert.False(room.Docked);
        Assert.False(BannerLayout.Plan(1100,1920,1080,620,250,.9).Docked);
        Assert.True(BannerLayout.Plan(1100,1920,1080,900,600,.3).Docked);
        Assert.Equal(room.Width,BannerLayout.Plan(1100,1920,1080,900,600,.3).Width);
    }
    [Fact] public void MissingArtworkHasNoEmptyBannerButKeepsAnUnclippedInformationRow()
    {
        var frame=BannerLayout.Plan(1000,0,0,620,260);
        Assert.True(frame.Docked); Assert.Equal(0,frame.Height); Assert.Equal(1000,frame.Width);
        Assert.Equal(new BannerFrame(0,0,true),BannerLayout.Plan(double.NaN,1920,620,620,260));
    }
}
