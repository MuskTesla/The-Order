using TheOrder.WinUI.Materials;
using Xunit;
using System.Xml.Linq;

namespace TheOrder.Tests;

public sealed class MaterialResourceTests
{
    private sealed class Resource(Action close) : IDisposable { public void Dispose() => close(); }
    [Fact] public void PartialInitializationReleasesDependenciesInReverseOrder()
    {
        var calls = new List<int>(); using var scope = new MaterialResources();
        scope.Own(new Resource(() => calls.Add(1))); scope.Own(new Resource(() => calls.Add(2)));
        scope.Dispose(); scope.Dispose(); Assert.Equal([2, 1], calls); Assert.Equal(0, scope.Remaining);
    }
    [Fact] public void FailedNativeCloseIsVisibleAndRetriedWithoutLosingReference()
    {
        var attempts = 0; var scope = new MaterialResources();
        scope.Own(new Resource(() => { if (++attempts == 1) throw new InvalidOperationException(); }));
        scope.Dispose(); Assert.Equal(1, scope.Remaining); Assert.Equal(1, scope.CleanupFailures);
        scope.Dispose(); Assert.Equal(0, scope.Remaining); Assert.Equal(2, attempts);
    }
    [Fact] public void FailureOfOneObjectDoesNotPreventOtherNativeCloses()
    {
        var closed = false; var scope = new MaterialResources();
        scope.Own(new Resource(() => closed = true)); scope.Own(new Resource(() => throw new InvalidOperationException()));
        scope.Dispose(); Assert.True(closed); Assert.Equal(1, scope.Remaining);
    }
    [Fact] public void LateAllocationAfterCancellationIsClosedBeforeRejectingOwnership()
    {
        var scope = new MaterialResources(); scope.Dispose(); var closed = false;
        Assert.Throws<ObjectDisposedException>(() => scope.Own(new Resource(() => closed = true)));
        Assert.True(closed); Assert.Equal(0, scope.Remaining);
    }
    [Fact] public void OrdinaryAccentTokensFollowSystemAndDoNotOverridePlatformControls()
    {
        var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "material-fixtures", "DesignTokens.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var theme in doc.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(x+"Key")?.Value is "Light" or "Dark"))
        {
            var brushes = theme.Elements().Where(e => e.Name.LocalName == "SolidColorBrush").ToArray();
            var accent = Assert.Single(brushes, e => e.Attribute(x+"Key")?.Value == "AccentBrush");
            Assert.Contains("ThemeResource SystemAccentColor", accent.Attribute("Color")!.Value);
            Assert.DoesNotContain(brushes, e => e.Attribute(x+"Key")?.Value.StartsWith("AccentButton", StringComparison.Ordinal) == true);
            foreach (var key in new[] {"ErrorTextBrush", "WarningTextBrush", "SuccessTextBrush", "RecoveryTextBrush"})
                Assert.DoesNotContain("Accent", Assert.Single(brushes, e => e.Attribute(x+"Key")?.Value == key).Attribute("Color")!.Value);
        }
    }
}
