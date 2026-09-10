using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Repl.Documentation;

namespace Repl;

/// <summary>
/// DI-enabled REPL application facade for common hosting scenarios.
/// </summary>
public sealed class ReplApp : IReplApp
{
	private readonly CoreReplApp _core;
	private readonly IServiceCollection _services;

	// Lazily built provider shared between MapModule<T>() and Run().
	// Ensures modules resolved via DI share the same service instances
	// as handler parameters resolved at runtime.
	private ServiceProvider? _sharedProvider;
	private ProcessSignalHandlingMode _defaultProcessSignalHandling = ProcessSignalHandlingMode.None;

	// Extension packages (e.g. Repl.Spectre) park per-app configuration here so it stays
	// reachable even when the shared provider was materialized before the Use* call —
	// service descriptors added after BuildServiceProvider are silently invisible.
	private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> _extensionState = new();

	internal IServiceCollection ServiceDescriptors => _services;

	internal void SetDefaultProcessSignalHandling(ProcessSignalHandlingMode mode) =>
		_defaultProcessSignalHandling = mode;

	internal void SetExtensionState<T>(T value) where T : class => _extensionState[typeof(T)] = value;

	internal T? GetExtensionState<T>() where T : class =>
		_extensionState.TryGetValue(typeof(T), out var value) ? value as T : null;

	private ReplApp(IServiceCollection services)
	{
		_services = services;
		_core = CoreReplApp.Create();
		EnsureDefaultServices(_services, _core);
		// The app registers itself so service factories (e.g. the Spectre console factory)
		// can reach per-app state regardless of registration/materialization ordering.
		_services.TryAddSingleton(this);
		_core.Use(ReplLoggingMiddleware.InvokeAsync);
	}

	/// <summary>
	/// Creates a DI-enabled REPL application.
	/// </summary>
	/// <returns>A new <see cref="ReplApp"/> instance.</returns>
	public static ReplApp Create() => new(new ServiceCollection());

	/// <summary>
	/// Creates a DI-enabled REPL application and configures services.
	/// </summary>
	/// <param name="configureServices">Service registration callback.</param>
	/// <returns>A new <see cref="ReplApp"/> instance.</returns>
	public static ReplApp Create(Action<IServiceCollection> configureServices)
	{
		ArgumentNullException.ThrowIfNull(configureServices);
		var services = new ServiceCollection();
		configureServices(services);
		return new ReplApp(services);
	}

	/// <summary>
	/// Sets an application description for discovery and banner usage.
	/// </summary>
	public ReplApp WithDescription(string text)
	{
		_core.WithDescription(text);
		return this;
	}

	/// <summary>
	/// Registers a banner delegate rendered at startup after the header line.
	/// </summary>
	public ReplApp WithBanner(Delegate bannerProvider)
	{
		_core.WithBanner(bannerProvider);
		return this;
	}

	/// <summary>
	/// Registers a static banner string rendered at startup after the header line.
	/// </summary>
	public ReplApp WithBanner(string text)
	{
		_core.WithBanner(text);
		return this;
	}

	/// <summary>
	/// Registers middleware in the execution pipeline.
	/// </summary>
	public ReplApp Use(Func<ReplExecutionContext, ReplNext, ValueTask> middleware)
	{
		_core.Use(middleware);
		return this;
	}

	/// <summary>
	/// Configures application options.
	/// </summary>
	public ReplApp Options(Action<ReplOptions> configure)
	{
		_core.Options(configure);
		return this;
	}

	/// <summary>
	/// Invalidates active routing cache so module presence predicates are re-evaluated on next resolution.
	/// </summary>
	public void InvalidateRouting() => _core.InvalidateRouting();

	/// <inheritdoc />
	public ReplDocumentationModel CreateDocumentationModel(string? targetPath = null)
	{
		if (_sharedProvider is { } sharedProvider)
		{
			return _core.CreateDocumentationModel(sharedProvider, targetPath);
		}

		// Documentation needs the configured services for provider-aware requiredness, but it
		// must not finalize the app's mutable registration phase. A short-lived provider gives
		// discovery the current descriptors while leaving Run free to build the shared provider
		// after later Use* extensions have registered their services.
		using var discoveryProvider = _services.BuildServiceProvider();
		return _core.CreateDocumentationModel(discoveryProvider, targetPath);
	}

	/// <summary>
	/// Builds a structured documentation model using an externally managed provider for service-backed parameters.
	/// </summary>
	/// <param name="serviceProvider">Provider used to determine whether direct handler parameters can be omitted.</param>
	/// <param name="targetPath">Optional target path to scope the model.</param>
	/// <returns>A structured documentation model.</returns>
	public ReplDocumentationModel CreateDocumentationModel(
		IServiceProvider serviceProvider,
		string? targetPath = null) =>
		_core.CreateDocumentationModel(serviceProvider, targetPath);

	/// <summary>
	/// Maps a route and command handler.
	/// </summary>
	public CommandBuilder Map(string route, Delegate handler) => _core.Map(route, handler);

	/// <summary>
	/// Creates a top-level context segment and configures nested routes.
	/// </summary>
	public IContextBuilder Context(string segment, Action<IReplApp> configure, Delegate? validation = null)
	{
		ArgumentNullException.ThrowIfNull(configure);
		return _core.Context(
			segment,
			scoped => configure(new ScopedReplApp(scoped, this)),
			validation);
	}

