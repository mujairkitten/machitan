using System;
using System.Collections.Generic;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Start;

/// <summary>
/// What Start and the bar's search offer on an app or a file besides opening it:
/// - for a file, a strip of icon buttons without text (each named by its tooltip) below its preview;
/// - the "..." menu for apps: Windows' Start entries plus the app's own jump list tasks;
/// - the one question before a store app is uninstalled.
/// Only actions that apply to the item are offered.
/// </summary>
internal sealed class ResultActions
{
    internal const double StripButtonDip = 28;

    private const int AdminGlyph = 0xE7EF;
    private const int FolderGlyph = 0xE838;
    private const int PinGlyph = 0xE718;
    private const int UnpinGlyph = 0xE77A;
    private const int DeleteGlyph = 0xE74D;
    private const int SettingsGlyph = 0xE713;
    private const int OpenGlyph = 0xE8E5;
    private const int ShareGlyph = 0xE72D;
    private const int CopyGlyph = 0xE8C8;
    private const int PathGlyph = 0xE71B;
    private const int PropertiesGlyph = 0xE946;

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly ShellHost _host;
    private readonly nint _hwnd;
    private readonly Action _close;
    private readonly Action _reloadApps;

    /// <param name="host">For the two pin lists, the app list and the accent colour.</param>
    /// <param name="hwnd">The window Windows' share and Properties windows belong to.</param>
    /// <param name="close">Closes Start or the search results (before an app or window opens).</param>
    /// <param name="reloadApps">Rebuilds the app list (after an uninstall), at the caller's icon size.</param>
    internal ResultActions(ShellHost host, nint hwnd, Action close, Action reloadApps)
    {
        _host = host;
        _hwnd = hwnd;
        _close = close;
        _reloadApps = reloadApps;
    }

    // ---------------------------------------------------------------- the file strip

    /// <summary>For a file: open, run as administrator (when Windows offers it), share, copy, copy as path, properties, open file location. Centred.</summary>
    internal StackPanel FileStrip(string path)
    {
        StackPanel strip = NewStrip(System.IO.Path.GetFileName(path));
        strip.HorizontalAlignment = HorizontalAlignment.Center;
        Add(strip, OpenGlyph, Strings.FileOpen, () =>
        {
            _close();
            FileActions.Open(path);
        });
        if (FileActions.CanRunAsAdmin(path))
        {
            Add(strip, AdminGlyph, Strings.RunAsAdmin, () =>
            {
                _close();
                FileActions.RunAsAdmin(path);
            });
        }

        Add(strip, ShareGlyph, Strings.FileShare, () => _ = FileActions.ShareAsync(path, _hwnd));
        Add(strip, CopyGlyph, Strings.FileCopy, () => _ = FileActions.CopyFileAsync(path));
        Add(strip, PathGlyph, Strings.FileCopyPath, () => FileActions.CopyPath(path));
        Add(strip, PropertiesGlyph, Strings.FileProperties, () => FileActions.ShowProperties(path, _hwnd));
        Add(strip, FolderGlyph, Strings.OpenFileLocation, () =>
        {
            _close();
            FileActions.ShowInFolder(path);
        });
        return strip;
    }

    // ---------------------------------------------------------------- the "..." menu

    /// <summary>
    /// Windows' Start menu for an app: pin to Start, run as administrator, pin to taskbar, app
    /// settings, uninstall, then the app's jump list tasks (read now, from Windows' file).
    /// </summary>
    internal MenuFlyout AppMenu(AppEntry app, Action askUninstall, int iconPixels)
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        PinnedApp pin = app.ToPin();

        bool onStart = _host.StartPins.IsPinned(pin.Path);
        menu.Items.Add(MenuItem(onStart ? Strings.UnpinFromStart : Strings.PinToStart, onStart ? UnpinGlyph : PinGlyph, () => Toggle(_host.StartPins, pin)));

        if (AppActions.CanRunAsAdmin(app))
        {
            menu.Items.Add(MenuItem(Strings.RunAsAdmin, AdminGlyph, () =>
            {
                _close();
                AppActions.RunAsAdmin(app);
            }));
        }

        bool onBar = _host.Pins.IsPinned(pin.Path);
        menu.Items.Add(MenuItem(onBar ? Strings.UnpinFromTaskbar : Strings.PinToTaskbar, onBar ? UnpinGlyph : PinGlyph, () => Toggle(_host.Pins, pin)));

        if (AppActions.HasAppSettings(app))
        {
            menu.Items.Add(MenuItem(Strings.AppSettings, SettingsGlyph, () =>
            {
                _close();
                AppActions.OpenAppSettings(app);
            }));
        }

        if (AppActions.UninstallWayOf(app) != UninstallWay.None)
        {
            menu.Items.Add(MenuItem(Strings.Uninstall, DeleteGlyph, () => Uninstall(app, askUninstall)));
        }

