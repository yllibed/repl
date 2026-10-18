using System.Diagnostics.CodeAnalysis;
using Repl.Internal.Options;

namespace Repl;

public sealed partial class CoreReplApp
{
	internal Func<IServiceProvider, Type, bool>? InputServiceAvailability { get; set; }
	private readonly AsyncLocal<ProcessedInput?> _currentInput = new();

	internal IDisposable PushProcessedInput(ProcessedInput? input)
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
		string? text, int? cursor, CancellationToken cancellationToken, bool isInteractive = false,
		ProcessedInput? previous = null, int skip = 0, Action<ProcessedInput>? onProcessed = null)
	{
		var original = previous?.Context.OriginalArguments.ToArray() ?? arguments.ToArray();
		IReadOnlyList<string> effective = previous?.Context.Arguments ?? original;
		IReadOnlyList<InputSourceRange?> origins = text is null
			? new InputSourceRange?[original.Length]
			: InputTokenizer.Tokenize(text).Select(static token => (InputSourceRange?)new InputSourceRange(token.Start, token.Length)).ToArray();
		var checks = new List<InputValidationResult>();
		if (previous is not null) { checks.Add(previous.Validation); }
		var context = CreateContext();
		onProcessed?.Invoke(new(context, CombineInputValidation(checks)));
		foreach (var adapter in _options.Input.Preprocessors.Skip(skip).ToArray())
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
			var processed = new ProcessedInput(context, CombineInputValidation(checks), decision.IsHandled, decision.Result);
			onProcessed?.Invoke(processed);
			if (decision.IsHandled) { return processed; }
		}
		return new(context, CombineInputValidation(checks));

		ArgumentPreprocessingContext CreateContext() => new(
			original, effective, scopeTokens, mode, IdentifyInputPurpose(effective, mode, isInteractive), text, cursor, origins, isInteractive);
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

	private InputPurpose IdentifyInputPurpose(IReadOnlyList<string> arguments, InputProcessingMode mode, bool isInteractive)
	{
		if (mode is InputProcessingMode.Completion or InputProcessingMode.ShellCompletion) { return InputPurpose.Completion; }
		var globals = GlobalOptionParser.Parse(arguments, _options.Output, _options.Parsing);
		if (globals.HelpRequested || (isInteractive && arguments.Count != 0 && IsHelpToken(arguments[0])))
		{
			return InputPurpose.Help;
		}
		if (IsAmbientCompletion(globals) || IsCompletionBridge(arguments)) { return InputPurpose.Completion; }
		return arguments.Count == 0 ? InputPurpose.Unknown : InputPurpose.Command;
	}

	private async ValueTask<ExecutionOutcome> ExecuteCoreOutcomeAsync(
		IReadOnlyList<string> args, IServiceProvider serviceProvider, bool isSubInvocation,
		CancellationToken cancellationToken, IServiceProvider? presenceServiceProvider = null)
	{
		// Structured adapters retain their native invocation contract; #115 owns their interception.
		if ((!_options.Input.IsEnabled && _startupInput.Value is null) || ReplSessionIO.IsProgrammatic)
		{
			using var inputScope = PushProcessedInput(input: null);
			return await ExecuteUnprocessedCoreOutcomeAsync(args, serviceProvider, isSubInvocation,
				cancellationToken, presenceServiceProvider).ConfigureAwait(false);
		}
		var previous = _currentInput.Value;
		using var runtimeScope = PushRuntimeState(serviceProvider, isInteractiveSession: false, presenceServiceProvider);
		try
		{
			if (IsCompletionBridge(args))
			{
				using var inputScope = PushProcessedInput(input: null);
				return await ExecuteUnprocessedCoreOutcomeAsync(args, serviceProvider, isSubInvocation,
					cancellationToken, presenceServiceProvider).ConfigureAwait(false);
			}
			var input = !isSubInvocation && _startupInput.Value is { } startup ? startup
				: await PreprocessInputAsync(args, [], InputProcessingMode.Execution, text: null, cursor: null, cancellationToken).ConfigureAwait(false);
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
					var globals = ParseInputGlobals(input);
					return await RefuseInputAsync(help.ApplicationValidation, globals.OutputFormat, cancellationToken).ConfigureAwait(false);
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

	private bool IsCompletionBridge(IReadOnlyList<string> arguments)
	{
		var globals = GlobalOptionParser.Parse(arguments, _options.Output, _options.Parsing);
		if (globals.HelpRequested || globals.RemainingTokens.Count < 2) { return false; }
		return string.Equals(globals.RemainingTokens[0], "completion", StringComparison.OrdinalIgnoreCase)
			&& string.Equals(globals.RemainingTokens[1], "__complete", StringComparison.OrdinalIgnoreCase);
	}

	private GlobalInvocationOptions ParseInputGlobals(ProcessedInput input) =>
		GlobalOptionParser.Parse([.. input.Context.ScopeTokens, .. input.Context.Arguments], _options.Output, _options.Parsing);

	internal async ValueTask<ExecutionOutcome?> TryFinishProcessedInputAsync(ProcessedInput input, CancellationToken cancellationToken)
	{
		if (!input.IsHandled && input.Validation.State == InputValidationState.Valid) { return null; }
		var globals = ParseInputGlobals(input);
		if (input.Validation.State != InputValidationState.Valid)
		{
			return await RefuseInputAsync(input.Validation, globals.OutputFormat, cancellationToken).ConfigureAwait(false);
		}
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

	private ValueTask<ExecutionOutcome> RefuseInputAsync(InputValidationResult validation, string? requestedFormat, CancellationToken cancellationToken) =>
		RefuseAsync(Results.Validation(validation.Diagnostics.Count != 0 ? validation.Diagnostics[0].Message : "Input validation failed."),
			requestedFormat, cancellationToken);

	internal async ValueTask<ParsedInputContext> AnalyzeInputAsync(
		ProcessedInput input, IServiceProvider services, CancellationToken cancellationToken)
	{
		var globals = GlobalOptionParser.Parse(
			[.. input.Context.ScopeTokens, .. input.Context.Arguments], _options.Output, _options.Parsing);
		using var globalsScope = _globalOptionsSnapshot.PushInputValues(globals.CustomGlobalNamedOptions);
		var context = CreateParsedInputContext(input, services, globals, match: null, parsedOptions: null);
		return await ValidateParsedInputAsync(context, services, cancellationToken).ConfigureAwait(false);
	}

	private ParsedInputContext CreateParsedInputContext(ProcessedInput input, IServiceProvider services,
		GlobalInvocationOptions globals, RouteMatch? match, OptionParsingResult? parsedOptions, InvocationBindingContext? executionBinding = null)
	{
		var checks = new List<InputValidationResult> { input.Validation };
		foreach (var diagnostic in globals.Diagnostics) { checks.Add(ToInputValidation(input, diagnostic)); }
		if (match is null && !input.IsHandled
			&& !(input.Context.IsInteractive && Interactive.IsAmbientCommandInvocation(input.Context.Arguments)))
		{
			match = ResolveInputRoute(input, globals, checks);
		}
		var values = new Dictionary<string, InputValue>(StringComparer.OrdinalIgnoreCase);
		if (match is not null)
		{
			parsedOptions ??= InvocationOptionParser.Parse(match.RemainingTokens, match.Route.OptionSchema,
				BuildEffectiveCommandParsingOptions(globals.GlobalOptionConfiguration.CaseSensitivity), globals.CustomGlobalTokenOwnership);
			foreach (var diagnostic in parsedOptions.Diagnostics) { checks.Add(ToInputValidation(input, diagnostic, match.Route.OptionSchema)); }
			ReadTypedInputValues(input, match, parsedOptions, services, values, checks, executionBinding);
		}
		var globalValues = ReadTypedGlobalValues(globals, checks);
		if (input.Context.Text is { } text && InputTokenizer.Tokenize(text).Exists(static token => token.IsIncomplete))
		{
			checks.Add(InputValidationResult.Incomplete("Close the quoted argument."));
		}
		return new(input.Context, match?.Route.Template.Template, values, globalValues, CombineInputValidation(checks))
		{
			ApplicationValidation = input.Validation,
		};
	}

	private async ValueTask<ParsedInputContext> ValidateParsedInputAsync(ParsedInputContext context,
		IServiceProvider services, CancellationToken cancellationToken)
	{
		var checks = new List<InputValidationResult> { context.Validation };
		var applicationChecks = new List<InputValidationResult> { context.ApplicationValidation };
		foreach (var validator in _options.Input.Validators.ToArray())
		{
			cancellationToken.ThrowIfCancellationRequested();
			var result = await InvokeInputValidatorAsync(validator, context, services, cancellationToken).ConfigureAwait(false);
			var validation = result as InputValidationResult
				?? throw new InvalidOperationException("Input validators must return InputValidationResult, Task<InputValidationResult> or ValueTask<InputValidationResult>.");
			checks.Add(validation);
			applicationChecks.Add(validation);
			context = new(context, CombineInputValidation(checks), CombineInputValidation(applicationChecks));
		}
		return context;
	}

	private RouteMatch? ResolveInputRoute(ProcessedInput input, GlobalInvocationOptions globals, List<InputValidationResult> checks)
	{
		var graph = ResolveActiveRoutingGraph(useDurableCache: false);
		var prefix = ResolveUniquePrefixes(globals.RemainingTokens, graph);
		var resolution = ResolveWithDiagnostics(prefix.Tokens, graph.Routes);
		if (resolution.Match is null && input.Context.Purpose == InputPurpose.Command)
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
		return resolution.Match;
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

	private void ReadTypedInputValues(ProcessedInput input, RouteMatch match, OptionParsingResult parsed, IServiceProvider services,
		Dictionary<string, InputValue> values, List<InputValidationResult> checks, InvocationBindingContext? executionBinding)
	{
		ReadTypedRouteValues(input, match, values, checks);
		var positionalIndex = 0;
		var positionalUncertain = false;
		foreach (var parameter in match.Route.OptionSchema.Parameters.Values)
		{
			IReadOnlyList<string>? raw = null;
			var rawStart = 0;
			int? rawCount = null;
			var missing = false;
			try
			{
				if (InputValueSelector.TrySelectNamed(parameter.Name, parameter.Mode, parsed.NamedOptions,
					parsed.PositionalArguments, positionalUncertain ? parsed.PositionalArguments.Count : positionalIndex, out var named)) { raw = named; }
				else
				{
					var fallback = ResolveInputServiceFallback(parameter, match.Route.Command.Handler, services, executionBinding);
					if (fallback is not false)
					{
						if (fallback is null)
						{
							positionalUncertain |= DeferInputServiceFallback(parameter, positionalIndex < parsed.PositionalArguments.Count, checks);
						}
						continue;
					}
					if (positionalUncertain && parameter.Mode != ReplParameterMode.OptionOnly) { continue; }
					if (parameter.Mode != ReplParameterMode.OptionOnly && positionalIndex < parsed.PositionalArguments.Count)
					{
						var selected = InputValueSelector.SelectPositional(parameter.ParameterType, parsed.PositionalArguments, positionalIndex);
						raw = parsed.PositionalArguments;
						rawStart = selected.Start;
						rawCount = selected.Count;
						HandlerArgumentBinder.ThrowIfExplicitUpperBoundExceeded(match.Route.OptionSchema, parameter.Name, selected.Count);
						positionalIndex += selected.Count;
					}
				}
				if (raw is null)
				{
					missing = true;
					HandlerArgumentBinder.ThrowIfExplicitLowerBoundUnsatisfied(match.Route.OptionSchema, parameter.Name);
				}
			}
			catch (InvalidOperationException exception) when (exception is not ReplBindingCallbackException)
			{
				values[parameter.Name] = new(missing ? InputValueState.Missing : InputValueState.Invalid);
				var state = missing && input.Context.Mode != InputProcessingMode.Execution
					? InputValidationState.Incomplete : InputValidationState.Invalid;
				checks.Add(new(state, [new(state, exception.Message)]));
				continue;
			}
			if (raw is null)
			{
				AddMissingTypedInputValue(parameter, match.Route.Command.Handler, values, checks);
				continue;
			}
			AddTypedInputValue(input, parameter.Name, raw, parameter.ParameterType, values, checks,
				(parameter.CaseSensitivity ?? _options.Parsing.OptionCaseSensitivity) == ReplCaseSensitivity.CaseInsensitive, rawStart, rawCount);
		}
	}

	private static bool DeferInputServiceFallback(OptionSchemaParameter parameter, bool hasPositionals,
		List<InputValidationResult> checks)
	{
		var positionalUncertain = parameter.Mode != ReplParameterMode.OptionOnly && hasPositionals;
		if (positionalUncertain || (!parameter.CanBeOmitted && IsInputScalar(parameter.ParameterType))
			|| parameter.ExplicitArity is ReplArity.OneOrMore or ReplArity.ExactlyOne)
		{
			checks.Add(InputValidationResult.Incomplete("Input values depend on service resolution at submission."));
		}
		return positionalUncertain;
	}

	private static void AddMissingTypedInputValue(OptionSchemaParameter parameter, Delegate handler,
		Dictionary<string, InputValue> values, List<InputValidationResult> checks)
	{
		var metadata = handler.Method.GetParameters()
			.FirstOrDefault(item => string.Equals(item.Name, parameter.Name, StringComparison.Ordinal));
		values[parameter.Name] = metadata?.HasDefaultValue == true
			? new(InputValueState.Available, metadata.DefaultValue) : new(InputValueState.Missing);
		if (!parameter.CanBeOmitted && IsInputScalar(parameter.ParameterType))
		{
			checks.Add(InputValidationResult.Incomplete($"Supply a value for '{parameter.Name}'."));
		}
	}

	private bool? ResolveInputServiceFallback(OptionSchemaParameter parameter, Delegate handler, IServiceProvider services,
		InvocationBindingContext? executionBinding)
	{
		if (!parameter.SupportsServiceFallback) { return false; }
		if (executionBinding is null)
		{
			return IsInputServiceAvailable(parameter.ParameterType, services) ? null : false;
		}
		var metadata = handler.Method.GetParameters().First(item => string.Equals(item.Name, parameter.Name, StringComparison.Ordinal));
		return HandlerArgumentBinder.TryResolveInputServiceFallback(metadata, executionBinding, out _);
	}

	private bool IsInputServiceAvailable(Type type, IServiceProvider services)
	{
		if (InteractionProgressFactory.IsSupportedProgressType(type) &&
			IsInputServiceAvailable(typeof(IReplInteractionChannel), services)) { return true; }
		return InputServiceAvailability?.Invoke(services, type)
			?? (services is DefaultServiceProvider && services.GetService(type) is not null);
	}

	private static bool IsInputScalar(Type type) => type.IsValueType || type == typeof(string)
		|| type == typeof(Uri) || type == typeof(FileInfo) || type == typeof(DirectoryInfo);

	private void ReadTypedRouteValues(ProcessedInput input, RouteMatch match, Dictionary<string, InputValue> values,
		List<InputValidationResult> checks)
	{
		foreach (var parameter in match.Route.Command.Handler.Method.GetParameters())
		{
			if (parameter.Name is { } name && match.Values.TryGetValue(name, out var raw))
			{
				AddTypedInputValue(input, name, [raw], parameter.ParameterType, values, checks,
					_options.Parsing.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive);
			}
		}
	}

	private void AddTypedInputValue(ProcessedInput input, string name, IReadOnlyList<string> raw, Type type,
		Dictionary<string, InputValue> values, List<InputValidationResult> checks, bool enumIgnoreCase,
		int start = 0, int? count = null)
	{
		var length = count ?? raw.Count;
		var first = length == 0 ? null : raw[start];
		try
		{
			var converted = HandlerArgumentBinder.TryGetCollectionElementType(type, out var elementType)
				? HandlerArgumentBinder.ConvertMany(start == 0 && length == raw.Count ? raw : raw.Skip(start).Take(length).ToArray(),
					type, elementType, _options.Parsing.NumericFormatProvider, enumIgnoreCase)
				: ParameterValueConverter.ConvertSingle(first, type,
					_options.Parsing.NumericFormatProvider, enumIgnoreCase);
			values[name] = new(InputValueState.Available, converted);
		}
		catch (Exception exception) when (IsInputConversionFailure(exception))
		{
			values[name] = new(InputValueState.Invalid);
			var incomplete = input.Context.Mode != InputProcessingMode.Execution && length == 1 && first is not null
				&& IsIncompleteValue(first, type, enumIgnoreCase);
			var state = incomplete ? InputValidationState.Incomplete : InputValidationState.Invalid;
			checks.Add(new(state, [new(state, $"Invalid value for '{name}': {exception.Message}", FindInputRange(input, first))]));
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
		GlobalInvocationOptions globals, OptionParsingResult parsed, IServiceProvider services,
		InvocationBindingContext executionBinding, CancellationToken cancellationToken)
	{
		if (_currentInput.Value is not { } input || _options.Input.Validators.Count == 0) { return null; }
		using var globalsScope = _globalOptionsSnapshot.PushInputValues(globals.CustomGlobalNamedOptions);
		// Fallback resolution belongs to binding; only validator invocation uses the callback-error policy.
		var context = CreateParsedInputContext(input, services, globals, match, parsed, executionBinding);
		try
		{
			context = await ValidateParsedInputAsync(context, services, cancellationToken).ConfigureAwait(false);
			return context.Validation.State == InputValidationState.Valid ? null
				: await RefuseInputAsync(context.Validation, globals.OutputFormat, cancellationToken).ConfigureAwait(false);
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
