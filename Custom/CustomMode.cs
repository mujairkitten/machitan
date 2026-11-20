using System;
using System.Collections.Generic;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Machitan.Custom;

/// <summary>What custom mode can pick: a bar bubble, Dynaland or a corner widget.</summary>
internal enum CustomPiece
{
    Start,
    Apps,
    Group,
    Tray,
    Dynaland,
    InfoLine,
    DuoRing,
}

/// <summary>The picker's pages, in carousel order: one per element with variants.</summary>
internal enum PickerPage
{
    StartCluster,
    AppDock,
    Tray,
    Dynaland,
    CornerWidgets,
}

/// <summary>One picked thing: a piece, and for a group bubble its ID.</summary>
internal sealed record CustomTarget(CustomPiece Piece, string? GroupId = null);

/// <summary>One choice in the picker: its name, a line about it, and whether it is in use now.</summary>
internal sealed record Variant(string Name, string About, bool Current, Action Apply);

/// <summary>
/// Custom mode: entered with Customize (the bar's right-click menu, or Settings > Taskbar).
/// While on, a click on a bar bubble, Dynaland or a corner widget picks it instead of using it
/// (a white x badge at its top-right removes it), a right-click gives a black menu
/// (Settings / Remove), and a toolbar at the top right offers Back (undo everything since
/// Customize), Auto-arrange, Reset, Add and Done. Settings opens the picker: a 2x2 grid of the
/// element's variants with a '&lt; 1 of N &gt;' carousel through the elements (Start, Apps, Tray,
/// Dynaland, Corner widgets). Dragging on the
/// bar works as always. Everything is saved as it changes; Back puts the state from before
/// Customize back.
/// </summary>
internal sealed class CustomMode
{
    private const int FollowMs = 250;

    private readonly ShellHost _host;
    private DispatcherQueueTimer? _follow;
    private CustomToolbarWindow? _toolbar;
    private CustomBadgeWindow? _badge;
    private VariantPickerWindow? _picker;
    private Look? _before;
    private string? _layoutBefore;

    internal CustomMode(ShellHost host)
    {
        _host = host;
    }

    /// <summary>On, off, or a new pick.</summary>
    internal event Action? Changed;

    internal bool IsOn { get; private set; }

    internal CustomTarget? Selected { get; private set; }

    /// <summary>The windows custom mode made, for the shell to close on shutdown.</summary>
    internal IEnumerable<Window> OpenWindows
    {
        get
        {
            if (_picker is not null)
            {
                yield return _picker;
            }

            if (_badge is not null)
            {
                yield return _badge;
            }

            if (_toolbar is not null)
            {
                yield return _toolbar;
            }
        }
    }

    /// <summary>The settings custom mode changes, as they were on Customize (for Back).</summary>
    private sealed record Look(
        DockAlignment Alignment,
        SearchMode Search,
        SearchPlace Place,
        bool Labels,
        bool Start,
        bool Apps,
        bool TrayOnBar,
        bool Dynaland,
        bool InfoLine,
        bool DuoRing);

    // ---------------------------------------------------------------- on and off

    internal void Enter()
    {
        if (IsOn)
        {
            return;
        }

        try
        {
            ShellSettings s = _host.Settings.Current;
            _before = new Look(s.DockAlignment, s.SearchMode, s.SearchPlace, s.DockLabels, s.ShowStartOnBar, s.ShowAppsOnBar, s.TrayOnBar, s.EnableDynaland, s.InfoLineEnabled, s.ShowDuoRing);
            _layoutBefore = _host.BarLayout.Snapshot();
            IsOn = true;
            Selected = null;

            _toolbar ??= new CustomToolbarWindow(_host, this);
            _badge ??= new CustomBadgeWindow(this);
            _toolbar.ShowToolbar();
            StartFollowing();
            Log.Info("Custom mode: on.");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Custom mode could not start.", ex);
            Leave("it could not start");
        }
    }

