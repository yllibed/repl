using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl;
using Repl.Documentation;
using Repl.Interaction;

namespace Repl.Mcp;

/// <summary>
/// Dispatches MCP tool calls through the Repl pipeline.
/// Native JSON arguments execute through standard Repl routing, binding, and middleware.
/// </summary>
internal sealed partial class McpToolAdapter
{
	private const int ProgrammaticInvocationContractVersion = 2;
	internal const string ForcedOutputFormat = "json";
	private const string TextPlainMimeType = "text/plain";

	// A _meta key under a prefix this project owns: MCP reserves the modelcontextprotocol and mcp labels.
	private const string OutcomeMetaKey = "io.github.yllibed.repl/outcome";
	private const string CompletedWithFailuresOutcome = "completedWithFailures";

	private readonly ICoreReplApp _app;
	private readonly ReplMcpServerOptions _options;
	private readonly IServiceProvider _services;
	private readonly McpRequestServerAccessor _requestServers;
	private readonly McpLoggerDiagnostics _diagnostics;
	// Whether the catalog this adapter serves was built from the frozen discovery view. A property of
	// the catalog, not of the request: a reusable BuildMcpServerOptions() result is frozen once and then
	// serves clients of either era, so asking the request would leave an initialize-era caller unable to
	// run what that catalog offered it.
	private readonly bool _catalogIsFrozen;
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReplDocCommand> _toolRoutes = new(StringComparer.OrdinalIgnoreCase);
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _staticToolResults = new(StringComparer.OrdinalIgnoreCase);

	public McpToolAdapter(
		ICoreReplApp app,
		ReplMcpServerOptions options,
		IServiceProvider services,
		McpRequestServerAccessor requestServers,
		bool catalogIsFrozen = false)
	{
		_app = app;
		_options = options;
		_services = services;
		_requestServers = requestServers;
		_catalogIsFrozen = catalogIsFrozen;
		_diagnostics = new McpLoggerDiagnostics(services);
	}

	/// <summary>
	/// Binds the flowing async context to <paramref name="request"/> before dispatching a command.
	/// </summary>
	/// <remarks>
	/// The pre-built primitives (<see cref="ReplMcpServerTool"/> and friends) are dispatched straight
	/// by the SDK on the <c>BuildMcpServerOptions</c> path, bypassing <see cref="McpServerHandler"/>'s
	/// request handlers entirely. Without this, capability services resolved from DI would have no
	/// request to resolve against and would report every client capability as unavailable.
	/// </remarks>
	internal void BindRequest(MessageContext request) => _requestServers.BindRequest(request);

	internal string ForcedOutputMimeType
	{
		get
		{
			if (_app is not CoreReplApp coreApp)
			{
				throw new InvalidOperationException("MCP tool adapter requires a CoreReplApp to resolve output metadata.");
			}

			if (!coreApp.OptionsSnapshot.Output.Transformers.TryGetValue(ForcedOutputFormat, out var transformer))
			{
				throw new InvalidOperationException("MCP server requires the 'json' output transformer.");
			}

			return transformer.MimeType;
		}
	}

	/// <summary>
	/// Clears all registered routes. Called before rebuilding on routing invalidation.
	/// </summary>
	public void ClearRoutes()
	{
		_toolRoutes.Clear();
		_staticToolResults.Clear();
	}

	/// <summary>
	/// Atomically replaces all routes from another adapter instance.
	/// Used for optimistic concurrency during routing invalidation.
	/// </summary>
	public void ReplaceRoutes(McpToolAdapter source)
	{
		_toolRoutes.Clear();
		_staticToolResults.Clear();
		foreach (var (key, value) in source._toolRoutes)
		{
			_toolRoutes[key] = value;
		}

		foreach (var (key, value) in source._staticToolResults)
		{
			_staticToolResults[key] = value;
		}
	}

	/// <summary>
	/// Registers a tool name → command mapping for dispatch.
	/// </summary>
	public void RegisterRoute(string toolName, ReplDocCommand command)
	{
		_toolRoutes[toolName] = command;
	}

	/// <summary>
	/// Registers a static text result for launcher-style tools.
	/// </summary>
	public void RegisterStaticResult(string toolName, string text)
	{
		_staticToolResults[toolName] = text;
	}

