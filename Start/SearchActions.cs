using System;
using System.Collections.Generic;
using System.IO;
using Machitan.Core;
using Machitan.Interop;
using Machitan.SettingsUi;
using Machitan.Shell;
using Windows.ApplicationModel.DataTransfer;

namespace Machitan.Start;

/// <summary>A search result that is not an app or a file: the calculator's answer, or something Machitan can do.</summary>
/// <param name="Name">What the row says, and what search matches.</param>
/// <param name="Detail">The small line below the name: what kind of result it is.</param>
/// <param name="Glyph">Its icon (Segoe Fluent Icons).</param>
/// <param name="Run">Does it; gets the search's own window.</param>
/// <param name="Ask">The question asked first (shut down, restart, sign out), or null.</param>
internal sealed record SearchAction(string Name, string Detail, int Glyph, Action<nint> Run, string? Ask = null)
{
    /// <summary>The calculator's answer: choosing it copies the number.</summary>
    internal bool IsAnswer { get; init; }

    /// <summary>Other words that find it (English), besides its name.</summary>
    internal string[] Words { get; init; } = Array.Empty<string>();

    /// <summary>
    /// A page of Machitan's Settings: found anywhere in its name, as the Settings window's search
    /// box finds it ("bar" finds Taskbar), and listed even when an app has the same name.
    /// </summary>
    internal bool IsMachitanPage { get; init; }
}

/// <summary>
/// The actions of Start's search and the bar's search box (not with "apps:" or "files:"), next
/// to the apps and ranked with them by SearchRank, best fit first:
/// - The calculator (Start/Calculator.cs): an expression shows its answer on top; Enter or a
///   click copies it.
/// - Things Machitan already does elsewhere: its own Settings and each of its pages (by their
///   names; SettingsPages, the same list as the Settings window's search box); Windows Settings
///   pages and Windows tools from the Win+X menu, and a few more Settings pages; Show desktop;
///   Lock and Start's power menu (only what the PC offers now, with the same questions), Sign
///   out (with a question), and Exit Machitan.
/// An action matches by its whole name, its start, or the start of a word in it (or one of its
/// other words), from two letters on; one with the same name as an app is left out (the app is
/// listed). Machitan's own pages also match inside their names and are never left out. No web
/// scope, deliberately.
/// </summary>
internal static class SearchActions
{
    /// <summary>The calculator's answer goes above everything.</summary>
    internal const int AnswerRank = -1;

    private const int MinLetters = 2;
    private const int CalculatorGlyph = 0xE8EF;
    private const int SettingsGlyph = 0xE713;
    private const int PageGlyph = 0xE713;
    private const int ToolGlyph = 0xE90F;
    private const int RunGlyph = 0xE7C4;
    private const int DesktopGlyph = 0xE7F4;
    private const int SignOutGlyph = 0xE77B;

    /// <summary>What fits <paramref name="query"/>, best first; equal fits keep the list's order.</summary>
    internal static List<(int Rank, SearchAction Action)> Find(ShellHost host, string query)
    {
        var found = new List<(int Rank, SearchAction Action)>();
        string text = query.Trim();
        if (text.Length == 0)
        {
            return found;
        }

        if (Calculator.TryEvaluate(text, out double value))
        {
            // An expression is not a name: only its answer.
            string answer = Calculator.Format(value);
            found.Add((AnswerRank, new SearchAction(answer, Strings.SearchCalcCopy, CalculatorGlyph, _ => CopyAnswer(answer)) { IsAnswer = true }));
            return found;
        }

        if (text.Length < MinLetters)
        {
            return found;
        }

        var ranked = new List<(int Rank, int Index, SearchAction Action)>();
        List<SearchAction> all = All(host);
        for (int i = 0; i < all.Count; i++)
        {
            int rank = RankOf(all[i], text);
            bool fits = all[i].IsMachitanPage ? rank <= Inside : rank <= WordStart;
            if (fits && (all[i].IsMachitanPage || !IsAnApp(host, all[i].Name)))
            {
                ranked.Add((rank, i, all[i]));
            }
        }

        ranked.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Index.CompareTo(b.Index));
        foreach ((int Rank, int Index, SearchAction Action) entry in ranked)
        {
            found.Add((entry.Rank, entry.Action));
        }