    /// <summary>Done: keeps everything and leaves.</summary>
    internal void Done()
    {
        Leave("done");
    }

    /// <summary>Back: puts everything as it was on Customize, then leaves.</summary>
    internal void Back()
    {
        if (!IsOn)
        {
            return;
        }

        if (_before is Look before)
        {
            _host.Settings.Update(
                s =>
                {
                    s.DockAlignment = before.Alignment;
                    s.SearchMode = before.Search;
                    s.SearchPlace = before.Place;
                    s.DockLabels = before.Labels;
                    s.ShowStartOnBar = before.Start;
                    s.ShowAppsOnBar = before.Apps;
                    s.TrayOnBar = before.TrayOnBar;
                    s.EnableDynaland = before.Dynaland;
                    s.InfoLineEnabled = before.InfoLine;
                    s.ShowDuoRing = before.DuoRing;
                },
                "custom mode (Back)");
        }

        if (_layoutBefore is string layout)
        {
            _host.BarLayout.Restore(layout);
        }

        Leave("back: everything as before");
    }

    private void Leave(string why)
    {
        bool wasOn = IsOn;
        IsOn = false;
        Selected = null;
        _before = null;
        _layoutBefore = null;
        _follow?.Stop();
        try
        {
            _picker?.HidePicker();
            _badge?.HideBadge();
            _toolbar?.HideToolbar();
        }
        catch (Exception ex)
        {
            Log.Warn("Custom mode: its windows could not all be hidden.", ex);
        }

        if (wasOn)
        {
            Log.Info("Custom mode: off (" + why + ").");
            Changed?.Invoke();
        }
    }

    // ---------------------------------------------------------------- the toolbar

    /// <summary>Start first, then the apps, then the groups; the bar centred.</summary>
    internal void AutoArrange()
    {
        _host.BarLayout.StandardOrder();
        _host.Settings.Update(s => s.DockAlignment = DockAlignment.Center, "custom mode (Auto-arrange)");
        Log.Info("Custom mode: auto-arranged.");
    }

    /// <summary>Everything custom mode changes back to the standard (Back still undoes it).</summary>
    internal void Reset()
    {
        Select(null);
        var standard = new ShellSettings();
        _host.Settings.Update(
            s =>
            {
                s.DockAlignment = standard.DockAlignment;
                s.SearchMode = standard.SearchMode;
                s.SearchPlace = standard.SearchPlace;
                s.DockLabels = standard.DockLabels;
                s.ShowStartOnBar = standard.ShowStartOnBar;
                s.ShowAppsOnBar = standard.ShowAppsOnBar;
                s.TrayOnBar = standard.TrayOnBar;
                s.EnableDynaland = standard.EnableDynaland;
                s.InfoLineEnabled = standard.InfoLineEnabled;
                s.ShowDuoRing = standard.ShowDuoRing;
            },
            "custom mode (Reset)");
        _host.BarLayout.ResetAll();
        Log.Info("Custom mode: reset to the standard bar.");
    }

    /// <summary>The first page with something removed or off, or null when everything is on.</summary>
    internal PickerPage? FirstRemoved()
    {
        ShellSettings s = _host.Settings.Current;
        if (!s.ShowStartOnBar)
        {
            return PickerPage.StartCluster;
        }

        if (!s.ShowAppsOnBar)
        {
            return PickerPage.AppDock;
        }

        if (!s.EnableDynaland)
        {
            return PickerPage.Dynaland;
        }

        if (!s.InfoLineEnabled || !s.ShowDuoRing)
        {
            return PickerPage.CornerWidgets;
        }

        return null;
    }

    /// <summary>Add: the picker on the first element that is removed or off.</summary>
    internal void Add()
    {
        if (FirstRemoved() is PickerPage page)
        {
            OpenPicker(page);
        }
    }

    // ---------------------------------------------------------------- picking

