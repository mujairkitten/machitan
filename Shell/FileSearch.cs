using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Machitan.Shell;

/// <summary>
/// "files:" search in the Start menu: file names in the user's own folders (Desktop,
/// Documents, Downloads, Pictures, Music, Videos), a few levels deep. No index, no web.
/// Runs on a worker thread and stops at a result or time limit.
/// </summary>
internal static class FileSearch
{
    private const int MaxDepth = 5;
    private static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(3);

    internal static List<string> Find(string query, int maxResults, CancellationToken cancel)
    {
        var results = new List<string>(maxResults);
        if (query.Length < 2)
        {
            return results;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = MaxDepth,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        var timer = Stopwatch.StartNew();
        foreach (string root in Roots())
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", options))
                {
                    if (cancel.IsCancellationRequested || timer.Elapsed > TimeLimit)
                    {
                        return results;
                    }

                    if (Path.GetFileName(file).Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    {
                        results.Add(file);
                        if (results.Count >= maxResults)
                        {
                            return results;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable folder is skipped.
            }
        }

        return results;
    }

    private static IEnumerable<string> Roots()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root) && seen.Add(root))
            {
                yield return root;
            }
        }
    }
}