	/// <summary>
	/// Invokes a Repl command through the pipeline for an MCP tool call.
	/// </summary>
	public async Task<CallToolResult> InvokeAsync(
		string toolName,
		IDictionary<string, JsonElement> arguments,
		McpServer? server,
		ProgressToken? progressToken,
		CancellationToken ct,
		bool allowStaticResults = true)
	{
		if (allowStaticResults && _staticToolResults.TryGetValue(toolName, out var staticResult))
		{
			return new CallToolResult
			{
				Content = [new TextContentBlock { Text = staticResult }],
			};
		}

		if (!_toolRoutes.TryGetValue(toolName, out var command))
		{
			return ErrorResult($"Unknown tool: {toolName}");
		}

		var (request, transport, prefills) = PrepareStructuredExecution(command, arguments);
		var invocation = await ExecuteThroughPipelineAsync(request, transport, prefills, server, progressToken, ct)
			.ConfigureAwait(false);
		var output = invocation.Completed ? invocation.Output : DescribeFailure(invocation);
		if (string.IsNullOrWhiteSpace(output))
		{
			// Only a completed run reaches here: DescribeFailure names the exit code when it has nothing else.
			// A client may hide _meta from the model, so the text alone must not read as a success.
			output = invocation.Kind == ReplExecutionOutcomeKind.CompletedWithFailures
				? "Completed with failures."
				: "OK";
		}

		return BuildToolResult(output, invocation, _options.PagedResultTextMode);
	}

	internal async Task<McpResourceReadInvocation> InvokeResourceAsync(
		string resourceName,
		IDictionary<string, JsonElement> arguments,
		McpServer? server,
		ProgressToken? progressToken,
		CancellationToken ct)
	{
		if (!_toolRoutes.TryGetValue(resourceName, out var command))
		{
			return new McpResourceReadInvocation($"Unknown resource: {resourceName}", TextPlainMimeType, IsError: true);
		}

		var (request, transport, prefills) = PrepareStructuredExecution(command, arguments);
		var invocation = await ExecuteThroughPipelineAsync(
			request,
			transport,
			prefills,
			server,
			progressToken,
			ct,
			captureCommandOutput: false)
			.ConfigureAwait(false);

		if (!invocation.Completed)
		{
			// A failed read has no body to carry what the command reported — the success path's body has
			// to match the advertised MIME type — so the surfaced error is the only place left for it.
			return new McpResourceReadInvocation(
				AppendMessages(DescribeFailure(invocation), invocation.UndeliveredMessages),
				TextPlainMimeType,
				IsError: true);
		}

		if (string.IsNullOrWhiteSpace(invocation.Output))
		{
			// Results.Exit(0) without a payload intentionally renders no CLI output.
			// Resource reads still need a body that matches the advertised forced JSON MIME type.
			return new McpResourceReadInvocation("null", ForcedOutputMimeType, IsError: false);
		}

		return new McpResourceReadInvocation(invocation.Output, ForcedOutputMimeType, IsError: false);
	}

	private async Task<McpPipelineInvocation> ExecuteThroughPipelineAsync(
		ReplInvocationRequest request,
		List<string> transport,
		Dictionary<string, string> prefills,
		McpServer? server,
		ProgressToken? progressToken,
		CancellationToken ct,
		bool captureCommandOutput = true)
	{
		var invocableApp = _app as ISubInvocableReplApp
			?? throw new InvalidOperationException("MCP tool adapter requires an app that supports sub-invocation.");

		await McpClientRootsService.PrimeFromServicesAsync(_services, ct).ConfigureAwait(false);

		var capture = CommandOutputCapture.Create(captureCommandOutput);
		var feedback = _services.GetService(typeof(IMcpFeedback)) as IMcpFeedback;
		var interactionChannel = new McpInteractionChannel(
			prefills, _options.InteractivityMode, server, progressToken, feedback);
		// Over the SESSION's services, which carry its DI scope: discovery resolved the command graph
		// from the same provider, so what was advertised resolves the same instances when it runs.
		var mcpServices = new McpServiceProviderOverlay(
			_services,
			new Dictionary<Type, object> { [typeof(IReplInteractionChannel)] = interactionChannel });
		var feedbackService = feedback as McpFeedbackService;
		using var feedbackScope = feedbackService?.PushProgressToken(progressToken);
		// Messages the client cannot receive as notifications ride back in the tool result instead,
		// so no feedback is lost on a request that never asked for log notifications.
		using var undeliveredScope = feedbackService?.PushUndeliveredMessages();

		// Force JSON output — agents consume structured data, not human tables/banners.
		transport.Add($"--output:{ForcedOutputFormat}");

		var completed = await RunSubInvocationAsync(invocableApp, request, transport, mcpServices, capture, ct)
			.ConfigureAwait(false);

		var output = capture.Output.ToString().Trim();
		var error = capture.CaptureCommandOutput ? string.Empty : capture.Error.ToString().Trim();
		var undelivered = undeliveredScope?.Messages.Drain() ?? [];
		return new McpPipelineInvocation(
			output, error, completed.ExitCode, completed.Kind, completed.Failure, undelivered);
	}

