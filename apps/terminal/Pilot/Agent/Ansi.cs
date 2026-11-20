using System.Text;

namespace JobPilot.Terminal.Pilot.Agent;

internal static class Ansi
{
    /// <summary>Removes CSI, OSC, and two-char escapes. An escape cut off at the end is dropped whole.</summary>
    public static string Strip(ReadOnlySpan<char> input)
    {
        var output = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '\x1b')
            {
                i = SkipEscape(input, i);
                continue;
            }

            output.Append(input[i]);
        }

        return output.ToString();
    }

    /// <summary>Returns the last index consumed by the escape sequence starting at <paramref name="i"/>.</summary>
    private static int SkipEscape(ReadOnlySpan<char> s, int i)
    {
        if (i + 1 >= s.Length)
        {
            return s.Length;
        }

        var next = s[i + 1];
        if (next == '[')
        {
            var j = i + 2;
            while (j < s.Length && s[j] is < '@' or > '~')
            {
                j++;
            }
            return Math.Min(j, s.Length - 1);
        }

        if (next == ']')
        {
            var j = i + 2;
            while (j < s.Length && s[j] != '\a')
            {
                if (s[j] == '\x1b' && j + 1 < s.Length && s[j + 1] == '\\')
                {
                    return j + 1;
                }
                j++;
            }
            return Math.Min(j, s.Length - 1);
        }

        return i + 1;
    }

    /// <summary>
    /// Removes only the sequences a terminal emulator answers (OSC queries, DA/DSR/CPR,
    /// DECRQM/DECRQSS, DCS). Used on replayed output: a remounted client would re-answer a stale
    /// query and inject its reply into the provider's stdin as fake input.
    /// </summary>
    public static string StripElicits(ReadOnlySpan<char> input)
    {
        var output = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '\x1b' && TrySkipElicit(input, i, out var end))
            {
                i = end;
                continue;
            }

            output.Append(input[i]);
        }

        return output.ToString();
    }

    private static bool TrySkipElicit(ReadOnlySpan<char> s, int i, out int end)
    {
        end = i;
        if (i + 1 >= s.Length)
        {
            return false;
        }

        var next = s[i + 1];
        if (next == ']')
        {
            // OSC: drop iff the payload is a query (ends with '?'), keep sets (title, colors, links).
            var j = i + 2;
            while (j < s.Length && s[j] != '\a' && !(s[j] == '\x1b' && j + 1 < s.Length && s[j + 1] == '\\'))
            {
                j++;
            }

            if (j >= s.Length)
            {
                end = s.Length - 1;
                return true;
            }

            if (s[j - 1] == '?')
            {
                end = s[j] == '\a' ? j : j + 1;
                return true;
            }

            return false;
        }

        if (next == 'P')
        {
            // DCS: control strings are device-side state or queries; never replay them.
            var j = i + 2;
            while (j < s.Length && s[j] != '\a' && !(s[j] == '\x1b' && j + 1 < s.Length && s[j + 1] == '\\'))
            {
                j++;
            }

            end = j < s.Length && s[j] == '\x1b' ? Math.Min(j + 1, s.Length - 1) : Math.Min(j, s.Length - 1);
            return true;
        }

        if (next == '[')
        {
            var j = i + 2;
            var hasDollar = false;
            while (j < s.Length && s[j] is < '@' or > '~')
            {
                hasDollar |= s[j] == '$';
                j++;
            }

            if (j >= s.Length)
            {
                end = s.Length - 1;
                return true;
            }

            var final = s[j];
            if (final is 'c' or 'n' || (final is 'p' or 'q' && hasDollar))
            {
                end = j;
                return true;
            }
        }

        return false;
    }
}
