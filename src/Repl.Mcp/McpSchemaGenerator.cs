using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Repl.Documentation;

namespace Repl.Mcp;

/// <summary>
/// Generates JSON Schema from <see cref="ReplDocCommand"/> metadata
/// and maps <see cref="CommandAnnotations"/> to MCP <see cref="ToolAnnotations"/>.
/// </summary>
internal static class McpSchemaGenerator
{
	/// <summary>
	/// Builds a JSON Schema <c>inputSchema</c> from command arguments and options.
	/// </summary>
	public static JsonElement BuildInputSchema(ReplDocCommand command)
	{
		var properties = new JsonObject();
		var required = new JsonArray();

		foreach (var arg in command.Arguments)
		{
			var prop = CreatePropertySchema(arg.Type, arg.Description);
			properties[arg.Name] = prop;
			if (arg.Required)
			{
				required.Add((JsonNode)arg.Name);
			}
		}

		foreach (var opt in command.Options)
		{
			var prop = CreatePropertySchema(opt.Type, opt.Description);

			if (opt.EnumValues is { Count: > 0 })
			{
				var enumArray = new JsonArray();
				foreach (var v in opt.EnumValues)
				{
					enumArray.Add((JsonNode)v);
				}

				prop["enum"] = enumArray;
			}

			if (opt.DefaultValue is not null)
			{
				prop["default"] = opt.DefaultValue;
			}

			properties[opt.Name] = prop;
			if (opt.Required)
			{
				required.Add((JsonNode)opt.Name);
			}
		}

		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = properties,
		};

		if (required.Count > 0)
		{
			schema["required"] = required;
		}

		return JsonSerializer.SerializeToElement(schema, McpJsonContext.Default.JsonObject);
	}

	/// <summary>
	/// Maps <see cref="CommandAnnotations"/> to MCP <see cref="ToolAnnotations"/>.
	/// </summary>
	/// <remarks>
	/// Repl defaults <c>Destructive = false</c> (opt-in), unlike the SDK default of <c>true</c>.
	/// Only flags explicitly set to <c>true</c> are emitted.
	/// </remarks>
	public static ToolAnnotations? MapAnnotations(CommandAnnotations? annotations)
	{
		if (annotations is null)
		{
			return null;
		}

		return new ToolAnnotations
		{
			DestructiveHint = annotations.Destructive ? true : null,
			ReadOnlyHint = annotations.ReadOnly ? true : null,
			IdempotentHint = annotations.Idempotent ? true : null,
			OpenWorldHint = annotations.OpenWorld ? true : null,
		};
	}

	/// <summary>
	/// Builds the MCP tool description from command metadata.
	/// </summary>
	public static string BuildDescription(ReplDocCommand command)
	{
		if (string.IsNullOrWhiteSpace(command.Details))
		{
			return command.Description ?? command.Path;
		}

		return string.IsNullOrWhiteSpace(command.Description)
			? command.Details
			: $"{command.Description}\n\n{command.Details}";
	}

	private static JsonObject CreatePropertySchema(string replType, string? description)
	{
		var (jsonType, format) = MapType(replType);
		var prop = new JsonObject { ["type"] = jsonType };

		if (format is not null)
		{
			prop["format"] = format;
		}

		if (description is not null)
		{
			prop["description"] = description;
		}

		return prop;
	}

	private static (string Type, string? Format) MapType(string replType) => replType.ToLowerInvariant() switch
	{
		"string" or "alpha" or "custom" => ("string", null),
		"int" or "integer" => ("integer", null),
		"long" => ("integer", null),
		"bool" or "boolean" => ("boolean", null),
		"double" or "decimal" => ("number", null),
		"email" => ("string", "email"),
		"guid" => ("string", "uuid"),
		"date" => ("string", "date"),
		"datetime" => ("string", "date-time"),
		"datetimeoffset" => ("string", "date-time"),
		"uri" or "url" => ("string", "uri"),
		"urn" => ("string", "urn"),
		"time" => ("string", "time"),
		"timespan" => ("string", "duration"),
		"date-range" => ("string", null),
		"datetime-range" => ("string", null),
		"datetimeoffset-range" => ("string", null),
		_ => ("string", null),
	};
}