	/// <summary>Runs the command with the captured I/O and throwaway session registration of one call.</summary>
	private async Task<SubInvocationOutcome> RunSubInvocationAsync(
		ISubInvocableReplApp invocableApp,
		ReplInvocationRequest request,
		List<string> transport,
		IServiceProvider mcpServices,
		CommandOutputCapture capture,
		CancellationToken ct)
	{
		var inputReader = new StringReader(string.Empty);
		// Command-backed resources expose the rendered return value as the resource body.
		// Low-level handler writes to IReplIoContext.Output/Error are side-channel output, not resource content.
		var commandOutput = capture.CaptureCommandOutput ? capture.Output : TextWriter.Null;
		using (ReplSessionIO.SetSession(
			output: capture.Output,
			input: inputReader,
			ansiMode: Rendering.AnsiMode.Never,
			sessionId: $"mcp-{Guid.NewGuid():N}",
			commandOutput: commandOutput,
			error: capture.Error,
			isHostedSession: true,
			// Minted for this call alone; inference would read it as a lifetime we own and never release.
			removeSessionOnDispose: true))
		{
			ReplSessionIO.IsProgrammatic = true;
			using var invocationContract = ReplSessionIO.PushProgrammaticInvocationContract(ProgrammaticInvocationContractVersion);
			// Presence is decided from the same answers that built this catalog, so what was advertised
			// is what can be called. Binding keeps the live services, so the command that runs still sees
			// the real client and can report what it is missing. A catalog resolved per session has
			// nothing to reconcile: it was built from the live view and may vary with it.
			var presenceServices = _catalogIsFrozen
				? new McpServiceProviderOverlay(
					mcpServices,
					McpDiscoveryCapabilities.CreateSessionScopedOverrides(_options.InteractivityMode))
				: null;
			return await invocableApp.RunSubInvocationWithOutcomeAsync(
				request, transport.ToArray(), mcpServices, presenceServices, ct).ConfigureAwait(false);
		}
	}

	internal readonly record struct McpResourceReadInvocation(string Text, string MimeType, bool IsError);

	private readonly record struct McpPipelineInvocation(
		string Output,
		string Error,
		int ExitCode,
		ReplExecutionOutcomeKind Kind,
		Exception? Failure,
		IReadOnlyList<string> UndeliveredMessages)
	{
		/// <summary>
		/// Whether the command ran to completion, so its output is the result rather than a failure to
		/// describe. A run that completed with failures exits non-zero yet produced its own report.
		/// </summary>
		public bool Completed => ExitCode == 0 || Kind == ReplExecutionOutcomeKind.CompletedWithFailures;
	}

	/// <summary>
	/// The two writers one pipeline run captures, and whether they are the same one. Bundled instead of
	/// three positional parameters — two of them adjacent <see cref="StringWriter"/>s — because a
	/// transposed pair compiles silently and a bare bool flag is unreadable at a call site.
	/// </summary>
	private readonly record struct CommandOutputCapture(StringWriter Output, StringWriter Error, bool CaptureCommandOutput)
	{
		public static CommandOutputCapture Create(bool captureCommandOutput)
		{
			var output = new StringWriter();
			return new CommandOutputCapture(output, captureCommandOutput ? output : new StringWriter(), captureCommandOutput);
		}
	}

