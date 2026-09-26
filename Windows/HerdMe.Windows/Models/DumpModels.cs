using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using HerdMe.Windows.Services;

namespace HerdMe.Windows.Models;

public sealed class CapturedDump
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.Now;

    public string Source { get; set; } = "Unknown source";

    public string Summary { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    /// <summary>True for list rows loaded without the payload; load the full capture to show it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSummary { get; set; }

    internal const int SummaryRowCharacters = 2_048;

    /// <summary>The columns the capture list needs, without the (possibly large) payload.</summary>
    public CapturedDump ToSummary() => new()
    {
        Id = Id,
        ReceivedAt = ReceivedAt,
        Source = Source.Length > SummaryRowCharacters ? Source[..SummaryRowCharacters] : Source,
        Summary = Summary.Length > SummaryRowCharacters ? Summary[..SummaryRowCharacters] : Summary,
        IsSummary = true
    };

    [JsonIgnore]
    public string ReceivedText => ReceivedAt.LocalDateTime.ToString("g");

    [JsonIgnore]
    public string SourcePreview => CapturePreview.LimitText(Source, 512).Text;

    [JsonIgnore]
    public string SummaryPreview => CapturePreview.LimitText(Summary, 1_024).Text;

    public static CapturedDump Decode(string payload)
    {
        try
        {
            if (payload.Length > PhpSerializationParser.MaximumEncodedLength)
            {
                throw new InvalidDataException("VarDumper payload exceeds the supported size.");
            }
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
    internal const int MaximumEncodedLength = 16 * 1_024 * 1_024;
    private const int MaximumDepth = 64;
    private const int MaximumNodes = 100_000;
    private const int MaximumStringBytes = 4 * 1_024 * 1_024;
    private const int MaximumTokenLength = 64;
    private readonly byte[] bytes;
    private int index;
    private int nodes;

    public PhpSerializationParser(byte[] bytes)
    {
        this.bytes = bytes;
    }

    public PhpSerializedValue Parse()
    {
        if (bytes.Length > MaximumEncodedLength / 4 * 3) throw LimitExceeded();
        var value = ParseValue(0);
        if (index != bytes.Length) throw Malformed();
        return value;
    }

    private PhpSerializedValue ParseValue(int depth)
    {
        if (depth > MaximumDepth || ++nodes > MaximumNodes) throw LimitExceeded();
        if (index >= bytes.Length) throw Malformed();
        var marker = (char)bytes[index++];
        return marker switch
        {
            'N' => ParseNull(),
            'b' => ParseBool(),
            'i' => ParseInteger(),
            'd' => ParseDouble(),
            's' => PhpSerializedValue.String(ReadString()),
            'a' => ParseArray(depth),
            'O' => ParseObject(depth),
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
        return ReadUntil(';') switch
        {
            "0" => PhpSerializedValue.Bool(false),
            "1" => PhpSerializedValue.Bool(true),
            _ => throw Malformed()
        };
    }

    private PhpSerializedValue ParseInteger()
    {
        Expect(':');
        return long.TryParse(ReadUntil(';'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? PhpSerializedValue.Integer(value)
            : throw Malformed();
    }

    private PhpSerializedValue ParseDouble()
    {
        Expect(':');
        var token = ReadUntil(';');
        if (token == "INF") return PhpSerializedValue.Double(double.PositiveInfinity);
        if (token == "-INF") return PhpSerializedValue.Double(double.NegativeInfinity);
        if (token == "NAN") return PhpSerializedValue.Double(double.NaN);
        if (!token.Any(char.IsAsciiDigit)
            || token.Any(character => !char.IsAsciiDigit(character) && character is not ('+' or '-' or '.' or 'e' or 'E')))
        {
            throw Malformed();
        }
        return double.TryParse(
            token,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture,
            out var value
        ) && double.IsFinite(value) ? PhpSerializedValue.Double(value) : throw Malformed();
    }

    private PhpSerializedValue ParseArray(int depth)
    {
        Expect(':');
        var count = ReadCount(':');
        Expect('{');
        var values = ReadEntries(count, depth);
        Expect('}');
        return PhpSerializedValue.Array(values);
    }

    private PhpSerializedValue ParseObject(int depth)
    {
        Expect(':');
        var nameLength = ReadCount(':');
        Expect('"');
        var name = ReadBytes(nameLength);
        Expect('"');
        Expect(':');
        var count = ReadCount(':');
        Expect('{');
        var values = ReadEntries(count, depth);
        Expect('}');
        return PhpSerializedValue.Object(name, values);
    }

    private PhpSerializedValue ParseReference()
    {
        Expect(':');
        return int.TryParse(ReadUntil(';'), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? PhpSerializedValue.Reference(value)
            : throw Malformed();
    }

    private List<(PhpSerializedValue, PhpSerializedValue)> ReadEntries(int count, int depth)
    {
        if (count > (MaximumNodes - nodes) / 2) throw LimitExceeded();
        var values = new List<(PhpSerializedValue, PhpSerializedValue)>();
        for (var item = 0; item < count; item++)
        {
            var key = ParseValue(depth + 1);
            if (key.Kind is not ("integer" or "string")) throw Malformed();
            values.Add((key, ParseValue(depth + 1)));
        }
        return values;
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
        return int.TryParse(ReadUntil(delimiter), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Malformed();
    }

    private string ReadBytes(int length)
    {
        if (length < 0 || length > bytes.Length - index) throw Malformed();
        if (length > MaximumStringBytes) throw LimitExceeded();
        var value = Encoding.UTF8.GetString(bytes, index, length);
        index += length;
        return value;
    }

    private string ReadUntil(char delimiter)
    {
        var start = index;
        while (index < bytes.Length && bytes[index] != delimiter)
        {
            if (index - start >= MaximumTokenLength) throw LimitExceeded();
            index++;
        }
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

    private static InvalidDataException LimitExceeded() => new("PHP serialized value exceeds the supported complexity or size.");
}
