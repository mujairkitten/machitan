using System;

namespace Machitan.Visuals;

/// <summary>
/// The one corner-radius scale (spec, Batch 7), defined once: 16 DIP on large surfaces (app
/// windows, Start menu boxes, flyouts, the Classic bar, preview cards), 10 DIP on medium
/// controls (task buttons' tiles, pinned tiles, All apps rows, Settings cards), 8 DIP on small
/// inner rows and buttons. Capsules (bar pills, search pills, the info line, the status ring,
/// switches) stay fully rounded; icon tiles keep about a quarter of their size.
/// Dynaland's own shapes (notch and island) are its design and keep their own radii.
/// </summary>
internal static class Radii
{
    internal const double Large = 16;
    internal const double Medium = 10;
    internal const double Small = 8;

    /// <summary>Fully rounded: half the height.</summary>
    internal static double Capsule(double heightDip)
    {
        return heightDip / 2;
    }

    /// <summary>An icon tile: about a quarter of its size.</summary>
    internal static double IconTile(double tileDip)
    {
        return Math.Max(tileDip * 0.25, 1);
    }
}
