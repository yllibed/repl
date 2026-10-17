using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Repl;

public sealed partial class ReplApp
{
	private ValueTask<ExecutionOutcome> RunHostedPipelineAsync(string[] args, IServiceProvider services,
		SessionScopeBehavior? sessionScope, bool startupBeforeServices, Action<IReadOnlyList<IHostedService>> onStarted,
		CancellationToken cancellationToken) => RunInSessionScopeAsync(services, sessionScope,
		(sessionServices, token) => startupBeforeServices
			? _core.RunOutcomeWithServicesAsync(args, sessionServices, async startToken =>
			{
				onStarted(await HostedServiceLifecycleCoordinator.StartAsync(services, startToken).ConfigureAwait(false));
			}, token)
			: _core.RunOutcomeWithServicesAsync(args, sessionServices, token), cancellationToken);

	/// <summary>Creates an application after successful preparation, then configures application services.</summary>
	/// <param name="prepared">Prepared options and declarations.</param>
	/// <param name="configureServices">Optional service registration callback.</param>
	/// <returns>A new application with a lazily constructed provider.</returns>
	public static ReplApp Create(PreparedReplStartup prepared, Action<IServiceCollection>? configureServices = null)
	{
		ArgumentNullException.ThrowIfNull(prepared);
		if (!prepared.CanContinue) { throw new InvalidOperationException("Complete terminal preparation before registering application services."); }
		var app = configureServices is null ? Create() : Create(configureServices);
		prepared.Apply(app._core);
		app.RegisterPreparedGlobals(app._services);
		return app;
	}

	internal void RegisterPreparedGlobals(IServiceCollection services)
	{
		services.TryAddSingleton(_core.GlobalOptionsAccessor);
		foreach (var type in _core.GlobalOptionsTypes)
		{
			services.TryAdd(ServiceDescriptor.Transient(type, sp => _core.CreateGlobalOptions(type,
				sp.GetRequiredService<IGlobalOptionsAccessor>()) ?? throw new InvalidOperationException($"Global options factory for '{type.Name}' is not registered.")));
		}
	}

	/// <inheritdoc cref="CoreReplApp.UseStartupHook"/>
	public ReplApp UseStartupHook(Delegate hook)
	{
		_core.UseStartupHook(hook);
		return this;
	}

	/// <summary>Runs prepared input using the application's services.</summary>
	/// <param name="prepared">Input prepared for the application's builder.</param>
	/// <param name="options">Run options.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code.</returns>
	/// <exception cref="InvalidOperationException">Preparation is terminal or belongs to a different builder.</exception>
	public async ValueTask<int> RunAsync(PreparedReplStartup prepared, ReplRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		using var scope = _core.PushPreparedStartup(prepared);
		return await RunAsync(prepared.Arguments.ToArray(), options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Runs prepared input with caller-owned services.</summary>
	/// <param name="prepared">Prepared input.</param>
	/// <param name="services">Caller-owned service provider.</param>
	/// <param name="options">Run options.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code.</returns>
	/// <exception cref="InvalidOperationException">Preparation is terminal or belongs to a different builder.</exception>
	public async ValueTask<int> RunAsync(PreparedReplStartup prepared, IServiceProvider services, ReplRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		using var scope = _core.PushPreparedStartup(prepared);
		return await RunAsync(prepared.Arguments.ToArray(), services, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Runs prepared input in a caller-owned generic host.</summary>
	/// <param name="prepared">Prepared input.</param>
	/// <param name="host">Caller-owned host.</param>
	/// <param name="options">Run options.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code.</returns>
	/// <exception cref="InvalidOperationException">Preparation is terminal or belongs to a different builder.</exception>
	public async ValueTask<int> RunAsync(PreparedReplStartup prepared, IHost host, ReplRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		using var scope = _core.PushPreparedStartup(prepared);
		return await RunAsync(prepared.Arguments.ToArray(), host, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Runs prepared input with externally managed input and output.</summary>
	/// <param name="prepared">Prepared input.</param>
	/// <param name="host">Input/output host.</param>
	/// <param name="options">Run options.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code.</returns>
	/// <exception cref="InvalidOperationException">Preparation is terminal or belongs to a different builder.</exception>
	public async ValueTask<int> RunAsync(PreparedReplStartup prepared, IReplHost host, ReplRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		using var scope = _core.PushPreparedStartup(prepared);
		return await RunAsync(prepared.Arguments.ToArray(), host, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Runs prepared input with externally managed input, output and services.</summary>
	/// <param name="prepared">Prepared input.</param>
	/// <param name="host">Input/output host.</param>
	/// <param name="services">Caller-owned service provider.</param>
	/// <param name="options">Run options.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code.</returns>
	/// <exception cref="InvalidOperationException">Preparation is terminal or belongs to a different builder.</exception>
	public async ValueTask<int> RunAsync(PreparedReplStartup prepared, IReplHost host, IServiceProvider services, ReplRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		using var scope = _core.PushPreparedStartup(prepared);
		return await RunAsync(prepared.Arguments.ToArray(), host, services, options, cancellationToken).ConfigureAwait(false);
	}
}