	/// <summary>
	/// What a failed run is allowed to tell the client.
	/// </summary>
	/// <remarks>
	/// The line is not the kind of failure but who the text was written for. A handler that returns a
	/// failure result, and the framework's own refusals — unknown command, a binding diagnostic naming
	/// what could not be converted — were all authored for whoever called, so they travel unchanged; an
	/// agent uses them to correct itself. What the framework rendered from an exception that escaped
	/// code nobody meant to surface does not: such messages routinely carry a filesystem path, a
	/// parameter and its full CLR type, or a connection string. On a console the reader is the
	/// operator; over MCP it is a remote client, which is the whole difference.
	/// <para>
	/// Two exceptions are themselves addressed to the client and keep their message: an
	/// <see cref="McpException"/>, which the SDK passes through verbatim precisely because raising one
	/// is deliberate, and an <see cref="McpInteractionException"/>, which exists to tell the caller
	/// which answer it failed to supply. Withholding those would leave a documented interaction mode
	/// unable to say what it needs.
	/// </para>
	/// </remarks>
	private string DescribeFailure(in McpPipelineInvocation invocation)
	{
		var withheld = $"Command failed with exit code {invocation.ExitCode}.";
		if (WithholdsFailureText(invocation))
		{
			// Withheld from the client, not discarded: the rendered detail and the exception behind it
			// are exactly what the operator needs, and the log is the one reader that should get them.
			var detail = string.IsNullOrWhiteSpace(invocation.Output) ? invocation.Error : invocation.Output;
			_diagnostics.ToolFailureWithheld(
				invocation.Failure,
				invocation.ExitCode,
				string.IsNullOrWhiteSpace(detail) ? "(nothing was rendered)" : detail);
			return withheld;
		}

		if (!string.IsNullOrWhiteSpace(invocation.Output))
		{
			return invocation.Output;
		}

		return string.IsNullOrWhiteSpace(invocation.Error) ? withheld : invocation.Error;
	}

	private static bool WithholdsFailureText(in McpPipelineInvocation invocation)
	{
		// Marked at the binder because nothing downstream can tell application code failing from a
		// diagnostic the binder wrote itself — same outcome kind, commonly the same exception type.
		if (invocation.Failure is ReplBindingCallbackException)
		{
			return true;
		}

		return invocation.Kind is ReplExecutionOutcomeKind.HandlerException
			&& invocation.Failure is not (McpException or McpInteractionException);
	}

	private static CallToolResult BuildToolResult(
		string output,
		in McpPipelineInvocation invocation,
		McpPagedResultTextMode pagedTextMode)
	{
		// A run that completed with failures is not a tool error: the agent reads the report, and the
		// marker tells it the report contains failures without parsing the payload.
		var meta = invocation.Kind == ReplExecutionOutcomeKind.CompletedWithFailures
			? new JsonObject { [OutcomeMetaKey] = CompletedWithFailuresOutcome }
			: null;
		if (invocation.Completed && TryCreatePagedStructuredResult(output, out var structuredContent, out var summary))
		{
			return new CallToolResult
			{
				Content = WithMessages(
					new TextContentBlock { Text = BuildPagedTextContent(output, summary, pagedTextMode) },
					invocation.UndeliveredMessages),
				StructuredContent = structuredContent,
				IsError = false,
				Meta = meta,
			};
		}

		return new CallToolResult
		{
			Content = WithMessages(new TextContentBlock { Text = output }, invocation.UndeliveredMessages),
			IsError = !invocation.Completed,
			Meta = meta,
		};
	}

	/// <summary>
	/// Flattens a failed invocation's text blocks into one message: the payload, unwrapped, followed by
	/// any feedback the client could not receive as a notification.
	/// </summary>
	/// <remarks>
	/// The inverse of <see cref="WithMessages"/>, for the two callers that surface a failure as an
	/// exception rather than a result. A failure is where those messages matter most — they are usually
	/// what explains it — and there is no content array left to put them in.
	/// </remarks>
	internal static string BuildErrorMessage(IReadOnlyList<TextContentBlock> blocks, string fallback)
	{
		if (blocks.Count == 0)
		{
			return fallback;
		}

		var primary = McpJsonStringOutput.UnwrapJsonStringLiteral(blocks[0].Text);
		if (blocks.Count == 1)
		{
			return primary;
		}

		var message = new System.Text.StringBuilder(primary);
		for (var i = 1; i < blocks.Count; i++)
		{
			message.Append(Environment.NewLine).Append(blocks[i].Text);
		}

		return message.ToString();
	}

	/// <summary>
	/// Appends <paramref name="messages"/> after <paramref name="primary"/>, one per line, returning
	/// <paramref name="primary"/> unchanged when there is nothing to append.
	/// </summary>
	internal static string AppendMessages(string primary, IReadOnlyList<string> messages)
	{
		if (messages.Count == 0)
		{
			return primary;
		}

		var builder = new System.Text.StringBuilder(primary);
		foreach (var message in messages)
		{
			builder.Append(Environment.NewLine).Append(message);
		}

		return builder.ToString();
	}

