using System.Diagnostics.CodeAnalysis;
using Repl.Internal.Options;

namespace Repl;

public sealed partial class CoreReplApp
{
	private readonly AsyncLocal<ProcessedInput?> _currentInput = new();

	internal IDisposable PushProcessedInput(ProcessedInput input)
	{
		var previous = _currentInput.Value;
		_currentInput.Value = input;
		return new ProcessedInputScope(this, previous);
	}

	private sealed class ProcessedInputScope(CoreReplApp owner, ProcessedInput? previous) : IDisposable
	{
		public void Dispose() => owner._currentInput.Value = previous;
	}

	internal async ValueTask<ProcessedInput> PreprocessInputAsync(
		IReadOnlyList<string> arguments, IReadOnlyList<string> scopeTokens, InputProcessingMode mode,
		string? text, int? cursor, CancellationToken cancellationToken)
	{
		var original = arguments.ToArray();
		IReadOnlyList<string> effective = original;
		IReadOnlyList<InputSourceRange?> origins = text is null
			? new InputSourceRange?[original.Length]
			: InputTokenizer.Tokenize(text).Select(static token => (InputSourceRange?)new InputSourceRange(token.Start, token.Length)).ToArray();
		var checks = new List<InputValidationResult>();
		var context = CreateContext();
		foreach (var adapter in _options.Input.Preprocessors.ToArray())
		{
			cancellationToken.ThrowIfCancellationRequested();
			var decision = await adapter(context, cancellationToken).ConfigureAwait(false)
				?? throw new InvalidOperationException("An argument preprocessor returned null.");
			checks.Add(decision.Validation);
			if (decision.Arguments is { } replacement)
			{
				var replacementOrigins = decision.Origins ?? PreserveInputOrigins(effective, origins, replacement);
				if (text is not null && replacementOrigins.Any(origin => origin is not null && origin.Start > text.Length - origin.Length))
				{
					throw new InvalidOperationException("An argument origin is outside the original input text.");
				}
				effective = replacement;
				origins = replacementOrigins;
				context = CreateContext();
			}
			if (decision.IsHandled)
			{
				return new(context, CombineInputValidation(checks), IsHandled: true, decision.Result);
			}
		}
		return new(context, CombineInputValidation(checks));

		ArgumentPreprocessingContext CreateContext() => new(
			original, effective, scopeTokens, mode, IdentifyInputPurpose(effective, mode), text, cursor, origins);
	}

	private static InputSourceRange?[] PreserveInputOrigins(IReadOnlyList<string> previous,
		IReadOnlyList<InputSourceRange?> origins, IReadOnlyList<string> replacement)
	{
		var mapped = new InputSourceRange?[replacement.Count];
		for (var index = 0; index < replacement.Count; index++)
		{
			var source = -1;
			for (var candidate = 0; candidate < previous.Count; candidate++)
			{
				if (!ReferenceEquals(previous[candidate], replacement[index])) { continue; }
				if (source >= 0) { source = -1; break; }
				source = candidate;
			}
			if (source >= 0 && source < origins.Count) { mapped[index] = origins[source]; }
		}
		return mapped;
	}

	private InputPurpose IdentifyInputPurpose(IReadOnlyList<string> arguments, InputProcessingMode mode)
	{
		if (mode is InputProcessingMode.Completion or InputProcessingMode.ShellCompletion) { return InputPurpose.Completion; }
		var comparison = _options.Parsing.OptionCaseSensitivity == ReplCaseSensitivity.CaseSensitive
			? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		if (arguments.Any(argument => string.Equals(argument, "--help", comparison))
			|| (arguments.Count != 0 && string.Equals(arguments[0], "help", StringComparison.OrdinalIgnoreCase)))
		{
			return InputPurpose.Help;
		}
		return arguments.Count == 0 ? InputPurpose.Unknown : InputPurpose.Command;
	}

