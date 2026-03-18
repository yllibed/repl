using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Documentation;

namespace Repl.Mcp;

/// <summary>
/// Dispatches MCP tool calls through the Repl pipeline.
/// Each call creates CLI tokens from the route template + JSON arguments,
/// then executes through standard Repl routing, binding, and middleware.
/// </summary>
internal sealed partial class McpToolAdapter
{
	private readonly ICoreReplApp _app;
	private readonly ReplMcpServerOptions _options;
	private readonly IServiceProvider _services;
	private readonly Dictionary<string, ReplDocCommand> _toolRoutes = new(StringComparer.OrdinalIgnoreCase);

	public McpToolAdapter(ICoreReplApp app, ReplMcpServerOptions options, IServiceProvider services)
	{
		_app = app;
		_options = options;
		_services = services;
	}

	/// <summary>
	/// Registers a tool name → command mapping for dispatch.
	/// </summary>
	public void RegisterRoute(string toolName, ReplDocCommand command)
	{
		_toolRoutes[toolName] = command;
	}

	/// <summary>
	/// Invokes a Repl command through the pipeline for an MCP tool call.
	/// </summary>
	public async Task<CallToolResult> InvokeAsync(
		string toolName,
		IDictionary<string, JsonElement> arguments,
		McpServer server,
		CancellationToken ct)
	{
		if (!_toolRoutes.TryGetValue(toolName, out var command))
		{
			return new CallToolResult
			{
				Content = [new TextContentBlock { Text = $"Unknown tool: {toolName}" }],
				IsError = true,
			};
		}

		var stringArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
		foreach (var (key, value) in arguments)
		{
			stringArgs[key] = value.ValueKind == JsonValueKind.String
				? value.GetString()
				: value.GetRawText();
		}

		var tokens = ReconstructTokens(command.Path, stringArgs);

		// Phase 2: Create a scoped execution context with:
		// - New IServiceProvider scope (one per tool call for isolation)
		// - In-memory IReplIoContext with StringWriter output capture
		// - McpInteractionChannel (prefill → elicitation → sampling → fail)
		// - ReplRuntimeChannel.Programmatic
		// Then execute through the Repl pipeline and capture output.
		_ = ct;

		var output = $"Tool '{toolName}' dispatched with tokens: [{string.Join(", ", tokens)}]";
		return await Task.FromResult(new CallToolResult
		{
			Content = [new TextContentBlock { Text = output }],
			IsError = false,
		}).ConfigureAwait(false);
	}

	/// <summary>
	/// Reconstructs CLI tokens from a route template and MCP arguments.
	/// </summary>
	internal static List<string> ReconstructTokens(
		string routePath,
		IDictionary<string, object?> arguments)
	{
		var tokens = new List<string>();
		var consumedArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var parts = routePath.Split(' ', StringSplitOptions.RemoveEmptyEntries);

		foreach (var part in parts)
		{
			var match = DynamicSegmentPattern().Match(part);
			if (match.Success)
			{
				var argName = match.Groups["name"].Value;
				if (arguments.TryGetValue(argName, out var value))
				{
					tokens.Add(value?.ToString() ?? "");
				}
				else
				{
					tokens.Add("");
				}

				consumedArgs.Add(argName);
			}
			else
			{
				tokens.Add(part);
			}
		}

		// Remaining arguments → named options (--key value)
		foreach (var (key, value) in arguments)
		{
			if (consumedArgs.Contains(key))
			{
				continue;
			}

			if (key.StartsWith("answer:", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			tokens.Add($"--{key}");
			tokens.Add(value?.ToString() ?? "");
		}

		// Interaction prefills → --answer:name=value tokens
		foreach (var (key, value) in arguments)
		{
			if (!key.StartsWith("answer:", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			tokens.Add($"--{key}={value}");
		}

		return tokens;
	}

	[GeneratedRegex(@"^\{(?<name>\w+)(?::\w+)?\}$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
	private static partial Regex DynamicSegmentPattern();
}
