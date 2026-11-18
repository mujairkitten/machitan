using System;

namespace Machitan.Start;

/// <summary>
/// How well an app's name fits what was typed, shared by Start's search and the bar's search
/// box, so both list results in the same order.
/// </summary>
internal static class SearchRank
{
    internal const int NoMatch = int.MaxValue;

    /// <summary>
    /// Best first: 0 the whole name, 1 the name starts with it, 2 a word in the name starts
    /// with it, 3 it is somewhere inside; NoMatch otherwise. So "WACUP" puts WACUP before
    /// Uninstall WACUP. Everything fits an empty search equally (0).
    /// </summary>
    internal static int Of(string name, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (name.Equals(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return 0;
        }

        int at = name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0)
        {
            return NoMatch;
        }

        if (at == 0)
        {
            return 1;
        }

        while (at > 0)
        {
            if (!char.IsLetterOrDigit(name[at - 1]))
            {
                return 2;
            }

            at = at + 1 < name.Length ? name.IndexOf(query, at + 1, StringComparison.CurrentCultureIgnoreCase) : -1;
        }

        return 3;
    }
}
