using System;
using System.Globalization;

namespace Machitan.Start;

/// <summary>
/// The search's calculator: what an expression typed in Start's or the bar's search comes to.
/// Numbers (with a decimal point, or a decimal comma where Windows' number format uses one),
/// + - * / (also the multiplication and division signs), ^ for powers, % after a number for
/// percent ("50%" is 0.5) or between two numbers for the remainder, and brackets. Only text that
/// is a whole expression with at least one operation counts: a plain number or a name is left to
/// the other results, and nothing is guessed.
/// </summary>
internal static class Calculator
{
    private const int MaxLength = 200;
    private const int MaxDepth = 64;
    private const char TimesSign = '\u00D7';
    private const char DivideSign = '\u00F7';

    /// <summary>True when <paramref name="text"/> is an expression with a finite answer.</summary>
    internal static bool TryEvaluate(string text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return false;
        }

        var parser = new Parser(text);
        return parser.TryParse(out value) && parser.HadOperation && double.IsFinite(value);
    }

    /// <summary>The answer as people read it here: Windows' number format, up to ten decimals, no group separators.</summary>
    internal static string Format(double value)
    {
        if (value == 0)
        {
            // No "-0".
            return "0";
        }

        double size = Math.Abs(value);
        if (size >= 1e15 || size < 1e-10)
        {
            return value.ToString("G12", CultureInfo.CurrentCulture);
        }

        return Math.Round(value, 10).ToString("0.##########", CultureInfo.CurrentCulture);
    }

    /// <summary>Reads the expression from the left: sums of products of powers, as in school.</summary>
    private sealed class Parser
    {
        private readonly string _text;
        private readonly bool _commaDecimal;
        private int _at;
        private int _depth;

        internal Parser(string text)
        {
            _text = text;
            _commaDecimal = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator == ",";
        }

        /// <summary>A +, -, *, /, ^ or % between or after numbers was read.</summary>
        internal bool HadOperation { get; private set; }

        internal bool TryParse(out double value)
        {
            if (!TrySum(out value))
            {
                return false;
            }

            SkipSpaces();
            return _at == _text.Length;
        }

        // sum: product (('+' | '-') product)*
        private bool TrySum(out double value)
        {
            if (!TryProduct(out value))
            {
                return false;
            }

            while (true)
            {
                char next = Peek();
                if (next != '+' && next != '-')
                {
                    return true;
                }

                _at++;
                if (!TryProduct(out double right))
                {
                    return false;
                }

                value = next == '+' ? value + right : value - right;
                HadOperation = true;
            }
        }

        // product: signed (('*' | '/' | '%') signed)*
        private bool TryProduct(out double value)
        {
            if (!TrySigned(out value))
            {
                return false;
            }

            while (true)
            {
                char next = Peek();
                if (next != '*' && next != '/' && next != TimesSign && next != DivideSign && next != '%')
                {
                    return true;
                }

                _at++;
                if (!TrySigned(out double right))
                {
                    return false;
                }

                if (next == '%')
                {
                    if (right == 0)
                    {
                        return false;
                    }

                    value %= right;
                }
                else if (next == '/' || next == DivideSign)
                {
                    if (right == 0)
                    {
                        return false;
                    }

                    value /= right;
                }
                else
                {
                    value *= right;
                }

                HadOperation = true;
            }
        }

        // signed: ('+' | '-') signed | power. So -2^2 is -4, as in school.
        private bool TrySigned(out double value)
        {
            char next = Peek();
            if (next == '+' || next == '-')
            {
                _at++;
                if (!Enter())
                {
                    value = 0;
                    return false;
                }

                bool read = TrySigned(out value);
                _depth--;
                if (next == '-')
                {
                    value = -value;
                }

                return read;
            }

            return TryPower(out value);
        }

        // power: percent ('^' signed)?  (2^3^2 is 2^9: the right-hand side goes first)
        private bool TryPower(out double value)
        {
            if (!TryPercent(out value))
            {
                return false;
            }

            if (Peek() != '^')
            {
                return true;
            }

            _at++;
            if (!Enter())
            {
                return false;
            }

            bool read = TrySigned(out double exponent);
            _depth--;
            if (!read)
            {
                return false;
            }

            value = Math.Pow(value, exponent);
            HadOperation = true;
            return true;
        }

        // percent: atom ('%')*, where a % with a number after it is a remainder instead (see product).
        private bool TryPercent(out double value)
        {
            if (!TryAtom(out value))
            {
                return false;
            }

            while (Peek() == '%' && !NumberFollows(_at + 1))
            {
                _at++;
                value /= 100;
                HadOperation = true;
            }

            return true;
        }

        // atom: number | '(' sum ')'
        private bool TryAtom(out double value)
        {
            value = 0;
            char next = Peek();
            if (next == '(')
            {
                _at++;
                if (!Enter())
                {
                    return false;
                }

                bool read = TrySum(out value);
                _depth--;
                if (!read || Peek() != ')')
                {
                    return false;
                }

                _at++;
                return true;
            }

            return TryNumber(out value);
        }

        private bool TryNumber(out double value)
        {
            value = 0;
            SkipSpaces();
            int start = _at;
            bool digits = false;
            bool point = false;
            var number = new System.Text.StringBuilder();
            while (_at < _text.Length)
            {
                char c = _text[_at];
                if (c >= '0' && c <= '9')
                {
                    digits = true;
                    number.Append(c);
                }
                else if (!point && (c == '.' || (c == ',' && _commaDecimal)))
                {
                    point = true;
                    number.Append('.');
                }
                else
                {
                    break;
                }

                _at++;
            }

            if (!digits)
            {
                _at = start;
                return false;
            }

            return double.TryParse(number.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>A number or an opening bracket comes next (after spaces), from <paramref name="from"/>.</summary>
        private bool NumberFollows(int from)
        {
            int at = from;
            while (at < _text.Length && char.IsWhiteSpace(_text[at]))
            {
                at++;
            }

            if (at >= _text.Length)
            {
                return false;
            }

            char c = _text[at];
            return (c >= '0' && c <= '9') || c == '.' || c == '(' || (c == ',' && _commaDecimal);
        }

        /// <summary>One level deeper (brackets, signs, powers); false past a sane depth.</summary>
        private bool Enter()
        {
            _depth++;
            return _depth <= MaxDepth;
        }

        /// <summary>The next character after spaces (they are skipped), or '\0' at the end.</summary>
        private char Peek()
        {
            SkipSpaces();
            return _at < _text.Length ? _text[_at] : '\0';
        }

        private void SkipSpaces()
        {
            while (_at < _text.Length && char.IsWhiteSpace(_text[_at]))
            {
                _at++;
            }
        }
    }
}
