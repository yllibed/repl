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

	public string CommandText { get; }

	public int ExitCode { get; }

	public string OutputText { get; }

	public object? ResultObject { get; }

	public IReadOnlyList<ReplInteractionEvent> InteractionEvents { get; }

	public IReadOnlyList<CommandEvent> TimelineEvents { get; }

	public DateTimeOffset StartedAtUtc { get; }

	public DateTimeOffset CompletedAtUtc { get; }

	public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;

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

	public T GetResult<T>() =>
		ResultObject is T typed
			? typed
			: throw new InvalidOperationException($"Command result is not available as '{typeof(T).FullName}'.");

	[RequiresUnreferencedCode("JSON deserialization of arbitrary T may require preserved metadata when trimming.")]
	public T ReadJson<T>()
	{
		var value = JsonSerializer.Deserialize<T>(OutputText, JsonOptions);
		return value ?? throw new InvalidOperationException("Unable to deserialize output text to requested JSON type.");
	}
}
