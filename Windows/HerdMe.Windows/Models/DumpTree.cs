using System.Globalization;
using System.Text.Json;

namespace HerdMe.Windows.Models;

// A browsable tree for a captured dump. Arrays and objects become expandable nodes, and a
// string that holds a JSON object or array expands into its JSON structure. Bounded so a
// huge dump cannot freeze the UI.
public sealed class DumpTreeNode
{
    public DumpTreeNode(string label, string? value, string kind, IReadOnlyList<DumpTreeNode>? children = null)
    {
        Label = label;
        Value = value;
        Kind = kind;
        Children = children ?? [];
    }

    public string Label { get; }
    public string? Value { get; }
    public string Kind { get; }
    public IReadOnlyList<DumpTreeNode> Children { get; }

    public override string ToString()
    {
        var head = Label.Length == 0 ? string.Empty : Label + ": ";
        return Value is null ? head + Kind : head + Value;
    }
}

public static class DumpTree
{
    public const int MaximumNodes = 5_000;
    public const int MaximumDepth = 32;
    private const int MaximumValueLength = 512;
    private const int MaximumJsonLength = 1_024 * 1_024;

    public static DumpTreeNode? FromPayload(string payload)
    {
        try
        {
            if (payload.Length == 0 || payload.Length > PhpSerializationParser.MaximumEncodedLength) return null;
            var value = new PhpSerializationParser(Convert.FromBase64String(payload)).Parse();
            var budget = new Budget();
            return FromPhp(string.Empty, value, 0, budget);
        }
        catch (Exception error) when (error is FormatException or InvalidDataException)
        {
            return null;
        }
    }

    public static DumpTreeNode? FromJson(string label, string text) => FromJson(label, text, 0, new Budget());

    private static DumpTreeNode? FromJson(string label, string text, int depth, Budget budget)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed.Length > MaximumJsonLength) return null;
        if (!(trimmed[0] == '{' && trimmed[^1] == '}') && !(trimmed[0] == '[' && trimmed[^1] == ']')) return null;
        try
        {
            using var document = JsonDocument.Parse(trimmed, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            return FromJsonElement(label, document.RootElement, depth, budget);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static int Count(DumpTreeNode node) => 1 + node.Children.Sum(Count);

    private sealed class Budget
    {
        public int Remaining = MaximumNodes;
        public bool Take() => --Remaining >= 0;
    }

    private static DumpTreeNode FromPhp(string label, PhpSerializedValue value, int depth, Budget budget)
    {
        if (!budget.Take() || depth >= MaximumDepth) return new DumpTreeNode(label, "...", "more");
        switch (value.Kind)
        {
            case "array":
            case "object":
                var children = new List<DumpTreeNode>();
                foreach (var (key, item) in value.Values)
                {
                    if (budget.Remaining <= 0)
                    {
                        children.Add(new DumpTreeNode(string.Empty, "...", "more"));
                        break;
                    }
                    children.Add(FromPhp(key.ShortKey(), item, depth + 1, budget));
                }
                var kind = value.Kind == "object"
                    ? Convert.ToString(value.Scalar, CultureInfo.InvariantCulture) ?? "object"
                    : "array(" + value.Values.Count.ToString(CultureInfo.InvariantCulture) + ")";
                return new DumpTreeNode(label, null, kind, children);
            case "string":
                var text = Convert.ToString(value.Scalar, CultureInfo.InvariantCulture) ?? string.Empty;
                if (budget.Remaining > 0 && FromJson(label, text, depth, budget) is { } json) return json;
                return new DumpTreeNode(label, Quote(text), "string");
            default:
                return new DumpTreeNode(label, value.Rendered(), value.Kind);
        }
    }

    private static DumpTreeNode FromJsonElement(string label, JsonElement element, int depth, Budget budget)
    {
        if (!budget.Take() || depth >= MaximumDepth) return new DumpTreeNode(label, "...", "more");
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = new List<DumpTreeNode>();
                foreach (var property in element.EnumerateObject())
                {
                    if (budget.Remaining <= 0)
                    {
                        properties.Add(new DumpTreeNode(string.Empty, "...", "more"));
                        break;
                    }
                    properties.Add(FromJsonElement(property.Name, property.Value, depth + 1, budget));
                }
                return new DumpTreeNode(label, null, "{" + properties.Count.ToString(CultureInfo.InvariantCulture) + "}", properties);
            case JsonValueKind.Array:
                var items = new List<DumpTreeNode>();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (budget.Remaining <= 0)
                    {
                        items.Add(new DumpTreeNode(string.Empty, "...", "more"));
                        break;
                    }
                    items.Add(FromJsonElement(index++.ToString(CultureInfo.InvariantCulture), item, depth + 1, budget));
                }
                return new DumpTreeNode(label, null, "[" + element.GetArrayLength().ToString(CultureInfo.InvariantCulture) + "]", items);
            case JsonValueKind.String:
                return new DumpTreeNode(label, Quote(element.GetString() ?? string.Empty), "string");
            default:
                return new DumpTreeNode(label, element.GetRawText(), element.ValueKind.ToString().ToLowerInvariant());
        }
    }

    private static string Quote(string text)
    {
        var single = text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        if (single.Length > MaximumValueLength) single = single[..MaximumValueLength] + "...";
        return "\"" + single + "\"";
    }
}
