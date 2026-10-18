using Repl.Internal.Options;
using System.Diagnostics.CodeAnalysis;

namespace Repl;

public sealed partial class CoreReplApp
{
	private readonly AsyncLocal<ReplInvocationRequest?> _submittedRequest = new();
	private readonly AsyncLocal<ReplInvocationChannel?> _submittedChannel = new();
	private readonly AsyncLocal<string[]?> _invocationTransport = new();

	private sealed class PendingInvocationScope : IDisposable
	{
		private readonly CoreReplApp _owner;
		private readonly string[]? _transport;
		private readonly ProcessedInput? _startupInput;
		internal PendingInvocationScope(CoreReplApp owner, bool newRun)
		{
			_owner = owner;
			Request = owner._submittedRequest.Value;
			Channel = owner._submittedChannel.Value;
			_transport = owner._invocationTransport.Value;
			_startupInput = owner._startupInput.Value;
			owner._submittedRequest.Value = null;
			owner._submittedChannel.Value = null;
			if (newRun) { owner._invocationTransport.Value = null; owner._startupInput.Value = null; }
		}
		internal ReplInvocationRequest? Request { get; }
		internal ReplInvocationChannel? Channel { get; }
		public void Dispose()
		{
			_owner._submittedRequest.Value = Request;
			_owner._submittedChannel.Value = Channel;
			_owner._invocationTransport.Value = _transport;
			_owner._startupInput.Value = _startupInput;
		}
	}

	private ProcessedInput CreateSubmittedInput(IReadOnlyList<string> args, ReplInvocationRequest request) =>
		new(new(args, args, [], InputProcessingMode.Execution, IdentifyInputPurpose(args, InputProcessingMode.Execution, isInteractive: false),
			text: null, cursor: null, new InputSourceRange?[args.Count]), InputValidationResult.Valid()) { Request = request.Copy(), OriginalRequest = request.Copy() };


	/// <summary>Adds an ordered hook after preliminary parsing and before final validation.</summary>
	/// <param name="hook">Delegate receiving context, command values, options, services or cancellation.</param>
	/// <returns>The application.</returns>
	public CoreReplApp UseInvocationHook(Delegate hook)
	{
		ArgumentNullException.ThrowIfNull(hook);
		_options.Input.InvocationHooks.Add(hook);
		return this;
	}

	/// <summary>Runs a textual or native structured request using the ordinary execution pipeline.</summary>
	/// <param name="request">Invocation target and values.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code.</returns>
	public async ValueTask<int> RunAsync(ReplInvocationRequest request, CancellationToken cancellationToken = default)
	{
		using var scope = PushInvocationRequest(request, ReplInvocationChannel.Programmatic);
		return await RunAsync(GetInvocationArguments(request), cancellationToken).ConfigureAwait(false);
	}

	internal IDisposable PushInvocationRequest(ReplInvocationRequest request, ReplInvocationChannel channel)
	{
		ArgumentNullException.ThrowIfNull(request);
		var previous = _submittedRequest.Value;
		var previousChannel = _submittedChannel.Value;
		var previousTransport = _invocationTransport.Value;
		_submittedRequest.Value = request.Copy();
		_submittedChannel.Value = channel;
		_invocationTransport.Value = null;
		return new StartupScope(() => { _submittedRequest.Value = previous; _submittedChannel.Value = previousChannel; _invocationTransport.Value = previousTransport; });
	}