	/// <summary>
	/// Creates a top-level context segment and configures nested routes.
	/// Compatibility overload for <see cref="IReplMap"/> callbacks.
	/// </summary>
	public IContextBuilder Context(string segment, Action<IReplMap> configure, Delegate? validation = null)
	{
		ArgumentNullException.ThrowIfNull(configure);
		return _core.Context(segment, configure, validation);
	}

	/// <summary>
	/// Maps a module resolved through runtime DI activation.
	/// </summary>
	public ReplApp MapModule<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TModule>()
		where TModule : class, IReplModule
	{
		var module = ResolveModuleFromServices<TModule>();
		return MapModule(module);
	}

	/// <summary>
	/// Maps a module instance.
	/// </summary>
	public ReplApp MapModule(IReplModule module)
	{
		_core.MapModule(module);
		return this;
	}

	/// <summary>
	/// Maps a module instance with a runtime presence predicate.
	/// </summary>
	public ReplApp MapModule(
		IReplModule module,
		Func<ModulePresenceContext, bool> isPresent)
	{
		_core.MapModule(module, isPresent);
		return this;
	}

	/// <summary>
	/// Maps a module instance with an injectable runtime presence predicate.
	/// </summary>
	public ReplApp MapModule(IReplModule module, Delegate isPresent)
	{
		_core.MapModule(module, AdaptModulePresencePredicate(isPresent));
		return this;
	}

	/// <summary>
	/// Runs using internally configured services and owns process signals according to <see cref="ReplRunOptions"/>.
	/// </summary>
	public int Run(string[] args, ReplRunOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(args);
#pragma warning disable VSTHRD002
		return RunAsync(args, options).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
	}

