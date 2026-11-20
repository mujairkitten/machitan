using System;
using System.Globalization;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// Passes AppBar messages that reach Machitan's tray window on to Explorer's taskbar in a form
/// Explorer can answer (tray thread).
///
/// SHAppBarMessage sends its message to the first "Shell_TrayWnd" window. For the messages that
/// get an answer (ABM_QUERYPOS, ABM_SETPOS, ABM_GETTASKBARPOS) it first makes a shared answer
/// block (SHAllocShared) for the process that owns that window, and puts the block and that
/// process's ID into the message. While Machitan's tray receiver runs, that process is Machitan.
/// Passed on unchanged, Explorer cannot open the block, so it answers without acting: the strip
/// and the bar's later reservations never changed the work area (Parts 13 and 14). So the block
/// is copied into a new one made for Explorer's process, the message is passed on with that one,
/// and Explorer's answer is copied back into the caller's block. That is what shell32 does itself
/// when Explorer's window is the first one.
///
/// The message, as shell32 on Windows build 26300 builds it (read from shell32.dll's
/// SHAppBarMessage, 2026-11-20): WM_COPYDATA, dwData 0, wParam the app bar's window, 0x40 bytes:
///   0x00  APPBARDATA with fixed-size fields (0x28 bytes): cbSize (0x28), window (32 bits),
///         callback message, edge, rect, lParam (64 bits)
///   0x28  the AppBar message; 0x2C padding
///   0x30  the answer block (64 bits; 0 for the messages without an answer)
///   0x38  the ID of the process the block is made for (GetWindowThreadProcessId of the window
///         SHAppBarMessage found); 0x3C padding
/// Other sizes, messages without a block, and blocks made for another process are passed on
/// unchanged, as before. Nothing here throws.
/// </summary>
internal static unsafe class AppBarRelay
{
    private const int MessageSize = 0x40;
    private const uint DataSize = 0x28;
    private const int OffRect = 0x10;
    private const int OffMessage = 0x28;
    private const int OffShared = 0x30;
    private const int OffProcess = 0x38;

    private const uint TimeoutMs = 4000;
    private const int MostLogLines = 12;

    // Tray thread only.
    private static int s_logLines;

    /// <summary>
    /// True when the message was passed on here, with Explorer's answer in
    /// <paramref name="answer"/>; false when it is to be passed on unchanged.
    /// </summary>
    internal static bool TryPassOn(nint explorer, nint window, byte* bytes, int length, out nint answer)
    {
        answer = 0;
        if (explorer == 0 || bytes == null || length != MessageSize)
        {
            return false;
        }

        uint message = *(uint*)(bytes + OffMessage);
        if (message is not (Shell32.ABM_QUERYPOS or Shell32.ABM_SETPOS or Shell32.ABM_GETTASKBARPOS))
        {
            return false;
        }

        long block = *(long*)(bytes + OffShared);
        uint owner = *(uint*)(bytes + OffProcess);
        uint self = (uint)Environment.ProcessId;
        if (block == 0 || owner != self)
        {
            return false;
        }

        User32.GetWindowThreadProcessId(explorer, out uint explorerProcess);
        if (explorerProcess == 0 || explorerProcess == self)
        {
            return false;
        }

        try
        {
            return PassOn(explorer, explorerProcess, window, bytes, (nint)block, message, out answer);
        }
        catch (Exception ex)
        {
            // shlwapi's calls missing, or anything unexpected: passed on unchanged, as before.
            Note("AppBar: " + Name(message) + " could not be passed on with a new answer block ("
                + ex.GetType().Name + "); passed on unchanged.");
            return false;
        }
    }