	private async ValueTask<ExecutionOutcome> ExecuteCoreOutcomeAsync(
		IReadOnlyList<string> args, IServiceProvider serviceProvider, bool isSubInvocation,
		CancellationToken cancellationToken, IServiceProvider? presenceServiceProvider = null)
	{
		// Structured adapters retain their native invocation contract; #115 owns their interception.
		if (!_options.Input.IsEnabled || ReplSessionIO.IsProgrammatic || IsCompletionBridge(args))
		{
			return await ExecuteUnprocessedCoreOutcomeAsync(args, serviceProvider, isSubInvocation,
				cancellationToken, presenceServiceProvider).ConfigureAwait(false);
		}
		var previous = _currentInput.Value;
		using var runtimeScope = PushRuntimeState(serviceProvider, isInteractiveSession: false, presenceServiceProvider);
		try
		{
			var input = await PreprocessInputAsync(args, [], InputProcessingMode.Execution, text: null, cursor: null, cancellationToken)
				.ConfigureAwait(false);
			_currentInput.Value = input;
			if (await TryFinishProcessedInputAsync(input, cancellationToken).ConfigureAwait(false) is { } finished)
			{
				return finished;
			}
			if (input.Context.Purpose == InputPurpose.Help && _options.Input.Validators.Count != 0)
			{
				var help = await AnalyzeInputAsync(input, serviceProvider, cancellationToken).ConfigureAwait(false);
				if (help.ApplicationValidation.State != InputValidationState.Valid)
				{
					return await RefuseInputAsync(help.ApplicationValidation, cancellationToken).ConfigureAwait(false);
				}
			}
			return await ExecuteUnprocessedCoreOutcomeAsync(input.Context.Arguments, serviceProvider,
				isSubInvocation, cancellationToken, presenceServiceProvider).ConfigureAwait(false);
		}
		catch (Exception exception) when (!IsCallerCancellation(exception, cancellationToken))
		{
			return await ReportInputCallbackFailureAsync(exception, cancellationToken).ConfigureAwait(false);
		}
		finally { _currentInput.Value = previous; }
	}

	private static bool IsCompletionBridge(IReadOnlyList<string> arguments) =>
		arguments.Count >= 2 && string.Equals(arguments[0], "completion", StringComparison.Ordinal)
		&& string.Equals(arguments[1], "__complete", StringComparison.Ordinal);

	internal async ValueTask<ExecutionOutcome?> TryFinishProcessedInputAsync(ProcessedInput input, CancellationToken cancellationToken)
	{
		if (input.Validation.State != InputValidationState.Valid)
		{
			return await RefuseInputAsync(input.Validation, cancellationToken).ConfigureAwait(false);
		}
		if (!input.IsHandled) { return null; }
		var globals = GlobalOptionParser.Parse(input.Context.Arguments, _options.Output, _options.Parsing);
		await TryClearProgressAsync(CurrentServiceProvider).ConfigureAwait(false);
		if (TupleDecomposer.IsTupleResult(input.Result, out var tuple))
		{
			var result = await RenderTupleResultAsync(tuple, scopeTokens: null, globals, CurrentServiceProvider, cancellationToken).ConfigureAwait(false);
			return result.Outcome;
		}
		ExecutionObserver?.OnResult(input.Result);
		var classified = ClassifyResult(input.Result);
		try
		{
			var rendered = await RenderOutputAsync(input.Result, globals.OutputFormat, cancellationToken, IsInteractiveSession, globals.ResultFlow)
				.ConfigureAwait(false);
			return rendered ? classified : ExecutionOutcome.UsageError(input.Result);
		}
		catch (OperationCanceledException exception) when (PreservesExplicitExitCode(classified, exception, cancellationToken))
		{
			return classified;
		}
	}

	internal async ValueTask<ExecutionOutcome> ReportInputCallbackFailureAsync(Exception exception, CancellationToken cancellationToken)
	{
		var failure = Results.Error("input_callback_error", DescribeLocally(exception));
		var report = await ReportFailureAsync(failure, requestedFormat: null, cancellationToken).ConfigureAwait(false);
		return report == FailureReport.Rendered
			? ExecutionOutcome.HandlerException(exception, failure)
			: ExecutionOutcome.FrameworkError(failure, exception);
	}

	private ValueTask<ExecutionOutcome> RefuseInputAsync(InputValidationResult validation, CancellationToken cancellationToken) =>
		RefuseAsync(Results.Validation(validation.Diagnostics.Count != 0 ? validation.Diagnostics[0].Message : "Input validation failed."),
			requestedFormat: null, cancellationToken);