    internal void Select(CustomTarget? target)
    {
        if (!IsOn || Equals(Selected, target))
        {
            return;
        }

        Selected = target;
        FollowSelected();
        Changed?.Invoke();
    }

    internal void OpenPicker(PickerPage page)
    {
        if (!IsOn)
        {
            return;
        }

        try
        {
            _picker ??= new VariantPickerWindow(_host, this);
            _picker.ShowPage(page);
        }
        catch (Exception ex)
        {
            Log.Error("Custom mode: the picker could not be shown.", ex);
        }
    }

    internal static PickerPage PageOf(CustomPiece piece)
    {
        return piece switch
        {
            CustomPiece.Start => PickerPage.StartCluster,
            CustomPiece.Apps or CustomPiece.Group => PickerPage.AppDock,
            CustomPiece.Tray => PickerPage.Tray,
            CustomPiece.Dynaland => PickerPage.Dynaland,
            _ => PickerPage.CornerWidgets,
        };
    }

    internal static string NameOf(PickerPage page)
    {
        return page switch
        {
            PickerPage.StartCluster => Strings.CmStart,
            PickerPage.AppDock => Strings.CmApps,
            PickerPage.Tray => Strings.CmTray,
            PickerPage.Dynaland => "Dynaland",
            _ => Strings.SetPageWidgets,
        };
    }

    /// <summary>Whether Remove can take this: the bar keeps at least one bubble.</summary>
    internal bool CanRemove(CustomTarget target)
    {
        return target.Piece switch
        {
            CustomPiece.Start or CustomPiece.Apps or CustomPiece.Tray => _host.ShownBubbleCount > 1,
            _ => true,
        };
    }

    internal void Remove(CustomTarget target)
    {
        if (!IsOn || !CanRemove(target))
        {
            return;
        }

        const string Source = "custom mode (Remove)";
        switch (target.Piece)
        {
            case CustomPiece.Start:
                _host.Settings.Update(s => s.ShowStartOnBar = false, Source);
                break;
            case CustomPiece.Apps:
                _host.Settings.Update(s => s.ShowAppsOnBar = false, Source);
                break;
            case CustomPiece.Group when target.GroupId is string id:
                _host.BarLayout.Ungroup(id);
                break;
            case CustomPiece.Tray:
                // Off the bar: back to Dynaland's general page.
                _host.Settings.Update(s => s.TrayOnBar = false, Source);
                break;
            case CustomPiece.Dynaland:
                _host.Settings.Update(s => s.EnableDynaland = false, Source);
                break;
            case CustomPiece.InfoLine:
                _host.Settings.Update(s => s.InfoLineEnabled = false, Source);
                break;
            case CustomPiece.DuoRing:
                _host.Settings.Update(s => s.ShowDuoRing = false, Source);
                break;
        }

        Log.Info("Custom mode: removed " + target.Piece + (target.GroupId is null ? string.Empty : " " + target.GroupId) + ".");
        if (Equals(Selected, target))
        {
            Select(null);
        }
    }

    internal void RemoveSelected()
    {
        if (Selected is CustomTarget target)
        {
            Remove(target);
        }
    }

