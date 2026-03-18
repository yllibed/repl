using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Interaction;

namespace Repl.Mcp;

/// <summary>
/// Implements <see cref="IReplInteractionChannel"/> for MCP mode with progressive degradation.
/// Tier 1: Prefill from tool arguments. Tier 2: Elicitation. Tier 3: Sampling. Tier 4: Default/Fail.
/// </summary>
internal sealed class McpInteractionChannel : IReplInteractionChannel
{
	private readonly IReadOnlyDictionary<string, string> _prefillAnswers;
	private readonly InteractivityMode _mode;
	private readonly McpServer? _server;
	private readonly ProgressToken? _progressToken;

	public McpInteractionChannel(
		IReadOnlyDictionary<string, string> prefillAnswers,
		InteractivityMode mode,
		McpServer? server = null,
		ProgressToken? progressToken = null)
	{
		_prefillAnswers = prefillAnswers;
		_mode = mode;
		_server = server;
		_progressToken = progressToken;
	}

	public async ValueTask<int> AskChoiceAsync(
		string name,
		string prompt,
		IReadOnlyList<string> choices,
		int? defaultIndex = null,
		AskOptions? options = null)
	{
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return ResolveChoiceIndex(prefill, choices);
		}

		if (await TryElicitChoiceAsync(name, prompt, choices).ConfigureAwait(false) is { } elicited)
		{
			return ResolveChoiceIndex(elicited, choices);
		}

		if (await TrySampleAsync($"{prompt}\nChoose one: {string.Join(", ", choices)}")
			.ConfigureAwait(false) is { } sampled)
		{
			return ResolveChoiceIndex(sampled, choices);
		}

		if (defaultIndex.HasValue)
		{
			return defaultIndex.Value;
		}

		if (_mode == InteractivityMode.PrefillThenDefaults)
		{
			return 0;
		}

