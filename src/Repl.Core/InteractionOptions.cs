namespace Repl;

/// <summary>
/// Prompt interaction behavior options.
/// </summary>
public sealed class InteractionOptions
{
	private IReadOnlyDictionary<string, string> _prefilledAnswers =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	private IReplExecutionObserver? _observer;

	/// <summary>
	/// Gets or sets the default progress label used when handlers report percentage-only progress.
	/// </summary>
	public string DefaultProgressLabel { get; set; } = "Progress";

	/// <summary>
	/// Gets or sets the progress rendering template.
	/// Supported placeholders: {label}, {percent}, {percent:0}, {percent:0.0}.
	/// </summary>
	public string ProgressTemplate { get; set; } = "{label}: {percent:0}%";

	/// <summary>
	/// Gets or sets fallback behavior for unanswered non-interactive prompts.
	/// </summary>
	public PromptFallback PromptFallback { get; set; } = PromptFallback.UseDefault;

	internal void SetPrefilledAnswers(IReadOnlyDictionary<string, string> answers)
	{
		_prefilledAnswers = answers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	internal bool TryGetPrefilledAnswer(string name, out string? value)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			value = null;
			return false;
		}

		if (!_prefilledAnswers.TryGetValue(name, out var candidate))
		{
			value = null;
			return false;
		}

		value = candidate;
		return true;
	}

	internal void SetObserver(IReplExecutionObserver? observer) => _observer = observer;

	internal IReplExecutionObserver? Observer => _observer;
}
