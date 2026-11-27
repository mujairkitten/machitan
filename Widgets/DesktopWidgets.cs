using System;
using System.Collections.Generic;
using Machitan.Core;
using Machitan.Shell;

namespace Machitan.Widgets;

/// <summary>
/// The widgets placed on the desktop (WidgetStore, place "Desktop"): one window each, made and
/// closed as widgets come and go. They show whether or not the sidebar is on. UI thread only.
/// </summary>
internal sealed class DesktopWidgets
{
    private readonly ShellHost _host;
    private readonly Dictionary<string, DesktopWidgetWindow> _windows = new(StringComparer.Ordinal);
    private bool _started;

    internal DesktopWidgets(ShellHost host)
    {
        _host = host;
    }

    internal void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _host.WidgetItems.Changed += Sync;
        Sync();
    }

    /// <summary>At exit: saves what was typed and hides them (Close closes them).</summary>
    internal void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _host.WidgetItems.Changed -= Sync;
        foreach (DesktopWidgetWindow window in _windows.Values)
        {
            try
            {
                window.Flush();
                window.HideFlyout();
            }
            catch (Exception ex)
            {
                Log.Error("Desktop widgets: one could not be put away.", ex);
            }
        }
    }

    /// <summary>Closes their windows (the last step at exit).</summary>
    internal void Close()
    {
        foreach (DesktopWidgetWindow window in _windows.Values)
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                Log.Warn("Desktop widgets: a window could not be closed.", ex);
            }
        }

        _windows.Clear();
    }

    /// <summary>A window for each widget on the desktop; none for the others.</summary>
    private void Sync()
    {
        if (!_started)
        {
            return;
        }

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (WidgetItem item in _host.WidgetItems.Items)
        {
            if (!item.OnDesktop || !WidgetKinds.IsKnown(item.Kind))
            {
                continue;
            }

            wanted.Add(item.Id);
            if (_windows.ContainsKey(item.Id) || WidgetView.Create(_host, item) is not WidgetView view)
            {
                continue;
            }

            try
            {
                var window = new DesktopWidgetWindow(_host, item, view);
                window.ShowWidget(_windows.Count);
                _windows[item.Id] = window;
            }
            catch (Exception ex)
            {
                Log.Error("Desktop widgets: one (" + item.Kind + ") could not be shown.", ex);
            }
        }

        var gone = new List<string>();
        foreach (string id in _windows.Keys)
        {
            if (!wanted.Contains(id))
            {
                gone.Add(id);
            }
        }

        foreach (string id in gone)
        {
            DesktopWidgetWindow window = _windows[id];
            _windows.Remove(id);
            try
            {
                window.RemoveWidget();
            }
            catch (Exception ex)
            {
                Log.Error("Desktop widgets: one could not be taken away.", ex);
            }
        }
    }
}
