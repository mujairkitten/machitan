using System.Collections.Generic;
using Machitan.Start;

namespace Machitan.SettingsUi;

/// <summary>One page of Machitan's Settings.</summary>
/// <param name="Key">The page's key (SettingsPages' constants), as the sidebar and OpenSettings use it.</param>
/// <param name="Name">Its name in the sidebar.</param>
/// <param name="Glyph">Its icon (Segoe Fluent Icons).</param>
internal sealed record SettingsPage(string Key, string Name, int Glyph)
{
    /// <summary>The page's name: what the Settings window's search box lists.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The pages of Machitan's Settings, in the sidebar's order: one list for the sidebar, the
/// Settings window's search box, and Start's and the bar's search (SearchActions). Search finds
/// pages by their names only.
/// </summary>
internal static class SettingsPages
{
    internal const string General = "general";
    internal const string Taskbar = "taskbar";
    internal const string Start = "start";
    internal const string Widgets = "widgets";
    internal const string Board = "board";
    internal const string Dynaland = "dynaland";
    internal const string Edge = "edge";
    internal const string Shortcuts = "shortcuts";
    internal const string Privacy = "privacy";
    internal const string About = "about";

    /// <summary>Every page, in the sidebar's order (names in the current language).</summary>
    internal static SettingsPage[] All()
    {
        return new[]
        {
            new SettingsPage(General, Strings.SetPageGeneral, 0xE713),
            new SettingsPage(Taskbar, Strings.SetPageTaskbar, 0xE71D),
            new SettingsPage(Start, Strings.SetPageStart, 0xE80F),
            new SettingsPage(Widgets, Strings.SetPageWidgets, 0xE823),
            new SettingsPage(Board, Strings.SetPageBoard, 0xF246),
            new SettingsPage(Dynaland, Strings.SetPageDynaland, 0xE8D6),
            new SettingsPage(Edge, Strings.SetPageEdge, 0xE7F4),
            new SettingsPage(Shortcuts, Strings.SetPageShortcuts, 0xE765),
            new SettingsPage(Privacy, Strings.SetPagePrivacy, 0xE72E),
            new SettingsPage(About, Strings.SetPageAbout, 0xE946),
        };
    }

    /// <summary>The page with this key, or null.</summary>
    internal static SettingsPage? Named(string key)
    {
        foreach (SettingsPage page in All())
        {
            if (page.Key == key)
            {
                return page;
            }
        }

        return null;
    }

    /// <summary>The pages whose names fit <paramref name="query"/> (anywhere in the name), best fit first (SearchRank); equal fits keep the sidebar's order.</summary>
    internal static List<SettingsPage> Find(string query)
    {
        var found = new List<SettingsPage>();
        string text = query.Trim();
        if (text.Length == 0)
        {
            return found;
        }

        var ranked = new List<(int Rank, int Index, SettingsPage Page)>();
        SettingsPage[] pages = All();
        for (int i = 0; i < pages.Length; i++)
        {
            int rank = SearchRank.Of(pages[i].Name, text);
            if (rank != SearchRank.NoMatch)
            {
                ranked.Add((rank, i, pages[i]));
            }
        }

        ranked.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Index.CompareTo(b.Index));
        foreach ((int Rank, int Index, SettingsPage Page) entry in ranked)
        {
            found.Add(entry.Page);
        }

        return found;
    }
}