	/// <summary>
	/// Appends messages the client could not receive as notifications, as a trailing content block.
	/// </summary>
	/// <remarks>
	/// The command's own payload stays the first block (and <c>StructuredContent</c> is untouched), so
	/// a caller reading the primary result is unaffected. Only requests that asked for no log level
	/// carry anything here — a client receiving message notifications would otherwise see each one
	/// twice. Resource reads deliberately get no such block: their body must match the advertised
	/// MIME type.
	/// </remarks>
	private static List<ContentBlock> WithMessages(
		TextContentBlock primary,
		IReadOnlyList<string> undeliveredMessages)
	{
		if (undeliveredMessages.Count == 0)
		{
			return [primary];
		}

		var blocks = new List<ContentBlock>(undeliveredMessages.Count + 1) { primary };
		foreach (var message in undeliveredMessages)
		{
			blocks.Add(new TextContentBlock { Text = message });
		}

		return blocks;
	}

	private static string BuildPagedTextContent(
		string serializedPage,
		string summary,
		McpPagedResultTextMode mode) =>
		mode switch
		{
			McpPagedResultTextMode.SummaryOnly => summary,
			McpPagedResultTextMode.SummaryAndSerializedJson => string.Concat(summary, Environment.NewLine, serializedPage),
			_ => serializedPage,
		};

