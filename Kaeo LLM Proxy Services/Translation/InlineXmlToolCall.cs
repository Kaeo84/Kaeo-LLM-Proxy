using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kaeo.LlmProxy.Services.Translation;

/// <summary>
/// Inline XML tool-call parsing for the Phase-B IR frame translator: some llama.cpp chat
/// templates emit calls as markup inside <c>content</c> instead of the structured
/// <c>tool_calls</c> field, so they have to be lifted out and re-emitted as OpenAI frames.
/// </summary>
/// <remarks>
/// The regex patterns are written with <c>\u003C</c>/<c>\u003E</c> escapes (which the .NET regex
/// engine resolves to the angle-bracket characters) so this source file never contains raw
/// markup sequences, matching the convention already used by the shared adapter sources.
/// </remarks>
internal static partial class InlineXmlToolCall
{
    [GeneratedRegex(
        @"\u003Ctool_call\u003E\s*\u003Cfunction=(?<name>[^\u003E\s]+)\u003E\s*(?<body>.*?)\s*\u003C/function\u003E\s*\u003C/tool_call\u003E",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockRegex();

    [GeneratedRegex(
        @"\u003Cparameter=(?<n>[^\u003E\s]+)\u003E\s*(?<v>.*?)\s*\u003C/parameter\u003E",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ParameterRegex();

    /// <summary>Opening marker of an inline call block, used by the incremental token scanner.</summary>
    public const string OpenMarker = "\u003Ctool_call\u003E";

    /// <summary>Closing marker of an inline call block.</summary>
    public const string CloseMarker = "\u003C/tool_call\u003E";

    /// <summary>
    /// Parses a complete inline call block into its function name and arguments, or null when the
    /// block does not match the expected shape.
    /// </summary>
    public static (string Name, Dictionary<string, object?> Arguments)? Parse(string block)
    {
        Match match = BlockRegex().Match(block);
        if (!match.Success)
            return null;

        Dictionary<string, object?> arguments = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match parameter in ParameterRegex().Matches(match.Groups["body"].Value))
            arguments[parameter.Groups["n"].Value.Trim()] = ParseParameterValue(parameter.Groups["v"].Value.Trim());

        return (match.Groups["name"].Value.Trim(), arguments);
    }

    /// <summary>
    /// Converts a parameter's text to the closest JSON scalar so numbers and booleans reach the
    /// client typed rather than as strings; anything else stays a string.
    /// </summary>
    private static object? ParseParameterValue(string value)
    {
        if (bool.TryParse(value, out bool booleanValue))
            return booleanValue;

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integerValue))
            return integerValue;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue))
            return doubleValue;

        return value;
    }

    /// <summary>Serializer options matching the handler's wire output (nulls omitted).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Builds the OpenAI wire shape for one synthesised call.</summary>
    public static JsonObject BuildCallObject(string name, Dictionary<string, object?> arguments, string callId, int index) => new()
    {
        ["index"] = index,
        ["id"] = callId,
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["arguments"] = JsonSerializer.Serialize(arguments, JsonOptions),
        },
    };
}