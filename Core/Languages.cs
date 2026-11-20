using System;
using System.Globalization;

namespace Machitan.Core;

/// <summary>
/// The languages Machitan's text comes in (Translations/Strings.*.resx), and the one in use.
/// A new translation adds its Strings.xx.resx and a line here (see TRANSLATING.md).
/// </summary>
internal static class Languages
{
    /// <summary>Language code and the language's own name, in the order Settings lists them.</summary>
    internal static readonly (string Code, string Name)[] Available =
    {
        ("en", "English"),
        ("id", "Bahasa Indonesia"),
    };

    /// <summary>
    /// Uses the language chosen in Settings for Machitan's text, or Windows' display language
    /// when none is chosen. Dates and numbers keep following Windows' regional format.
    /// Call once, before any window is made.
    /// </summary>
    internal static void Apply(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            Log.Info("Language: Windows' display language (" + CultureInfo.CurrentUICulture.Name + ").");
            return;
        }

        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(code);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
            Log.Info("Language: " + culture.Name + " (chosen in Settings).");
        }
        catch (CultureNotFoundException ex)
        {
            Log.Warn("Language: \"" + code + "\" is not a language Windows knows; Windows' display language is used.", ex);
        }
    }

    /// <summary>The place of a language code in Settings' list (0 = Windows' language).</summary>
    internal static int IndexOf(string code)
    {
        for (int i = 0; i < Available.Length; i++)
        {
            if (string.Equals(Available[i].Code, code, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>The language code at a place in Settings' list (empty for Windows' language).</summary>
    internal static string CodeAt(int index)
    {
        return index >= 1 && index <= Available.Length ? Available[index - 1].Code : string.Empty;
    }
}
