using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Repl;

/// <summary>Declares preparation configuration, frozen on the first preparation.</summary>
/// <remarks>Configure sequentially before preparation. Independent preparations may then run concurrently.</remarks>
public sealed class ReplStartupBuilder
{
	private readonly List<Action<CoreReplApp>> _configuration = [];
	private readonly List<Delegate> _hooks = [];
	private bool _frozen;
	internal ReplStartupBuilder() { }

	/// <summary>Configures options used for preparation and imported into created applications.</summary>
	/// <param name="configure">Configuration callback; it must be safe to apply to independent applications.</param>
	/// <returns>The builder.</returns>
	public ReplStartupBuilder Options(Action<ReplOptions> configure)
	{
		ArgumentNullException.ThrowIfNull(configure);
		return Configure(app => app.Options(configure));
	}

	/// <summary>Declares a typed global option group once for preparation and execution.</summary>
	/// <typeparam name="T">Options type.</typeparam>
	/// <returns>The builder.</returns>
	public ReplStartupBuilder UseGlobalOptions<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>() where T : class, new() =>
		Configure(app => app.UseGlobalOptions<T>());

	/// <summary>Adds an argument adapter that also transfers to the application's interactive pipeline.</summary>
	/// <param name="adapter">Argument adapter.</param>
	/// <returns>The builder.</returns>
	[OverloadResolutionPriority(1)]
	public ReplStartupBuilder UseArgumentPreprocessor(Func<ArgumentPreprocessingContext, CancellationToken, ValueTask<ArgumentPreprocessingResult>> adapter)
	{
		ArgumentNullException.ThrowIfNull(adapter);
		return Configure(app => app.UseArgumentPreprocessor(adapter));
	}

	/// <inheritdoc cref="UseArgumentPreprocessor(Func{ArgumentPreprocessingContext, CancellationToken, ValueTask{ArgumentPreprocessingResult}})"/>
	public ReplStartupBuilder UseArgumentPreprocessor(Func<ArgumentPreprocessingContext, CancellationToken, ArgumentPreprocessingResult> adapter)
	{
		ArgumentNullException.ThrowIfNull(adapter);
		return Configure(app => app.UseArgumentPreprocessor(adapter));
	}

	/// <summary>Adds an ordered preparation hook. Application services are not available here.</summary>
	/// <param name="hook">Hook returning an argument decision, synchronously or asynchronously.</param>
	/// <returns>The builder.</returns>
	public ReplStartupBuilder UsePreparationHook(Delegate hook)
	{
		ArgumentNullException.ThrowIfNull(hook);
		ThrowIfFrozen();
		_hooks.Add(hook);
		return this;
	}

	/// <summary>Normalizes and prepares an independent invocation without constructing application services.</summary>
	/// <param name="args">Original arguments.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>A snapshot or a terminal result usable before application construction.</returns>
	public async ValueTask<PreparedReplStartup> PrepareAsync(string[] args, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(args);
		cancellationToken.ThrowIfCancellationRequested();
		_frozen = true;
		var configuration = _configuration.ToArray();
		var hooks = _hooks.ToArray();
		var app = CoreReplApp.Create();
		foreach (var configure in configuration) { configure(app); }
		var invocation = await app.PrepareStartupAsync(args, hooks, cancellationToken).ConfigureAwait(false);
		return new(this, configuration, app, invocation);
	}

	private ReplStartupBuilder Configure(Action<CoreReplApp> configure)
	{
		ThrowIfFrozen();
		_configuration.Add(configure);
		return this;
	}

	private void ThrowIfFrozen()
	{
		if (_frozen) { throw new InvalidOperationException("Preparation configuration is frozen. Create a new builder to change it."); }
	}
}
