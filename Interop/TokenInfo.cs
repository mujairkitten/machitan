using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>A process's rights: whether it runs as administrator, and its integrity level.</summary>
internal static unsafe partial class TokenInfo
{
    /// <summary>Integrity level of an ordinary app (Machitan's own, normally).</summary>
    internal const int MediumIntegrity = 0x2000;

    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;
    private const int TokenIntegrityLevelClass = 25;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>The current process's own pseudo handle (needs no closing).</summary>
    private static readonly nint CurrentProcess = -1;

    /// <summary>True when this process runs as administrator (elevated).</summary>
    internal static bool IsElevated()
    {
        if (!OpenProcessToken(CurrentProcess, TokenQuery, out nint token))
        {
            return false;
        }

        try
        {
            uint elevated = 0;
            uint size = 0;
            return GetTokenInformation(token, TokenElevationClass, &elevated, sizeof(uint), &size) && elevated != 0;
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }

    /// <summary>A process's integrity level (0x2000 medium, 0x3000 high, ...), or -1 when Windows will not say.</summary>
    internal static int IntegrityOf(uint processId)
    {
        nint process = Kernel32.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return -1;
        }

        try
        {
            return IntegrityOfProcess(process);
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }

    private static int IntegrityOfProcess(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out nint token))
        {
            return -1;
        }

        try
        {
            uint size = 0;
            GetTokenInformation(token, TokenIntegrityLevelClass, null, 0, &size);
            if (size < (uint)sizeof(nint) || size > 1024)
            {
                return -1;
            }

            byte* buffer = stackalloc byte[(int)size];
            if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, size, &size))
            {
                return -1;
            }

            // TOKEN_MANDATORY_LABEL: the label's SID first; its last sub-authority is the level.
            nint sid = ((nint*)buffer)[0];
            byte* count = GetSidSubAuthorityCount(sid);
            if (count == null || *count == 0)
            {
                return -1;
            }

            uint* level = GetSidSubAuthority(sid, (uint)(*count - 1));
            return level == null ? -1 : (int)*level;
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, void* information, uint length, uint* returned);

    [LibraryImport("advapi32.dll")]
    private static partial byte* GetSidSubAuthorityCount(nint sid);

    [LibraryImport("advapi32.dll")]
    private static partial uint* GetSidSubAuthority(nint sid, uint index);
}
