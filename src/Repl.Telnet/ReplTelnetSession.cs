using System.IO.Pipelines;
using System.Net.WebSockets;

namespace Repl.Telnet;

/// <summary>
/// Runs a REPL session over a Telnet-framed transport.
/// Handles Telnet negotiation (BINARY, SGA, ECHO, NAWS, TERMINAL-TYPE)
/// and delegates to <see cref="StreamedReplHost"/> for session management.
/// </summary>
public static class ReplTelnetSession
{
	/// <summary>
	/// Runs a REPL app session over a Telnet-framed bidirectional pipe.
	/// This is the primary overload — all other overloads adapt to <see cref="IDuplexPipe"/>.
	/// </summary>
	public static async ValueTask<int> RunAsync(
		ReplApp app,
		IDuplexPipe pipe,
		ReplRunOptions? options = null,
		CancellationToken cancellationToken = default) =>
		await RunAsync(
			app,
			pipe,
			options,
			onWindowSizeChanged: null,
			onTerminalTypeChanged: null,
			cancellationToken).ConfigureAwait(false);

	/// <summary>
	/// Runs a REPL app session over a Telnet-framed bidirectional pipe and exposes
	/// Telnet negotiation metadata callbacks (NAWS and TERMINAL-TYPE).
	/// </summary>
	public static async ValueTask<int> RunAsync(
		ReplApp app,
		IDuplexPipe pipe,
		ReplRunOptions? options,
		Action<int, int>? onWindowSizeChanged,
		Action<string>? onTerminalTypeChanged,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(app);
		ArgumentNullException.ThrowIfNull(pipe);

		var framing = new TelnetFraming(pipe);
		return await RunCoreAsync(app, framing, options, onWindowSizeChanged, onTerminalTypeChanged, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Runs a REPL app session over a Telnet-framed bidirectional stream
	/// (TCP <see cref="System.Net.Sockets.NetworkStream"/>, named pipe, etc.).
	/// The stream is adapted to an <see cref="IDuplexPipe"/> internally.
	/// </summary>
	public static async ValueTask<int> RunAsync(
		ReplApp app,
		Stream stream,
		ReplRunOptions? options = null,
		CancellationToken cancellationToken = default) =>
		await RunAsync(
			app,
			stream,
			options,
			onWindowSizeChanged: null,
			onTerminalTypeChanged: null,
			cancellationToken).ConfigureAwait(false);

	/// <summary>
	/// Runs a REPL app session over a Telnet-framed bidirectional stream and exposes
	/// Telnet negotiation metadata callbacks (NAWS and TERMINAL-TYPE).
	/// </summary>
	public static async ValueTask<int> RunAsync(
		ReplApp app,
		Stream stream,
		ReplRunOptions? options,
		Action<int, int>? onWindowSizeChanged,
		Action<string>? onTerminalTypeChanged,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(app);
		ArgumentNullException.ThrowIfNull(stream);

		var pipe = new StreamDuplexPipe(stream);
		try
		{
			return await RunAsync(
				app,
				pipe,
				options,
				onWindowSizeChanged,
				onTerminalTypeChanged,
				cancellationToken)
				.ConfigureAwait(false);
		}
		finally
		{
			await pipe.DisposeAsync().ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Runs a REPL app session over a Telnet-framed WebSocket connection.
	/// The WebSocket is adapted to an <see cref="IDuplexPipe"/> internally.
	/// </summary>
	public static async ValueTask<int> RunAsync(
		ReplApp app,
		WebSocket socket,
		ReplRunOptions? options = null,
		CancellationToken cancellationToken = default) =>
		await RunAsync(
			app,
			socket,
			options,
			onWindowSizeChanged: null,
			onTerminalTypeChanged: null,
			cancellationToken).ConfigureAwait(false);

	/// <summary>
	/// Runs a REPL app session over a Telnet-framed WebSocket connection and exposes
	/// Telnet negotiation metadata callbacks (NAWS and TERMINAL-TYPE).
	/// </summary>
	public static async ValueTask<int> RunAsync(
		ReplApp app,
		WebSocket socket,
		ReplRunOptions? options,
		Action<int, int>? onWindowSizeChanged,
		Action<string>? onTerminalTypeChanged,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(app);
		ArgumentNullException.ThrowIfNull(socket);

		var stream = new WebSocketStream(socket, cancellationToken);
		try
		{
			return await RunAsync(
				app,
				stream,
				options,
				onWindowSizeChanged,
				onTerminalTypeChanged,
				cancellationToken)
				.ConfigureAwait(false);
		}
		finally
		{
			await stream.DisposeAsync().ConfigureAwait(false);
		}
	}

	private static async ValueTask<int> RunCoreAsync(
		ReplApp app,
		TelnetFraming framing,
		ReplRunOptions? options,
		Action<int, int>? onWindowSizeChanged,
		Action<string>? onTerminalTypeChanged,
		CancellationToken cancellationToken)
	{
		var nawsProvider = new NawsWindowSizeProvider(framing);
		var host = new StreamedReplHost(framing.Output, nawsProvider)
		{
			TransportName = "telnet",
			TerminalIdentityResolver = () => framing.TerminalType,
		};
		framing.TerminalTypeChanged += (_, eventArgs) => host.UpdateTerminalIdentity(eventArgs.TerminalType);
		if (onWindowSizeChanged is not null)
		{
			framing.WindowSizeChanged += (_, eventArgs) => onWindowSizeChanged(eventArgs.Width, eventArgs.Height);
		}

		if (onTerminalTypeChanged is not null)
		{
			framing.TerminalTypeChanged += (_, eventArgs) => onTerminalTypeChanged(eventArgs.TerminalType);
		}

		// Telnet clients support VT — force ANSI if mode is Auto.
		var runOptions = options ?? new ReplRunOptions();
		if (runOptions.AnsiSupport == AnsiMode.Auto)
		{
			runOptions = runOptions with { AnsiSupport = AnsiMode.Always };
		}

		var framingTask = framing.RunAsync(cancellationToken);
		Task pipeTask = Task.CompletedTask;

		try
		{
			pipeTask = PipeInputAsync(framing.Input, host, cancellationToken);

			int exitCode;
			try
			{
				exitCode = await host.RunSessionAsync(app, runOptions, cancellationToken)
					.ConfigureAwait(false);
			}
			finally
			{
				host.Complete();
			}

			return exitCode;
		}
		finally
		{
			await framing.DisposeAsync().ConfigureAwait(false);
			try { await pipeTask.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
			try { await framingTask.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
	}

	private static async Task PipeInputAsync(
		ChannelTextReader framingInput,
		StreamedReplHost host,
		CancellationToken ct)
	{
		var buffer = new char[4096];
		try
		{
			while (!ct.IsCancellationRequested)
			{
				var read = await framingInput.ReadAsync(buffer, ct).ConfigureAwait(false);
				if (read == 0) break;
				host.EnqueueInput(new string(buffer, 0, read));
			}
		}
		catch (OperationCanceledException) { }
	}
}
