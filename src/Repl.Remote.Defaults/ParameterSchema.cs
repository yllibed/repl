using System.Text.Json;
using System.Text.Json.Nodes;

namespace Repl.Remote;

internal static class ParameterSchema
{
	internal static JsonElement Create(Type type)
	{
		var schema = Build(type);
		schema["$schema"] = "http://json-schema.org/draft-07/schema#";
		return JsonSerializer.SerializeToElement(schema);
	}

	private static JsonObject Build(Type type)
	{
		if (Nullable.GetUnderlyingType(type) is { } underlying)
		{
			return new() { ["anyOf"] = new JsonArray(Build(underlying), new JsonObject { ["type"] = "null" }) };
		}
		if (type.IsEnum) { return new() { ["type"] = "string", ["enum"] = JsonSerializer.SerializeToNode(Enum.GetNames(type)) }; }
		if (type.IsArray && type.GetArrayRank() == 1 && type.GetElementType() is { } arrayElementType)
		{
			return new() { ["type"] = "array", ["items"] = Build(arrayElementType) };
		}
		if (!type.IsArray && HandlerArgumentBinder.TryGetCollectionElementType(type, out var elementType))
		{
			return new() { ["type"] = "array", ["items"] = Build(elementType) };
		}
		return Build(type.Name);
	}

	internal static JsonElement Create(string type)
	{
		var schema = Build(type);
		schema["$schema"] = "http://json-schema.org/draft-07/schema#";

		return JsonSerializer.SerializeToElement(schema);
	}

	private static JsonObject Build(string type)
	{
		if (type.EndsWith('?')) { return new() { ["anyOf"] = new JsonArray(Build(type[..^1]), new JsonObject { ["type"] = "null" }) }; }
		if (type.EndsWith("[]", StringComparison.Ordinal)) { return new() { ["type"] = "array", ["items"] = Build(type[..^2]) }; }
		if (type.EndsWith('>') && type.IndexOf('<') is > 0 and var start &&
			type[..start] is "List" or "IList" or "IReadOnlyList" or "IEnumerable" or "ICollection" or "IReadOnlyCollection")
		{
			return new() { ["type"] = "array", ["items"] = Build(type[(start + 1)..^1]) };
		}
		var jsonType = type.TrimEnd('?').ToLowerInvariant() switch
		{
			"int" or "integer" or "int16" or "int32" or "int64" or "uint16" or "uint32" or "uint64" or "long" or "short" or "byte" or "sbyte" or "uint" or "ulong" => "integer",
			"bool" or "boolean" => "boolean",
			"double" or "float" or "single" or "decimal" => "number",
			"string" or "char" or "alpha" or "custom" or "guid" or "date" or "dateonly" or "datetime" or "datetimeoffset" or "uri" or "url" or "time" or "timeonly" or "timespan" => "string",
			_ => null,
		};
		return jsonType is null ? new() : new() { ["type"] = jsonType };
	}
}
