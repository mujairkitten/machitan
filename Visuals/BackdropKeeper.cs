using System.Collections.Generic;

namespace Machitan.Visuals;

/// <summary>
/// Keeps backdrop objects referenced until the process ends.
///
/// Why: some of the Windows App SDK objects a backdrop receives or creates (the link that
/// attaches a backdrop to one element lives in Microsoft.UI.Input.dll) may only be released
/// on the UI thread. When .NET drops its last reference, it releases it later on its own
/// clean-up (finalizer) thread instead, and the Windows App SDK then stops the whole app
/// (fail-fast 0xc0000409, "The caller must call Close / Dispose from the owning thread").
/// Held here, they are never released while Machitan runs; .NET does not run that clean-up
/// at exit. Controllers are still closed properly on the UI thread when detached.
/// The cost is a few small objects each time a fill is replaced.
/// </summary>
internal static class BackdropKeeper
{
    private static readonly object Gate = new();
    private static readonly List<object> Held = new();

    internal static void Hold(object? item)
    {
        if (item is null)
        {
            return;
        }

        lock (Gate)
        {
            Held.Add(item);
        }
    }
}
