using System.Text;
using System.Text.Json.Serialization;

namespace HerdMe.Windows.Models;

public sealed class CapturedDump
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;

    public string Source { get; init; } = "Unknown source";

    public string Summary { get; init; } = string.Empty;

    public string Payload { get; init; } = string.Empty;

    [JsonIgnore]
    public string ReceivedText => ReceivedAt.LocalDateTime.ToString("g");

    public static CapturedDump Decode(string payload)
    {
        try
        {
            var serialized = Convert.FromBase64String(payload);
            var value = new PhpSerializationParser(serialized).Parse();
            return new CapturedDump
            {
                Source = value.FirstString(["file", "source"]) ?? "Local application",
                Summary = value.Rendered(),
                Payload = payload
            };
        }
        catch (Exception error) when (error is FormatException or InvalidDataException)
        {
            return new CapturedDump
            {
                Summary = "Unable to parse VarDumper payload: " + error.Message,
                Payload = payload
            };
        }
    }
}

internal sealed class PhpSerializedValue
{
    private PhpSerializedValue(string kind, object? scalar = null, List<(PhpSerializedValue, PhpSerializedValue)>? values = null)
    {
        Kind = kind;
        Scalar = scalar;
        Values = values ?? [];
    }

    public string Kind { get; }
    public object? Scalar { get; }
    public List<(PhpSerializedValue Key, PhpSerializedValue Value)> Values { get; }

    public static PhpSerializedValue Null() => new("null");
    public static PhpSerializedValue Bool(bool value) => new("bool", value);
    public static PhpSerializedValue Integer(long value) => new("integer", value);
    public static PhpSerializedValue Double(double value) => new("double", value);
    public static PhpSerializedValue String(string value) => new("string", value);
    public static PhpSerializedValue Array(List<(PhpSerializedValue, PhpSerializedValue)> values) => new("array", values: values);
    public static PhpSerializedValue Object(string name, List<(PhpSerializedValue, PhpSerializedValue)> values) => new("object", name, values);
    public static PhpSerializedValue Reference(int value) => new("reference", value);

    public string Rendered(int depth = 0)
    {
        if (depth >= 16) return "...";
        return Kind switch
        {
            "null" => "null",
            "bool" => (bool)Scalar! ? "true" : "false",
            "integer" or "double" => Convert.ToString(Scalar, System.Globalization.CultureInfo.InvariantCulture)!,
            "string" => $"\"{Scalar}\"",
            "reference" => $"reference({Scalar})",
            "array" => RenderValues("[", "]", depth),
            "object" => $"{Scalar} " + RenderValues("{", "}", depth),
            _ => string.Empty
        };
    }

    public string? FirstString(IEnumerable<string> keys)
    {
        if (Kind is not ("array" or "object")) return null;
        foreach (var pair in Values)
        {
            var key = pair.Key.ShortKey().ToLowerInvariant();
            if (keys.Any(candidate => key.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                && pair.Value.Kind == "string")
            {
                return pair.Value.Scalar?.ToString();
            }
            var nested = pair.Value.FirstString(keys);
            if (nested is not null) return nested;
        }
        return null;
    }

    private string RenderValues(string opening, string closing, int depth)
    {
        if (Values.Count == 0) return opening + closing;
        var indent = new string(' ', (depth + 1) * 2);
        var closingIndent = new string(' ', depth * 2);
        return opening + Environment.NewLine
            + string.Join("," + Environment.NewLine, Values.Select(pair =>
                indent + pair.Key.ShortKey() + ": " + pair.Value.Rendered(depth + 1)
            ))
            + Environment.NewLine + closingIndent + closing;
    }

    private string ShortKey()
    {
        return Kind switch
        {
            "string" => Scalar?.ToString()?.Split('\0').LastOrDefault() ?? string.Empty,
            "integer" => Scalar?.ToString() ?? string.Empty,
            _ => Rendered()
        };
    }
}

internal sealed class PhpSerializationParser
{
    private readonly byte[] bytes;
    private int index;

    public PhpSerializationParser(byte[] bytes)
    {
        this.bytes = bytes;
    }

    public PhpSerializedValue Parse()
    {
        if (index >= bytes.Length) throw Malformed();
        var marker = (char)bytes[index++];
        return marker switch
        {
            'N' => ParseNull(),
            'b' => ParseBool(),
            'i' => ParseInteger(),
            'd' => ParseDouble(),
            's' => PhpSerializedValue.String(ReadString()),
            'a' => ParseArray(),
            'O' => ParseObject(),
            'R' or 'r' => ParseReference(),
            _ => throw new InvalidDataException($"Unsupported PHP serialized type: {marker}")
        };
    }

    private PhpSerializedValue ParseNull()
    {
        Expect(';');
        return PhpSerializedValue.Null();
    }

    private PhpSerializedValue ParseBool()
    {
        Expect(':');
        return PhpSerializedValue.Bool(ReadUntil(';') == "1");
    }

    private PhpSerializedValue ParseInteger()
    {
        Expect(':');
        return long.TryParse(ReadUntil(';'), out var value)
            ? PhpSerializedValue.Integer(value)
            : throw Malformed();
    }

    private PhpSerializedValue ParseDouble()
    {
        Expect(':');
        return double.TryParse(
            ReadUntil(';'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value
        ) ? PhpSerializedValue.Double(value) : throw Malformed();
    }

    private PhpSerializedValue ParseArray()
    {
        Expect(':');
        var count = ReadCount(':');
        Expect('{');
        var values = new List<(PhpSerializedValue, PhpSerializedValue)>();
        for (var item = 0; item < count; item++) values.Add((Parse(), Parse()));
        Expect('}');
        return PhpSerializedValue.Array(values);
    }

    private PhpSerializedValue ParseObject()
    {
        Expect(':');
        var nameLength = ReadCount(':');
        Expect('"');
        var name = ReadBytes(nameLength);
        Expect('"');
        Expect(':');
        var count = ReadCount(':');
        Expect('{');
        var values = new List<(PhpSerializedValue, PhpSerializedValue)>();
        for (var item = 0; item < count; item++) values.Add((Parse(), Parse()));
        Expect('}');
        return PhpSerializedValue.Object(name, values);
    }

    private PhpSerializedValue ParseReference()
    {
        Expect(':');
        return int.TryParse(ReadUntil(';'), out var value)
            ? PhpSerializedValue.Reference(value)
            : throw Malformed();
    }

    private string ReadString()
    {
        Expect(':');
        var length = ReadCount(':');
        Expect('"');
        var value = ReadBytes(length);
        Expect('"');
        Expect(';');
        return value;
    }

    private int ReadCount(char delimiter)
    {
        return int.TryParse(ReadUntil(delimiter), out var value) && value >= 0
            ? value
            : throw Malformed();
    }

    private string ReadBytes(int length)
    {
        if (length < 0 || index + length > bytes.Length) throw Malformed();
        var value = Encoding.UTF8.GetString(bytes, index, length);
        index += length;
        return value;
    }

    private string ReadUntil(char delimiter)
    {
        var start = index;
        while (index < bytes.Length && bytes[index] != delimiter) index++;
        if (index >= bytes.Length) throw Malformed();
        var value = Encoding.UTF8.GetString(bytes, start, index - start);
        index++;
        return value;
    }

    private void Expect(char value)
    {
        if (index >= bytes.Length || bytes[index++] != value) throw Malformed();
    }

    private static InvalidDataException Malformed() => new("Malformed PHP serialized value.");
}
