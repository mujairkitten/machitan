using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Custom;

/// <summary>
/// Custom mode's black right-click menu (Settings / Remove), the same on the bar, Dynaland
/// and the corner widgets; and the look back to normal for menus used both ways.
/// </summary>
internal static class CustomMenus
{
    private static Style? _black;

    /// <summary>Black, white text, a faint white edge.</summary>
    internal static void MakeBlack(MenuFlyout menu)
    {
        if (_black is null)
        {
            var style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ElementTheme.Dark));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Colors.Black)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(ColorHelper.FromArgb(60, 255, 255, 255))));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(Radii.Medium)));
            _black = style;
        }

        menu.MenuFlyoutPresenterStyle = _black;
    }

    /// <summary>The menu's usual look (follows the theme).</summary>
    internal static void MakePlain(MenuFlyout menu)
    {
        menu.MenuFlyoutPresenterStyle = null;
    }

    /// <summary>
    /// Fills <paramref name="menu"/> for custom mode: Settings and Remove for <paramref name="target"/>
    /// (when there is one), then Done.
    /// </summary>
    internal static void Fill(MenuFlyout menu, CustomMode mode, CustomTarget? target)
    {
        menu.Items.Clear();
        MakeBlack(menu);

        if (target is not null)
        {
            mode.Select(target);

            var settings = new MenuFlyoutItem { Text = Strings.BarSettings };
            settings.Click += (_, _) => mode.OpenPicker(CustomMode.PageOf(target.Piece));
            menu.Items.Add(settings);

            bool canRemove = mode.CanRemove(target);
            var remove = new MenuFlyoutItem
            {
                Text = target.Piece == CustomPiece.Group ? Strings.CmRemoveUngroup : Strings.CmRemove,
                IsEnabled = canRemove,
            };
            if (!canRemove)
            {
                ToolTipService.SetToolTip(remove, Strings.CmKeepOne);
            }

            remove.Click += (_, _) => mode.Remove(target);
            menu.Items.Add(remove);
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var done = new MenuFlyoutItem { Text = Strings.CmDoneCustomizing };
        done.Click += (_, _) => mode.Done();
        menu.Items.Add(done);
    }
}
