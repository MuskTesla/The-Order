namespace TheOrder.WinUI.Materials;

// Independent finite profiles; the historical LensOptics baseline stays unchanged.
internal readonly record struct OpticalProfile(float BevelWidth, float Depth, float Dispersion, float Gloss)
{
    internal static OpticalProfile Reference => new(30, 30, .8f, .55f);
    internal static OpticalProfile Daily => new(20, 16, .28f, .32f);
}
