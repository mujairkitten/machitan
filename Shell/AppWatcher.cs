using System;
using System.Collections.Generic;
using System.IO;
using Machitan.Core;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;

namespace Machitan.Shell;

/// <summary>
/// Notices apps being installed, removed or updated while Machitan runs, so the All apps list
/// (and search) is built again at once instead of a minute later when Start opens: shortcuts
/// added to or removed from the Start menu's Programs folders (the current user's and all
/// users'), and store apps through Windows' package catalog. An installer writes several
/// shortcuts in a row, so <see cref="Changed"/> is raised once, on the UI thread, when things
/// have been quiet for a moment.
/// </summary>
internal sealed class AppWatcher : IDisposable
{
    private const int QuietMs = 2000;

    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _quiet;
    private readonly List<FileSystemWatcher> _folders = new();
    private PackageCatalog? _packages;
    private bool _overflowNoted;
    private bool _disposed;

    internal AppWatcher(DispatcherQueue queue)
    {
        _queue = queue;
        _quiet = queue.CreateTimer();
        _quiet.Interval = TimeSpan.FromMilliseconds(QuietMs);
        _quiet.IsRepeating = false;
        _quiet.Tick += (_, _) => OnQuiet();
    }

    /// <summary>On the UI thread: apps were installed, removed or updated.</summary>
    internal event Action? Changed;

    internal void Start()
    {
        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                 })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    InternalBufferSize = 32 * 1024,
                };
                watcher.Created += OnFolderChanged;
                watcher.Deleted += OnFolderChanged;
                watcher.Changed += OnFolderChanged;
                watcher.Renamed += OnFolderRenamed;
                watcher.Error += OnFolderError;
                watcher.EnableRaisingEvents = true;
                _folders.Add(watcher);
            }
            catch (Exception ex)
            {
                Log.Warn("App watcher: a Start menu folder cannot be watched (" + root + ").", ex);
            }
        }

        try
        {
            _packages = PackageCatalog.OpenForCurrentUser();
            _packages.PackageInstalling += OnPackageInstalling;
            _packages.PackageUninstalling += OnPackageUninstalling;
            _packages.PackageUpdating += OnPackageUpdating;
        }
        catch (Exception ex)
        {
            _packages = null;
            Log.Warn("App watcher: Windows' package catalog cannot be followed; new store apps show when Start opens.", ex);
        }

        Log.Info("App watcher: following " + _folders.Count + " Start menu folder(s)" + (_packages is null ? "." : " and store apps."));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _quiet.Stop();
        foreach (FileSystemWatcher watcher in _folders)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception)
            {
                // Shutting down: nothing more to do.
            }
        }

        _folders.Clear();
        if (_packages is PackageCatalog packages)
        {
            try
            {
                packages.PackageInstalling -= OnPackageInstalling;
                packages.PackageUninstalling -= OnPackageUninstalling;
                packages.PackageUpdating -= OnPackageUpdating;
            }
            catch (Exception)
            {
                // Shutting down: nothing more to do.
            }

            _packages = null;
        }
    }

    // The events below come on worker threads.

    private void OnFolderChanged(object sender, FileSystemEventArgs e)
    {
        if (Matters(e.FullPath))
        {
            Poke();
        }
    }

    private void OnFolderRenamed(object sender, RenamedEventArgs e)
    {
        if (Matters(e.FullPath) || Matters(e.OldFullPath))
        {
            Poke();
        }
    }

    private void OnFolderError(object sender, ErrorEventArgs e)
    {
        // Too many changes at once for Windows to list: build the list again anyway.
        if (!_overflowNoted)
        {
            _overflowNoted = true;
            Log.Warn("App watcher: Windows reported more Start menu changes than it could list; the app list is built again.", e.GetException());
        }

        Poke();
    }

    private void OnPackageInstalling(PackageCatalog sender, PackageInstallingEventArgs args)
    {
        if (args.IsComplete)
        {
            Poke();
        }
    }

    private void OnPackageUninstalling(PackageCatalog sender, PackageUninstallingEventArgs args)
    {
        if (args.IsComplete)
        {
            Poke();
        }
    }

    private void OnPackageUpdating(PackageCatalog sender, PackageUpdatingEventArgs args)
    {
        if (args.IsComplete)
        {
            Poke();
        }
    }

    /// <summary>A shortcut, or a folder (its name has no extension); not desktop.ini and the like.</summary>
    private static bool Matters(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string extension = Path.GetExtension(path);
        return extension.Length == 0 || string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Starts (or restarts) the quiet wait, on the UI thread.</summary>
    private void Poke()
    {
        if (_disposed)
        {
            return;
        }

        _queue.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            _quiet.Stop();
            _quiet.Start();
        });
    }

    private void OnQuiet()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("App watcher: a part that follows app changes failed.", ex);
        }
    }
}
