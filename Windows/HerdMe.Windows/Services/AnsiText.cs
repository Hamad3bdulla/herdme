using System.Text;

namespace HerdMe.Windows.Services;

public enum AnsiColor
{
    Default,
    Black,
    Red,
    Green,
    Yellow,
    Blue,
    Magenta,
    Cyan,
    White,
    Gray
}

public readonly record struct AnsiSpan(int Start, int Length, AnsiColor Color);

// Streaming ANSI parser for command output: keeps the text, turns SGR colour codes into
// spans and drops every other escape sequence (cursor moves, OSC titles, hyperlinks).
// A sequence split across two chunks is held back until the rest arrives.
public sealed class AnsiParser
{
    private const int MaximumPending = 64;
    private string pending = string.Empty;
    private AnsiColor color = AnsiColor.Default;

    public AnsiColor Current => color;

    public (string Text, IReadOnlyList<AnsiSpan> Spans) Append(string chunk)
    {
        var input = pending + chunk;
        pending = string.Empty;
        var text = new StringBuilder(input.Length);
        var spans = new List<AnsiSpan>();
        var spanStart = 0;

        void Close()
        {
            if (color != AnsiColor.Default && text.Length > spanStart)
            {
                spans.Add(new AnsiSpan(spanStart, text.Length - spanStart, color));
            }
            spanStart = text.Length;
        }

        var index = 0;
        while (index < input.Length)
        {
            var character = input[index];
            if (character != '\u001b')
            {
                // Carriage returns are dropped: offsets then match the text the view shows.
                if (character != '\r') text.Append(character);
                index++;
                continue;
            }
            var end = SequenceEnd(input, index);
            if (end < 0)
            {
                var rest = input[index..];
                // Incomplete: wait for the next chunk (unless it is clearly garbage).
                if (rest.Length <= MaximumPending) pending = rest;
                break;
            }
            if (input[index + 1] == '[' && input[end] == 'm')
            {
                var next = Apply(input[(index + 2)..end], color);
                if (next != color)
                {
                    Close();
                    color = next;
                    spanStart = text.Length;
                }
            }
            index = end + 1;
        }
        Close();
        return (text.ToString(), spans);
    }

    public static (string Text, IReadOnlyList<AnsiSpan> Spans) Parse(string value)
    {
        return new AnsiParser().Append(value);
    }

    public static string Strip(string value) => Parse(value).Text;

    // Asks Artisan (Symfony Console) for colours; the option goes right after the command
    // name so it is never taken as an argument after "--". Explicit choices are kept.
    public static IReadOnlyList<string> WithArtisanColors(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0
            || arguments.Any(argument => argument is "--ansi" or "--no-ansi")) return arguments;
        return [arguments[0], "--ansi", .. arguments.Skip(1)];
    }

    // Index of the final character of the escape sequence at start, or -1 if incomplete.
    private static int SequenceEnd(string input, int start)
    {
        if (start + 1 >= input.Length) return -1;
        var kind = input[start + 1];
        if (kind == '[')
        {
            for (var index = start + 2; index < input.Length; index++)
            {
                var character = input[index];
                if (character >= '@' && character <= '~') return index;
                if (index - start > MaximumPending) return index;
            }
            return -1;
        }
        if (kind == ']')
        {
            // OSC ends with BEL or ESC \.
            for (var index = start + 2; index < input.Length; index++)
            {
                if (input[index] == '\u0007') return index;
                if (input[index] == '\u001b' && index + 1 < input.Length && input[index + 1] == '\\') return index + 1;
            }
            return input.Length - start > 2_048 ? input.Length - 1 : -1;
        }
        return start + 1;
    }

    private static AnsiColor Apply(string parameters, AnsiColor current)
    {
        var result = current;
        string[] parts = parameters.Length == 0 ? ["0"] : parameters.Split(';');
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out var code)) continue;
            switch (code)
            {
                case 0:
                case 39:
                    result = AnsiColor.Default;
                    break;
                case >= 30 and <= 37:
                    result = (AnsiColor)(code - 30 + (int)AnsiColor.Black);
                    break;
                case 90:
                    result = AnsiColor.Gray;
                    break;
                case >= 91 and <= 97:
                    result = (AnsiColor)(code - 90 + (int)AnsiColor.Black);
                    break;
                case 38:
                    // 256-colour and true-colour foregrounds: skip their arguments.
                    index += index + 1 < parts.Length && parts[index + 1] == "5" ? 2 : 4;
                    result = AnsiColor.Default;
                    break;
                case 48:
                    index += index + 1 < parts.Length && parts[index + 1] == "5" ? 2 : 4;
                    break;
            }
        }
        return result;
    }
}