        List<JumpTask> tasks = JumpListTasks.For(app);
        if (tasks.Exists(task => !task.IsSeparator))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = Strings.JumpTasks, IsEnabled = false });
            foreach (JumpTask task in tasks)
            {
                if (task.IsSeparator)
                {
                    menu.Items.Add(new MenuFlyoutSeparator());
                    continue;
                }

                var item = new MenuFlyoutItem { Text = task.Title, Icon = TaskIcon(task, iconPixels) };
                item.Click += (_, _) =>
                {
                    _close();
                    JumpListTasks.Run(task);
                };
                menu.Items.Add(item);
            }
        }

        return menu;
    }

    // ---------------------------------------------------------------- uninstall

    /// <summary>Desktop apps: Windows' Installed apps page. Store apps: the caller asks first (<paramref name="askUninstall"/>).</summary>
    internal void Uninstall(AppEntry app, Action askUninstall)
    {
        switch (AppActions.UninstallWayOf(app))
        {
            case UninstallWay.Settings:
                _close();
                AppActions.OpenInstalledApps();
                break;
            case UninstallWay.Package:
                askUninstall();
                break;
        }
    }

    /// <summary>
    /// The question shown in place of a row, as Windows' Start asks it: the app and its data
    /// go. <paramref name="finished"/> runs when the question is answered or the removal ends.
    /// </summary>
    internal Grid UninstallQuestion(AppEntry app, Action finished)
    {
        var question = new TextBlock
        {
            Text = Fmt.F(Strings.UninstallAsk, app.Name),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 0, 0),
        };
        AutomationProperties.SetLiveSetting(question, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        Button confirm = TextButton(Strings.Uninstall);
        PaintAccent(confirm, _host.Accent.Current);
        Button cancel = TextButton(Strings.Cancel);
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);

        var panel = new Grid { ColumnSpacing = 8 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        panel.Children.Add(question);
        panel.Children.Add(buttons);

        cancel.Click += (_, _) => finished();
        confirm.Click += async (_, _) =>
        {
            buttons.Visibility = Visibility.Collapsed;
            question.Text = Fmt.F(Strings.Uninstalling, app.Name);
            string? problem = await AppActions.UninstallPackageAsync(app);
            if (problem is null)
            {
                _host.Catalog.MarkStale();
                _reloadApps();
                finished();
                return;
            }

            question.Text = problem;
            buttons.Children.Remove(confirm);
            cancel.Content = "OK";
            AutomationProperties.SetName(cancel, "OK");
            buttons.Visibility = Visibility.Visible;
        };

        panel.Loaded += (_, _) => confirm.Focus(FocusState.Programmatic);
        return panel;
    }

    /// <summary>
    /// A question in a result's row before something that cannot be undone (shut down, restart,
    /// sign out from search): the question, the thing's own button in the accent colour (it
    /// does it) and Cancel (puts the row back).
    /// </summary>
    internal Grid Question(string text, string doLabel, Action doIt, Action cancelled)
    {
        var question = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 0, 0),
        };
        AutomationProperties.SetLiveSetting(question, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        ToolTipService.SetToolTip(question, text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        Button confirm = TextButton(doLabel);
        PaintAccent(confirm, _host.Accent.Current);
        Button cancel = TextButton(Strings.Cancel);
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);

        var panel = new Grid { ColumnSpacing = 8 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        panel.Children.Add(question);
        panel.Children.Add(buttons);

        confirm.Click += (_, _) => doIt();
        cancel.Click += (_, _) => cancelled();
        return panel;
    }

    /// <summary>An accent button in Machitan's accent colour, with the better-contrast text (WCAG).</summary>
    internal static void PaintAccent(Button button, Windows.UI.Color colour)
    {
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? style) && style is Style accent)
        {
            button.Style = accent;
        }

        double luminance = Visuals.WallpaperSampler.Luminance(colour.R, colour.G, colour.B);
        var text = new SolidColorBrush(luminance > 0.179 ? Colors.Black : Colors.White);
        button.Resources["AccentButtonBackground"] = new SolidColorBrush(colour);
        button.Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(ColorHelper.FromArgb(230, colour.R, colour.G, colour.B));
        button.Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(ColorHelper.FromArgb(200, colour.R, colour.G, colour.B));
        button.Resources["AccentButtonForeground"] = text;
        button.Resources["AccentButtonForegroundPointerOver"] = text;
        button.Resources["AccentButtonForegroundPressed"] = text;
    }

    // ---------------------------------------------------------------- parts

    private static StackPanel NewStrip(string itemName)
    {
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(strip, Fmt.F(Strings.ActionsFor, itemName));
        return strip;
    }

    private static Button Add(StackPanel strip, int glyph, string name, Action onClick)
    {
        var icon = new FontIcon { Glyph = ((char)glyph).ToString(), FontFamily = IconFont, FontSize = 14 };
        var button = new Button
        {
            Content = icon,
            Width = StripButtonDip,
            Height = StripButtonDip,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(Visuals.Radii.Small),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => onClick();
        strip.Children.Add(button);
        return button;
    }

    private static void Toggle(PinStore list, PinnedApp pin)
    {
        if (list.IsPinned(pin.Path))
        {
            list.Unpin(pin.Path);
        }
        else
        {
            list.Pin(pin);
        }
    }

    private static MenuFlyoutItem MenuItem(string text, int glyph, Action onClick)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = ((char)glyph).ToString(), FontFamily = IconFont },
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>The task's own icon (usually the app's exe), or none.</summary>
    private static IconElement? TaskIcon(JumpTask task, int iconPixels)
    {
        try
        {
            if (task.IconPath is string path && System.IO.File.Exists(path)
                && IconReader.FromExecutable(path, iconPixels, task.IconIndex) is IconPixels pixels)
            {
                return new ImageIcon { Source = StartMenuWindow.ToBitmap(pixels) };
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Jump list: a task icon could not be read.", ex);
        }

        return null;
    }

    private static Button TextButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(Visuals.Radii.Small),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, text);
        return button;
    }
}
