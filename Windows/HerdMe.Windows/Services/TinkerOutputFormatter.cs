using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HerdMe.Windows.Services;

public enum TinkerOutputView
{
    Dump,
    Json,
    Table
}

/// <summary>
/// Turns the JSON a Tinker run printed (TinkerOutputMode.Json) into indented JSON or a plain
/// text table. Returns null when the output is not JSON (an echo, a warning, an error), so the
/// page can fall back to the raw text.
/// </summary>
public static class TinkerOutputFormatter
{
    public const int MaximumTableRows = 200;
    public const int MaximumTableColumns = 12;
    public const int MaximumCellCharacters = 40;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string? IndentJson(string output)
    {
        var node = Parse(output);
        return node?.ToJsonString(Indented);
    }

    // Arrays of objects (Eloquent collections, query results), a single object (a model), or a
    // Laravel paginator ({"data": [...]}) become a table. Scalars in an array get one column.
    public static string? Table(string output)
    {
        var node = Parse(output);
        if (node is JsonObject page && page["data"] is JsonArray pageRows) node = pageRows;
        if (node is JsonObject single) node = new JsonArray(single.DeepClone());
        if (node is not JsonArray array || array.Count == 0) return null;

        var rows = array.Take(MaximumTableRows).ToList();
        var objects = rows.All(row => row is JsonObject);
        var columns = objects
            ? rows.Cast<JsonObject>()
                .SelectMany(row => row.Select(property => property.Key))
                .Distinct(StringComparer.Ordinal)
                .Take(MaximumTableColumns)
                .ToList()
            : new List<string> { "value" };
        if (columns.Count == 0) return null;
        var cells = rows
            .Select(row => objects && row is JsonObject record
                ? columns.Select(column => record.TryGetPropertyValue(column, out var value) ? Cell(value) : string.Empty).ToArray()
                : new[] { Cell(row) })
            .ToList();
        return TextTable(columns, cells, array.Count - rows.Count);
    }

    // Shared by Tinker and the Sites database peek: a header, a rule and one line per row.
    // Cells are cleaned (no line breaks or tabs) and cut at MaximumCellCharacters.
    public static string TextTable(IReadOnlyList<string> columns, IReadOnlyList<string[]> rows, int hiddenRows = 0)
    {
        var header = columns.Select(CleanCell).ToArray();
        var cells = rows
            .Select(row => header.Select((_, index) => index < row.Length ? CleanCell(row[index]) : string.Empty).ToArray())
            .ToList();
        var widths = header
            .Select((column, index) => Math.Max(column.Length, cells.Count == 0 ? 0 : cells.Max(row => row[index].Length)))
            .ToArray();

        var builder = new StringBuilder();
        AppendRow(builder, header, widths);
        builder.AppendLine(string.Join("-+-", widths.Select(width => new string('-', width))));
        foreach (var row in cells) AppendRow(builder, row, widths);
        if (hiddenRows > 0)
        {
            builder.AppendLine(string.Format(
                CultureInfo.CurrentCulture,
                ServiceText.Get("TinkerTableMoreRows", "({0} more rows not shown)"),
                hiddenRows
            ));
        }
        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> values, int[] widths)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0) builder.Append(" | ");
            builder.Append(index == values.Count - 1 ? values[index] : values[index].PadRight(widths[index]));
        }
        builder.AppendLine();
    }

    private static string Cell(JsonNode? value)
    {
        string text;
        if (value is null)
        {
            text = "null";
        }
        else if (value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String)
        {
            text = scalar.GetValue<string>();
        }
        else
        {
            text = value.ToJsonString();
        }
        return CleanCell(text);
    }

    private static string CleanCell(string? value)
    {
        var text = (value ?? string.Empty).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);
        return text.Length > MaximumCellCharacters ? text[..(MaximumCellCharacters - 3)] + "..." : text;
    }

    private static JsonNode? Parse(string output)
    {
        var text = output.Trim();
        if (text.Length == 0 || (text[0] != '{' && text[0] != '[')) return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
