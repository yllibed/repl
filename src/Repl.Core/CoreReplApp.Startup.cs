using Repl.Internal.Options;

namespace Repl;

public sealed partial class CoreReplApp
{
	private readonly Dictionary<Type, Func<IGlobalOptionsAccessor, object>> _globalOptionsFactories = [];
	private readonly List<Delegate> _startupHooks = [];
	private readonly AsyncLocal<PreparedReplStartup?> _preparedStartup = new();
	private readonly AsyncLocal<ProcessedInput?> _startupInput = new();
	private object? _startupConfiguration;

	/// <summary>Creates a core application using the declarations of a successful preparation.</summary>
	/// <param name="prepared">Prepared input whose configuration is imported.</param>
	/// <returns>An independent application.</returns>
	public static CoreReplApp Create(PreparedReplStartup prepared)
	{
		ArgumentNullException.ThrowIfNull(prepared);
		if (!prepared.CanContinue) { throw new InvalidOperationException("Complete terminal preparation before creating an application."); }
		var app = Create();
		prepared.Apply(app);
		return app;
	}

	/// <summary>Adds a startup hook, run once per run before dispatch, including help and completion.</summary>
	/// <param name="hook">Delegate receiving startup context, typed globals and available services.</param>
	/// <returns>The application.</returns>
	public CoreReplApp UseStartupHook(Delegate hook)
	{
		ArgumentNullException.ThrowIfNull(hook);
		_startupHooks.Add(hook);
		return this;
	}

	/// <summary>Runs prepared input without replaying its preparation callbacks.</summary>
	/// <param name="prepared">Input prepared for this application's configuration.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code.</returns>
	/// <exception cref="InvalidOperationException">Preparation is terminal or belongs to a different builder.</exception>
	public async ValueTask<int> RunAsync(PreparedReplStartup prepared, CancellationToken cancellationToken = default)
	{
		using var scope = PushPreparedStartup(prepared);
		return await RunAsync(prepared.Arguments.ToArray(), cancellationToken).ConfigureAwait(false);
	}

	internal void RegisterGlobalOptionsFactory(Type type, Func<IGlobalOptionsAccessor, object> factory) => _globalOptionsFactories[type] = factory;
	internal object? CreateGlobalOptions(Type type, IGlobalOptionsAccessor accessor) =>
		_globalOptionsFactories.TryGetValue(type, out var factory) ? factory(accessor) : null;
	internal IEnumerable<Type> GlobalOptionsTypes => _globalOptionsFactories.Keys;
	internal void SetStartupConfiguration(object key) => _startupConfiguration = key;
	internal bool IsStartupCompletion(IReadOnlyList<string> args) => IsCompletionBridge(args);
	internal bool HasStartupWork => _startupHooks.Count != 0 || _preparedStartup.Value is not null;

	internal IDisposable PushPreparedStartup(PreparedReplStartup prepared)
	{
		ArgumentNullException.ThrowIfNull(prepared);
		if (!prepared.CanContinue) { throw new InvalidOperationException("Complete terminal preparation instead of running it."); }
		if (!ReferenceEquals(_startupConfiguration, prepared.ConfigurationKey))
		{
			throw new InvalidOperationException("Create the application from preparation produced by the same builder.");
		}
		var previous = _preparedStartup.Value;
		_preparedStartup.Value = prepared;
		return new StartupScope(() => _preparedStartup.Value = previous);
	}