	internal async ValueTask<ParsedInputContext> AnalyzeInputAsync(
		ProcessedInput input, IServiceProvider services, CancellationToken cancellationToken,
		RouteMatch? match = null, GlobalInvocationOptions? globals = null, OptionParsingResult? parsedOptions = null)
	{
		var checks = new List<InputValidationResult> { input.Validation };
		var applicationChecks = new List<InputValidationResult> { input.Validation };
		globals ??= GlobalOptionParser.Parse(
			[.. input.Context.ScopeTokens, .. input.Context.Arguments], _options.Output, _options.Parsing);
		foreach (var diagnostic in globals.Diagnostics) { checks.Add(ToInputValidation(input, diagnostic)); }
		using var globalsScope = _globalOptionsSnapshot.PushInputValues(globals.CustomGlobalNamedOptions);
		if (match is null && !input.IsHandled)
		{
			var graph = ResolveActiveRoutingGraph(useDurableCache: false);
			var prefix = ResolveUniquePrefixes(globals.RemainingTokens, graph);
			var resolution = ResolveWithDiagnostics(prefix.Tokens, graph.Routes);
			match = resolution.Match;
			if (match is null && input.Context.Purpose == InputPurpose.Command)
			{
				var comparison = _options.Parsing.OptionCaseSensitivity == ReplCaseSensitivity.CaseSensitive
					? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
				var isContext = ContextResolver.ResolveExact(graph.Contexts, prefix.Tokens, _options.Parsing) is not null;
				var canComplete = prefix.Tokens.Length == 0 || prefix.IsAmbiguous || resolution.MissingArgumentsFailure is not null
					|| Autocomplete.CollectVisibleMatchingRoutes(prefix.Tokens.ToArray(), comparison, graph.Routes, graph.Contexts).Count != 0;
				if (!isContext)
				{
					checks.Add(resolution.ConstraintFailure is not null
						? InputValidationResult.Invalid("An argument does not satisfy its route constraint.")
						: canComplete ? InputValidationResult.Incomplete("Complete the command and its required arguments.")
						: InputValidationResult.Invalid("Unknown command."));
				}
			}
		}
		var values = new Dictionary<string, InputValue>(StringComparer.OrdinalIgnoreCase);
		if (match is not null)
		{
			parsedOptions ??= InvocationOptionParser.Parse(match.RemainingTokens, match.Route.OptionSchema,
				BuildEffectiveCommandParsingOptions(globals.GlobalOptionConfiguration.CaseSensitivity), globals.CustomGlobalTokenOwnership);
			foreach (var diagnostic in parsedOptions.Diagnostics) { checks.Add(ToInputValidation(input, diagnostic, match.Route.OptionSchema)); }
			ReadTypedInputValues(input, match, parsedOptions, values, checks);
		}
		var globalValues = ReadTypedGlobalValues(globals, checks);
		if (input.Context.Text is { } text && InputTokenizer.Tokenize(text).Exists(static token => token.IsIncomplete))
		{
			checks.Add(InputValidationResult.Incomplete("Close the quoted argument."));
		}
		var context = new ParsedInputContext(input.Context, match?.Route.Template.Template, values, globalValues, CombineInputValidation(checks))
		{
			ApplicationValidation = CombineInputValidation(applicationChecks),
		};
		foreach (var validator in _options.Input.Validators.ToArray())
		{
			cancellationToken.ThrowIfCancellationRequested();
			var result = await InvokeInputValidatorAsync(validator, context, services, cancellationToken).ConfigureAwait(false);
			var validation = result as InputValidationResult
				?? throw new InvalidOperationException("Input validators must return InputValidationResult, Task<InputValidationResult> or ValueTask<InputValidationResult>.");
			checks.Add(validation);
			applicationChecks.Add(validation);
			context = new(input.Context, context.Command, values, globalValues, CombineInputValidation(checks))
			{
				ApplicationValidation = CombineInputValidation(applicationChecks),
			};
		}
		return context;
	}

	private InputValidationResult ToInputValidation(ProcessedInput input, ParseDiagnostic diagnostic, OptionSchema? schema = null)
	{
		var comparison = _options.Parsing.OptionCaseSensitivity == ReplCaseSensitivity.CaseSensitive
			? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		var isOptionPrefix = diagnostic.Token is { } token && schema is not null
			&& schema.ResolveDiscoverableTokens(_options.Parsing.OptionCaseSensitivity)
				.Any(known => known.StartsWith(token, comparison) && !string.Equals(known, token, comparison));
		var state = diagnostic.Severity != ParseDiagnosticSeverity.Error ? InputValidationState.Valid
			: (diagnostic.IsIncomplete || isOptionPrefix) && input.Context.Mode != InputProcessingMode.Execution ? InputValidationState.Incomplete
			: InputValidationState.Invalid;
		return new(state, [new(state, diagnostic.Message, FindInputRange(input, diagnostic.Token))]);
	}