    /// <summary>The variants of one page, the one in use marked; choosing one puts the element on.</summary>
    internal List<Variant> VariantsOf(PickerPage page)
    {
        ShellSettings s = _host.Settings.Current;
        const string Source = "custom mode (picker)";
        var list = new List<Variant>();
        switch (page)
        {
            case PickerPage.StartCluster:
                bool start = s.ShowStartOnBar;
                list.Add(new Variant(Strings.CmStartIcon, Strings.CmStartIconAbout, start && s.SearchMode == SearchMode.Icon,
                    () => _host.Settings.Update(x => { x.ShowStartOnBar = true; x.SearchMode = SearchMode.Icon; }, Source)));
                list.Add(new Variant(Strings.CmStartBox, Strings.CmStartBoxAbout, start && s.SearchMode == SearchMode.Box,
                    () => _host.Settings.Update(x => { x.ShowStartOnBar = true; x.SearchMode = SearchMode.Box; }, Source)));
                list.Add(new Variant(Strings.CmStartOnly, Strings.CmStartOnlyAbout, start && s.SearchMode == SearchMode.Hidden,
                    () => _host.Settings.Update(x => { x.ShowStartOnBar = true; x.SearchMode = SearchMode.Hidden; }, Source)));
                break;

            case PickerPage.AppDock:
                bool apps = s.ShowAppsOnBar;
                list.Add(new Variant(Strings.CmIconsOnly, Strings.CmIconsOnlyAbout, apps && !s.DockLabels,
                    () => _host.Settings.Update(x => { x.ShowAppsOnBar = true; x.DockLabels = false; }, Source)));
                list.Add(new Variant(Strings.CmIconsLabels, Strings.CmIconsLabelsAbout, apps && s.DockLabels,
                    () => _host.Settings.Update(x => { x.ShowAppsOnBar = true; x.DockLabels = true; }, Source)));
                break;

            case PickerPage.Tray:
                list.Add(new Variant(Strings.SetHidden, Strings.CmTrayHiddenAbout, !s.TrayOnBar,
                    () => _host.Settings.Update(x => x.TrayOnBar = false, Source)));
                list.Add(new Variant(Strings.CmTrayOnBar, Strings.CmTrayOnBarAbout, s.TrayOnBar,
                    () => _host.Settings.Update(x => x.TrayOnBar = true, Source)));
                break;

            case PickerPage.Dynaland:
                list.Add(new Variant(Strings.CmMusic, Strings.CmMusicAbout, s.EnableDynaland,
                    () => _host.Settings.Update(x => x.EnableDynaland = true, Source)));
                list.Add(new Variant(Strings.Off, Strings.CmNoDynaland, !s.EnableDynaland,
                    () => _host.Settings.Update(x => x.EnableDynaland = false, Source)));
                break;

            default:
                bool info = s.InfoLineEnabled;
                bool ring = s.ShowDuoRing;
                list.Add(new Variant(Strings.CmInfoAndRing, Strings.CmInfoAndRingAbout, info && ring,
                    () => _host.Settings.Update(x => { x.InfoLineEnabled = true; x.ShowDuoRing = true; }, Source)));
                list.Add(new Variant(Strings.CmInfoOnly, Strings.CmInfoOnlyAbout, info && !ring,
                    () => _host.Settings.Update(x => { x.InfoLineEnabled = true; x.ShowDuoRing = false; }, Source)));
                list.Add(new Variant(Strings.CmRingOnly, Strings.CmRingOnlyAbout, !info && ring,
                    () => _host.Settings.Update(x => { x.InfoLineEnabled = false; x.ShowDuoRing = true; }, Source)));
                list.Add(new Variant(Strings.CmNone, Strings.CmNoneAbout, !info && !ring,
                    () => _host.Settings.Update(x => { x.InfoLineEnabled = false; x.ShowDuoRing = false; }, Source)));
                break;
        }

        return list;
    }

    // ---------------------------------------------------------------- following the pick

    private void StartFollowing()
    {
        if (_follow is null)
        {
            _follow = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _follow.Interval = TimeSpan.FromMilliseconds(FollowMs);
            _follow.Tick += (_, _) => FollowSelected();
        }

        _follow.Start();
    }

    /// <summary>The badge stays on the picked thing's top-right corner, also when the bar is built again.</summary>
    private void FollowSelected()
    {
        try
        {
            if (!IsOn)
            {
                return;
            }

            _toolbar?.KeepOnTop();
            if (Selected is CustomTarget target && _host.ElementRect(target) is RECT rect && rect.Width > 0)
            {
                _badge?.ShowAt(rect, CanRemove(target));
            }
            else
            {
                _badge?.HideBadge();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Custom mode: the badge could not follow the pick.", ex);
        }
    }
}