    private static bool PassOn(nint explorer, uint explorerProcess, nint window, byte* bytes, nint block, uint message, out nint answer)
    {
        answer = 0;
        uint self = (uint)Environment.ProcessId;
        byte* data = stackalloc byte[(int)DataSize];
        byte* repacked = stackalloc byte[MessageSize];

        // The caller's block, made for this process.
        void* callerView = Shlwapi.SHLockShared(block, self);
        if (callerView == null)
        {
            Note("AppBar: the answer block of " + Name(message) + " could not be opened; passed on unchanged.");
            return false;
        }

        Buffer.MemoryCopy(callerView, data, DataSize, DataSize);
        Shlwapi.SHUnlockShared(callerView);

        nint shared = Shlwapi.SHAllocShared(data, DataSize, explorerProcess);
        if (shared == 0)
        {
            Note("AppBar: no answer block could be made for Explorer (" + Name(message) + "); passed on unchanged.");
            return false;
        }

        bool sent;
        nint result;
        string problem = string.Empty;
        RECT got = default;
        try
        {
            Buffer.MemoryCopy(bytes, repacked, MessageSize, MessageSize);
            *(long*)(repacked + OffShared) = (long)shared;
            *(uint*)(repacked + OffProcess) = explorerProcess;

            var copy = new COPYDATASTRUCT
            {
                dwData = 0,
                cbData = MessageSize,
                lpData = (nint)repacked,
            };

            sent = User32.SendMessageTimeoutW(explorer, User32.WM_COPYDATA, window, (nint)(&copy),
                User32.SMTO_ABORTIFHUNG, TimeoutMs, out result) != 0;
            if (sent)
            {
                void* answerView = Shlwapi.SHLockShared(shared, explorerProcess);
                if (answerView == null)
                {
                    problem = "its answer could not be read";
                }
                else
                {
                    void* back = Shlwapi.SHLockShared(block, self);
                    if (back == null)
                    {
                        problem = "its answer could not be handed back";
                    }
                    else
                    {
                        Buffer.MemoryCopy(answerView, back, DataSize, DataSize);
                        Shlwapi.SHUnlockShared(back);
                    }

                    got = *(RECT*)((byte*)answerView + OffRect);
                    Shlwapi.SHUnlockShared(answerView);
                }
            }
        }
        finally
        {
            // The first call to SHFreeShared comes after the message was sent: it must not throw
            // out of here, or the message would be passed on a second time.
            try
            {
                Shlwapi.SHFreeShared(shared, explorerProcess);
            }
            catch (Exception ex)
            {
                Note("AppBar: the answer block for Explorer could not be freed (" + ex.GetType().Name + ").");
            }
        }

        answer = sent ? result : 0;
        Note("AppBar: passed on " + Name(message) + " for window " + Hex(window) + " (" + ProgramOf(window)
            + ") with an answer block for Explorer; "
            + (sent
                ? "Explorer answered " + N(result) + ", rect " + Describe(got) + (problem.Length > 0 ? " (" + problem + ")" : string.Empty)
                : "no answer within " + N(TimeoutMs / 1000) + " s")
            + ".");
        return true;
    }

    private static void Note(string line)
    {
        if (s_logLines >= MostLogLines)
        {
            return;
        }

        s_logLines++;
        Log.Info(s_logLines == MostLogLines ? line + " (Further such lines this run are left out.)" : line);
    }

    private static string ProgramOf(nint window)
    {
        User32.GetWindowThreadProcessId(window, out uint process);
        if (process == 0)
        {
            return "unknown";
        }

        if (process == (uint)Environment.ProcessId)
        {
            return "Machitan";
        }

        string? path = WindowInfo.GetProcessPath(process);
        return path is null ? "process " + N(process) : System.IO.Path.GetFileName(path);
    }

    private static string Name(uint message)
    {
        return message switch
        {
            Shell32.ABM_QUERYPOS => "ABM_QUERYPOS",
            Shell32.ABM_SETPOS => "ABM_SETPOS",
            Shell32.ABM_GETTASKBARPOS => "ABM_GETTASKBARPOS",
            _ => "AppBar message " + N(message),
        };
    }

    private static string Hex(nint handle)
    {
        return "0x" + ((long)handle).ToString("X", CultureInfo.InvariantCulture);
    }

    private static string Describe(RECT rect)
    {
        return N(rect.Left) + "," + N(rect.Top) + " to " + N(rect.Right) + "," + N(rect.Bottom);
    }

    private static string N(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
