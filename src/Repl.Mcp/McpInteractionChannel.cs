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

	public ValueTask<int> AskChoiceAsync(
		string name,
		string prompt,
		IReadOnlyList<string> choices,
		int? defaultIndex = null,
		AskOptions? options = null)
	{
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return ValueTask.FromResult(ResolveChoiceIndex(prefill, choices));
		}

		// Tier 2/3 (elicitation/sampling) deferred to future implementation.

		if (defaultIndex.HasValue)
		{
			return ValueTask.FromResult(defaultIndex.Value);
		}

		if (_mode == InteractivityMode.PrefillThenDefaults)
		{
			return ValueTask.FromResult(0);
		}

		throw new McpInteractionException(
			$"Interactive prompt '{name}' requires a value. " +
			$"Provide it as a tool argument 'answer:{name}'. Choices: {string.Join(", ", choices)}");
	}

	public ValueTask<bool> AskConfirmationAsync(
		string name,
		string prompt,
		bool defaultValue = false,
		AskOptions? options = null)
	{
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return ValueTask.FromResult(
				string.Equals(prefill, "true", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(prefill, "yes", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(prefill, "y", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(prefill, "1", StringComparison.Ordinal));
		}

		if (_mode is InteractivityMode.PrefillThenDefaults)
		{
			return ValueTask.FromResult(defaultValue);
		}

		if (defaultValue || _mode is not InteractivityMode.PrefillThenFail)
		{
			return ValueTask.FromResult(defaultValue);
		}

		throw new McpInteractionException(
			$"Interactive prompt '{name}' requires a value. " +
			$"Provide it as a tool argument 'answer:{name}' (true/false).");
	}

	public ValueTask<string> AskTextAsync(
		string name,
		string prompt,
		string? defaultValue = null,
		AskOptions? options = null)
	{
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			return ValueTask.FromResult(prefill);
		}

		if (defaultValue is not null)
		{
			return ValueTask.FromResult(defaultValue);
		}

		if (_mode == InteractivityMode.PrefillThenDefaults)
		{
			return ValueTask.FromResult(string.Empty);
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

	public ValueTask<IReadOnlyList<int>> AskMultiChoiceAsync(
		string name,
		string prompt,
		IReadOnlyList<string> choices,
		IReadOnlyList<int>? defaultIndices = null,
		AskMultiChoiceOptions? options = null)
	{
		if (_prefillAnswers.TryGetValue(name, out var prefill))
		{
			var indices = prefill
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(token => ResolveChoiceIndex(token, choices))
				.ToArray();
			return ValueTask.FromResult<IReadOnlyList<int>>(indices);
		}

		if (defaultIndices is { Count: > 0 })
		{
			return ValueTask.FromResult(defaultIndices);
		}

		if (_mode == InteractivityMode.PrefillThenDefaults)
		{
			return ValueTask.FromResult<IReadOnlyList<int>>([]);
		}

		throw new McpInteractionException(
			$"Interactive prompt '{name}' requires a value. " +
			$"Provide it as a tool argument 'answer:{name}' (comma-separated indices or values). " +
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

	public ValueTask WriteStatusAsync(string text, CancellationToken cancellationToken) =>
		ValueTask.CompletedTask;

	public ValueTask ClearScreenAsync(CancellationToken cancellationToken) =>
		ValueTask.CompletedTask;

	public ValueTask<TResult> DispatchAsync<TResult>(
		InteractionRequest<TResult> request,
		CancellationToken cancellationToken) =>
		throw new NotSupportedException(
			"Custom interaction dispatch is not supported in MCP mode. " +
			"Consider marking the command as AutomationHidden().");

	private static int ResolveChoiceIndex(string value, IReadOnlyList<string> choices)
	{
		// Try exact match by value.
		for (var i = 0; i < choices.Count; i++)
		{
			if (string.Equals(choices[i], value, StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}

		// Try numeric index.
		if (int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var index) && index >= 0 && index < choices.Count)
		{
			return index;
		}

		throw new McpInteractionException(
			$"Cannot resolve choice '{value}'. Available: {string.Join(", ", choices)}");
	}
}