	internal string[] GetInvocationArguments(ReplInvocationRequest request)
	{
		var transport = _invocationTransport.Value ?? [];
		if (request.Command is null) { return [.. request.Arguments, .. transport]; }
		// Only routing metadata and globals enter the token pipeline. Native command values
		// are resolved against the declared route and bound directly, including empty collections.
		return [.. GetInvocationGlobalArguments(request),
			.. request.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries), .. transport,];
	}

	private IEnumerable<string> GetInvocationGlobalArguments(ReplInvocationRequest request)
	{
		foreach (var pair in request.GlobalValues)
		{
			if (pair.Key is "help" or "interactive" or "no-interactive" or "no-logo")
			{
				if (!TryReadInvocationFlag(pair.Value, out var flag)) { yield return $"--{pair.Key}={FormatInvocationScalar(pair.Value)}"; }
				else if (flag) { yield return $"--{pair.Key}"; }
			}
			else if (string.Equals(pair.Key, "output", StringComparison.Ordinal)) { yield return $"--output:{FormatInvocationScalar(pair.Value)}"; }
			else { yield return $"--{pair.Key}={FormatInvocationScalar(pair.Value)}"; }
		}
	}

	async ValueTask<SubInvocationOutcome> ISubInvocableReplApp.RunSubInvocationWithOutcomeAsync(
		ReplInvocationRequest request, string[] transportArguments, IServiceProvider serviceProvider,
		IServiceProvider? presenceServiceProvider, CancellationToken cancellationToken)
	{
		using var scope = PushInvocationRequest(request, ReplInvocationChannel.Mcp);
		var previous = _invocationTransport.Value;
		_invocationTransport.Value = transportArguments.ToArray();
		try
		{
			var outcome = await RunUnderCancellationPolicyAsync(GetInvocationArguments(request), serviceProvider,
				isSubInvocation: true, cancellationToken, presenceServiceProvider).ConfigureAwait(false);
			return new(ResolveProcessExitCode(outcome, isSubInvocation: true), outcome.Kind, outcome.Exception);
		}
		finally { _invocationTransport.Value = previous; }
	}

	internal async ValueTask<ProcessedInput> InvokeInvocationHooksAsync(ProcessedInput input,
		IServiceProvider services, CancellationToken cancellationToken)
	{
		if (input.IsHandled || input.Validation.State != InputValidationState.Valid) { return input; }
		var request = input.Request ?? ReplInvocationRequest.FromArguments(input.Context.Arguments);
		var original = input.OriginalRequest?.Copy() ?? input.Request?.Copy() ?? ReplInvocationRequest.FromArguments(input.Context.OriginalArguments);
		foreach (var hook in _options.Input.InvocationHooks.ToArray())
		{
			cancellationToken.ThrowIfCancellationRequested();
			using var inputScope = PushProcessedInput(input);
			var globals = ParseInputGlobals(input);
			using var globalsScope = _globalOptionsSnapshot.PushInputValues(globals.CustomGlobalNamedOptions, inheritSessionBaseline: input.InheritSessionGlobals);
			ParsedInputContext parsed;
			try { parsed = CreateParsedInputContext(input, services, globals, match: null, parsedOptions: null, invocationHook: hook, cancellationToken: cancellationToken); }
			catch (ReplBindingCallbackException failure) { return input with { IsHandled = true, BindingFailure = failure }; }
			var channel = input.Channel ?? (ReplSessionIO.IsProgrammatic ? ReplInvocationChannel.Mcp
				: input.Context.IsInteractive ? ReplSessionIO.IsHostedSession ? ReplInvocationChannel.HostedInteractive : ReplInvocationChannel.Interactive
				: ReplInvocationChannel.Cli);
			var context = new ReplInvocationContext(original, request, parsed, channel);
			if (FindUnavailableHookValue(hook, parsed, services) is { } unavailable)
			{
				return input with { Validation = InputValidationResult.Invalid($"Cannot bind invocation hook input '{unavailable}'. Inspect ReplInvocationContext to handle partial or invalid input.") };
			}
			var result = await CommandInvoker.InvokeAsync(hook, BindInvocationHookArguments(hook, context, parsed, services, cancellationToken)).ConfigureAwait(false);
			var decision = result as ReplInvocationResult ?? throw new InvalidOperationException(
				"Invocation hooks must return ReplInvocationResult, Task<ReplInvocationResult> or ValueTask<ReplInvocationResult>.");
			if (decision.IsHandled) { return input with { IsHandled = true, Result = decision.Result }; }
			if (decision.Request is { } replacement)
			{
				request = replacement.Copy();
				var args = GetInvocationArguments(request);
				var previous = input.Context;
				input = new(new(previous.OriginalArguments, args, request.Command is null ? previous.ScopeTokens : [], previous.Mode,
					IdentifyInputPurpose(args, previous.Mode, previous.IsInteractive), previous.Text, previous.Cursor,
					new InputSourceRange?[args.Length], previous.IsInteractive), InputValidationResult.Valid())
				{
					Request = request, OriginalRequest = original, Channel = input.Channel, InheritSessionGlobals = input.InheritSessionGlobals,
				};
			}
		}
		return _options.Input.InvocationHooks.Count != 0 || input.Request?.Command is not null ? ValidateInvocationGlobals(input) : input;
	}

	private object?[] BindInvocationHookArguments(Delegate hook, ReplInvocationContext context, ParsedInputContext parsed,
		IServiceProvider services, CancellationToken cancellationToken)
	{
		var provider = new InvocationServiceProvider(this, services);
		var binding = new InvocationBindingContext(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
			[], OptionSchema.Empty, _options.Parsing.OptionCaseSensitivity, [context, parsed], _options.Parsing.NumericFormatProvider,
			provider, _options.Interaction, _implicitServiceParameters, cancellationToken)
		{
			KeyedServiceProvider = services,
			NativeValues = parsed.Values.Where(static pair => pair.Value.State == InputValueState.Available)
				.ToDictionary(static pair => pair.Key, static pair => pair.Value.Value, StringComparer.OrdinalIgnoreCase),
		};
		return HandlerArgumentBinder.Bind(hook, binding);
	}

	private ProcessedInput ValidateInvocationGlobals(ProcessedInput input)
	{
		using var inputScope = PushProcessedInput(input);
		var globals = ParseInputGlobals(input);
		using var frame = _globalOptionsSnapshot.PushInputValues(globals.CustomGlobalNamedOptions, inheritSessionBaseline: input.InheritSessionGlobals);
		var checks = new List<InputValidationResult> { input.Validation };
		foreach (var diagnostic in globals.Diagnostics) { checks.Add(ToInputValidation(input, diagnostic)); }
		ReadTypedGlobalValues(globals, checks);
		return input with { Validation = CombineInputValidation(checks) };
	}

	private void ReadNativeInvocationFlags(Dictionary<string, InputValue> values, List<InputValidationResult> checks)
	{
		if (_currentInput.Value?.Request is not { Command: not null } request) { return; }
		foreach (var pair in request.GlobalValues)
		{
			if (pair.Key is not ("help" or "interactive" or "no-interactive" or "no-logo")) { continue; }
			if (TryReadInvocationFlag(pair.Value, out var flag)) { values[pair.Key] = new(InputValueState.Available, flag); }
			else
			{
				values[pair.Key] = new(InputValueState.Invalid);
				checks.Add(InputValidationResult.Invalid($"Invalid boolean global '{pair.Key}'."));
			}
		}
	}

	private bool TryReadInvocationFlag(object? value, out bool flag)
	{
		try
		{
			if (ParameterValueConverter.ConvertNative(value, typeof(bool), _options.Parsing.NumericFormatProvider) is bool converted)
			{
				flag = converted;
				return true;
			}
			flag = false;
			return false;
		}
		catch (Exception exception) when (IsInputConversionFailure(exception)) { flag = false; return false; }
	}

	[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Options-group properties are preserved by the registered command handler and group binding contract.")]
	private string? FindUnavailableHookValue(Delegate hook, ParsedInputContext parsed, IServiceProvider services)
	{
		if (FindUnavailableHookGlobal(hook, parsed.GlobalValues) is { } global) { return global; }
		foreach (var parameter in hook.Method.GetParameters())
		{
			if (parameter.ParameterType == typeof(ReplInvocationContext) || parameter.ParameterType == typeof(ParsedInputContext)
				|| parameter.ParameterType == typeof(CancellationToken)) { continue; }
			if (parameter.IsDefined(typeof(FromServicesAttribute), inherit: true)
				|| parameter.IsDefined(typeof(FromContextAttribute), inherit: true)
				|| _implicitServiceParameters.TryGetGlobalOptionsServiceType(parameter.ParameterType, out _)) { continue; }
			if (parameter.Name is { } name && parsed.Values.TryGetValue(name, out var value)
				&& (value.State == InputValueState.Invalid || (value.State == InputValueState.Missing && !parameter.HasDefaultValue))) { return name; }
			if (IsInputScalar(parameter.ParameterType) && !parameter.HasDefaultValue
				&& !parsed.Values.ContainsKey(parameter.Name ?? string.Empty) && !IsInputServiceAvailable(parameter.ParameterType, services)) { return parameter.Name; }
			if (parameter.ParameterType.IsDefined(typeof(ReplOptionsGroupAttribute), inherit: true))
			{
				foreach (var property in parameter.ParameterType.GetProperties())
				{
					if (!parsed.Values.TryGetValue(property.Name, out var propertyValue)) { continue; }
					if (propertyValue.State == InputValueState.Invalid
						|| (propertyValue.State == InputValueState.Missing
							&& parsed.OptionSchema.TryGetParameter(property.Name, out var metadata)
							&& metadata.ExplicitArity is ReplArity.ExactlyOne or ReplArity.OneOrMore)) { return property.Name; }
				}
			}
		}
		return null;
	}

	private string? FindUnavailableHookGlobal(Delegate hook, IReadOnlyDictionary<string, InputValue> values)
	{
		foreach (var parameter in hook.Method.GetParameters())
		{
			if (parameter.IsDefined(typeof(FromServicesAttribute), inherit: true) || parameter.IsDefined(typeof(FromContextAttribute), inherit: true)
				|| !_implicitServiceParameters.TryGetGlobalOptionsServiceType(parameter.ParameterType, out var globalType)) { continue; }
			foreach (var definition in _options.Parsing.GlobalOptions.Values)
			{
				if (definition.OwnerType == globalType && values.TryGetValue(definition.Name, out var value)
					&& value.State == InputValueState.Invalid) { return definition.Name; }
			}
		}
		return null;
	}

	private sealed class InvocationServiceProvider(CoreReplApp app, IServiceProvider services) : IServiceProvider
	{
		public object? GetService(Type serviceType) => serviceType == typeof(IServiceProvider) ? this
			: serviceType == typeof(IGlobalOptionsAccessor) ? app.GlobalOptionsAccessor
			: app.CreateGlobalOptions(serviceType, app.GlobalOptionsAccessor) ?? services.GetService(serviceType);
	}

	private RouteResolver.RouteResolutionResult ResolveInvocationRoute(IReadOnlyList<string> tokens, IReadOnlyList<RouteDefinition> routes)
	{
		if (_currentInput.Value?.Request is not { Command: { } command } request)
		{
			return RouteResolver.ResolveWithDiagnostics(routes, tokens, _options.Parsing);
		}
		var route = routes.FirstOrDefault(candidate => string.Equals(candidate.Template.Template, command, StringComparison.Ordinal));
		if (route is null) { return new(Match: null, ConstraintFailure: null, MissingArgumentsFailure: null); }
		var routeTokens = new List<string>();
		foreach (var segment in route.Template.Segments)
		{
			if (segment is DynamicRouteSegment dynamic)
			{
				if (!request.Values.TryGetValue(dynamic.Name, out var value)) { break; }
				routeTokens.Add(FormatInvocationScalar(value));
			}
			else { routeTokens.Add(segment.RawText); }
		}
		return RouteResolver.ResolveWithDiagnostics([route], routeTokens, _options.Parsing);
	}

	private string FormatInvocationScalar(object? value)
	{
		if (value is System.Text.Json.JsonElement json)
		{
			value = json.ValueKind switch
			{
				System.Text.Json.JsonValueKind.Number => json.TryGetDecimal(out var number) ? number : json.GetDouble(),
				System.Text.Json.JsonValueKind.String => json.GetString(),
				_ => json.ToString(),
			};
		}
		return Convert.ToString(value, _options.Parsing.NumericFormatProvider) ?? string.Empty;
	}

	private OptionParsingResult ParseInvocationOptions(RouteMatch match, ParsingOptions parsing, GlobalInvocationOptions globals)
	{
		if (_currentInput.Value?.Request is not { Command: not null } request)
		{
			return InvocationOptionParser.Parse(match.RemainingTokens, match.Route.OptionSchema, parsing, globals.CustomGlobalTokenOwnership);
		}
		var named = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
		var diagnostics = new List<ParseDiagnostic>();
		foreach (var key in request.GlobalValues.Keys)
		{
			if (!globals.GlobalOptionConfiguration.Definitions.ContainsKey(key)
				&& key is not ("help" or "interactive" or "no-interactive" or "no-logo" or "output" or "result:cursor" or "result:page-size" or "result:pager" or "result:all"))
			{
				diagnostics.Add(new(ParseDiagnosticSeverity.Error, $"Unknown invocation global '{key}'."));
			}
		}
		foreach (var pair in request.Values)
		{
			if (match.Values.ContainsKey(pair.Key)) { continue; }
			if (!match.Route.OptionSchema.TryGetParameter(pair.Key, out _))
			{
				diagnostics.Add(new(ParseDiagnosticSeverity.Error, $"Unknown invocation parameter '{pair.Key}'."));
				continue;
			}
			// Presence is consumed by shared typed readers; the binder uses NativeValues.
			named[pair.Key] = [];
		}
		return new(named, [], diagnostics);
	}
}
