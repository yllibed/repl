using System.Text.RegularExpressions;

namespace Repl.Mcp;

/// <summary>
/// Converts Repl route templates into flat MCP tool names.
/// Dynamic segments (<c>{name}</c>, <c>{name:constraint}</c>) are removed
/// from the tool name and become required input properties on the JSON Schema.
/// </summary>
internal static partial class McpToolNameFlattener
{
	/// <summary>
	/// Flattens a route template into an MCP tool name.
	/// </summary>
	/// <param name="routePath">Route template (e.g. <c>contact {id:guid} show</c>).</param>
	/// <param name="separator">Separator character between segments.</param>
	/// <returns>Flattened tool name (e.g. <c>contact_show</c>).</returns>
	public static string Flatten(string routePath, char separator)
	{
		var segments = routePath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var parts = new List<string>(segments.Length);

		foreach (var segment in segments)
		{
			if (DynamicSegmentPattern().IsMatch(segment))
			{
				continue;
			}

			parts.Add(segment);
		}

		return string.Join(separator, parts);
	}

	/// <summary>
	/// Builds an MCP resource URI template from a route template,
	/// preserving dynamic segments as RFC 6570 template variables.
	/// </summary>
	/// <param name="routePath">Route template (e.g. <c>contact {id:guid} show</c>).</param>
	/// <param name="scheme">URI scheme (default: <c>repl</c>).</param>
	/// <returns>URI template (e.g. <c>repl://contact/{id}/show</c>).</returns>
	public static string BuildResourceUri(string routePath, string scheme = "repl")
	{
		var segments = routePath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var parts = new List<string>(segments.Length);

		foreach (var segment in segments)
		{
			var match = DynamicSegmentPattern().Match(segment);
			if (match.Success)
			{
				// Strip optional markers and constraints: {name?:constraint} -> {name}
				var name = match.Groups["name"].Value;
				parts.Add($"{{{name}}}");
			}
			else
			{
				parts.Add(segment);
			}
		}

		return $"{scheme}://{string.Join('/', parts)}";
	}

	/// <summary>
	/// Resolves the separator character from the <see cref="ToolNamingSeparator"/> enum.
	/// </summary>
	public static char ResolveSeparator(ToolNamingSeparator separator) => separator switch
	{
		ToolNamingSeparator.Underscore => '_',
		ToolNamingSeparator.Slash => '/',
		ToolNamingSeparator.Dot => '.',
		_ => '_',
	};

	[GeneratedRegex(@"^\{(?<name>[^:{}?]+)(?:\?)?(?::[^{}:]+)?\}$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
	private static partial Regex DynamicSegmentPattern();
}