	internal async ValueTask<StartupInvocation> PrepareStartupAsync(string[] args, Delegate[] hooks, CancellationToken cancellationToken)
	{
		var input = new ProcessedInput(new(args, args, [], InputProcessingMode.Execution,
			IdentifyInputPurpose(args, InputProcessingMode.Execution, isInteractive: false), null, null, new InputSourceRange?[args.Length]), InputValidationResult.Valid());
		var invocation = new StartupInvocation(input);
		try
		{
			invocation.Input = await PrepareStartupInputAsync(args, prepared: null, cancellationToken).ConfigureAwait(false);
			await InvokeStartupHooksAsync(invocation, hooks, _services, preparation: true, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (!IsCallerCancellation(exception, cancellationToken)) { invocation.Failure = exception; }
		return invocation;
	}

	private async ValueTask<ProcessedInput> PrepareStartupInputAsync(IReadOnlyList<string> args, PreparedReplStartup? prepared, CancellationToken cancellationToken)
	{
		var mode = IsCompletionBridge(args) ? InputProcessingMode.ShellCompletion : InputProcessingMode.Execution;
		var input = await PreprocessInputAsync(args, [], mode, text: null, cursor: null, cancellationToken,
			previous: prepared?.Invocation.Input, skip: prepared?.AdapterCount ?? 0).ConfigureAwait(false);
		return ValidateStartupInput(input);
	}

	private ProcessedInput ValidateStartupInput(ProcessedInput input)
	{
		var globals = ParseInputGlobals(input);
		var checks = new List<InputValidationResult> { input.Validation };
		foreach (var diagnostic in globals.Diagnostics) { checks.Add(ToInputValidation(input, diagnostic)); }
		using var frame = _globalOptionsSnapshot.PushInputValues(globals.CustomGlobalNamedOptions, inheritSessionBaseline: false);
		ReadTypedGlobalValues(globals, checks);
		return input with { Validation = CombineInputValidation(checks) };
	}

	private async ValueTask InvokeStartupHooksAsync(StartupInvocation invocation, Delegate[] hooks,
		IServiceProvider services, bool preparation, CancellationToken cancellationToken)
	{
		foreach (var hook in hooks)
		{
			var input = invocation.Input;
			cancellationToken.ThrowIfCancellationRequested();
			if (input.IsHandled || input.Validation.State != InputValidationState.Valid) { break; }
			if (preparation) { ValidatePreparationParameters(hook); }
			var context = new ReplStartupContext(this, input);
			var globals = ParseInputGlobals(input);
			using var frame = _globalOptionsSnapshot.PushInputValues(globals.CustomGlobalNamedOptions, inheritSessionBaseline: false);
			var provider = new StartupServiceProvider(this, context, services);
			var binding = new InvocationBindingContext(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
				new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), [], OptionSchema.Empty,
				_options.Parsing.OptionCaseSensitivity, [context], _options.Parsing.NumericFormatProvider, provider,
				_options.Interaction, _implicitServiceParameters, cancellationToken);
			var result = await CommandInvoker.InvokeAsync(hook, HandlerArgumentBinder.Bind(hook, binding)).ConfigureAwait(false);
			var decision = result as ArgumentPreprocessingResult ?? throw new InvalidOperationException("Startup hooks must return ArgumentPreprocessingResult, Task<ArgumentPreprocessingResult> or ValueTask<ArgumentPreprocessingResult>.");
			var arguments = decision.Arguments ?? input.Context.Arguments;
			var rewritten = new ArgumentPreprocessingContext(input.Context.OriginalArguments, arguments, [], input.Context.Mode,
				IdentifyInputPurpose(arguments, input.Context.Mode, isInteractive: false), text: null, cursor: null, new InputSourceRange?[arguments.Count]);
			invocation.Input = ValidateStartupInput(new(rewritten, CombineInputValidation([input.Validation, decision.Validation]), decision.IsHandled, decision.Result));
		}
	}

	private void ValidatePreparationParameters(Delegate hook)
	{
		foreach (var parameter in hook.Method.GetParameters())
		{
			if ((parameter.ParameterType != typeof(ReplStartupContext) && parameter.ParameterType != typeof(CancellationToken)
				&& !_implicitServiceParameters.TryGetGlobalOptionsServiceType(parameter.ParameterType, out _))
				|| parameter.IsDefined(typeof(FromServicesAttribute), inherit: true))
			{
				throw new InvalidOperationException($"Preparation parameter '{parameter.Name}' must be startup context, registered typed global options or CancellationToken; application services are unavailable before composition.");
			}
		}
	}

	private async ValueTask<ExecutionOutcome> FinishStartupAsync(StartupInvocation invocation, CancellationToken cancellationToken)
	{
		if (invocation.Failure is { } exception)
		{
			var failure = Results.Error("startup_error", DescribeLocally(exception));
			return await RenderFailureAsync(failure, exception, bound: true, ParseInputGlobals(invocation.Input),
				CurrentServiceProvider, cancellationToken).ConfigureAwait(false);
		}
		await TryClearProgressAsync(CurrentServiceProvider).ConfigureAwait(false);
		return await TryFinishProcessedInputAsync(invocation.Input, cancellationToken).ConfigureAwait(false) ?? ExecutionOutcome.Success;
	}

	internal async ValueTask<int> CompleteStartupAsync(StartupInvocation invocation, CancellationToken cancellationToken)
	{
		using var runtime = PushRuntimeState(_services, isInteractiveSession: false);
		ExecutionOutcome outcome;
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			outcome = await FinishStartupAsync(invocation, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException ex) when (IsConvertibleCancellation(isSubInvocation: false, cancellationToken))
		{
			outcome = ExecutionOutcome.Cancelled(ex);
		}
		return ResolveProcessExitCode(outcome);
	}

	private async ValueTask<ExecutionOutcome> RunStartupOutcomeAsync(string[] args, IServiceProvider services,
		Func<CancellationToken, ValueTask>? beforeDispatch, CancellationToken cancellationToken)
	{
		if (!HasStartupWork && beforeDispatch is null)
		{
			return await RunUnderCancellationPolicyAsync(args, services, isSubInvocation: false, cancellationToken).ConfigureAwait(false);
		}
		var prepared = _preparedStartup.Value;
		_preparedStartup.Value = null;
		var previous = _startupInput.Value;
		_startupInput.Value = null;
		var started = false;
		using var runtime = PushRuntimeState(services, isInteractiveSession: false);
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			started = true;
			if (_startupHooks.Count != 0 || prepared is not null)
			{
				var invocation = await PrepareRunStartupAsync(args, prepared, services, cancellationToken).ConfigureAwait(false);
				if (invocation.Failure is not null || invocation.Input.IsHandled || invocation.Input.Validation.State != InputValidationState.Valid)
				{
					return await FinishStartupAsync(invocation, cancellationToken).ConfigureAwait(false);
				}
				_startupInput.Value = invocation.Input;
			}
			var globals = GlobalOptionParser.Parse(_startupInput.Value?.Context.Arguments ?? args, _options.Output, _options.Parsing);
			using var frame = _globalOptionsSnapshot.PushInputValues(globals.CustomGlobalNamedOptions, inheritSessionBaseline: false);
			if (beforeDispatch is not null) { await beforeDispatch(cancellationToken).ConfigureAwait(false); }
			return await RunUnderCancellationPolicyAsync(_startupInput.Value?.Context.Arguments ?? args, services,
				isSubInvocation: false, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException ex) when (IsConvertibleCancellation(isSubInvocation: false, cancellationToken)) { return ExecutionOutcome.Cancelled(ex); }
		finally
		{
			if (started) { await TryClearProgressAsync(services).ConfigureAwait(false); }
			_startupInput.Value = previous;
			_preparedStartup.Value = prepared;
		}
	}

	private async ValueTask<StartupInvocation> PrepareRunStartupAsync(string[] args, PreparedReplStartup? prepared,
		IServiceProvider services, CancellationToken cancellationToken)
	{
		var input = prepared?.Invocation.Input ?? new(new(args, args, [], InputProcessingMode.Execution, InputPurpose.Unknown,
			text: null, cursor: null, new InputSourceRange?[args.Length]), InputValidationResult.Valid());
		var invocation = new StartupInvocation(input);
		try
		{
			invocation.Input = await PrepareStartupInputAsync(args, prepared, cancellationToken).ConfigureAwait(false);
			await InvokeStartupHooksAsync(invocation, _startupHooks.ToArray(), services, preparation: false, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (!IsCallerCancellation(exception, cancellationToken))
		{
			invocation.Failure = exception;
		}
		return invocation;
	}

	private sealed class StartupServiceProvider(CoreReplApp app, ReplStartupContext context, IServiceProvider services) : IServiceProvider
	{
		public object? GetService(Type serviceType) => serviceType == typeof(IServiceProvider) ? this
			: serviceType == typeof(ReplStartupContext) ? null
			: serviceType == typeof(IGlobalOptionsAccessor) ? context.GlobalOptions
			: app.CreateGlobalOptions(serviceType, context.GlobalOptions) ?? services.GetService(serviceType);
	}

	private sealed class StartupScope(Action restore) : IDisposable
	{
		public void Dispose() => restore();
	}
}
