using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Windows.Graphics.Effects;
using WinRT;

namespace TheOrder.WinUI.Materials;

internal static class ChannelEffect
{
    [DllImport("TheOrder.MaterialEffects.dll", ExactSpelling = true)]
    private static extern int CreateChannelEffect(IntPtr red, IntPtr green, IntPtr blue, out IntPtr effect);

    internal static CompositionEffectFactory CreateFactory(Compositor compositor)
    {
        // Three parameters refer to brushes over ONE live source surface.
        using var r = MarshalInterface<IGraphicsEffectSource>.CreateMarshaler(new CompositionEffectSourceParameter("red"));
        using var g = MarshalInterface<IGraphicsEffectSource>.CreateMarshaler(new CompositionEffectSourceParameter("green"));
        using var b = MarshalInterface<IGraphicsEffectSource>.CreateMarshaler(new CompositionEffectSourceParameter("blue"));
        Marshal.ThrowExceptionForHR(CreateChannelEffect(r.ThisPtr, g.ThisPtr, b.ThisPtr, out var pointer));
        try
        {
            var graph = MarshalInterface<IGraphicsEffect>.FromAbi(pointer);
            try { return compositor.CreateEffectFactory(graph); }
            finally { ((IWinRTObject)graph).NativeObject.Dispose(); }
        }
        finally { Marshal.Release(pointer); }
    }
}
