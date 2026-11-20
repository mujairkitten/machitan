using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>One entry of an app's jump list "Tasks" (e.g. "New Chat"); a separator has no title.</summary>
/// <param name="Title">What the menu says; empty for a separator line.</param>
/// <param name="IconPath">Where the task's icon is (often the app's exe).</param>
/// <param name="IconIndex">Which icon in that file.</param>
/// <param name="Link">The task's shortcut, as Windows stored it.</param>
internal sealed record JumpTask(string Title, string? IconPath, int IconIndex, byte[] Link)
{
    public bool IsSeparator => Title.Length == 0;
}

/// <summary>
/// Reads the "Tasks" an app gave Windows for its jump list (ICustomDestinationList), from
/// %AppData%\Microsoft\Windows\Recent\CustomDestinations\&lt;app ID hash&gt;.customDestinations-ms,
/// the file Windows' Start and taskbar read. Format (libyal "Jump lists format"): header
/// (version 2, category count, 4 unused bytes); per category a type (0 custom: name and entries,
/// 1 known: an ID, 2 tasks: entries) and the end mark 0xBABFFBAB; each entry is a class ID and a
/// shortcut saved by its IPersistStream. The file name is Windows' app ID hash: CRC-64
/// (reflected, polynomial 0x92C64265D32139A4, start ~0) of the upper-case app ID in UTF-16.
/// Anything unexpected means no tasks, never invented ones.
/// </summary>
internal static partial class JumpListTasks
{
    private const uint EndMark = 0xBABFFBAB;
    private const int MaxFileBytes = 4 * 1024 * 1024;

    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly ulong[] CrcTable = MakeTable();

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent", "CustomDestinations");

    /// <summary>The app's tasks, read now (the file is small). Empty when it has none.</summary>
    internal static List<JumpTask> For(AppEntry app)
    {
        try
        {
            foreach (string appId in AppIds(app))
            {
                string file = Path.Combine(Folder, Hash(appId) + ".customDestinations-ms");
                if (File.Exists(file))
                {
                    return Read(file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Jump list: tasks for " + app.Name + " could not be read.", ex);
        }

        return new List<JumpTask>();
    }

    /// <summary>Runs a task the way a double-click on its shortcut would: saved as a .lnk, then opened.</summary>
    internal static void Run(JumpTask task)
    {
        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "Machitan");
            Directory.CreateDirectory(folder);
            ClearOld(folder);
            string path = Path.Combine(folder, "task-" + Guid.NewGuid().ToString("N")[..8] + ".lnk");

            object link = new ShellLinkObject();
            try
            {
                if (LoadLink(link, task.Link, 0, out _) < 0)
                {
                    Log.Warn("Jump list: the task \"" + task.Title + "\" could not be read again.");
                    return;
                }

                ((IPersistFile)link).Save(path, true);
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }

            using Process? started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Log.Info("Jump list: ran \"" + task.Title + "\".");
        }
        catch (Exception ex)
        {
            Log.Error("Jump list: the task \"" + task.Title + "\" could not be run.", ex);
        }
    }

    /// <summary>
    /// The IDs Windows may file the app under: a store app's ID, a shortcut's own app ID, else
    /// the exe path with a known folder written as its ID ({System}\notepad.exe), as Windows does
    /// for apps without one. The first file that exists is the app's.
    /// </summary>
    private static IEnumerable<string> AppIds(AppEntry app)
    {
        if (app.AppUserModelId is string packaged)
        {
            yield return packaged;
        }

        if (app.ShortcutAppId is string own)
        {
            yield return own;
        }

        if (app.TargetPath is not string target || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        (string Path, string Id)[] folders =
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.System), "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"),
            (Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "{6D809377-6AF0-444B-8957-A3773F02200E}"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "{905E63B6-C1BF-494E-B29C-65B732D3D21A}"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"),
            (Path.Combine(localAppData, "Programs"), "{5CD7AEE2-2219-4A67-B85D-6C9CE15660CB}"),
            (localAppData, "{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"),
            (Environment.GetFolderPath(Environment.SpecialFolder.Windows), "{F38BF404-1D43-42F2-9305-67DE0B28FC23}"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "{5E6C858F-0E22-4760-9AFE-EA3317B67173}"),
        };

        foreach ((string folder, string id) in folders)
        {
            if (folder.Length > 0 && target.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
            {
                yield return id + target[folder.Length..];
            }
        }

        yield return target;
    }

    private static List<JumpTask> Read(string file)
    {
        var tasks = new List<JumpTask>();
        byte[] data;
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length < 12 || stream.Length > MaxFileBytes)
            {
                return tasks;
            }

            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }

