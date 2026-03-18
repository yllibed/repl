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
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReplDocCommand> _toolRoutes = new(StringComparer.OrdinalIgnoreCase);

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
		McpServer? server,
		ProgressToken? progressToken,
		CancellationToken ct)
	{
		if (!_toolRoutes.TryGetValue(toolName, out var command))
		{
			return ErrorResult($"Unknown tool: {toolName}");
		}

		var (tokens, prefills) = PrepareExecution(command.Path, arguments);
		return await ExecuteThroughPipelineAsync(tokens, prefills, server, progressToken, ct)
			.ConfigureAwait(false);
	}

	private async Task<CallToolResult> ExecuteThroughPipelineAsync(
		List<string> tokens,
		Dictionary<string, string> prefills,
		McpServer? server,
		ProgressToken? progressToken,
		CancellationToken ct)
	{
		var coreApp = _app as CoreReplApp
			?? throw new InvalidOperationException("MCP tool adapter requires CoreReplApp.");

		var outputWriter = new StringWriter();
		var inputReader = new StringReader(string.Empty);
		var interactionChannel = new McpInteractionChannel(
			prefills, _options.InteractivityMode, server, progressToken);
		var mcpServices = new McpServiceProviderOverlay(_services, interactionChannel);

		using (ReplSessionIO.SetSession(
			output: outputWriter,
			input: inputReader,
			ansiMode: Rendering.AnsiMode.Never,
			sessionId: $"mcp-{Guid.NewGuid():N}",
			isHostedSession: true))
		{
			ReplSessionIO.IsProgrammatic = true;
			var exitCode = await coreApp.RunWithServicesAsync(
				tokens.ToArray(), mcpServices, ct).ConfigureAwait(false);

			var output = outputWriter.ToString().Trim();
			if (string.IsNullOrWhiteSpace(output))
			{
				output = exitCode == 0 ? "OK" : $"Command failed with exit code {exitCode}.";
			}

			return new CallToolResult
			{
				Content = [new TextContentBlock { Text = output }],
				IsError = exitCode != 0,
			};
		}
	}

	private static (List<string> Tokens, Dictionary<string, string> Prefills) PrepareExecution(
		string routePath,
		IDictionary<string, JsonElement> arguments)
	{
		var stringArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
		var prefills = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var (key, value) in arguments)
		{
			var strValue = value.ValueKind == JsonValueKind.String
				? value.GetString() ?? ""
				: value.GetRawText();

			if (key.StartsWith("answer:", StringComparison.OrdinalIgnoreCase))
			{
				prefills[key["answer:".Length..]] = strValue;
			}
			else
			{
				stringArgs[key] = strValue;
			}
		}

		return (ReconstructTokens(routePath, stringArgs), prefills);
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

		foreach (var part in routePath.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			var match = DynamicSegmentPattern().Match(part);
			if (match.Success)
			{
				var argName = match.Groups["name"].Value;
				tokens.Add(arguments.TryGetValue(argName, out var value) ? value?.ToString() ?? "" : "");
				consumedArgs.Add(argName);
			}
			else
			{
				tokens.Add(part);
			}
		}

		// Remaining arguments become named options.
		// Note: answer: prefixes are separated by PrepareExecution upstream
		// and never reach this method.
		foreach (var (key, value) in arguments)
		{
			if (!consumedArgs.Contains(key))
			{
				tokens.Add($"--{key}");
				tokens.Add(value?.ToString() ?? "");
			}
		}

		return tokens;
	}

	private static CallToolResult ErrorResult(string message) => new()
	{
		Content = [new TextContentBlock { Text = message }],
		IsError = true,
	};

	[GeneratedRegex(@"^\{(?<name>\w+)(?::\w+)?\}$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
	private static partial Regex DynamicSegmentPattern();
}
