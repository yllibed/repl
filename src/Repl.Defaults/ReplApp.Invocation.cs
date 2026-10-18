using Microsoft.Extensions.Hosting;

namespace Repl;

public sealed partial class ReplApp
{
	/// <inheritdoc cref="CoreReplApp.UseInvocationHook"/>
	public ReplApp UseInvocationHook(Delegate hook)
	{
		_core.UseInvocationHook(hook);
		return this;
	}

	/// <summary>Runs a textual or native structured request using application services.</summary>
	/// <param name="request">Invocation target and values.</param>
	/// <param name="options">Run options.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code.</returns>
	public async ValueTask<int> RunAsync(ReplInvocationRequest request, ReplRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		using var scope = _core.PushInvocationRequest(request, ReplInvocationChannel.Programmatic);
		return await RunAsync(_core.GetInvocationArguments(request), options, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc cref="RunAsync(ReplInvocationRequest, ReplRunOptions?, CancellationToken)"/>
	public ValueTask<int> RunAsync(ReplInvocationRequest request, CancellationToken cancellationToken) =>
		RunAsync(request, options: null, cancellationToken);

	/// <summary>Runs a structured request with caller-owned services.</summary>
	/// <param name="request">Invocation target and values.</param>
	/// <param name="services">Caller-owned services.</param>
	/// <param name="options">Run options.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code.</returns>
	public async ValueTask<int> RunAsync(ReplInvocationRequest request, IServiceProvider services,
		ReplRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		using var scope = _core.PushInvocationRequest(request, ReplInvocationChannel.Programmatic);
		return await RunAsync(_core.GetInvocationArguments(request), services, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Runs a structured request in a caller-owned generic host.</summary>
	/// <param name="request">Invocation target and values.</param>
	/// <param name="host">Caller-owned host.</param>
	/// <param name="options">Run options.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code.</returns>
	public ValueTask<int> RunAsync(ReplInvocationRequest request, IHost host, ReplRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(host);
		return RunAsync(request, host.Services, options, cancellationToken);
	}

	/// <summary>Runs a structured request with externally managed input and output.</summary>
	/// <param name="request">Invocation target and values.</param>
	/// <param name="host">Input/output host.</param>
	/// <param name="options">Run options.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code.</returns>
	public async ValueTask<int> RunAsync(ReplInvocationRequest request, IReplHost host,
		ReplRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		using var scope = _core.PushInvocationRequest(request, ReplInvocationChannel.Programmatic);
		return await RunAsync(_core.GetInvocationArguments(request), host, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Runs a structured request with externally managed input, output and services.</summary>
	/// <param name="request">Invocation target and values.</param>
	/// <param name="host">Input/output host.</param>
	/// <param name="services">Caller-owned services.</param>
	/// <param name="options">Run options.</param>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code.</returns>
	public async ValueTask<int> RunAsync(ReplInvocationRequest request, IReplHost host, IServiceProvider services,
		ReplRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		using var scope = _core.PushInvocationRequest(request, ReplInvocationChannel.Programmatic);
		return await RunAsync(_core.GetInvocationArguments(request), host, services, options, cancellationToken).ConfigureAwait(false);
	}
}
