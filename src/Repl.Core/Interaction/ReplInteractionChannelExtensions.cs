using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace Repl.Interaction;

/// <summary>
/// Extension methods that compose on top of <see cref="IReplInteractionChannel"/> primitives.
/// </summary>
public static class ReplInteractionChannelExtensions
{
	/// <summary>
	/// Writes an informational user-facing notice.
	/// </summary>
	public static async ValueTask WriteNoticeAsync(
		this IReplInteractionChannel channel,
		string text,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(channel);
		_ = await channel.DispatchAsync(
				new WriteNoticeRequest(text, cancellationToken),
				cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Writes a user-facing warning.
	/// </summary>
	public static async ValueTask WriteWarningAsync(
		this IReplInteractionChannel channel,
		string text,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(channel);
		_ = await channel.DispatchAsync(
				new WriteWarningRequest(text, cancellationToken),
				cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Writes a user-facing problem summary.
	/// </summary>
	public static async ValueTask WriteProblemAsync(
		this IReplInteractionChannel channel,
		string summary,
		string? details = null,
		string? code = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(channel);
		_ = await channel.DispatchAsync(
				new WriteProblemRequest(summary, details, code, cancellationToken),
				cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Writes a structured progress update.
	/// </summary>
	public static async ValueTask WriteProgressAsync(
		this IReplInteractionChannel channel,
		ReplProgressEvent progress,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(channel);
		ArgumentNullException.ThrowIfNull(progress);
		_ = await channel.DispatchAsync(
				new WriteProgressRequest(
					progress.Label,
					progress.ResolvePercent(),
					progress.State,
					progress.Details,
					cancellationToken),
				cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Writes an indeterminate progress update.
	/// </summary>
	public static ValueTask WriteIndeterminateProgressAsync(
		this IReplInteractionChannel channel,
		string label,
		string? details = null,
		CancellationToken cancellationToken = default) =>
		channel.WriteProgressAsync(
			new ReplProgressEvent(label, State: ReplProgressState.Indeterminate, Details: details),
			cancellationToken);

	/// <summary>
	/// Writes a warning-state progress update.
	/// </summary>
	public static ValueTask WriteWarningProgressAsync(
		this IReplInteractionChannel channel,
		string label,
		double? percent = null,
		string? details = null,
		CancellationToken cancellationToken = default) =>
		channel.WriteProgressAsync(
			new ReplProgressEvent(label, Percent: percent, State: ReplProgressState.Warning, Details: details),
			cancellationToken);

	/// <summary>
	/// Writes an error-state progress update.
	/// </summary>
	public static ValueTask WriteErrorProgressAsync(
		this IReplInteractionChannel channel,
		string label,
		double? percent = null,
		string? details = null,
		CancellationToken cancellationToken = default) =>
		channel.WriteProgressAsync(
			new ReplProgressEvent(label, Percent: percent, State: ReplProgressState.Error, Details: details),
			cancellationToken);

	/// <summary>
	/// Clears any currently visible progress indicator.
	/// </summary>
	public static async ValueTask ClearProgressAsync(
		this IReplInteractionChannel channel,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(channel);
		_ = await channel.DispatchAsync(
				new WriteProgressRequest(
					Label: string.Empty,
					Percent: null,
					State: ReplProgressState.Clear,
					Details: null,
					CancellationToken: cancellationToken),
				cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Prompts the user to select a value from an enum type.
	/// Uses <see cref="DescriptionAttribute"/> or <see cref="DisplayAttribute"/> names when present,
	/// otherwise humanizes the enum member name (<c>CamelCase</c> becomes <c>Camel case</c>).
	/// </summary>
	public static async ValueTask<TEnum> AskEnumAsync<TEnum>(
		this IReplInteractionChannel channel,
		string name,
		string prompt,
		TEnum? defaultValue = null,
		AskOptions? options = null) where TEnum : struct, Enum
	{
		var values = Enum.GetValues<TEnum>();
		var names = values.Select(FormatEnumName).ToList();
		var defaultIndex = defaultValue is not null
			? Array.IndexOf(values, defaultValue.Value)
			: (int?)null;
		var index = await channel.AskChoiceAsync(name, prompt, names, defaultIndex, options)
			.ConfigureAwait(false);
		return values[index];
	}

	/// <summary>
	/// Prompts the user to select one or more values from a <c>[Flags]</c> enum type.
	/// </summary>
	public static async ValueTask<TEnum> AskFlagsEnumAsync<TEnum>(
		this IReplInteractionChannel channel,
		string name,
		string prompt,
		TEnum? defaultValue = null,
		AskMultiChoiceOptions? options = null) where TEnum : struct, Enum
	{
		var values = Enum.GetValues<TEnum>();
		var names = values.Select(FormatEnumName).ToList();

		IReadOnlyList<int>? defaultIndices = null;
		if (defaultValue is not null)
		{
			var defaultLong = Convert.ToInt64(defaultValue.Value, System.Globalization.CultureInfo.InvariantCulture);
			defaultIndices = values
				.Select((v, i) => (Value: Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture), Index: i))
				.Where(x => x.Value != 0 && (defaultLong & x.Value) == x.Value)
				.Select(x => x.Index)
				.ToArray();
		}

		var selectedIndices = await channel.AskMultiChoiceAsync(name, prompt, names, defaultIndices, options)
			.ConfigureAwait(false);

		long result = 0;
		foreach (var idx in selectedIndices)
		{
			result |= Convert.ToInt64(values[idx], System.Globalization.CultureInfo.InvariantCulture);
		}

		return (TEnum)Enum.ToObject(typeof(TEnum), result);
	}

	/// <summary>
	/// Prompts the user for a typed numeric value with optional min/max bounds.
	/// </summary>
	public static async ValueTask<T> AskNumberAsync<T>(
		this IReplInteractionChannel channel,
		string name,
		string prompt,
		T? defaultValue = null,
		AskNumberOptions<T>? options = null) where T : struct, INumber<T>, IParsable<T>
	{
		var decoratedPrompt = BuildNumberPrompt(prompt, defaultValue, options);
		var askOptions = options is not null
			? new AskOptions(options.Timeout, options.CancellationToken)
			: null;
		var defaultText = defaultValue?.ToString();
		string? previousLine = null;

		while (true)
		{
			var line = await channel.AskTextAsync(name, decoratedPrompt, defaultText, askOptions)
				.ConfigureAwait(false);

			if (T.TryParse(line, System.Globalization.NumberStyles.Any,
				System.Globalization.CultureInfo.InvariantCulture, out var value))
			{
				if (options?.Min is not null && value < options.Min.Value)
				{
					ThrowIfRepeatedInput(line, ref previousLine,
						$"Value must be at least {options.Min.Value}.");
					await channel.WriteStatusAsync(
							$"Value must be at least {options.Min.Value}.",
							options.CancellationToken)
						.ConfigureAwait(false);
					continue;
				}

				if (options?.Max is not null && value > options.Max.Value)
				{
					ThrowIfRepeatedInput(line, ref previousLine,
						$"Value must be at most {options.Max.Value}.");
					await channel.WriteStatusAsync(
							$"Value must be at most {options.Max.Value}.",
							options.CancellationToken)
						.ConfigureAwait(false);
					continue;
				}

				return value;
			}

			var ct = options?.CancellationToken ?? default;
			ThrowIfRepeatedInput(line, ref previousLine,
				$"'{line}' is not a valid {typeof(T).Name}.");
			await channel.WriteStatusAsync($"'{line}' is not a valid {typeof(T).Name}.", ct)
				.ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Prompts the user for text with inline validation. Re-prompts until the validator
	/// returns <c>null</c> (meaning valid).
	/// </summary>
	/// <param name="channel">The interaction channel.</param>
	/// <param name="name">Prompt name.</param>
	/// <param name="prompt">Prompt text.</param>
	/// <param name="validate">
	/// Validator function. Returns <c>null</c> when the input is valid, or an error
	/// message string otherwise.
	/// </param>
	/// <param name="defaultValue">Optional default value.</param>
	/// <param name="options">Optional ask options.</param>
	/// <returns>The validated text.</returns>
	public static async ValueTask<string> AskValidatedTextAsync(
		this IReplInteractionChannel channel,
		string name,
		string prompt,
		Func<string, string?> validate,
		string? defaultValue = null,
		AskOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(validate);
		var ct = options?.CancellationToken ?? default;
		string? previousInput = null;

		while (true)
		{
			var input = await channel.AskTextAsync(name, prompt, defaultValue, options)
				.ConfigureAwait(false);
			var error = validate(input);
			if (error is null)
			{
				return input;
			}

			ThrowIfRepeatedInput(input, ref previousInput, error);
			await channel.WriteStatusAsync(error, ct).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Displays a message and waits for the user to press any key.
	/// </summary>
	public static async ValueTask PressAnyKeyAsync(
		this IReplInteractionChannel channel,
		string prompt = "Press any key to continue...",
		CancellationToken cancellationToken = default)
	{
		await channel.AskTextAsync("__press_any_key__", prompt, string.Empty,
				new AskOptions(CancellationToken: cancellationToken))
			.ConfigureAwait(false);
	}

	internal static string FormatEnumName<TEnum>(TEnum value) where TEnum : struct, Enum
	{
		var name = value.ToString();
		var memberInfo = typeof(TEnum).GetField(name);
		if (memberInfo is not null)
		{
			var descAttr = memberInfo.GetCustomAttribute<DescriptionAttribute>();
			if (descAttr is not null)
			{
				return descAttr.Description;
			}

			var displayAttr = memberInfo.GetCustomAttribute<DisplayAttribute>();
			if (displayAttr?.Name is not null)
			{
				return displayAttr.Name;
			}
		}

		return HumanizePascalCase(name);
	}

	internal static string HumanizePascalCase(string input)
	{
		if (string.IsNullOrEmpty(input))
		{
			return input;
		}

		var sb = new StringBuilder(input.Length + 4);
		for (var i = 0; i < input.Length; i++)
		{
			var c = input[i];
			if (i > 0 && char.IsUpper(c) && !char.IsUpper(input[i - 1]))
			{
				sb.Append(' ');
				sb.Append(char.ToLowerInvariant(c));
			}
			else
			{
				sb.Append(c);
			}
		}

		return sb.ToString();
	}

	private static string BuildNumberPrompt<T>(
		string prompt,
		T? defaultValue,
		AskNumberOptions<T>? options) where T : struct, INumber<T>
	{
		if (options is null)
		{
			return prompt;
		}

		if (options.Min is null && options.Max is null)
		{
			return prompt;
		}

		var sb = new StringBuilder(prompt);
		sb.Append(" (");
		sb.Append(options.Min?.ToString() ?? "..");
		sb.Append("..");
		sb.Append(options.Max?.ToString() ?? "");
		sb.Append(')');
		return sb.ToString();
	}

	private static void ThrowIfRepeatedInput(
		string? current, ref string? previous, string validationMessage)
	{
		if (current is not null && string.Equals(current, previous, StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				$"Prefilled answer failed validation: {validationMessage}");
		}

		previous = current;
	}
}
