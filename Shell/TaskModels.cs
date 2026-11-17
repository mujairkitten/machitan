using System.Collections.Generic;

namespace Machitan.Shell;

/// <summary>One open window that belongs on the bar.</summary>
public sealed class TaskWindow
{
    public TaskWindow(nint handle, string title, uint processId, bool isMinimized)
    {
        Handle = handle;
        Title = title;
        ProcessId = processId;
        IsMinimized = isMinimized;
    }

    public nint Handle { get; }

    public string Title { get; }

    public uint ProcessId { get; }

    public bool IsMinimized { get; }
}

/// <summary>All open windows of one app, shown as one button.</summary>
public sealed class TaskGroup
{
    public TaskGroup(string key, string displayName, string? executablePath, string? packageFullName, IReadOnlyList<TaskWindow> windows, bool isFocused)
    {
        PackageFullName = packageFullName;
        Key = key;
        DisplayName = displayName;
        ExecutablePath = executablePath;
        Windows = windows;
        IsFocused = isFocused;
    }

    /// <summary>Stable identity of the app: its executable path when known.</summary>
    public string Key { get; }

    public string DisplayName { get; }

    /// <summary>Null when Windows would not reveal the path (some protected processes).</summary>
    public string? ExecutablePath { get; }

    /// <summary>Package full name for packaged apps (store apps and packaged desktop apps), else null.</summary>
    public string? PackageFullName { get; }

    /// <summary>Front-most window first.</summary>
    public IReadOnlyList<TaskWindow> Windows { get; }

    /// <summary>True when one of the windows is the foreground window.</summary>
    public bool IsFocused { get; }
}
