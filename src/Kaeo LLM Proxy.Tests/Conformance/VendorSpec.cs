using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kaeo.LlmProxy.Tests.Conformance;

/// <summary>
/// Loads and evaluates the vendored vendor API specifications under <c>API Specs\</c>.
/// </summary>
/// <remarks>
/// Phase B6 of Plans/20260914-proxy-meai-phase-b-design.md. The specs are the authoritative
/// definition of the wire contracts the proxy claims to speak, so the proxy's own output is checked
/// against them rather than against a second hand-written expectation that could drift the same way
/// the code might.
///
/// This is a deliberately small JSON Schema evaluator rather than a dependency: the test project has
/// no schema-validation package, and the subset actually needed for these documents (object
/// properties, required members, enums, primitive types, arrays, and <c>$ref</c> composition) is
/// small enough to implement directly and keep readable.
///
/// Guidance taken from the documents themselves:
/// - An absent <c>additionalProperties</c> permits extra members. This matters: the proxy
///   deliberately adds provider extensions such as <c>reasoning_content</c> and llama.cpp's
///   <c>timings</c> alongside the standard chunk fields, and the specs neither list nor forbid them.
/// - <c>nullable: true</c> is the OpenAPI 3.0 way of admitting an explicit null.
/// </remarks>
internal sealed class VendorSpec
{
    private readonly JsonObject _schemas;

    private VendorSpec(JsonObject document)
    {
        Document = document;
        _schemas = document["components"]?["schemas"] as JsonObject
            ?? throw new InvalidOperationException("The vendored spec has no components.schemas section.");
    }

    /// <summary>The parsed root of the OpenAPI document.</summary>
    public JsonObject Document { get; }

    /// <summary>Names of every schema the document declares.</summary>
    public IEnumerable<string> SchemaNames => _schemas.Select(pair => pair.Key);

    /// <summary>Loads a spec document from an absolute path.</summary>
    public static VendorSpec Load(string path)
    {
        string json = File.ReadAllText(path);
        JsonObject? document = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"Spec at {path} is not a JSON object.");

        return new VendorSpec(document);
    }

    /// <summary>True when the document declares a schema with this exact name.</summary>
    public bool HasSchema(string name) => _schemas.ContainsKey(name);

    /// <summary>Returns a schema by name, or null when it is not declared.</summary>
    public JsonObject? Schema(string name) => _schemas[name] as JsonObject;

    /// <summary>
    /// Validates <paramref name="instance"/> against the named schema, returning one message per
    /// violation. An empty result means the instance conforms.
    /// </summary>
    public IReadOnlyList<string> Validate(string schemaName, JsonNode instance)
    {
        JsonObject? schema = Schema(schemaName);
        if (schema is null)
            return [$"spec does not declare a schema named '{schemaName}'"];

        List<string> violations = [];
        ValidateNode(schema, instance, schemaName, violations);
        return violations;
    }

    /// <summary>
    /// Validates one node against one schema, resolving local <c>$ref</c> pointers and following
    /// <c>allOf</c> so the referenced contract is enforced rather than skipped.
    /// </summary>
    private void ValidateNode(JsonObject schema, JsonNode? instance, string path, List<string> violations)
    {
        // A $ref replaces the current schema entirely (OpenAPI 3.0 semantics without siblings).
        if (schema["$ref"] is JsonValue referenceValue
            && referenceValue.TryGetValue(out string? reference)
            && TryResolveReference(reference, out JsonObject? referenced))
        {
            ValidateNode(referenced, instance, path, violations);
            return;
        }

        if (schema["allOf"] is JsonArray allOf)
        {
            foreach (JsonNode? part in allOf)
            {
                if (part is JsonObject partSchema)
                    ValidateNode(partSchema, instance, path, violations);
            }
        }

        if (instance is null)
        {
            if (!IsNullable(schema))
                violations.Add($"{path}: expected a value but found null");
            return;
        }

        if (schema["enum"] is JsonArray allowed
            && !allowed.Any(candidate => JsonNode.DeepEquals(candidate, instance)))
        {
            violations.Add($"{path}: '{instance.ToJsonString()}' is not one of the allowed values");
        }

        string? expectedType = schema["type"]?.GetValue<string>();
        if (expectedType is not null && !MatchesType(expectedType, instance))
        {
            violations.Add($"{path}: expected type '{expectedType}' but found '{DescribeType(instance)}'");
            // Type mismatch makes deeper checks meaningless.
            return;
        }

        if (instance is JsonObject obj)
        {
            if (schema["properties"] is JsonObject properties)
            {
                if (schema["required"] is JsonArray required)
                {
                    foreach (JsonNode? nameNode in required)
                    {
                        string? name = nameNode?.GetValue<string>();
                        if (name is not null && !obj.ContainsKey(name))
                            violations.Add($"{path}: required member '{name}' is missing");
                    }
                }

                foreach ((string name, JsonNode? memberSchema) in properties)
                {
                    if (!obj.ContainsKey(name) || memberSchema is not JsonObject memberObject)
                        continue;

                    ValidateNode(memberObject, obj[name], $"{path}.{name}", violations);
                }
            }
        }

        if (instance is JsonArray array && schema["items"] is JsonObject itemSchema)
        {
            for (int i = 0; i < array.Count; i++)
                ValidateNode(itemSchema, array[i], $"{path}[{i}]", violations);
        }
    }

    /// <summary>Resolves a local <c>#/components/schemas/Name</c> pointer.</summary>
    private bool TryResolveReference(string reference, out JsonObject schema)
    {
        const string prefix = "#/components/schemas/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            schema = null!;
            return false;
        }

        string name = reference[prefix.Length..];
        JsonObject? resolved = Schema(name);
        if (resolved is null)
        {
            schema = null!;
            return false;
        }

        schema = resolved;
        return true;
    }

    private static bool IsNullable(JsonObject schema)
        => schema["nullable"] is JsonValue nullable && nullable.TryGetValue(out bool value) && value;

    /// <summary>
    /// JSON has one number type, so both integer and number schemas accept any numeric value.
    /// </summary>
    /// <remarks>
    /// Never use the generic <c>TryGetValue&lt;T&gt;</c> for this: a <see cref="JsonValue"/> built from a
    /// CLR value only matches <em>its own</em> type, and probing for an unrelated numeric type throws
    /// rather than returning false. The kind check avoids both problems.
    /// </remarks>
    private static bool MatchesType(string expectedType, JsonNode instance) => expectedType switch
    {
        "object" => instance is JsonObject,
        "array" => instance is JsonArray,
        "string" => IsString(instance),
        "integer" or "number" => IsNumber(instance),
        "boolean" => instance.GetValueKind() == JsonValueKind.True
            || instance.GetValueKind() == JsonValueKind.False,
        _ => true,
    };

    private static bool IsString(JsonNode instance) => instance.GetValueKind() == JsonValueKind.String;

    private static bool IsNumber(JsonNode instance) => instance.GetValueKind() == JsonValueKind.Number;

    private static string DescribeType(JsonNode instance) => instance.GetValueKind() switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown",
    };
}