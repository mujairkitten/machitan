using System.Globalization;

namespace Machitan;

/// <summary>Fills a translated text's {0}, {1} ... places, in the language's own number and date forms.</summary>
internal static class Fmt
{
    internal static string F(string format, params object?[] values)
    {
        return string.Format(CultureInfo.CurrentCulture, format, values);
    }
}
