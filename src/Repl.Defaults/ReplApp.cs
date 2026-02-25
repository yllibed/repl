using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Repl;

/// <summary>
/// DI-enabled REPL application facade for common hosting scenarios.
/// </summary>
public sealed class ReplApp : IReplApp
{
	private readonly CoreReplApp _core;
	private readonly IServiceCollection _services;

	private ReplApp(IServiceCollection services)
	{
		_services = services;
		_core = CoreReplApp.Create();
		EnsureDefaultServices(_services, _core);
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
	/// Maps a route and command handler.
	/// </summary>
	public CommandBuilder Map(string route, Delegate handler) => _core.Map(route, handler);

	/// <summary>
	/// Creates a top-level context segment and configures nested routes.
	/// </summary>
	public ReplApp Context(string segment, Action<IReplApp> configure, Delegate? validation = null)
	{
		ArgumentNullException.ThrowIfNull(configure);
		_core.Context(
			segment,
			scoped => configure(new ScopedReplApp(scoped, _services)),
			validation);
		return this;
	}

	/// <summary>
	/// Creates a top-level context segment and configures nested routes.
	/// Compatibility overload for <see cref="IReplMap"/> callbacks.
	/// </summary>
	public ReplApp Context(string segment, Action<IReplMap> configure, Delegate? validation = null)
	{
		ArgumentNullException.ThrowIfNull(configure);
		_core.Context(segment, configure, validation);
		return this;
	}

	/// <summary>
	/// Maps a module resolved through runtime DI activation.
	/// </summary>
	public ReplApp MapModule<TModule>()
		where TModule : class, IReplModule
	{
		var module = ResolveModuleFromServices<TModule>(_services);
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
	/// Runs using internally configured services.
	/// </summary>
	public int Run(string[] args, ReplRunOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(args);
		using var provider = _services.BuildServiceProvider();
#pragma warning disable VSTHRD002
		return RunAsync(args, provider, options).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
	}

	/// <summary>
	/// Runs using internally configured services.
	/// </summary>
	public async ValueTask<int> RunAsync(
		string[] args,
		ReplRunOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(args);
		using var provider = _services.BuildServiceProvider();
		return await RunAsync(args, provider, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Runs using internally configured services.
	/// </summary>
	public ValueTask<int> RunAsync(string[] args, CancellationToken cancellationToken) =>
		RunAsync(args, options: null, cancellationToken);

	/// <summary>
	/// Runs using an externally managed service provider.
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
	/// Runs using an externally managed host.
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
	/// Runs using an externally managed service provider.
	/// </summary>
	public async ValueTask<int> RunAsync(
		string[] args,
		IServiceProvider services,
		ReplRunOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(services);
		var runOptions = options ?? new ReplRunOptions();
		if (runOptions.HostedServiceLifecycle is HostedServiceLifecycleMode.None or HostedServiceLifecycleMode.Guest)
		{
			return await _core.RunWithServicesAsync(args, services, cancellationToken).ConfigureAwait(false);
		}

		var started = Array.Empty<Microsoft.Extensions.Hosting.IHostedService>();
		var exitCode = 0;
		try
		{
			started = [..
				await HostedServiceLifecycleCoordinator.StartAsync(services, cancellationToken)
					.ConfigureAwait(false),
			];
			exitCode = await _core.RunWithServicesAsync(args, services, cancellationToken).ConfigureAwait(false);
		}
		catch (HostedServiceLifecycleException ex)
		{
			await ReplSessionIO.Output.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
			exitCode = 1;
		}
		finally
		{
			try
			{
				await HostedServiceLifecycleCoordinator.StopAsync(started, CancellationToken.None)
					.ConfigureAwait(false);
			}
			catch (HostedServiceLifecycleException ex)
			{
				await ReplSessionIO.Output.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
				exitCode = 1;
			}
		}

		return exitCode;
	}

	/// <summary>
	/// Runs using an externally managed host.
	/// </summary>
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
	/// Runs against an externally managed input/output host.
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
	/// Runs against an externally managed input/output host.
	/// </summary>
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
			return await RunAsync(args, runOptions, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Runs against an externally managed input/output host with an external service provider.
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
	/// Runs against an externally managed input/output host with an external service provider.
	/// </summary>
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
			ApplyTerminalOverrides(runOptions);
			var sessionProvider = CreateSessionOverlay(services);
			return await _core.RunWithServicesAsync(args, sessionProvider, cancellationToken)
				.ConfigureAwait(false);
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

	internal CoreReplApp Core => _core;

	internal RouteMatch? Resolve(IReadOnlyList<string> inputTokens) => _core.Resolve(inputTokens);

	ICoreReplApp ICoreReplApp.Context(string segment, Action<ICoreReplApp> configure, Delegate? validation) =>
		Context(segment, scoped => configure(scoped), validation);

	IReplApp IReplApp.Context(string segment, Action<IReplApp> configure, Delegate? validation) =>
		Context(segment, configure, validation);

	IReplMap IReplMap.Context(string segment, Action<IReplMap> configure, Delegate? validation) =>
		Context(segment, configure, validation);

	ICoreReplApp ICoreReplApp.MapModule(IReplModule module) => MapModule(module);

	ICoreReplApp ICoreReplApp.WithBanner(Delegate bannerProvider) => WithBanner(bannerProvider);

	ICoreReplApp ICoreReplApp.WithBanner(string text) => WithBanner(text);

	IReplApp IReplApp.MapModule(IReplModule module) => MapModule(module);

	IReplApp IReplApp.MapModule<TModule>() => MapModule<TModule>();

	IReplApp IReplApp.WithBanner(Delegate bannerProvider) => WithBanner(bannerProvider);

	IReplApp IReplApp.WithBanner(string text) => WithBanner(text);

	IReplMap IReplMap.MapModule(IReplModule module) => MapModule(module);

	IReplMap IReplMap.WithBanner(Delegate bannerProvider) => WithBanner(bannerProvider);

	IReplMap IReplMap.WithBanner(string text) => WithBanner(text);

	private static TModule ResolveModuleFromServices<TModule>(IServiceCollection services)
		where TModule : class, IReplModule
	{
		using var provider = services.BuildServiceProvider();
		return provider.GetService<TModule>()
			?? throw new InvalidOperationException(
				$"Unable to resolve module '{typeof(TModule).FullName}'. Register it in services or call MapModule(IReplModule).");
	}

	private SessionOverlayServiceProvider CreateSessionOverlay(IServiceProvider external)
	{
		var defaults = new Dictionary<Type, object>
		{
			[typeof(IReplSessionState)] = new DefaultsSessionState(),
			[typeof(IHistoryProvider)] = new InMemoryHistoryProvider(),
			[typeof(TimeProvider)] = TimeProvider.System,
			[typeof(IReplKeyReader)] = new ConsoleKeyReader(),
		};

		var channel = new DefaultsInteractionChannel(
			_core.OptionsSnapshot.Interaction,
			_core.OptionsSnapshot.Output,
			external.GetService(typeof(TimeProvider)) as TimeProvider);
		defaults[typeof(IReplInteractionChannel)] = channel;
		defaults[typeof(IReplSessionInfo)] = new LiveSessionInfo();

		return new SessionOverlayServiceProvider(external, defaults);
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
		services.TryAddSingleton<IReplSessionState, DefaultsSessionState>();
		services.TryAddSingleton<IHistoryProvider, InMemoryHistoryProvider>();
		services.TryAddSingleton(TimeProvider.System);
		services.TryAdd(ServiceDescriptor.Singleton<IReplInteractionChannel>(sp =>
			new DefaultsInteractionChannel(
				core.OptionsSnapshot.Interaction,
				core.OptionsSnapshot.Output,
				sp.GetService<TimeProvider>())));
		services.TryAddSingleton<IReplKeyReader, ConsoleKeyReader>();
		services.TryAddSingleton<IReplSessionInfo, LiveSessionInfo>();
	}

	private sealed class ScopedReplApp(ICoreReplApp map, IServiceCollection services) : IReplApp
	{
		private readonly ICoreReplApp _map = map;
		private readonly IServiceCollection _services = services;

		public CommandBuilder Map(string route, Delegate handler) => _map.Map(route, handler);

		public IReplApp Context(string segment, Action<IReplApp> configure, Delegate? validation = null)
		{
			ArgumentNullException.ThrowIfNull(configure);
			_map.Context(
				segment,
				scoped => configure(new ScopedReplApp(scoped, _services)),
				validation);
			return this;
		}

		public IReplApp MapModule<TModule>()
			where TModule : class, IReplModule
		{
			return MapModule(ResolveModuleFromServices<TModule>(_services));
		}

		public IReplApp MapModule(IReplModule module)
		{
			_map.MapModule(module);
			return this;
		}

		ICoreReplApp ICoreReplApp.Context(string segment, Action<ICoreReplApp> configure, Delegate? validation) =>
			Context(segment, scoped => configure(scoped), validation);

		IReplMap IReplMap.Context(string segment, Action<IReplMap> configure, Delegate? validation) =>
			((IReplMap)_map).Context(segment, configure, validation);

		ICoreReplApp ICoreReplApp.MapModule(IReplModule module) => MapModule(module);

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
	}
}