		throw new McpInteractionException(
			$"Interactive prompt '{name}' requires a value. " +
			$"Provide it as a tool argument 'answer:{name}'. Choices: {string.Join(", ", choices)}");
	}

	public async ValueTask<bool> AskConfirmationAsync(
		string name,
		string prompt,
		bool defaultValue = false,
		AskOptions? options = null)
	{
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return ParseBool(prefill);
		}

		if (await TryElicitBoolAsync(name, prompt, defaultValue).ConfigureAwait(false) is { } elicited)
		{
			return elicited;
		}

		if (await TrySampleAsync($"{prompt} (yes/no)").ConfigureAwait(false) is { } sampled)
		{
			return ParseBool(sampled);
		}

		return defaultValue;
	}

	public async ValueTask<string> AskTextAsync(
		string name,
		string prompt,
		string? defaultValue = null,
		AskOptions? options = null)
	{
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return prefill;
		}

		if (await TryElicitTextAsync(name, prompt, defaultValue).ConfigureAwait(false) is { } elicited)
		{
			return elicited;
		}

		if (await TrySampleAsync(prompt).ConfigureAwait(false) is { } sampled)
		{
			return sampled;
		}

		if (defaultValue is not null)
		{
			return defaultValue;
		}

		if (_mode == InteractivityMode.PrefillThenDefaults)
		{
			return string.Empty;
		}

		throw new McpInteractionException(
			$"Interactive prompt '{name}' requires a value. " +
			$"Provide it as a tool argument 'answer:{name}'.");
	}

	public ValueTask<string> AskSecretAsync(
		string name,
		string prompt,
		AskSecretOptions? options = null)
	{
		// Secrets: prefill ONLY, never elicitation or sampling (security).
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return ValueTask.FromResult(prefill);
		}

		throw new McpInteractionException(
			$"Secret prompt '{name}' requires a prefilled value. " +
			$"Provide it as a tool argument 'answer:{name}'.");
	}

	public async ValueTask<IReadOnlyList<int>> AskMultiChoiceAsync(
		string name,
		string prompt,
		IReadOnlyList<string> choices,
		IReadOnlyList<int>? defaultIndices = null,
		AskMultiChoiceOptions? options = null)
	{
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return ParseMultiChoice(prefill, choices);
		}

		if (await TrySampleAsync($"{prompt}\nSelect from: {string.Join(", ", choices)}")
			.ConfigureAwait(false) is { } sampled)
		{
			return ParseMultiChoice(sampled, choices);
		}

		if (defaultIndices is { Count: > 0 })
		{
			return defaultIndices;
		}

		if (_mode == InteractivityMode.PrefillThenDefaults)
		{
			return [];
		}

		throw new McpInteractionException(
			$"Interactive prompt '{name}' requires a value. " +
			$"Provide it as a tool argument 'answer:{name}' (comma-separated). " +
			$"Choices: {string.Join(", ", choices)}");
	}

	public async ValueTask WriteProgressAsync(string label, double? percent, CancellationToken cancellationToken)
	{
		if (_server is not null && _progressToken is { } token)
		{
			await _server.NotifyProgressAsync(
				token,
				new ProgressNotificationValue
				{
					Progress = (float)(percent ?? 0),
					Total = 100f,
					Message = label,
				},
				cancellationToken: cancellationToken).ConfigureAwait(false);
		}
	}

	public async ValueTask WriteStatusAsync(string text, CancellationToken cancellationToken)
	{
		if (_server is null)
		{
			return;
		}

		await _server.SendNotificationAsync(
			NotificationMethods.LoggingMessageNotification,
			new LoggingMessageNotificationParams
			{
				Level = LoggingLevel.Info,
				Data = JsonSerializer.SerializeToElement(text, McpJsonContext.Default.String),
			},
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	public ValueTask ClearScreenAsync(CancellationToken cancellationToken) =>
		ValueTask.CompletedTask;

	public ValueTask<TResult> DispatchAsync<TResult>(
		InteractionRequest<TResult> request,
		CancellationToken cancellationToken) =>
		throw new NotSupportedException(
			"Custom interaction dispatch is not supported in MCP mode. " +
			"Consider marking the command as AutomationHidden().");

	// ── Elicitation (Tier 2) ───────────────────────────────────────────

	private async Task<string?> TryElicitChoiceAsync(string name, string prompt, IReadOnlyList<string> choices)
	{
		if (!CanElicit)
		{
			return null;
		}

		var result = await _server!.ElicitAsync(new ElicitRequestParams
		{
			Message = prompt,
			RequestedSchema = new ElicitRequestParams.RequestSchema
			{
				Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
				{
					[name] = new ElicitRequestParams.UntitledSingleSelectEnumSchema
					{
						Enum = choices.ToList(),
					},
				},
			},
		}).ConfigureAwait(false);

		return ExtractElicitedString(result, name);
	}

	private async Task<bool?> TryElicitBoolAsync(string name, string prompt, bool defaultValue)
	{
		if (!CanElicit)
		{
			return null;
		}

		var result = await _server!.ElicitAsync(new ElicitRequestParams
		{
			Message = prompt,
			RequestedSchema = new ElicitRequestParams.RequestSchema
			{
				Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
				{
					[name] = new ElicitRequestParams.BooleanSchema(),
				},
			},
		}).ConfigureAwait(false);

		if (result.IsAccepted && result.Content?.TryGetValue(name, out var value) is true)
		{
			return value.ValueKind == JsonValueKind.True;
		}

		return null;
	}

	private async Task<string?> TryElicitTextAsync(string name, string prompt, string? defaultValue)
	{
		if (!CanElicit)
		{
			return null;
		}

		var result = await _server!.ElicitAsync(new ElicitRequestParams
		{
			Message = prompt,
			RequestedSchema = new ElicitRequestParams.RequestSchema
			{
				Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
				{
					[name] = new ElicitRequestParams.StringSchema(),
				},
			},
		}).ConfigureAwait(false);

		return ExtractElicitedString(result, name);
	}

	private bool CanElicit =>
		_mode is InteractivityMode.PrefillThenElicitation
		&& _server?.ClientCapabilities?.Elicitation is not null;

	private static string? ExtractElicitedString(ElicitResult result, string name)
	{
		if (!result.IsAccepted || result.Content?.TryGetValue(name, out var value) is not true)
		{
			return null;
		}

		return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
	}

	// ── Sampling (Tier 3) ──────────────────────────────────────────────

	private async Task<string?> TrySampleAsync(string prompt)
	{
		if (!CanSample)
		{
			return null;
		}

		var result = await _server!.SampleAsync(new CreateMessageRequestParams
		{
			Messages =
			[
				new SamplingMessage
				{
					Role = Role.User,
					Content = [new TextContentBlock { Text = prompt }],
				},
			],
			MaxTokens = 100,
		}).ConfigureAwait(false);

		return result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
	}

	private bool CanSample =>
		_mode is InteractivityMode.PrefillThenElicitation or InteractivityMode.PrefillThenSampling
		&& _server?.ClientCapabilities?.Sampling is not null;

	// ── Helpers ─────────────────────────────────────────────────────────

	private static int ResolveChoiceIndex(string value, IReadOnlyList<string> choices)
	{
		for (var i = 0; i < choices.Count; i++)
		{
			if (string.Equals(choices[i], value, StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}

		if (int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var index)
			&& index >= 0 && index < choices.Count)
		{
			return index;
		}

		throw new McpInteractionException(
			$"Cannot resolve choice '{value}'. Available: {string.Join(", ", choices)}");
	}

	private static bool ParseBool(string value) =>
		string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(value, "y", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(value, "1", StringComparison.Ordinal);

	private static int[] ParseMultiChoice(string value, IReadOnlyList<string> choices) =>
		value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(token => ResolveChoiceIndex(token, choices))
			.ToArray();
}
