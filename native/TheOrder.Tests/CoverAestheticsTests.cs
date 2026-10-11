using System.Collections.Immutable;
using TheOrder.Application;
using TheOrder.Domain;
using Xunit;
using static TheOrder.Tests.RecognitionBoundaryTests;

namespace TheOrder.Tests;

public sealed class CoverAestheticsTests
{
    private static RasterPixels Picture(int width, int height, Func<double, double, byte>? gray = null)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4; var v = gray?.Invoke(x / (double)width, y / (double)height) ?? (byte)100;
            bytes[i] = bytes[i + 1] = bytes[i + 2] = v; bytes[i + 3] = 255;
        }
        return new(width, height, bytes);
    }
    private static CoverCandidate Candidate(string id, int width, int height, bool primary = false) =>
        new(new(id, "https://images.example/" + id, width, height, 0, "safe"), primary ? "catalog" : "vndb", primary);
    [Fact] public void PortraitCompositionBeatsLargeLandscapeThatLosesHalfTheArt()
    {
        var portrait = CoverAesthetics.Assess(Candidate("portrait", 600, 800), Picture(96, 128));
        var wide = CoverAesthetics.Assess(Candidate("wide", 4000, 2000), Picture(256, 128));
        Assert.Equal(1, portrait.Framing); Assert.True(wide.Framing < .4);
        Assert.Equal(portrait, CoverAesthetics.Choose([wide, portrait], wide.Candidate.Cover.Url));
    }
    [Fact] public void ExtraPixelsBeyondDisplayNeedDoNotBuyAestheticPoints()
    {
        var pixels = Picture(90, 120);
        var sufficient = CoverAesthetics.Assess(Candidate("normal", 360, 480), pixels);
        var huge = CoverAesthetics.Assess(Candidate("huge", 3600, 4800), pixels);
        var tiny = CoverAesthetics.Assess(Candidate("tiny", 90, 120), pixels);
        Assert.Equal(sufficient.Score, huge.Score); Assert.Equal(1, sufficient.Resolution);
        Assert.True(sufficient.Score - tiny.Score >= 14);
    }
    [Fact] public void SimilarPosterCompositionKeepsCuratedDefaultDespiteLargerAlternative()
    {
        var primary = CoverAesthetics.Assess(Candidate("poster", 600, 900, true), Picture(80, 120));
        var alternative = CoverAesthetics.Assess(Candidate("front", 1238, 1500), Picture(99, 120));
        Assert.Equal(primary, CoverAesthetics.Choose([alternative, primary], alternative.Candidate.Cover.Url));
        // The policy is generic: it never reads a work title or a particular artwork ID.
        var renamed = primary with { Candidate = Candidate("another-game", 600, 900, true) };
        Assert.Equal(renamed, CoverAesthetics.Choose([renamed, alternative], null));
    }
    [Fact] public void ClearImprovementOverridesDefaultAndSmallChangesDoNotChurnOtherChoices()
    {
        var pixels = Picture(90, 120);
        var primary = CoverAesthetics.Assess(Candidate("tiny-default", 60, 80, true), pixels);
        var best = CoverAesthetics.Assess(Candidate("clear", 600, 800), pixels);
        Assert.Equal(best, CoverAesthetics.Choose([primary, best], primary.Candidate.Cover.Url));
        var previous = best with { Candidate = Candidate("previous", 600, 800), Score = best.Score - 1 };
        Assert.Equal(previous, CoverAesthetics.Choose([best, previous], previous.Candidate.Cover.Url));
    }
    [Fact] public void CroppingPenalizesLostDetailAndKeepsAllComponentsBounded()
    {
        static byte Mark(double x, double y) => x > .02 && x < .10 || x > .9 && x < .98 ? (byte)(((int)(y * 64) % 2) * 255) : (byte)100;
        var assessment = CoverAesthetics.Assess(Candidate("wide", 1200, 600), Picture(256, 128, Mark));
        Assert.True(assessment.DetailPreserved < .8);
        foreach (var value in new[] { assessment.Framing, assessment.DetailPreserved, assessment.Boundary, assessment.Balance, assessment.Resolution }) Assert.InRange(value, 0, 1);
        Assert.InRange(assessment.Score, 0, 100);
    }
    [Fact] public void ChangingBrightnessOrInvertingColorsDoesNotEarnBeautyPoints()
    {
        var candidate = Candidate("cover", 600, 800);
        byte Light(double x, double y) => (byte)(70 + (x > .35 && x < .65 && y > .2 && y < .8 ? 80 : 0));
        var first = CoverAesthetics.Assess(candidate, Picture(90, 120, Light));
        var inverted = CoverAesthetics.Assess(candidate, Picture(90, 120, (x, y) => (byte)(255 - Light(x, y))));
        Assert.Equal(first.Score, inverted.Score, 8);
    }
    [Fact] public void GalleryScansNeverEnterRankingAndProviderBudgetIsThree()
    {
        WorkCover primary = new("1", "https://images.example/primary", 600, 900, 0, null);
        WorkCover vndb = new("vndb:cv1", "https://t.vndb.org/cv/01/1.jpg", 600, 800, null, "safe");
        WorkCover steam = new("steam:42", "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/42/library_600x900.jpg", 600, 900, null, null);
        var scans = Enumerable.Range(2, 100).Select(i => new WorkCover(i.ToString(), "https://images.example/disc-" + i, 6000, 8000, 99999, null));
        var work = Work() with { Covers = [primary, vndb, steam, .. scans] };
        var candidates = CoverAesthetics.Candidates(work, primary);
        Assert.Equal(3, candidates.Length); Assert.DoesNotContain(candidates, c => c.Cover.Url.Contains("disc"));
        Assert.Single(CoverAesthetics.Candidates(work with { Covers = [primary, vndb, vndb with { Id = "vndb:cv2", Url = "https://t.vndb.org/cv/02/2.jpg" }] }, primary));
        Assert.Equal(2, CoverAesthetics.Candidates(work, vndb).Length); // duplicated primary URL once
    }
    [Fact] public void InputSignatureIgnoresUnqualifiedGalleryButChangesWithPrimaryOrDimensions()
    {
        WorkCover primary = new("1", "https://images.example/front", 600, 800, 0, null);
        var work = Work() with { Covers = [primary] };
        string Key(WorkMetadata w, WorkCover p) => CoverAesthetics.Signature(CoverAesthetics.Candidates(w, p));
        Assert.Equal(Key(work, primary), Key(work with { Covers = [primary, new("2", "https://images.example/book", 6000, 9000, 0, null)] }, primary));
        Assert.NotEqual(Key(work, primary), Key(work, primary with { Width = 800 }));
        Assert.NotEqual(Key(work, primary), Key(work, primary with { Url = "https://images.example/new" }));
    }
}
