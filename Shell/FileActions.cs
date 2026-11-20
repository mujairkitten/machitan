using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Machitan.Shell;

/// <summary>
/// What search can do with a file, as File Explorer and Windows' search do it: open, run as
/// administrator (when Windows offers it for that kind of file), share (Windows' share window),
/// copy the file, copy its path, show its Properties, open its folder. Run on the UI thread.
/// </summary>
internal static partial class FileActions
{
    private const uint ShopFilePath = 0x2;
    private const int ErrorCancelled = 1223;

    private static readonly Guid IidDataTransferManagerInterop = new("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8");
    private static readonly Guid IidDataTransferManager = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");

    /// <summary>The share handler per window, so a second share replaces the first.</summary>
    private static readonly Dictionary<nint, (DataTransferManager Manager, Windows.Foundation.TypedEventHandler<DataTransferManager, DataRequestedEventArgs> Handler)> s_sharing = new();

    internal static void Open(string path)
    {
        Launcher.Start(path);
    }

    internal static void ShowInFolder(string path)
    {
        AppActions.ShowInExplorer(path);
    }

    /// <summary>
    /// True when Windows itself offers "Run as administrator" for this kind of file (its file
    /// type has a runas verb: programs, scripts, consoles), or for a shortcut's target.
    /// </summary>
    internal static bool CanRunAsAdmin(string path)
    {
        try
        {
            string file = path;
            if (string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                if (ShortcutReader.Read(path)?.TargetPath is not string target || !File.Exists(target))
                {
                    return false;
                }

                file = target;
            }

            string extension = Path.GetExtension(file);
            if (extension.Length < 2)
            {
                return false;
            }

            if (HasRunAs(extension) || HasRunAs(@"SystemFileAssociations\" + extension))
            {
                return true;
            }

            using RegistryKey? type = Registry.ClassesRoot.OpenSubKey(extension);
            return type?.GetValue(null) is string progId && progId.Length > 0 && HasRunAs(progId);
        }
        catch (Exception ex)
        {
            Log.Warn("Run as administrator: could not tell whether " + Path.GetFileName(path) + " can run elevated.", ex);
            return false;
        }
    }

    /// <summary>Starts the file elevated (Windows asks first). Saying no to Windows' question is not an error.</summary>
    internal static void RunAsAdmin(string path)
    {
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
            });
            Log.Info("Run as administrator: " + Path.GetFileName(path) + ".");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Info("Run as administrator: " + Path.GetFileName(path) + " was not allowed in Windows' question.");
        }
        catch (Exception ex)
        {
            Log.Error("Run as administrator failed: " + Path.GetFileName(path), ex);
        }
    }

    private static bool HasRunAs(string fileClass)
    {
        using RegistryKey? verb = Registry.ClassesRoot.OpenSubKey(fileClass + @"\shell\runas");
        return verb is not null;
    }

    /// <summary>The file itself on the clipboard, ready to paste in File Explorer.</summary>
    internal static async Task CopyFileAsync(string path)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems(new IStorageItem[] { file });
            Clipboard.SetContent(package);
            Clipboard.Flush();
            Log.Info("Search: file copied to the clipboard.");
        }
        catch (Exception ex)
        {
            Log.Error("Search: the file could not be copied to the clipboard.", ex);
        }
    }

    /// <summary>The path in quotes, as Windows' "Copy as path" gives it.</summary>
    internal static void CopyPath(string path)
    {
        try
        {
            var package = new DataPackage();
            package.SetText("\"" + path + "\"");
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            Log.Error("Search: the path could not be copied to the clipboard.", ex);
        }
    }

    /// <summary>Windows' Properties window for the file.</summary>
    internal static void ShowProperties(string path, nint hwnd)
    {
        if (SHObjectProperties(hwnd, ShopFilePath, path, null) == 0)
        {
            Log.Warn("Search: Windows did not open the Properties window for " + path + ".");
        }
    }

    /// <summary>Windows' share window for the file, placed by Windows near <paramref name="hwnd"/>.</summary>
    internal static async Task ShareAsync(string path, nint hwnd)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            ShowShare(file, hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("Search: sharing failed.", ex);
        }
    }

    /// <summary>No awaiting in here: raw COM calls need an unsafe method, which cannot await.</summary>
    private static unsafe void ShowShare(StorageFile file, nint hwnd)
    {
        nint interop = Interop();
        if (interop == 0)
        {
            Log.Warn("Search: Windows' share window is not available.");
            return;
        }

        try
        {
            nint pointer = 0;
            Guid managerId = IidDataTransferManager;

            // IDataTransferManagerInterop::GetForWindow / ShowShareUIForWindow
            int got = ((delegate* unmanaged<nint, nint, Guid*, nint*, int>)ComCall.Slot(interop, 3))(interop, hwnd, &managerId, &pointer);
            if (got < 0 || pointer == 0)
            {
                Log.Warn("Search: the share window could not be prepared (0x" + got.ToString("X8") + ").");
                return;
            }

            DataTransferManager manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(pointer);
            ComCall.Release(pointer);

            if (s_sharing.TryGetValue(hwnd, out var previous))
            {
                previous.Manager.DataRequested -= previous.Handler;
            }

            Windows.Foundation.TypedEventHandler<DataTransferManager, DataRequestedEventArgs> handler = (_, args) =>
            {
                args.Request.Data.Properties.Title = file.Name;
                args.Request.Data.SetStorageItems(new IStorageItem[] { file });
            };
            manager.DataRequested += handler;
            s_sharing[hwnd] = (manager, handler);

            int shown = ((delegate* unmanaged<nint, nint, int>)ComCall.Slot(interop, 4))(interop, hwnd);
            if (shown < 0)
            {
                Log.Warn("Search: Windows' share window did not open (0x" + shown.ToString("X8") + ").");
            }
        }
        finally
        {
            ComCall.Release(interop);
        }
    }

    /// <summary>DataTransferManager's IDataTransferManagerInterop, for windows without a CoreWindow.</summary>
    private static unsafe nint Interop()
    {
        const string ClassName = "Windows.ApplicationModel.DataTransfer.DataTransferManager";
        if (WindowsCreateString(ClassName, (uint)ClassName.Length, out nint name) < 0)
        {
            return 0;
        }

        try
        {
            Guid interopId = IidDataTransferManagerInterop;
            nint factory = 0;
            return RoGetActivationFactory(name, &interopId, &factory) >= 0 ? factory : 0;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHObjectProperties(nint hwnd, uint objectType, string objectName, string? propertyPage);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static unsafe partial int RoGetActivationFactory(nint classId, Guid* interfaceId, nint* factory);
}
