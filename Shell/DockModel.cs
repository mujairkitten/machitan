using System;
using System.Collections.Generic;

namespace Machitan.Shell;

/// <summary>One button in the app dock: a pinned app, a running app, or both.</summary>
public sealed class DockItem
{
    public DockItem(
        string key,
        string displayName,
        string? path,
        string? packageFullName,
        string? appId,
        string? pinKey,
        string? launchTarget,
        TaskGroup? group)
    {
        Key = key;
        DisplayName = displayName;
        Path = path;
        PackageFullName = packageFullName;
        AppId = appId;
        PinKey = pinKey;
        LaunchTarget = launchTarget;
        Group = group;
    }

    public string Key { get; }

    public string DisplayName { get; }

    /// <summary>Executable path, when known.</summary>
    public string? Path { get; }

    /// <summary>Package of a running packaged app.</summary>
    public string? PackageFullName { get; }

    /// <summary>App ID of a pinned store app.</summary>
    public string? AppId { get; }

    /// <summary>The pin's identity, when the app is pinned.</summary>
    public string? PinKey { get; }

    /// <summary>What a click starts when the app is not running.</summary>
    public string? LaunchTarget { get; }

    /// <summary>The running app, or null when the app is pinned but not running.</summary>
    public TaskGroup? Group { get; }

    public bool IsPinned => PinKey is not null;

    public bool IsRunning => Group is not null;

    /// <summary>
    /// Packaged (store) apps cannot be started from their executable path, so the bar cannot
    /// pin them; the Start menu's All apps list pins them by app ID.
    /// </summary>
    public bool CanPinByPath => Path is not null && PackageFullName is null && AppId is null;
}

/// <summary>Merges pins and running apps: pinned apps first in pin order, then other running apps.</summary>
internal static class DockModel
{
    internal static List<DockItem> Build(IReadOnlyList<PinnedApp> pins, IReadOnlyList<TaskGroup> groups)
    {
        var items = new List<DockItem>(pins.Count + groups.Count);
        var used = new HashSet<TaskGroup>();

        foreach (PinnedApp pin in pins)
        {
            TaskGroup? match = pin.AppId is not null ? FindByFamily(groups, used, pin.AppId) : FindByPath(groups, used, pin.Path);

            if (match is not null)
            {
                used.Add(match);
                items.Add(new DockItem(
                    match.Key,
                    match.DisplayName,
                    match.ExecutablePath,
                    match.PackageFullName,
                    pin.AppId,
                    pin.Path,
                    pin.LaunchTarget,
                    match));
            }
            else
            {
                items.Add(new DockItem(
                    pin.Path,
                    pin.Name,
                    pin.AppId is null ? pin.Path : null,
                    null,
                    pin.AppId,
                    pin.Path,
                    pin.LaunchTarget,
                    null));
            }
        }

        foreach (TaskGroup group in groups)
        {
            if (!used.Contains(group))
            {
                items.Add(new DockItem(
                    group.Key,
                    group.DisplayName,
                    group.ExecutablePath,
                    group.PackageFullName,
                    null,
                    null,
                    group.ExecutablePath,
                    group));
            }
        }

        return items;
    }

    private static TaskGroup? FindByPath(IReadOnlyList<TaskGroup> groups, HashSet<TaskGroup> used, string path)
    {
        foreach (TaskGroup group in groups)
        {
            if (!used.Contains(group)
                && group.OwnAppId is null
                && group.ExecutablePath is not null
                && string.Equals(group.ExecutablePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }
        }

        return null;
    }

    /// <summary>A pinned store app matches the running app from the same package family.</summary>
    private static TaskGroup? FindByFamily(IReadOnlyList<TaskGroup> groups, HashSet<TaskGroup> used, string appId)
    {
        string? family = PackageLogo.FamilyFromAppId(appId);
        if (family is null)
        {
            return null;
        }

        foreach (TaskGroup group in groups)
        {
            if (!used.Contains(group)
                && string.Equals(PackageLogo.FamilyFromFullName(group.PackageFullName), family, StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }
        }

        return null;
    }
}
