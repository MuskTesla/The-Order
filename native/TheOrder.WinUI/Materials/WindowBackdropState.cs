namespace TheOrder.WinUI.Materials;

// The caller supplies UI operations; this owner is also exercised without a WinRT
// compositor. Attachment failure is latched to avoid a retry/crash loop on refresh.
internal sealed class WindowBackdropState
{
    internal bool UsingMica { get; private set; }
    internal string? Failure { get; private set; }

    internal void Apply(bool effectsAllowed, bool micaSupported, Action mica, Action solid)
    {
        if (effectsAllowed && micaSupported && Failure is null)
        {
            try { mica(); UsingMica = true; return; }
            catch (Exception exception) { Failure = exception.GetType().Name; }
        }
        UsingMica = false;
        solid();
    }
}
