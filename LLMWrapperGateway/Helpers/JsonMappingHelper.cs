using System.Text.Json;
using System.Text.RegularExpressions;

namespace LLMWrapperGateway.Helpers;

public static partial class JsonMappingHelper
{
    /// <summary>
    /// Applies a JSON template by replacing placeholders like {model}, {messages},
    /// {messages[0].content}, {temperature}, {session}, etc. with values from the source JSON.
    /// If template is null/empty, returns the original body unchanged.
    /// </summary>
    public static string ApplyMapping(string? template, string sourceJson, Dictionary<string, string>? extraVars = null)
    {
        if (string.IsNullOrWhiteSpace(template))
            return sourceJson;

        using var doc = JsonDocument.Parse(sourceJson);
        var root = doc.RootElement;

        var result = PlaceholderRegex().Replace(template, match =>
        {
            var path = match.Groups[1].Value;

            // Check extra variables first (e.g., {session})
            if (extraVars is not null && extraVars.TryGetValue(path, out var extraVal))
                return JsonEscape(extraVal);

            var value = ResolvePath(root, path);
            return value;
        });

        return result;
    }

    /// <summary>
    /// Extracts a string value from JSON by dotted path like "result.output".
    /// Returns null if path not found.
    /// </summary>
    public static string? ExtractByPath(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = ResolvePath(doc.RootElement, path);
            return result == "null" ? null : result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a dotted path like "messages[0].content" or "model" from a JsonElement.
    /// Returns the raw JSON representation for objects/arrays, or the value for primitives.
    /// </summary>
    private static string ResolvePath(JsonElement element, string path)
    {
        var current = element;
        var segments = TokenizePath(path);

        foreach (var segment in segments)
        {
            if (segment.ArrayIndex.HasValue)
            {
                // Access property first, then index
                if (!string.IsNullOrEmpty(segment.Name))
                {
                    if (current.ValueKind != JsonValueKind.Object ||
                        !current.TryGetProperty(segment.Name, out current))
                        return "null";
                }

                if (current.ValueKind != JsonValueKind.Array ||
                    segment.ArrayIndex.Value >= current.GetArrayLength())
                    return "null";

                current = current[segment.ArrayIndex.Value];
            }
            else
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(segment.Name, out current))
                    return "null";
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => JsonEscape(current.GetString() ?? ""),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => current.GetRawText() // objects and arrays returned as raw JSON
        };
    }

    private static string JsonEscape(string value)
    {
        // If the value looks like JSON (object/array), return as-is for embedding
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
            return value;

        // Escape for embedding inside a JSON string value - return without quotes
        // so the template can wrap it: "field": "{placeholder}"
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    private static List<PathSegment> TokenizePath(string path)
    {
        var segments = new List<PathSegment>();
        var parts = path.Split('.');

        foreach (var part in parts)
        {
            var arrayMatch = ArrayAccessRegex().Match(part);
            if (arrayMatch.Success)
            {
                segments.Add(new PathSegment
                {
                    Name = arrayMatch.Groups[1].Value,
                    ArrayIndex = int.Parse(arrayMatch.Groups[2].Value)
                });
            }
            else
            {
                segments.Add(new PathSegment { Name = part });
            }
        }

        return segments;
    }

    private struct PathSegment
    {
        public string Name;
        public int? ArrayIndex;
    }

    [GeneratedRegex(@"\{([^}]+)\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^(\w+)\[(\d+)\]$")]
    private static partial Regex ArrayAccessRegex();
}