	private static bool TryCreatePagedStructuredResult(
		string output,
		out JsonElement structuredContent,
		out string summary)
	{
		structuredContent = default;
		summary = string.Empty;
		try
		{
			using var document = JsonDocument.Parse(output);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object
				|| !root.TryGetProperty(ReplPageWireNames.Type, out var type)
				|| type.ValueKind != JsonValueKind.String
				|| !string.Equals(type.GetString(), ReplPageWireNames.PageType, StringComparison.Ordinal)
				|| !root.TryGetProperty(ReplPageWireNames.Items, out var items)
				|| items.ValueKind != JsonValueKind.Array
				|| !root.TryGetProperty(ReplPageWireNames.PageInfo, out var pageInfo)
				|| pageInfo.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			structuredContent = root.Clone();
			summary = BuildPagedSummary(items.GetArrayLength(), pageInfo);
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static string BuildPagedSummary(int count, JsonElement pageInfo)
	{
		var summary = $"Returned {count.ToString(System.Globalization.CultureInfo.InvariantCulture)} item(s).";
		if (pageInfo.TryGetProperty(ReplPageWireNames.TotalCount, out var totalCount)
			&& totalCount.ValueKind == JsonValueKind.Number
			&& totalCount.TryGetInt64(out var total))
		{
			summary += $" Total: {total.ToString(System.Globalization.CultureInfo.InvariantCulture)}.";
		}

		if (pageInfo.TryGetProperty(ReplPageWireNames.NextCursor, out var nextCursor)
			&& nextCursor.ValueKind == JsonValueKind.String
			&& !string.IsNullOrWhiteSpace(nextCursor.GetString()))
		{
			summary += $" Continue with {McpResultFlowArgumentNames.Cursor}; cursor available in structured content.";
		}

		return summary;
	}

	internal static (ReplInvocationRequest Request, List<string> Transport, Dictionary<string, string> Prefills) PrepareStructuredExecution(
		ReplDocCommand command, IDictionary<string, JsonElement> arguments)
	{
		var allowed = BuildAllowedArgumentNames(command);
		var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
		var prefills = new Dictionary<string, string>(StringComparer.Ordinal);
		var transport = new List<string>();
		var supplied = new HashSet<string>(StringComparer.Ordinal);
		foreach (var pair in arguments)
		{
			var field = allowed.Resolve(pair.Key);
			if (!supplied.Add(field.Name)) { throw new InvalidOperationException($"The MCP argument '{pair.Key}' duplicates the schema field '{field.Name}'."); }
			var value = pair.Value;
			switch (field.Kind)
			{
				case McpArgumentFieldKind.Answer:
					prefills.Add(field.Name["answer.".Length..], ReadArgumentText(value));
					break;
				case McpArgumentFieldKind.Cursor:
					var cursor = ReadArgumentText(value);
					ValidateResultCursor(cursor);
					transport.AddRange([ReplResultFlowOptionNames.Cursor, cursor]);
					break;
				case McpArgumentFieldKind.PageSize:
					var pageSize = ReadArgumentText(value);
					ValidateResultPageSize(pageSize);
					transport.AddRange([ReplResultFlowOptionNames.PageSize, pageSize]);
					break;
				default:
					var option = command.Options.FirstOrDefault(option => string.Equals(option.Name, field.Name, StringComparison.Ordinal));
					var parameterName = option is null ? field.Name : DocumentationEngine.ResolveInvocationParameterName(option);
					var collection = option is not null && McpSchemaGenerator.IsCollectionType(option.Type);
					if (collection && value.ValueKind == JsonValueKind.Array)
					{
						ValidateCollectionValues(field.Name, value);
						// Empty MCP arrays historically mean no occurrence, preserving option defaults.
						if (value.GetArrayLength() != 0)
						{
							values.Add(parameterName, value.EnumerateArray().Select(static item => (object?)item.Clone()).ToArray());
						}
					}
					else { values.Add(parameterName, value.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Null ? ReadArgumentText(value) : value.Clone()); }
					break;
			}
		}
		return (ReplInvocationRequest.ForCommand(command.Path, values), transport, prefills);
	}

	private static string ReadArgumentText(JsonElement value) =>
		value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

	private static void ValidateCollectionValues(string name, JsonElement array)
	{
		foreach (var item in array.EnumerateArray())
		{
			if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
			{
				throw new McpException($"The MCP collection argument '{name}' must contain only non-null scalar values.");
			}
		}
	}

	private static void ValidateResultCursor(string cursor)
		=> ResultFlowCursorPolicy.ValidateOrThrow(cursor);

	private static void ValidateResultPageSize(string pageSize)
	{
		if (pageSize.Length > 10)
		{
			throw new InvalidOperationException("The MCP result page size cannot exceed 10 characters.");
		}

		if (pageSize.Length == 0 || pageSize.AsSpan().IndexOfAnyExceptInRange('0', '9') >= 0)
		{
			throw new InvalidOperationException("The MCP result page size must be numeric.");
		}

		if (!int.TryParse(pageSize, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
			|| value <= 0)
		{
			throw new InvalidOperationException("The MCP result page size must fit in a positive 32-bit integer.");
		}
	}

	internal static void ValidateArgumentNames(ReplDocCommand command) =>
		_ = BuildAllowedArgumentNames(command);

	private static AllowedArgumentNames BuildAllowedArgumentNames(ReplDocCommand command)
	{
		var names = new AllowedArgumentNames();
		foreach (var argument in command.Arguments)
		{
			names.Add(argument.Name, McpArgumentFieldKind.Command);
		}

		foreach (var option in command.Options)
		{
			names.Add(option.Name, McpArgumentFieldKind.Command);
		}

		if (command.Answers is { Count: > 0 })
		{
			foreach (var answer in command.Answers)
			{
				names.Add($"answer.{answer.Name}", McpArgumentFieldKind.Answer);
			}
		}

		if (command.AcceptsPagingInput || command.EmitsPagedResult)
		{
			names.Add(McpResultFlowArgumentNames.Cursor, McpArgumentFieldKind.Cursor);
			names.Add(McpResultFlowArgumentNames.PageSize, McpArgumentFieldKind.PageSize);
		}

		return names;
	}

	private enum McpArgumentFieldKind
	{
		Command,
		Answer,
		Cursor,
		PageSize,
	}

	private readonly record struct AllowedArgumentField(string Name, McpArgumentFieldKind Kind);

	private sealed class AllowedArgumentNames
	{
		private readonly Dictionary<string, AllowedArgumentField> _exactFields = new(StringComparer.Ordinal);
		private readonly List<AllowedArgumentField> _orderedFields = [];

		internal void Add(string name, McpArgumentFieldKind kind)
		{
			var field = new AllowedArgumentField(name, kind);
			if (!_exactFields.TryAdd(name, field))
			{
				throw new InvalidOperationException(
					$"MCP argument name collision: '{name}' is declared more than once by the tool schema.");
			}

			_orderedFields.Add(field);
		}

		internal AllowedArgumentField Resolve(string requestedName)
		{
			if (_exactFields.TryGetValue(requestedName, out var exact))
			{
				return exact;
			}

			var matches = _orderedFields
				.Where(field => string.Equals(field.Name, requestedName, StringComparison.OrdinalIgnoreCase))
				.ToArray();
			return matches.Length switch
			{
				0 => throw new InvalidOperationException(
					$"The MCP argument '{requestedName}' is not defined by the tool schema."),
				1 => matches[0],
				_ => throw new InvalidOperationException(
					$"The MCP argument '{requestedName}' is ambiguous between schema fields "
					+ $"{string.Join(", ", matches.Select(static field => $"'{field.Name}'"))}."),
			};
		}
	}

	private static CallToolResult ErrorResult(string message) => new()
	{
		Content = [new TextContentBlock { Text = message }],
		IsError = true,
	};

}
