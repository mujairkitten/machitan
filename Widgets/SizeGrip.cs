using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Machitan.Widgets;

/// <summary>
/// A see-through strip that shows a resize pointer: left-right (the sidebar's inner edge, a
/// desktop widget's sides), up-down (a widget's bottom edge, a desktop widget's top) or
/// slanted (a desktop widget's corners).
/// </summary>
public sealed partial class SizeGrip : Grid
{
    public SizeGrip()
        : this(InputSystemCursorShape.SizeWestEast)
    {
    }

    internal SizeGrip(InputSystemCursorShape shape)
    {
        ProtectedCursor = InputSystemCursor.Create(shape);
    }
}
