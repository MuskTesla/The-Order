using System.Runtime.InteropServices;
using TheOrder.WinUI.Materials;
using Xunit;

namespace TheOrder.Tests;

public sealed class MaterialTreeProbeTests
{
    [Fact] public void StowedComFailureIsUnavailableAndLaterReadsRecover()
    {
        var probe = new MaterialTreeProbe();
        Assert.Null(probe.Read(() => throw new COMException("Tree unavailable", unchecked((int)0x80004005))));
        Assert.Equal(1, probe.Failures);
        Assert.Equal("COMException:0x80004005", probe.LastError);
        Assert.Equal(29, probe.Read(() => 29));
        Assert.Equal(1, probe.Failures);
    }
    [Fact] public void RepeatedFailuresNeverBecomeZeroOrAccumulateExceptionHistory()
    {
        var probe = new MaterialTreeProbe();
        for (var i = 0; i < 64; i++) Assert.Null(probe.Read(() => throw new COMException()));
        Assert.Equal(64, probe.Failures);
        Assert.Equal(0, probe.Read(() => 0));
    }
    [Fact] public void ProgrammingErrorsAreNotHiddenAsPlatformSamplingFailures()
    {
        var probe = new MaterialTreeProbe();
        Assert.Throws<InvalidOperationException>(() => probe.Read(() => throw new InvalidOperationException()));
        Assert.Equal(0, probe.Failures);
    }
}