	private async ValueTask<object?> InvokeInputValidatorAsync(Delegate validator, ParsedInputContext context,
		IServiceProvider services, CancellationToken cancellationToken)
	{
		var binding = new InvocationBindingContext(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), [], OptionSchema.Empty,
			_options.Parsing.OptionCaseSensitivity, [context], _options.Parsing.NumericFormatProvider,
			services, _options.Interaction, _implicitServiceParameters, cancellationToken);
		var arguments = HandlerArgumentBinder.Bind(validator, binding);
		return await CommandInvoker.InvokeAsync(validator, arguments).ConfigureAwait(false);
	}

	private Dictionary<string, InputValue> ReadTypedGlobalValues(GlobalInvocationOptions globals, List<InputValidationResult> checks)
	{
		var values = new Dictionary<string, InputValue>(StringComparer.OrdinalIgnoreCase);
		foreach (var definition in globals.GlobalOptionConfiguration.Definitions.Values)
		{
			var raw = _globalOptionsSnapshot.GetRawValues(definition.Name);
			var value = raw.Count != 0 ? raw[0] : definition.DefaultValue;
			if (value is null) { values[definition.Name] = new(InputValueState.Missing); continue; }
			try
			{
				values[definition.Name] = new(InputValueState.Available,
					ParameterValueConverter.ConvertSingle(value, definition.ValueType, _options.Parsing.NumericFormatProvider));
			}
			catch (Exception exception) when (IsInputConversionFailure(exception))
			{
				values[definition.Name] = new(InputValueState.Invalid);
				checks.Add(InputValidationResult.Invalid(exception.Message));
			}
		}
		return values;
	}

	private void ReadTypedInputValues(ProcessedInput input, RouteMatch match, OptionParsingResult parsed,
		Dictionary<string, InputValue> values, List<InputValidationResult> checks)
	{
		foreach (var parameter in match.Route.Command.Handler.Method.GetParameters())
		{
			if (parameter.Name is { } name && match.Values.TryGetValue(name, out var raw))
			{
				AddTypedInputValue(input, name, [raw], parameter.ParameterType, values, checks,
					_options.Parsing.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive);
			}
		}
		var positionalIndex = 0;
		foreach (var parameter in match.Route.OptionSchema.Parameters.Values)
		{
			IReadOnlyList<string>? raw = null;
			var missing = false;
			try
			{
				if (InputValueSelector.TrySelectNamed(parameter.Name, parameter.Mode, parsed.NamedOptions,
					parsed.PositionalArguments, positionalIndex, out var named)) { raw = named; }
				else if (parameter.Mode != ReplParameterMode.OptionOnly && positionalIndex < parsed.PositionalArguments.Count)
				{
					raw = InputValueSelector.SelectPositional(parameter.ParameterType, parsed.PositionalArguments, positionalIndex);
					HandlerArgumentBinder.ThrowIfExplicitUpperBoundExceeded(match.Route.OptionSchema, parameter.Name, raw.Count);
					positionalIndex += raw.Count;
				}
				if (raw is null)
				{
					missing = true;
					HandlerArgumentBinder.ThrowIfExplicitLowerBoundUnsatisfied(match.Route.OptionSchema, parameter.Name);
				}
			}
			catch (InvalidOperationException exception)
			{
				values[parameter.Name] = new(missing ? InputValueState.Missing : InputValueState.Invalid);
				var state = missing && input.Context.Mode != InputProcessingMode.Execution
					? InputValidationState.Incomplete : InputValidationState.Invalid;
				checks.Add(new(state, [new(state, exception.Message)]));
				continue;
			}
			if (raw is null)
			{
				var metadata = match.Route.Command.Handler.Method.GetParameters()
					.FirstOrDefault(item => string.Equals(item.Name, parameter.Name, StringComparison.Ordinal));
				values[parameter.Name] = metadata?.HasDefaultValue == true
					? new(InputValueState.Available, metadata.DefaultValue) : new(InputValueState.Missing);
				if (!parameter.CanBeOmitted && IsInputScalar(parameter.ParameterType))
				{
					checks.Add(InputValidationResult.Incomplete($"Supply a value for '{parameter.Name}'."));
				}
				continue;
			}
			AddTypedInputValue(input, parameter.Name, raw, parameter.ParameterType, values, checks,
				(parameter.CaseSensitivity ?? _options.Parsing.OptionCaseSensitivity) == ReplCaseSensitivity.CaseInsensitive);
		}
	}

	private static bool IsInputScalar(Type type) => type.IsValueType || type == typeof(string)
		|| type == typeof(Uri) || type == typeof(FileInfo) || type == typeof(DirectoryInfo);

	private void AddTypedInputValue(ProcessedInput input, string name, IReadOnlyList<string> raw, Type type,
		Dictionary<string, InputValue> values, List<InputValidationResult> checks, bool enumIgnoreCase)
	{
		try
		{
			var converted = HandlerArgumentBinder.TryGetCollectionElementType(type, out var elementType)
				? HandlerArgumentBinder.ConvertMany(raw, type, elementType, _options.Parsing.NumericFormatProvider, enumIgnoreCase)
				: ParameterValueConverter.ConvertSingle(raw.Count == 0 ? null : raw[0], type,
					_options.Parsing.NumericFormatProvider, enumIgnoreCase);
			values[name] = new(InputValueState.Available, converted);
		}
		catch (Exception exception) when (IsInputConversionFailure(exception))
		{
			values[name] = new(InputValueState.Invalid);
			var incomplete = input.Context.Mode != InputProcessingMode.Execution && raw.Count == 1
				&& IsIncompleteValue(raw[0], type, enumIgnoreCase);
			var state = incomplete ? InputValidationState.Incomplete : InputValidationState.Invalid;
			checks.Add(new(state, [new(state, $"Invalid value for '{name}': {exception.Message}", FindInputRange(input, raw.Count == 0 ? null : raw[0]))]));
		}
	}

	private static bool IsIncompleteValue(string value, Type type, bool enumIgnoreCase)
	{
		type = Nullable.GetUnderlyingType(type) ?? type;
		if (value.Length == 0 || value is "-" or "+") { return true; }
		return type.IsEnum && Enum.GetNames(type).Any(name => name.StartsWith(value,
			enumIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
	}

	private static bool IsInputConversionFailure(Exception exception) =>
		exception is FormatException or OverflowException or InvalidCastException or ArgumentException;

	internal static InputSourceRange? FindInputRange(ProcessedInput input, string? value)
	{
		InputSourceRange? range = null;
		var found = false;
		for (var index = 0; index < input.Context.Arguments.Count; index++)
		{
			if (!string.Equals(input.Context.Arguments[index], value, StringComparison.Ordinal)) { continue; }
			if (found) { return null; }
			found = true;
			range = input.Context.ArgumentOrigins.ElementAtOrDefault(index);
		}
		return range;
	}

	private async ValueTask<ExecutionOutcome?> ValidateInputForExecutionAsync(RouteMatch match,
		GlobalInvocationOptions globals, OptionParsingResult parsed, IServiceProvider services, CancellationToken cancellationToken)
	{
		if (_currentInput.Value is not { } input || _options.Input.Validators.Count == 0) { return null; }
		try
		{
			var context = await AnalyzeInputAsync(input, services, cancellationToken, match, globals, parsed).ConfigureAwait(false);
			return context.Validation.State == InputValidationState.Valid ? null
				: await RefuseInputAsync(context.Validation, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (!IsCallerCancellation(exception, cancellationToken))
		{
			return await ReportInputCallbackFailureAsync(exception, cancellationToken).ConfigureAwait(false);
		}
	}

	internal static InputValidationResult CombineInputValidation(IEnumerable<InputValidationResult> checks)
	{
		var array = checks.ToArray();
		var diagnostics = array.SelectMany(static item => item.Diagnostics).ToArray();
		var states = array.Select(static item => item.State).Concat(diagnostics.Select(static item => item.State));
		return new(states.DefaultIfEmpty(InputValidationState.Valid).Max(), diagnostics);
	}
}