	/// <summary>
	/// Runs using internally configured services and owns process signals according to <see cref="ReplRunOptions"/>.
	/// </summary>
	/// <param name="args">Command-line arguments.</param>
	/// <param name="options">Per-run options. A null signal-handling value inherits the active profile.</param>
	/// <param name="cancellationToken">Caller-owned cancellation token. Automatic signal mode injects a linked, run-scoped token; caller-owned mode passes this token through directly.</param>
	public async ValueTask<int> RunAsync(
		string[] args,
		ReplRunOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(args);
		var runOptions = options ?? new ReplRunOptions();
		var processSignalHandling = options?.ProcessSignalHandling ?? _defaultProcessSignalHandling;

		// Rejected rather than treated as Automatic. Numeric configuration or deserialization can produce
		// an undefined value, and taking process-wide signal ownership — which also swaps the handler's
		// token for a run-scoped one — is too consequential to acquire by falling through a negative
		// test. The profile default is framework-set and always defined, so a bad value came from here.
		if (processSignalHandling is not (ProcessSignalHandlingMode.None or ProcessSignalHandlingMode.Automatic))
		{
			throw new ArgumentOutOfRangeException(
				nameof(options),
				processSignalHandling,
				$"ReplRunOptions.{nameof(ReplRunOptions.ProcessSignalHandling)} is not a defined {nameof(ProcessSignalHandlingMode)}.");
		}

		if (processSignalHandling == ProcessSignalHandlingMode.None)
		{
			var provider = EnsureSharedProvider();
			return await RunWithServicesAsync(args, provider, runOptions, cancellationToken)
				.ConfigureAwait(false);
		}

		var signals = new ProcessSignalCancellationScope(cancellationToken);
		ExecutionOutcome? outcome = null;
		OperationCanceledException? cancellationException = null;
		try
		{
			var provider = EnsureSharedProvider();
			outcome = await RunOutcomeAsync(args, provider, runOptions, signals.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException ex)
		{
			cancellationException = ex;
		}
		finally
		{
			await signals.DisposeAsync().ConfigureAwait(false);
		}

		if (cancellationException is not null && signals.ExitCode is null)
		{
			// Scope disposal must finish before deciding whether cancellation came from a claimed
			// process signal; ExceptionDispatchInfo preserves the original cancellation stack.
			ExceptionDispatchInfo.Capture(cancellationException).Throw();
		}

		// One resolve for the whole run, on the outcome the signal may have reclassified — rather than
		// overwriting an already-resolved number, which would hand a resolver two outcomes for one run
		// and leave ExitCodes.Interrupted with nothing to govern.
		return _core.ResolveProcessExitCode(ApplySignalInterruption(outcome, cancellationException, signals));
	}

	/// <summary>
	/// Runs using internally configured services and owns process signals according to <see cref="ReplRunOptions"/>.
	/// </summary>
	/// <param name="args">Command-line arguments.</param>
	/// <param name="cancellationToken">Caller-owned cancellation token. Automatic signal mode injects a linked, run-scoped token; caller-owned mode passes this token through directly.</param>
	public ValueTask<int> RunAsync(string[] args, CancellationToken cancellationToken) =>
		RunAsync(args, options: null, cancellationToken);

	/// <summary>
	/// Reclassifies a run as <see cref="ReplExecutionOutcomeKind.Interrupted"/> when a process signal was
	/// claimed while it ran, so the interruption travels through <see cref="ExitCodeOptions"/> and reaches
	/// <see cref="ExitCodeOptions.Resolver"/> like any other outcome. The conventional <c>128 + signal</c>
	/// code the signal carries becomes the outcome's own code, which the table honours unless
	/// <see cref="ExitCodeOptions.Interrupted"/> overrides it.
	/// </summary>
	private static ExecutionOutcome ApplySignalInterruption(
		ExecutionOutcome? outcome,
		OperationCanceledException? cancellationException,
		ProcessSignalCancellationScope signals)
	{
		if (signals.ExitCode is not { } signalExitCode)
		{
			// No signal was claimed. A null outcome here means the run threw a cancellation that the
			// rethrow above already handled, so this is only reached with one in hand.
			return outcome ?? ExecutionOutcome.FrameworkError(rendered: null);
		}

		// A run that produced its own refusal or failure keeps reporting it; the signal arrived after the
		// fact and replacing a usage error with 130 would hide why the command was wrong.
		if (outcome is { } produced && !produced.IsInterruptible)
		{
			return produced;
		}

		// No token here: the scope is disposed by now and reading Token would throw. The exception is
		// informational for a resolver, so a plain one stands in when the run returned instead of throwing.
		return ExecutionOutcome.Interrupted(
			cancellationException ?? new OperationCanceledException(),
			signalExitCode);
	}

	/// <summary>
	/// Runs using an externally managed service provider; standalone signal bridging and cancellation remain caller-owned. Interactive mode retains its own Ctrl+C policy.
	/// </summary>
	public int Run(string[] args, IServiceProvider services, ReplRunOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(services);
#pragma warning disable VSTHRD002
		return RunAsync(args, services, options).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
	}

	/// <summary>
	/// Runs using an externally managed host; standalone signal bridging and cancellation remain caller-owned. Interactive mode retains its own Ctrl+C policy.
	/// </summary>
	public int Run(string[] args, IHost host, ReplRunOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(host);
#pragma warning disable VSTHRD002
		return RunAsync(args, host, options).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
	}

	/// <summary>
	/// Runs using an externally managed service provider; standalone signal bridging and cancellation remain caller-owned. Interactive mode retains its own Ctrl+C policy.
	/// </summary>
	/// <param name="args">Command-line arguments.</param>
	/// <param name="services">Caller-owned service provider.</param>
	/// <param name="options">Per-run options. An explicit <see cref="ProcessSignalHandlingMode.Automatic"/> request is diagnosed and ignored because this overload is externally owned.</param>
	/// <param name="cancellationToken">Caller-owned cancellation token. This overload does not install a standalone process-signal bridge or create a signal-linked token.</param>
	public async ValueTask<int> RunAsync(
		string[] args,
		IServiceProvider services,
		ReplRunOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(services);
		var runOptions = options ?? new ReplRunOptions();
		DiagnoseIgnoredProcessSignalHandling(runOptions);
		return await RunWithServicesAsync(args, services, runOptions, cancellationToken)
			.ConfigureAwait(false);
	}

	private async ValueTask<int> RunWithServicesAsync(
		string[] args,
		IServiceProvider services,
		ReplRunOptions runOptions,
		CancellationToken cancellationToken)
	{
		var outcome = await RunOutcomeAsync(args, services, runOptions, cancellationToken)
			.ConfigureAwait(false);
		return _core.ResolveProcessExitCode(outcome);
	}

	/// <summary>
	/// Runs and reports the outcome without resolving an exit code, so a wrapper with teardown of its own
	/// — the hosted lifecycle, or a process-signal scope that may reclassify the run as interrupted —
	/// resolves once at the end instead of once per stage.
	/// </summary>
	private async ValueTask<ExecutionOutcome> RunOutcomeAsync(
		string[] args,
		IServiceProvider services,
		ReplRunOptions runOptions,
		CancellationToken cancellationToken)
	{
		if (runOptions.HostedServiceLifecycle is HostedServiceLifecycleMode.None or HostedServiceLifecycleMode.Guest)
		{
			return await _core.RunOutcomeWithServicesAsync(args, services, cancellationToken)
				.ConfigureAwait(false);
		}

		// Routed through the policy before starting hosted services, so an already-cancelled caller token
		// stops this overload as early as it stops the others and still honours ExitCodes.Cancelled.
		if (_core.TryObserveCallerCancellation(cancellationToken) is { } cancelled)
		{
			return cancelled;
		}

		return await RunHostedLifecycleOutcomeAsync(args, services, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Starts hosted services, runs the pipeline, stops them, and resolves one exit code for the whole
	/// lifecycle. A failure the pipeline propagates is held rather than lost, because hosted services
	/// still have to be stopped and a failed shutdown outranks whatever the command reported.
	/// </summary>
	private async ValueTask<ExecutionOutcome> RunHostedLifecycleOutcomeAsync(
		string[] args,
		IServiceProvider services,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<IHostedService> started = [];
		ExecutionOutcome? outcome = null;
		Exception? propagating = null;
		try
		{
			// A startup failure rolls back whatever the coordinator managed to start. That rollback is
			// best-effort: it swallows its own stop errors, so a service that refuses to stop during
			// rollback survives unreported, unlike one that fails in the normal shutdown below.
			started = await HostedServiceLifecycleCoordinator.StartAsync(services, cancellationToken)
				.ConfigureAwait(false);
			outcome = await _core.RunOutcomeWithServicesAsync(args, services, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (HostedServiceLifecycleException ex)
		{
			if (TryClassifyStartupFailure(ex, cancellationToken) is { } startupOutcome)
			{
				outcome = startupOutcome;
				if (startupOutcome.Kind == ReplExecutionOutcomeKind.FrameworkError)
				{
					// Only a real hosting defect is a startup error; a caller cancellation is not.
					await TryWriteLifecycleFailureAsync(ex).ConfigureAwait(false);
				}
			}
			else
			{
				propagating = ex.InnerException;
			}
		}
		catch (Exception ex)
		{
			propagating = ex;
		}

		if (await TryStopHostedServicesAsync(started, propagating).ConfigureAwait(false) is { } stopOutcome)
		{
			outcome = stopOutcome;
		}
		else if (propagating is not null)
		{
			ExceptionDispatchInfo.Capture(propagating).Throw();
		}

		// Reported once, after the whole lifecycle, and resolved by the caller: a consumer must observe
		// exactly one outcome per run. A null here would mean a catch arm above set neither an outcome nor
		// a propagating exception, which is a framework defect and must not be reported as a clean run.
		return outcome ?? ExecutionOutcome.FrameworkError(rendered: null);
	}

	/// <summary>
	/// Stops the hosted services that started, reporting a failure as the run's outcome. Returns
	/// <see langword="null"/> when shutdown was clean. A failed shutdown outranks both the command's own
	/// outcome and any exception the pipeline was propagating, because the process is leaving dirty. An
	/// exception it outranks travels on the outcome beside the stop failure rather than being lost; an
	/// <em>outcome</em> it outranks is replaced, so a command that had chosen its own code through
	/// <c>Results.Exit(n)</c> reports the shutdown failure instead. Not reclassified as a
	/// cancellation: shutdown runs on <see cref="CancellationToken.None"/>, so a cancellation surfacing
	/// here is the service's own.
	/// </summary>
	private static async ValueTask<ExecutionOutcome?> TryStopHostedServicesAsync(
		IReadOnlyList<IHostedService> started,
		Exception? propagating)
	{
		try
		{
			await HostedServiceLifecycleCoordinator.StopAsync(started, CancellationToken.None)
				.ConfigureAwait(false);
			return null;
		}
		catch (HostedServiceLifecycleException ex)
		{
			await TryWriteLifecycleFailureAsync(ex).ConfigureAwait(false);
			if (propagating is null)
			{
				return ExecutionOutcome.FrameworkError(rendered: null, exception: ex);
			}

			await TryWriteLifecycleDiagnosticAsync(
					$"Error: the shutdown failure suppressed {Describe(propagating)}")
				.ConfigureAwait(false);

			// Two causes, so both travel: the same shape CoreReplApp uses for routing-invalidation
			// failures — one exception as itself, several wrapped with a message naming the situation.
			return ExecutionOutcome.FrameworkError(
				rendered: null,
				exception: new AggregateException(
					"The host failed to stop, suppressing the exception the run was propagating.",
					[ex, propagating]));
		}
	}

	// The coordinator's message names only which service failed; the reason it gives is the exception it
	// wrapped, which is non-null by HostedServiceLifecycleException's constructor. Both are needed: an
	// operator reading stderr alone would otherwise get the service and not the cause.
	private static ValueTask TryWriteLifecycleFailureAsync(HostedServiceLifecycleException failure) =>
		TryWriteLifecycleDiagnosticAsync($"Error: {failure.Message} {Describe(failure.InnerException)}");

	// Type plus message, the shape TryWriteResolverDiagnostic already uses for a reported exception.
	private static string Describe(Exception? exception) =>
		exception is null ? "(no inner exception)" : $"{exception.GetType().Name}: {exception.Message}";

	[SuppressMessage(
		"Design",
		"CA1031:Do not catch general exception types",
		Justification = "Reporting a lifecycle failure must not itself fail the run: the error stream may be disposed or its transport already torn down.")]
	private static async ValueTask TryWriteLifecycleDiagnosticAsync(string message)
	{
		try
		{
			// Error, not Output: a headless run's stdout carries the machine-readable payload, and a
			// framework diagnostic written there corrupts it for the parent process.
			await ReplSessionIO.Error.WriteLineAsync(message).ConfigureAwait(false);
		}
		catch
		{
			// Best-effort: the resolved exit code is the contract, the diagnostic is a courtesy.
		}
	}

	/// <summary>
	/// Classifies a hosted-service startup failure. The coordinator wraps whatever the service threw,
	/// cancellation included, so a startup the caller cancelled is a cancellation rather than a framework
	/// defect and follows the same policy as any other caller-token cancellation. Returns
	/// <see langword="null"/> when that cancellation must propagate instead of becoming an exit code —
	/// the default an application gets by opting into no conversion.
	/// </summary>
	private ExecutionOutcome? TryClassifyStartupFailure(
		HostedServiceLifecycleException ex,
		CancellationToken cancellationToken)
	{
		if (ex.InnerException is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			return ExecutionOutcome.FrameworkError(rendered: null, exception: ex);
		}

		return _core.ConvertsCancellationToExitCode ? ExecutionOutcome.Cancelled(ex) : null;
	}

	/// <summary>
	/// Runs using an externally managed host; standalone signal bridging and cancellation remain caller-owned. Interactive mode retains its own Ctrl+C policy.
	/// </summary>
	/// <param name="args">Command-line arguments.</param>
	/// <param name="host">Caller-owned application host.</param>
	/// <param name="options">Per-run options. An explicit <see cref="ProcessSignalHandlingMode.Automatic"/> request is diagnosed and ignored because this overload is externally owned.</param>
	/// <param name="cancellationToken">Caller-owned cancellation token. This overload does not install a standalone process-signal bridge or create a signal-linked token.</param>
	public ValueTask<int> RunAsync(
		string[] args,
		IHost host,
		ReplRunOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(host);
		return RunAsync(args, host.Services, options, cancellationToken);
	}

	/// <summary>
	/// Runs against an externally managed input/output host; standalone signal bridging and cancellation remain caller-owned. Interactive mode retains its own Ctrl+C policy.
	/// </summary>
	public int Run(string[] args, IReplHost host, ReplRunOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(host);
#pragma warning disable VSTHRD002
		return RunAsync(args, host, options).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
	}

	/// <summary>
	/// Runs against an externally managed input/output host; standalone signal bridging and cancellation remain caller-owned. Interactive mode retains its own Ctrl+C policy.
	/// </summary>
	/// <param name="args">Command-line arguments.</param>
	/// <param name="host">Caller-owned input/output host.</param>
	/// <param name="options">Per-run options. An explicit <see cref="ProcessSignalHandlingMode.Automatic"/> request is diagnosed and ignored because this overload is externally owned.</param>
	/// <param name="cancellationToken">Caller-owned cancellation token. This overload does not install a standalone process-signal bridge or create a signal-linked token.</param>
	public async ValueTask<int> RunAsync(
		string[] args,
		IReplHost host,
		ReplRunOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(host);

		var runOptions = options ?? new ReplRunOptions();
		var sessionHost = host as IReplSessionHost;
		using (ReplSessionIO.SetSession(host.Output, host.Input, runOptions.AnsiSupport, sessionHost?.SessionId))
		{
			ApplyTerminalOverrides(runOptions);
			var provider = EnsureSharedProvider();
			return await RunAsync(args, provider, runOptions, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Runs against an externally managed input/output host with an external service provider; standalone signal bridging and cancellation remain caller-owned. Interactive mode retains its own Ctrl+C policy.
	/// </summary>
	public int Run(string[] args, IReplHost host, IServiceProvider services, ReplRunOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(host);
		ArgumentNullException.ThrowIfNull(services);
#pragma warning disable VSTHRD002
		return RunAsync(args, host, services, options).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
	}

	/// <summary>
	/// Runs against an externally managed input/output host with an external service provider; standalone signal bridging and cancellation remain caller-owned. Interactive mode retains its own Ctrl+C policy.
	/// </summary>
	/// <param name="args">Command-line arguments.</param>
	/// <param name="host">Caller-owned input/output host.</param>
	/// <param name="services">Caller-owned service provider.</param>
	/// <param name="options">Per-run options. An explicit <see cref="ProcessSignalHandlingMode.Automatic"/> request is diagnosed and ignored because this overload is externally owned.</param>
	/// <param name="cancellationToken">Caller-owned cancellation token. This overload does not install a standalone process-signal bridge or create a signal-linked token.</param>
	public async ValueTask<int> RunAsync(
		string[] args,
		IReplHost host,
		IServiceProvider services,
		ReplRunOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(host);
		ArgumentNullException.ThrowIfNull(services);

		var runOptions = options ?? new ReplRunOptions();
		var sessionHost = host as IReplSessionHost;
		using (ReplSessionIO.SetSession(host.Output, host.Input, runOptions.AnsiSupport, sessionHost?.SessionId))
		{
			DiagnoseIgnoredProcessSignalHandling(runOptions);
			ApplyTerminalOverrides(runOptions);

			// Before building the overlay, which resolves the presenter, the interaction handlers and
			// TimeProvider from the caller's provider: an already-cancelled token must stop here rather
			// than letting user service factories run for a request nobody is waiting on. Inside the
			// session, so a resolver and its diagnostic reach the host's writers and not Console.Error.
			if (_core.TryObserveCallerCancellation(cancellationToken) is { } cancelled)
			{
				return _core.ResolveProcessExitCode(cancelled);
			}

			var sessionProvider = CreateSessionOverlay(services);
			return await _core.RunWithServicesAsync(args, sessionProvider, cancellationToken)
				.ConfigureAwait(false);
		}
	}

	private static void DiagnoseIgnoredProcessSignalHandling(ReplRunOptions runOptions)
	{
		if (runOptions.ProcessSignalHandling == ProcessSignalHandlingMode.Automatic)
		{
			ProcessSignalCoordinator.WriteDiagnostic(
				"Ignoring ReplRunOptions.ProcessSignalHandling=Automatic because this overload "
				+ "uses an externally managed host or service provider; the caller owns process signals.");
		}
	}

	private static void ApplyTerminalOverrides(ReplRunOptions runOptions)
	{
		var overrides = runOptions.TerminalOverrides;
		if (overrides is null)
		{
			return;
		}

		if (!string.IsNullOrWhiteSpace(overrides.TransportName))
		{
			ReplSessionIO.TransportName = overrides.TransportName;
		}

		if (!string.IsNullOrWhiteSpace(overrides.RemotePeer))
		{
			ReplSessionIO.RemotePeer = overrides.RemotePeer;
		}

		if (!string.IsNullOrWhiteSpace(overrides.TerminalIdentity))
		{
			ReplSessionIO.TerminalIdentity = overrides.TerminalIdentity;
		}

		if (overrides.WindowSize is { } size)
		{
			ReplSessionIO.WindowSize = size;
		}

		if (overrides.AnsiSupported is { } ansi)
		{
			ReplSessionIO.AnsiSupport = ansi;
		}

		if (overrides.TerminalCapabilities is { } capabilities)
		{
			ReplSessionIO.TerminalCapabilities = capabilities;
		}
	}

	/// <summary>
	/// Returns the shared service provider, building it on first access.
	/// This provider is reused for both module resolution and runtime execution,
	/// ensuring DI-resolved modules share the same service instances as handlers.
	/// </summary>
	public IServiceProvider Services => EnsureSharedProvider();

	internal CoreReplApp Core => _core;

	internal RouteMatch? Resolve(IReadOnlyList<string> inputTokens) => _core.Resolve(inputTokens);

	IContextBuilder ICoreReplApp.Context(string segment, Action<ICoreReplApp> configure, Delegate? validation) =>
		Context(segment, scoped => configure(scoped), validation);

	IContextBuilder IReplApp.Context(string segment, Action<IReplApp> configure, Delegate? validation) =>
		Context(segment, configure, validation);

	IContextBuilder IReplMap.Context(string segment, Action<IReplMap> configure, Delegate? validation) =>
		Context(segment, configure, validation);

	ICoreReplApp ICoreReplApp.MapModule(IReplModule module) => MapModule(module);

	ICoreReplApp ICoreReplApp.MapModule(IReplModule module, Func<ModulePresenceContext, bool> isPresent) =>
		MapModule(module, isPresent);

	ICoreReplApp ICoreReplApp.WithBanner(Delegate bannerProvider) => WithBanner(bannerProvider);

	ICoreReplApp ICoreReplApp.WithBanner(string text) => WithBanner(text);

	void ICoreReplApp.InvalidateRouting() => InvalidateRouting();

	IReplApp IReplApp.MapModule(IReplModule module) => MapModule(module);

	IReplApp IReplApp.MapModule(IReplModule module, Func<ModulePresenceContext, bool> isPresent) =>
		MapModule(module, isPresent);

	IReplApp IReplApp.MapModule(IReplModule module, Delegate isPresent) =>
		MapModule(module, isPresent);

	[UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "Annotation flows from IReplApp.MapModule<TModule>().")]
	[UnconditionalSuppressMessage("Trimming", "IL2095", Justification = "Annotation flows from IReplApp.MapModule<TModule>().")]
	IReplApp IReplApp.MapModule<TModule>() => MapModule<TModule>();

	IReplApp IReplApp.WithBanner(Delegate bannerProvider) => WithBanner(bannerProvider);

	IReplApp IReplApp.WithBanner(string text) => WithBanner(text);

	void IReplApp.InvalidateRouting() => InvalidateRouting();

	IReplMap IReplMap.MapModule(IReplModule module) => MapModule(module);

	IReplMap IReplMap.MapModule(IReplModule module, Func<ModulePresenceContext, bool> isPresent) =>
		MapModule(module, isPresent);

	IReplMap IReplMap.WithBanner(Delegate bannerProvider) => WithBanner(bannerProvider);

	IReplMap IReplMap.WithBanner(string text) => WithBanner(text);

	/// <summary>
	/// Returns the shared provider, building it on first access.
	/// This provider is reused for both module resolution and runtime execution,
	/// ensuring DI-resolved modules share the same service instances as handlers.
	/// </summary>
	private ServiceProvider EnsureSharedProvider() =>
		_sharedProvider ??= _services.BuildServiceProvider();

	private TModule ResolveModuleFromServices<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TModule>()
		where TModule : class, IReplModule
	{
		// Resolve from the shared provider — its lifetime spans the entire app,
		// so disposable dependencies captured by the module stay alive.
		// Uses GetServiceOrCreateInstance so callers don't need to register the
		// module explicitly — constructor dependencies are resolved from DI.
		var provider = EnsureSharedProvider();
		return Microsoft.Extensions.DependencyInjection.ActivatorUtilities
			.GetServiceOrCreateInstance<TModule>(provider);
	}

	private static Func<ModulePresenceContext, bool> AdaptModulePresencePredicate(Delegate isPresent)
	{
		ArgumentNullException.ThrowIfNull(isPresent);
		var parameters = isPresent.Method.GetParameters();
		ValidateModulePresencePredicateSignature(isPresent, parameters);
		var argumentResolvers = parameters
			.Select(parameter => BuildModulePresenceArgumentResolver(parameter, isPresent))
			.ToArray();
		var invoker = CompileModulePresencePredicateInvoker(isPresent, parameters);

		return context =>
		{
			var arguments = new object?[argumentResolvers.Length];
			for (var i = 0; i < argumentResolvers.Length; i++)
			{
				arguments[i] = argumentResolvers[i](context);
			}

			try
			{
				return invoker(arguments);
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException(
					$"Failed to evaluate module presence predicate '{FormatDelegateMethod(isPresent)}': {ex.Message}",
					ex);
			}
		};
	}

	private static Func<ModulePresenceContext, object?> BuildModulePresenceArgumentResolver(
		ParameterInfo parameter,
		Delegate isPresent)
	{
		if (parameter.ParameterType == typeof(ModulePresenceContext))
		{
			return static context => context;
		}

		if (parameter.ParameterType == typeof(ReplRuntimeChannel))
		{
			return static context => context.Channel;
		}

		if (parameter.ParameterType == typeof(IReplSessionState))
		{
			return static context => context.SessionState;
		}

		if (parameter.ParameterType == typeof(IReplSessionInfo))
		{
			return static context => context.SessionInfo;
		}

		return context => ResolveModulePresenceServiceArgument(parameter, context, isPresent);
	}

	private static object? ResolveModulePresenceServiceArgument(
		ParameterInfo parameter,
		ModulePresenceContext context,
		Delegate isPresent)
	{
		var resolved = context.ServiceProvider.GetService(parameter.ParameterType);
		if (resolved is not null)
		{
			return resolved;
		}

		if (parameter.HasDefaultValue)
		{
			return parameter.DefaultValue;
		}

		if (!parameter.ParameterType.IsValueType
			|| Nullable.GetUnderlyingType(parameter.ParameterType) is not null)
		{
			return null;
		}

		throw new InvalidOperationException(
			$"Unable to resolve module presence predicate parameter '{parameter.Name}' ({parameter.ParameterType.Name}) in '{FormatDelegateMethod(isPresent)}'.");
	}

	private static Func<object?[], bool> CompileModulePresencePredicateInvoker(Delegate isPresent, ParameterInfo[] parameters)
	{
		var argumentsParameter = Expression.Parameter(typeof(object[]), "arguments");
		var callArguments = new Expression[parameters.Length];
		for (var i = 0; i < parameters.Length; i++)
		{
			var argumentIndex = Expression.Constant(i);
			var argumentValue = Expression.ArrayIndex(argumentsParameter, argumentIndex);
			callArguments[i] = Expression.Convert(argumentValue, parameters[i].ParameterType);
		}

		Expression call = isPresent.Target is null
			? Expression.Call(isPresent.Method, callArguments)
			: Expression.Call(Expression.Constant(isPresent.Target), isPresent.Method, callArguments);
		return Expression.Lambda<Func<object?[], bool>>(call, argumentsParameter).Compile();
	}

	private static void ValidateModulePresencePredicateSignature(Delegate isPresent, ParameterInfo[] parameters)
	{
		if (isPresent.Method.ReturnType != typeof(bool))
		{
			throw new InvalidOperationException(
				$"Module presence predicate '{FormatDelegateMethod(isPresent)}' must return bool.");
		}

		if (isPresent.Method.ContainsGenericParameters)
		{
			throw new InvalidOperationException(
				$"Module presence predicate '{FormatDelegateMethod(isPresent)}' cannot use open generic parameters.");
		}

		foreach (var parameter in parameters)
		{
			if (parameter.ParameterType.IsByRef || parameter.IsOut)
			{
				throw new InvalidOperationException(
					$"Module presence predicate '{FormatDelegateMethod(isPresent)}' cannot declare ref/out parameter '{parameter.Name}'.");
			}

			if (parameter.ParameterType == typeof(IServiceProvider))
			{
				throw new InvalidOperationException(
					$"Module presence predicate '{FormatDelegateMethod(isPresent)}' cannot declare IServiceProvider. Use ModulePresenceContext, IReplSessionState, or IReplSessionInfo.");
			}
		}
	}

	private static string FormatDelegateMethod(Delegate value)
	{
		var method = value.Method;
		return method.DeclaringType is { } declaringType
			? $"{declaringType.FullName}.{method.Name}"
			: method.Name;
	}

	private SessionOverlayServiceProvider CreateSessionOverlay(IServiceProvider external)
	{
		var defaults = new Dictionary<Type, object>
		{
			[typeof(IReplSessionState)] = new DefaultsSessionState(),
			[typeof(IHistoryProvider)] = new InMemoryHistoryProvider(),
			[typeof(TimeProvider)] = TimeProvider.System,
			[typeof(IReplKeyReader)] = new ConsoleKeyReader(),
			[typeof(IReplIoContext)] = new LiveReplIoContext(),
		};

		defaults[typeof(ITerminalInfo)] = new ConsoleTerminalInfo(_core.OptionsSnapshot.Output);

		var presenterInstance = external.GetService(typeof(IReplInteractionPresenter)) as IReplInteractionPresenter;
		var userHandlers = ResolveHandlers(external);
		var richHandler = new RichPromptInteractionHandler(_core.OptionsSnapshot.Output, presenterInstance);
		IReplInteractionHandler[] allHandlers = [.. userHandlers, richHandler];
		var channel = new DefaultsInteractionChannel(
			_core.OptionsSnapshot.Interaction,
			_core.OptionsSnapshot.Output,
			presenterInstance,
			allHandlers,
			external.GetService(typeof(TimeProvider)) as TimeProvider);
		defaults[typeof(IReplInteractionChannel)] = channel;
		defaults[typeof(IReplSessionInfo)] = new LiveSessionInfo();
		if (EnsureSharedProvider().GetService<IReplResultFlowDiagnostics>() is { } diagnostics)
		{
			defaults[typeof(IReplResultFlowDiagnostics)] = diagnostics;
		}

		return new SessionOverlayServiceProvider(external, defaults);
	}

	private static IReplInteractionHandler[] ResolveHandlers(IServiceProvider sp)
	{
		var handlers = sp.GetService(typeof(IEnumerable<IReplInteractionHandler>)) as IEnumerable<IReplInteractionHandler>;
		return handlers?.ToArray() ?? [];
	}

	private sealed class SessionOverlayServiceProvider(
		IServiceProvider external,
		IReadOnlyDictionary<Type, object> defaults) : IServiceProvider
	{
		public object? GetService(Type serviceType)
		{
			var service = external.GetService(serviceType);
			if (service is not null)
			{
				return service;
			}

			return defaults.TryGetValue(serviceType, out var fallback) ? fallback : null;
		}
	}

	private static void EnsureDefaultServices(IServiceCollection services, CoreReplApp core)
	{
		services.AddReplLogging();
		services.TryAddSingleton(core);
		services.TryAddSingleton<ICoreReplApp>(core);
		services.TryAddSingleton<IReplSessionState, DefaultsSessionState>();
		services.TryAddSingleton<IHistoryProvider, InMemoryHistoryProvider>();
		services.TryAddSingleton(TimeProvider.System);
		services.TryAdd(ServiceDescriptor.Singleton<IReplInteractionChannel>(sp =>
		{
			var presenterSvc = sp.GetService<IReplInteractionPresenter>();
			var userHandlers = sp.GetServices<IReplInteractionHandler>().ToArray();
			var richHandler = new RichPromptInteractionHandler(core.OptionsSnapshot.Output, presenterSvc);
			IReplInteractionHandler[] allHandlers = [.. userHandlers, richHandler];
			return new DefaultsInteractionChannel(
				core.OptionsSnapshot.Interaction,
				core.OptionsSnapshot.Output,
				presenterSvc,
				allHandlers,
				sp.GetService<TimeProvider>());
		}));
		services.TryAddSingleton<IReplKeyReader, ConsoleKeyReader>();
		services.TryAddSingleton<IReplSessionInfo, LiveSessionInfo>();
		services.TryAddSingleton<IReplIoContext, LiveReplIoContext>();
		services.TryAddSingleton(_ => core.OptionsSnapshot.Interaction);
		services.TryAddSingleton(_ => core.OptionsSnapshot.Output);
		services.TryAddSingleton<ITerminalInfo>(
			_ => new ConsoleTerminalInfo(core.OptionsSnapshot.Output));
		services.TryAddSingleton(_ => core.GlobalOptionsAccessor);
	}

	private sealed class ScopedReplApp(ICoreReplApp map, ReplApp root) : IReplApp
	{
		private readonly ICoreReplApp _map = map;

		public CommandBuilder Map(string route, Delegate handler) => _map.Map(route, handler);

		public IContextBuilder Context(string segment, Action<IReplApp> configure, Delegate? validation = null)
		{
			ArgumentNullException.ThrowIfNull(configure);
			return _map.Context(
				segment,
				scoped => configure(new ScopedReplApp(scoped, root)),
				validation);
		}

		public IReplApp MapModule<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TModule>()
			where TModule : class, IReplModule
		{
			return MapModule(root.ResolveModuleFromServices<TModule>());
		}

		public IReplApp MapModule(IReplModule module)
		{
			_map.MapModule(module);
			return this;
		}

		public IReplApp MapModule(
			IReplModule module,
			Func<ModulePresenceContext, bool> isPresent)
		{
			_map.MapModule(module, isPresent);
			return this;
		}

		public IReplApp MapModule(IReplModule module, Delegate isPresent)
		{
			_map.MapModule(module, AdaptModulePresencePredicate(isPresent));
			return this;
		}

		public void InvalidateRouting() => _map.InvalidateRouting();

		public ReplDocumentationModel CreateDocumentationModel(string? targetPath = null) =>
			root.CreateDocumentationModel(targetPath);

		IContextBuilder ICoreReplApp.Context(string segment, Action<ICoreReplApp> configure, Delegate? validation) =>
			Context(segment, scoped => configure(scoped), validation);

		IContextBuilder IReplMap.Context(string segment, Action<IReplMap> configure, Delegate? validation) =>
			((IReplMap)_map).Context(segment, configure, validation);

		ICoreReplApp ICoreReplApp.MapModule(IReplModule module) => MapModule(module);

		ICoreReplApp ICoreReplApp.MapModule(IReplModule module, Func<ModulePresenceContext, bool> isPresent) =>
			MapModule(module, isPresent);

		ICoreReplApp ICoreReplApp.WithBanner(Delegate bannerProvider)
		{
			_map.WithBanner(bannerProvider);
			return this;
		}

		IReplApp IReplApp.WithBanner(Delegate bannerProvider)
		{
			_map.WithBanner(bannerProvider);
			return this;
		}

		IReplMap IReplMap.WithBanner(Delegate bannerProvider)
		{
			((IReplMap)_map).WithBanner(bannerProvider);
			return this;
		}

		ICoreReplApp ICoreReplApp.WithBanner(string text)
		{
			_map.WithBanner(text);
			return this;
		}

		void ICoreReplApp.InvalidateRouting() => InvalidateRouting();

		IReplApp IReplApp.WithBanner(string text)
		{
			_map.WithBanner(text);
			return this;
		}

		IReplMap IReplMap.WithBanner(string text)
		{
			((IReplMap)_map).WithBanner(text);
			return this;
		}

		IReplMap IReplMap.MapModule(IReplModule module) => MapModule(module);

		IReplMap IReplMap.MapModule(IReplModule module, Func<ModulePresenceContext, bool> isPresent) =>
			MapModule(module, isPresent);
	}
}
