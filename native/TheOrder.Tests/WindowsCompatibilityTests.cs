using TheOrder.Platform.Windows;
using TheOrder.WinUI.Materials;
using Xunit;

namespace TheOrder.Tests;

public sealed class WindowsCompatibilityTests
{
    [Theory]
    [InlineData(6, 3, 9600, false)]
    [InlineData(10, 0, 17134, false)]
    [InlineData(10, 0, 17762, false)]
    [InlineData(10, 0, 17763, true)]
    [InlineData(10, 0, 19044, true)]
    [InlineData(10, 0, 19045, true)]
    [InlineData(10, 0, 22000, true)]
    [InlineData(10, 0, 26100, true)]
    public void StartupFloorAcceptsWindows10WithoutAdmittingOlderHosts(int major, int minor, int build, bool expected)
        => Assert.Equal(expected, WindowsCompatibility.IsSupported(new(major, minor, build)));

    [Theory][InlineData(17763)][InlineData(19044)][InlineData(19045)]
    public void Windows10DoesNotActivateOrEvenProbeWindows11Mica(int build)
    {
        var supported = WindowsCompatibility.CanUseMica(new(10, 0, build), () => throw new Xunit.Sdk.XunitException("Win11-only probe invoked on Win10"));
        Assert.False(supported);
        var owner = new WindowBackdropState(); var operations = new List<string>();
        owner.Apply(true, supported, () => operations.Add("mica"), () => operations.Add("solid"));
        Assert.Equal(new[] { "solid" }, operations); Assert.False(owner.UsingMica); Assert.Null(owner.Failure);
    }

    [Fact] public void ControllerProbeFailureOnNewHostUsesSolidInsteadOfFailingStartup()
        => Assert.False(WindowsCompatibility.CanUseMica(new(10, 0, 26100), () => throw new System.Runtime.InteropServices.COMException("Unavailable")));

    [Fact] public void MicaAttachmentFailureIsLatchedAndPolicyChangesCannotCreateRetryLoop()
    {
        var owner = new WindowBackdropState(); int tries = 0, fallback = 0;
        void Mica() { tries++; throw new InvalidOperationException("Synthetic compositor failure"); }
        for (int i = 0; i < 50; i++) owner.Apply(i % 3 != 0, true, Mica, () => fallback++);
        Assert.Equal(1, tries); Assert.Equal(50, fallback); Assert.False(owner.UsingMica);
        Assert.Equal(nameof(InvalidOperationException), owner.Failure);
    }

    [Fact] public void SupportedHostStillTracksLiveTransparencyAndContrastPolicy()
    {
        var owner = new WindowBackdropState(); var operations = new List<string>();
        void Apply(bool effects) => owner.Apply(effects, true, () => operations.Add("mica"), () => operations.Add("solid"));
        Apply(true); Assert.True(owner.UsingMica);
        Apply(false); Assert.False(owner.UsingMica);
        Apply(true); Assert.True(owner.UsingMica);
        Assert.Equal(new[] { "mica", "solid", "mica" }, operations); Assert.Null(owner.Failure);
    }

    [Fact] public void MissingControllerSupportDoesNotCreateMaterialEvenWithTransparencyEnabled()
    {
        var owner = new WindowBackdropState(); int mica = 0, solid = 0;
        owner.Apply(true, false, () => mica++, () => solid++);
        Assert.Equal(0, mica); Assert.Equal(1, solid);
    }
}
