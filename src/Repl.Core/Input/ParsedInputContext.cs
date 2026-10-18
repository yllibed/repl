using System.Collections.ObjectModel;
using Repl.Internal.Options;

namespace Repl;

/// <summary>A parsed input snapshot for validators; services are injected separately into their signatures.</summary>
public sealed class ParsedInputContext
{
	internal ParsedInputContext(ArgumentPreprocessingContext input, string? command,
		Dictionary<string, InputValue> values, Dictionary<string, InputValue> globals,
		InputValidationResult validation)
	{
		Input = input;
		Command = command;
		Values = new ReadOnlyDictionary<string, InputValue>(values);
		GlobalValues = new ReadOnlyDictionary<string, InputValue>(globals);
		Validation = validation;
	}

	internal ParsedInputContext(ParsedInputContext snapshot, InputValidationResult validation, InputValidationResult applicationValidation)
	{
		Input = snapshot.Input;
		Command = snapshot.Command;
		Values = snapshot.Values;
		GlobalValues = snapshot.GlobalValues;
		Validation = validation;
		ApplicationValidation = applicationValidation;
		OptionSchema = snapshot.OptionSchema;
	}

	/// <summary>Gets original and effective input and the processing mode.</summary>
	public ArgumentPreprocessingContext Input { get; }
	/// <summary>Gets the resolved route template, or null for unresolved or partial input.</summary>
	public string? Command { get; }
	/// <summary>Gets route, option and positional values keyed by their parameter names.</summary>
	public IReadOnlyDictionary<string, InputValue> Values { get; }
	/// <summary>Gets typed global values keyed by their registered names.</summary>
	public IReadOnlyDictionary<string, InputValue> GlobalValues { get; }
	/// <summary>Gets parser and earlier-validator diagnostics.</summary>
	public InputValidationResult Validation { get; }

	internal OptionSchema OptionSchema { get; init; } = OptionSchema.Empty;

	internal InputValidationResult ApplicationValidation { get; init; } = InputValidationResult.Valid();

	/// <summary>Reads an available command input value without performing another conversion.</summary>
	/// <typeparam name="T">Expected declared value type.</typeparam>
	/// <param name="name">Parameter name.</param>
	/// <param name="value">Converted value when available.</param>
	public bool TryGetValue<T>(string name, out T? value)
	{
		if (Values.TryGetValue(name, out var input) && input.State == InputValueState.Available && input.Value is T typed)
		{
			value = typed;
			return true;
		}
		value = default;
		return false;
	}
}
