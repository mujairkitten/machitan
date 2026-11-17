using System;
using System.IO;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.Win32;

namespace Machitan.Shell;

/// <summary>Who is signed in: display name and account picture, read from Windows.</summary>
internal static unsafe class UserInfo
{
    /// <summary>The display name ("Sam Smith"), else the account name.</summary>
    internal static string DisplayName()
    {
        try
        {
            const int Capacity = 256;
            char* buffer = stackalloc char[Capacity];
            uint size = Capacity;
            if (Secur32.GetUserNameEx(Secur32.NameDisplay, buffer, ref size) && size > 0)
            {
                string name = new string(buffer, 0, (int)size).Trim();
                if (name.Length > 0)
                {
                    return name;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The display name could not be read; using the account name.", ex);
        }

        return Environment.UserName;
    }

    /// <summary>
    /// Path of the account picture Windows keeps for the current user, or null when there is none.
    /// Windows records it per user under HKLM\...\AccountPicture\Users\&lt;SID&gt;.
    /// </summary>
    internal static string? AccountPicturePath()
    {
        try
        {
            string? sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (sid is null)
            {
                return null;
            }

            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\" + sid);
            if (key is null)
            {
                return null;
            }

            foreach (string size in new[] { "Image192", "Image96", "Image208", "Image240", "Image64" })
            {
                if (key.GetValue(size) is string path && File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The account picture could not be found.", ex);
        }

        return null;
    }
}
