using TheOrder.WinUI.Materials;
using Xunit;

namespace TheOrder.Tests;

public sealed class LensOpticsTests
{
    [Fact] public void NonFiniteReviewInputUsesSafeFiniteDefaults()
    {
        var p = new LensOptics(float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.NaN).Bounded();
        Assert.Equal(LensOptics.Recommended, p);
    }
    [Fact] public void ExtremeReviewInputCannotExpandResourceOrSamplingRange()
    {
        Assert.Equal(new LensOptics(1.5f, 2, 8, 1), new LensOptics(1000, 1000, -100, 1000).Bounded());
        Assert.Equal(new LensOptics(0, 0, 22, 0), new LensOptics(-10, -10, 100, -10).Bounded());
    }
    [Theory] [InlineData(90)] [InlineData(160)] [InlineData(320)]
    public void ZeroDispersionMakesAllThreeSamplingCoordinatesIdentical(float extent)
    {
        var p = LensOptics.Recommended with { Dispersion = 0 };
        Assert.Equal(p.Scale(extent, 0), p.Scale(extent, -1)); Assert.Equal(p.Scale(extent, 0), p.Scale(extent, 1));
    }
    [Fact] public void ChannelsRemainInsideOpaqueLensSourceEvenAtZeroRefraction()
    {
        foreach (var strength in new[] { 0f, 1, 1.5f }) foreach (var spread in new[] { 0f, .75f, 2f })
            foreach (var extent in new[] { 1f, 90, 160 }) foreach (var channel in new[] { -1, 0, 1 })
            {
                var scale = new LensOptics(strength, spread, 18, .45f).Scale(extent, channel);
                Assert.True(scale >= 1 && float.IsFinite(scale));
                Assert.InRange(extent / scale, 0, extent);
            }
    }
    [Fact] public void EdgeFadeIsMonotonicAndHasQuietInnerLimit()
    {
        var previous = 1f;
        for (var i = 0; i <= 100; i++) { var a = LensOptics.EdgeOpacity(i / 100f); Assert.InRange(a, 0, previous); previous = a; }
        Assert.Equal(1, LensOptics.EdgeOpacity(0)); Assert.Equal(0, LensOptics.EdgeOpacity(1));
        Assert.InRange(LensOptics.EdgeOpacity(.999f), 0, .00001f);
        Assert.InRange(1 - LensOptics.EdgeOpacity(.001f), 0, .00001f);
    }
    [Fact] public void CollapsedLayoutDoesNotCreateAnInvalidTransform()
    {
        Assert.Equal(1, LensOptics.Recommended.Scale(0, 1)); Assert.Equal(1, LensOptics.Recommended.Scale(float.NaN, -1));
    }
}
