using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>
/// Windows' own SQLite (winsqlite3.dll, part of Windows 10 and 11): just enough to read rows
/// from a database Windows keeps (no package needed). Used for Windows' notification
/// database (Shell/NotificationPictures.cs).
/// </summary>
internal static partial class WinSqlite
{
    internal const int Ok = 0;
    internal const int Row = 100;
    internal const int Done = 101;
    internal const int OpenReadOnly = 0x00000001;
    internal const int OpenReadWrite = 0x00000002;

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string fileName, out nint db, int flags, nint vfs);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2")]
    internal static partial int Close(nint db);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_busy_timeout")]
    internal static partial int BusyTimeout(nint db, int milliseconds);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_errmsg")]
    internal static partial nint ErrorMessage(nint db);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Prepare(nint db, string sql, int sqlBytes, out nint statement, nint tail);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_int64")]
    internal static partial int BindInt64(nint statement, int index, long value);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_step")]
    internal static partial int Step(nint statement);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize")]
    internal static partial int FinalizeStatement(nint statement);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int64")]
    internal static partial long ColumnInt64(nint statement, int column);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_column_blob")]
    internal static partial nint ColumnBlob(nint statement, int column);

    [LibraryImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes")]
    internal static partial int ColumnBytes(nint statement, int column);

    /// <summary>The last error's text, for the log.</summary>
    internal static string LastError(nint db)
    {
        nint text = db == 0 ? 0 : ErrorMessage(db);
        return text == 0 ? "unknown error" : Marshal.PtrToStringUTF8(text) ?? "unknown error";
    }

    /// <summary>A column's bytes (a blob or text), or null when empty.</summary>
    internal static byte[]? ColumnBytesCopy(nint statement, int column)
    {
        nint data = ColumnBlob(statement, column);
        int length = ColumnBytes(statement, column);
        if (data == 0 || length <= 0)
        {
            return null;
        }

        var bytes = new byte[length];
        Marshal.Copy(data, bytes, 0, length);
        return bytes;
    }
}
