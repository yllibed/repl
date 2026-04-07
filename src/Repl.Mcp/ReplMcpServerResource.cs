using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Documentation;

namespace Repl.Mcp;

/// <summary>
/// Custom <see cref="McpServerResource"/> subclass that extracts URI template variables
/// and dispatches reads through the Repl pipeline via <see cref="McpToolAdapter"/>.
/// </summary>
internal sealed partial class ReplMcpServerResource : McpServerResource
{
	private readonly string _resourceName;
	private readonly McpToolAdapter _adapter;
	private readonly ResourceTemplate _protocolResourceTemplate;
	private readonly Regex? _uriParser;
	private readonly string[] _variableNames;

	public ReplMcpServerResource(
		ReplDocResource resource,
		string resourceName,
		string uriTemplate,
		McpToolAdapter adapter)
	{
		_resourceName = resourceName;
		_adapter = adapter;
		_protocolResourceTemplate = new ResourceTemplate
		{
			Name = resourceName,
			Description = resource.Description,
			UriTemplate = uriTemplate,
			MimeType = "text/plain",
		};

		// Build a regex to extract template variables from the URI.
		// Each {varName} becomes (?<varName>[^/]+), literals are Regex.Escape'd.
		_variableNames = BuildUriParser(uriTemplate, out _uriParser);
	}

	/// <inheritdoc />
	public override ResourceTemplate ProtocolResourceTemplate => _protocolResourceTemplate;

	/// <inheritdoc />
	public override IReadOnlyList<object> Metadata { get; } = [];

	/// <inheritdoc />
	public override bool IsMatch(string uri)
	{
		ArgumentNullException.ThrowIfNull(uri);

		if (_uriParser is not null)
		{
			return _uriParser.IsMatch(uri);
		}

		return string.Equals(uri, _protocolResourceTemplate.UriTemplate, StringComparison.OrdinalIgnoreCase);
	}

	/// <inheritdoc />
	public override async ValueTask<ReadResourceResult> ReadAsync(
		RequestContext<ReadResourceRequestParams> request,
		CancellationToken cancellationToken = default)
	{
		var arguments = ExtractArguments(request.Params.Uri);

		var result = await _adapter.InvokeAsync(
			_resourceName,
			arguments,
			request.Server,
			progressToken: null,
			cancellationToken,
			allowStaticResults: false)
			.ConfigureAwait(false);

		if (result.IsError == true)
		{
			var errorText = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text
				?? "Resource read failed.";
			throw new McpException(errorText);
		}

		var text = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
		return new ReadResourceResult
		{
			Contents =
			[
				new TextResourceContents
				{
					Uri = request.Params.Uri,
					MimeType = "text/plain",
					Text = text,
				},
			],
		};
	}

	private Dictionary<string, JsonElement> ExtractArguments(string uri)
	{
		var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

		if (_uriParser is null)
		{
			return arguments;
		}

		var match = _uriParser.Match(uri);
		if (!match.Success)
		{
			return arguments;
		}

		foreach (var name in _variableNames)
		{
			if (match.Groups[name] is { Success: true } group)
			{
				var value = Uri.UnescapeDataString(group.Value);
				arguments[name] = JsonSerializer.SerializeToElement(value, McpJsonContext.Default.String);
			}
		}

		return arguments;
	}

	private static string[] BuildUriParser(string uriTemplate, out Regex? parser)
	{
		var variableNames = new List<string>();
		var regexParts = new System.Text.StringBuilder("^");

		var remaining = uriTemplate.AsSpan();
		while (remaining.Length > 0)
		{
			var braceIndex = remaining.IndexOf('{');
			if (braceIndex < 0)
			{
				regexParts.Append(Regex.Escape(remaining.ToString()));
				break;
			}

			// Literal before the brace.
			if (braceIndex > 0)
			{
				regexParts.Append(Regex.Escape(remaining[..braceIndex].ToString()));
			}

			var closeIndex = remaining.IndexOf('}');
			var name = remaining[(braceIndex + 1)..closeIndex].ToString();
			variableNames.Add(name);
			regexParts.Append($"(?<{name}>[^/]+)");
			remaining = remaining[(closeIndex + 1)..];
		}

		regexParts.Append('$');

		if (variableNames.Count == 0)
		{
			parser = null;
			return [];
		}

		parser = new Regex(
			regexParts.ToString(),
			RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
			TimeSpan.FromSeconds(1));
		return [.. variableNames];
	}
}