        uint categories = BitConverter.ToUInt32(data, 4);
        int at = 12;
        for (uint c = 0; c < categories && at + 4 <= data.Length; c++)
        {
            uint type = BitConverter.ToUInt32(data, at);
            at += 4;
            if (type == 1)
            {
                at += 4; // A known category (frequent, recent): an ID, no entries.
            }
            else if (type == 0 || type == 2)
            {
                if (type == 0)
                {
                    if (at + 2 > data.Length)
                    {
                        return tasks;
                    }

                    at += 2 + (BitConverter.ToUInt16(data, at) * 2); // The category's name.
                }

                if (at + 4 > data.Length)
                {
                    return tasks;
                }

                uint entries = BitConverter.ToUInt32(data, at);
                at += 4;
                if (!ReadEntries(data, ref at, entries, type == 2 ? tasks : null))
                {
                    return tasks;
                }
            }
            else
            {
                return tasks;
            }

            if (at + 4 > data.Length || BitConverter.ToUInt32(data, at) != EndMark)
            {
                return tasks;
            }

            at += 4;
        }

        return tasks;
    }

    /// <summary>Steps over a category's entries; keeps them as tasks when <paramref name="tasks"/> is given.</summary>
    private static bool ReadEntries(byte[] data, ref int at, uint entries, List<JumpTask>? tasks)
    {
        for (uint e = 0; e < entries; e++)
        {
            if (at + 16 > data.Length || new Guid(data.AsSpan(at, 16)) != ShellLinkClass)
            {
                return false;
            }

            at += 16;
            object link = new ShellLinkObject();
            try
            {
                if (LoadLink(link, data, at, out int used) < 0 || used <= 0 || at + used > data.Length)
                {
                    return false;
                }

                tasks?.Add(Describe(link, data.AsSpan(at, used).ToArray()));
                at += used;
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }

        return true;
    }

    private static JumpTask Describe(object link, byte[] bytes)
    {
        if (PropertyReader.Flag(link, PropertyReader.IsSeparator))
        {
            return new JumpTask(string.Empty, null, 0, bytes);
        }

        var shellLink = (IShellLinkW)link;
        var icon = new StringBuilder(260);
        shellLink.GetIconLocation(icon, icon.Capacity, out int iconIndex);
        var target = new StringBuilder(260);
        shellLink.GetPath(target, target.Capacity, IntPtr.Zero, 0);
        var description = new StringBuilder(260);
        shellLink.GetDescription(description, description.Capacity);

        string? title = PropertyReader.Text(link, PropertyReader.Title);
        if (title is not null && title.StartsWith('@'))
        {
            title = IndirectText(title);
        }

        title ??= ShortcutReader.Clean(description.ToString())
            ?? Path.GetFileNameWithoutExtension(target.ToString());
        string? iconPath = ShortcutReader.Clean(icon.ToString()) ?? ShortcutReader.Clean(target.ToString());
        return new JumpTask(title ?? string.Empty, iconPath is null ? null : Environment.ExpandEnvironmentVariables(iconPath), iconIndex, bytes);
    }

    /// <summary>Loads a shortcut from <paramref name="data"/> at <paramref name="offset"/>; <paramref name="used"/> is how many bytes it took.</summary>
    private static unsafe int LoadLink(object link, byte[] data, int offset, out int used)
    {
        used = 0;
        nint stream;
        fixed (byte* start = data)
        {
            stream = SHCreateMemStream(start + offset, (uint)(data.Length - offset));
        }

        if (stream == 0)
        {
            return -1;
        }

        try
        {
            int loaded = ((IPersistStream)link).Load(stream);
            if (loaded < 0)
            {
                return loaded;
            }

            // IStream::Seek(0, STREAM_SEEK_CUR, &position): where the shortcut's data ended.
            ulong position = 0;
            int sought = ((delegate* unmanaged<nint, long, uint, ulong*, int>)ComCall.Slot(stream, 5))(stream, 0, 1, &position);
            used = sought >= 0 ? (int)position : 0;
            return sought;
        }
        finally
        {
            ComCall.Release(stream);
        }
    }

    /// <summary>"@file.dll,-101" style text, looked up the way Windows does.</summary>
    private static unsafe string? IndirectText(string source)
    {
        char* buffer = stackalloc char[512];
        buffer[0] = '\0';
        return SHLoadIndirectString(source, buffer, 512, 0) >= 0 ? new string(buffer) : null;
    }

    private static void ClearOld(string folder)
    {
        foreach (string old in Directory.EnumerateFiles(folder, "task-*.lnk"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromMinutes(1))
                {
                    File.Delete(old);
                }
            }
            catch (Exception)
            {
                // Still in use; the next run clears it.
            }
        }
    }

    private static string Hash(string appId)
    {
        ulong crc = ulong.MaxValue;
        foreach (char c in appId.ToUpperInvariant())
        {
            crc = CrcTable[(byte)(crc ^ (byte)c)] ^ (crc >> 8);
            crc = CrcTable[(byte)(crc ^ (byte)(c >> 8))] ^ (crc >> 8);
        }

        return crc.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ulong[] MakeTable()
    {
        const ulong Polynomial = 0x92C64265D32139A4;
        var table = new ulong[256];
        for (ulong i = 0; i < 256; i++)
        {
            ulong value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    [LibraryImport("shlwapi.dll")]
    private static unsafe partial nint SHCreateMemStream(byte* initial, uint size);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SHLoadIndirectString(string source, char* output, uint size, nint reserved);
}