        return found;
    }

    /// <summary>SearchRank's "a word in the name starts with it": actions match up to there, not inside a word.</summary>
    private const int WordStart = 2;

    /// <summary>SearchRank's "it is somewhere inside": Machitan's pages match up to there.</summary>
    private const int Inside = 3;

    private static int RankOf(SearchAction action, string text)
    {
        int best = SearchRank.Of(action.Name, text);
        foreach (string word in action.Words)
        {
            best = Math.Min(best, SearchRank.Of(word, text));
        }

        return best;
    }

    private static bool IsAnApp(ShellHost host, string name)
    {
        if (!host.Catalog.IsReady)
        {
            return false;
        }

        foreach (AppEntry app in host.Catalog.Apps)
        {
            if (string.Equals(app.Name, name, StringComparison.CurrentCultureIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every action, in the order they list on equal fits.</summary>
    private static List<SearchAction> All(ShellHost host)
    {
        string windowsSettings = Strings.SearchActWindowsSettings;
        string windows = Strings.SearchActWindows;
        string power = Strings.SearchActPower;
        var list = new List<SearchAction>
        {
            new(Strings.SearchActMachitanSettings, Strings.SearchActMachitan, SettingsGlyph, _ => host.OpenSettings()) { Words = new[] { "Machitan" } },
        };

        // Machitan's Settings pages, in the sidebar's order: each opens Settings at that page.
        foreach (SettingsPage page in SettingsPages.All())
        {
            string key = page.Key;
            list.Add(new SearchAction(page.Name, Strings.SearchActMachitanSettings, page.Glyph, _ => host.OpenSettings(key)) { IsMachitanPage = true });
        }

        list.AddRange(new SearchAction[]
        {
            Page(Label(Strings.WinXSettings), "ms-settings:", windowsSettings, "Windows Settings"),
            Page(Strings.SearchActDisplay, "ms-settings:display", windowsSettings, "screen", "brightness", "resolution"),
            Page(Strings.SearchActSound, "ms-settings:sound", windowsSettings, "volume", "audio", "speakers"),
            Page(Strings.SearchActBluetooth, "ms-settings:bluetooth", windowsSettings, "devices"),
            Page(Strings.SearchActWifi, "ms-settings:network-wifi", windowsSettings, "wifi", "wireless"),
            Page(Label(Strings.WinXNetworkConnections), "ms-settings:network", windowsSettings, "network", "internet"),
            Page(Strings.SearchActPersonalization, "ms-settings:personalization", windowsSettings, "wallpaper", "background", "theme", "colors"),
            Page(Label(Strings.WinXInstalledApps), "ms-settings:appsfeatures", windowsSettings, "uninstall", "apps"),
            Page(Strings.SearchActDefaultApps, "ms-settings:defaultapps", windowsSettings),
            Page(Strings.SearchActNotifications, "ms-settings:notifications", windowsSettings),
            Page(Label(Strings.WinXPowerOptions), "ms-settings:powersleep", windowsSettings, "battery", "power", "sleep"),
            Page(Strings.SearchActStorage, "ms-settings:storagesense", windowsSettings, "disk space"),
            Page(Strings.SearchActDateTime, "ms-settings:dateandtime", windowsSettings, "time", "clock"),
            Page(Strings.SearchActWindowsUpdate, "ms-settings:windowsupdate", windowsSettings, "update"),
            Page(Label(Strings.WinXSystem), "ms-settings:about", windowsSettings, "about"),

            Tool(Label(Strings.WinXTaskManager), SystemFile("taskmgr.exe"), windows),
            Tool(Label(Strings.WinXDeviceManager), SystemFile("devmgmt.msc"), windows),
            Tool(Label(Strings.WinXEventViewer), SystemFile("eventvwr.msc"), windows),
            Tool(Label(Strings.WinXDiskManagement), SystemFile("diskmgmt.msc"), windows),
            Tool(Label(Strings.WinXComputerManagement), SystemFile("compmgmt.msc"), windows),
            new(Label(Strings.WinXRun), windows, RunGlyph, _ =>
            {
                Log.Info("Search: Run.");
                HotkeyActions.ShowRunBox();
            }),
            new(Label(Strings.WinXDesktop), windows, DesktopGlyph, _ =>
            {
                Log.Info("Search: Show desktop.");
                host.Desktop.Toggle(host.Tasks.Groups);
            }) { Words = new[] { "show desktop" } },

            new(Strings.PowerLock, power, PowerActions.LockGlyph, _ => Lock()) { Words = new[] { "lock" } },
        });

        // Start's power menu, as it is now: only what this PC offers, each with its question.
        foreach (PowerChoice choice in PowerActions.Choices(host.Settings.Current))
        {
            string[] words = choice.Glyph switch
            {
                PowerActions.ShutDownGlyph => new[] { "shutdown", "power off", "turn off" },
                PowerActions.RestartGlyph => new[] { "reboot", "restart" },
                PowerActions.SleepGlyph => new[] { "sleep" },
                PowerActions.HibernateGlyph => new[] { "hibernate" },
                _ => Array.Empty<string>(),
            };
            list.Add(new SearchAction(choice.Label, power, choice.Glyph, hwnd => choice.Run(hwnd), choice.Ask) { Words = words });
        }

        list.Add(new SearchAction(Label(Strings.WinXSignOut), power, SignOutGlyph, _ => SignOut(), Strings.SearchActSignOutAsk) { Words = new[] { "sign out", "log off", "logout" } });
        list.Add(new SearchAction(Strings.BarExit, Strings.SearchActMachitan, PowerActions.ExitGlyph, _ =>
        {
            Log.Info("Search: Exit Machitan.");
            host.Shutdown();
        }) { Words = new[] { "exit", "quit" } });
        return list;
    }

    /// <summary>A Windows Settings page (opened with its ms-settings address).</summary>
    private static SearchAction Page(string name, string address, string detail, params string[] words)
    {
        return new SearchAction(name, detail, PageGlyph, _ => Open(address, name)) { Words = words };
    }

    /// <summary>A Windows tool from the Win+X menu.</summary>
    private static SearchAction Tool(string name, string path, string detail)
    {
        return new SearchAction(name, detail, ToolGlyph, _ => Open(path, name));
    }

    private static void Open(string target, string name)
    {
        if (Launcher.Start(target))
        {
            Log.Info("Search: opened " + target + " (" + name + ").");
        }
    }

    private static string SystemFile(string name)
    {
        return Path.Combine(Environment.SystemDirectory, name);
    }

    /// <summary>A Win+X label without its key mark ("Task &amp;Manager" reads "Task Manager").</summary>
    private static string Label(string label)
    {
        return label.Replace("&&", "\u0001", StringComparison.Ordinal)
            .Replace("&", string.Empty, StringComparison.Ordinal)
            .Replace("\u0001", "&", StringComparison.Ordinal);
    }

    private static void Lock()
    {
        if (User32.LockWorkStation())
        {
            Log.Info("Search: Lock.");
        }
        else
        {
            Log.Warn("Search: Windows did not lock the PC (error " + System.Runtime.InteropServices.Marshal.GetLastPInvokeError() + ").");
        }
    }

    private static void SignOut()
    {
        foreach (PowerChoice choice in PowerActions.WinXChoices())
        {
            if (choice.Label == Strings.WinXSignOut)
            {
                Log.Info("Search: Sign out.");
                choice.Run(0);
                return;
            }
        }

        Log.Warn("Search: Sign out was not found in the Win+X choices.");
    }

    /// <summary>The calculator's answer on the clipboard, as shown.</summary>
    private static void CopyAnswer(string answer)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(answer);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            Log.Info("Search: a calculator answer was copied.");
        }
        catch (Exception ex)
        {
            Log.Error("Search: the calculator answer could not be copied.", ex);
        }
    }
}
