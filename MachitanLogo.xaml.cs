// MachitanLogo.xaml.cs — code-behind for the Machitan logo control.
// Drives the Normal / PointerOver / Pressed visual states from pointer input,
// so the logo reacts on its own wherever it is placed (Start button, titlebar,
// Settings window). If the logo sits inside a Button, the Button's own states
// still work; these handlers only change the logo's own colours.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Machitan.Controls;

public sealed partial class MachitanLogo : UserControl
{
    public MachitanLogo()
    {
        InitializeComponent();
        PointerEntered += (_, _) => VisualStateManager.GoToState(this, "PointerOver", true);
        PointerExited += (_, _) => VisualStateManager.GoToState(this, "Normal", true);
        PointerPressed += (_, _) => VisualStateManager.GoToState(this, "Pressed", true);
        PointerReleased += (_, _) => VisualStateManager.GoToState(this, "PointerOver", true);
    }
}
