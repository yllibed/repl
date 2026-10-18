using System.Collections.ObjectModel;

namespace Repl;

/// <summary>An invocation expressed as tokens or as a canonical route with native parameter values.</summary>
public sealed class ReplInvocationRequest
{
	private ReplInvocationRequest(IReadOnlyList<string> arguments, string? command,
		IReadOnlyDictionary<string, object?>? values, IReadOnlyDictionary<string, object?>? globals)
	{
		Arguments = Array.AsReadOnly(arguments.ToArray());
		Command = command;
		Values = Snapshot(values);
		GlobalValues = Snapshot(globals);
	}

	/// <summary>Gets textual arguments. Structured requests have no textual arguments.</summary>
	public IReadOnlyList<string> Arguments { get; }
	/// <summary>Gets the exact registered route template, or null for textual requests.</summary>
	public string? Command { get; }
	/// <summary>Gets native route, argument and option values keyed by parameter name.</summary>
	public IReadOnlyDictionary<string, object?> Values { get; }
	/// <summary>Gets native globals keyed by their registered names.</summary>
	public IReadOnlyDictionary<string, object?> GlobalValues { get; }

	/// <summary>Creates a textual request, copying its tokens.</summary>
	/// <param name="arguments">Command-line tokens.</param>
	/// <returns>A request snapshot.</returns>
	public static ReplInvocationRequest FromArguments(IReadOnlyList<string> arguments)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		return new(arguments, command: null, values: null, globals: null);
	}

	/// <summary>Creates a structured request without converting its values to command-line tokens.</summary>
	/// <param name="command">Exact registered route template.</param>
	/// <param name="values">Native values keyed by parameter name.</param>
	/// <param name="globalValues">Native global options; omitted globals use the ordinary defaults or session baseline.</param>
	/// <returns>A request snapshot.</returns>
	public static ReplInvocationRequest ForCommand(string command, IReadOnlyDictionary<string, object?>? values = null,
		IReadOnlyDictionary<string, object?>? globalValues = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(command);
		return new([], command, values, globalValues);
	}

	internal ReplInvocationRequest Copy() => new(Arguments, Command, Values, GlobalValues);

	private static ReadOnlyDictionary<string, object?> Snapshot(IReadOnlyDictionary<string, object?>? source)
	{
		var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
		foreach (var pair in source ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase))
		{
			copy.Add(pair.Key, CopyValue(pair.Value));
		}
		return new(copy);
	}

	private static object? CopyValue(object? value) => value switch
	{
		System.Text.Json.JsonElement json => json.Clone(),
		Array array => CopyArray(array),
		_ => value,
	};

	private static Array CopyArray(Array array)
	{
		var copy = (Array)array.Clone();
		if (copy.Rank == 1)
		{
			for (var index = copy.GetLowerBound(0); index <= copy.GetUpperBound(0); index++)
			{
				copy.SetValue(CopyValue(copy.GetValue(index)), index);
			}
		}
		return copy;
	}
}
