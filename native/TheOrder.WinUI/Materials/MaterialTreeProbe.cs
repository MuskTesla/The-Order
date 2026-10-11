using System.Runtime.InteropServices;

namespace TheOrder.WinUI.Materials;

// A diagnostic read must not turn an unavailable WinUI tree into an application
// crash or a false zero. Keep one error and a cumulative count, never a history.
internal sealed class MaterialTreeProbe
{
    internal long Failures { get; private set; }
    internal string? LastError { get; private set; }
    internal int? Read(Func<int> sample)
    {
        try { return sample(); }
        catch (COMException exception)
        {
            Failures++;
            LastError = $"COMException:0x{exception.HResult:X8}";
            return null;
        }
    }
}
