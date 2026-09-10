using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Repl.Testing;

/// <summary>
/// Result for one executed command within a simulated session.
/// </summary>
public sealed class CommandExecution
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	internal CommandExecution(
		string commandText,
		int exitCode,
		string outputText,
		object? resultObject,
		IReadOnlyList<ReplInteractionEvent> interactionEvents,
		IReadOnlyList<CommandEvent> timelineEvents,
		DateTimeOffset startedAtUtc,
		DateTimeOffset completedAtUtc)
	{
		CommandText = commandText;
		ExitCode = exitCode;
		OutputText = outputText;
		ResultObject = resultObject;
		InteractionEvents = interactionEvents;
		TimelineEvents = timelineEvents;
		StartedAtUtc = startedAtUtc;
		CompletedAtUtc = completedAtUtc;
	}

	/// <summary>
	/// The command line as it was handed to <see cref="ReplSessionHandle.RunCommandAsync(string, CancellationToken)"/>,
	/// before tokenization and before any prefilled answers were appended.
	/// </summary>
	public string CommandText { get; }

	/// <summary>
	/// The process-style status the run resolved to, through the application's own
	/// <see cref="ReplOptions.ExitCodes"/> policy rather than a value this toolkit chooses.
	/// </summary>
	public int ExitCode { get; }

	/// <summary>
	/// Everything the command wrote, with ANSI escape sequences stripped unless
	/// <see cref="ReplScenarioOptions.NormalizeAnsi"/> was turned off.
	/// </summary>
	public string OutputText { get; }

	/// <summary>
	/// The normalized handler result, or <see langword="null"/> when the command produced none.
	/// Prefer <see cref="TryGetResult{T}"/> or <see cref="GetResult{T}"/> over casting this yourself.
	/// </summary>
	public object? ResultObject { get; }

	/// <summary>
	/// The semantic interaction events the command raised — statuses, notices, warnings, problems —
	/// in the order they were observed.
	/// </summary>
	public IReadOnlyList<ReplInteractionEvent> InteractionEvents { get; }

	/// <summary>
	/// The command's events in order: what it wrote, then each interaction it raised, then the result
	/// it produced. Use this when the ordering between output and interactions is what matters.
	/// </summary>
	public IReadOnlyList<CommandEvent> TimelineEvents { get; }

	/// <summary>When the command started, in UTC.</summary>
	public DateTimeOffset StartedAtUtc { get; }

	/// <summary>When the command finished, in UTC.</summary>
	public DateTimeOffset CompletedAtUtc { get; }

	/// <summary>How long the command took, measured across the whole execution.</summary>
	public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;

	/// <summary>
	/// Reads <see cref="ResultObject"/> as <typeparamref name="T"/>, reporting failure instead of
	/// throwing when the command produced no result or produced a different type.
	/// </summary>
	/// <typeparam name="T">The type the result is expected to have.</typeparam>
	/// <param name="result">The typed result when this returns <see langword="true"/>.</param>
	/// <returns><see langword="true"/> when the result was available as <typeparamref name="T"/>.</returns>
	public bool TryGetResult<T>([NotNullWhen(true)] out T? result)
	{
		if (ResultObject is T typed)
		{
			result = typed;
			return true;
		}

		result = default;
		return false;
	}

	/// <summary>
	/// Reads <see cref="ResultObject"/> as <typeparamref name="T"/>, failing the test outright when the
	/// command produced no result or produced a different type.
	/// </summary>
	/// <typeparam name="T">The type the result is expected to have.</typeparam>
	/// <returns>The typed result.</returns>
	/// <exception cref="InvalidOperationException">The result is not available as <typeparamref name="T"/>.</exception>
	public T GetResult<T>() =>
		ResultObject is T typed
			? typed
			: throw new InvalidOperationException($"Command result is not available as '{typeof(T).FullName}'.");

	/// <summary>
	/// Deserializes <see cref="OutputText"/> as JSON. Asserts on what the command actually rendered, so
	/// it needs the command to have produced JSON — typically through <c>--output:json</c>.
	/// </summary>
	/// <typeparam name="T">The type to deserialize the output into.</typeparam>
	/// <returns>The deserialized value.</returns>
	/// <exception cref="InvalidOperationException">The output deserialized to <see langword="null"/>.</exception>
	/// <exception cref="System.Text.Json.JsonException">The output is not valid JSON for <typeparamref name="T"/>.</exception>
	[RequiresUnreferencedCode("JSON deserialization of arbitrary T may require preserved metadata when trimming.")]
	public T ReadJson<T>()
	{
		var value = JsonSerializer.Deserialize<T>(OutputText, JsonOptions);
		return value ?? throw new InvalidOperationException("Unable to deserialize output text to requested JSON type.");
	}
}
