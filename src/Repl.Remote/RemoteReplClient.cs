using System.Text.Json;
using System.Threading.Channels;
using StreamJsonRpc;

namespace Repl.Remote;

/// <summary>A CLI-profile client. Disposing it leaves caller-provided streams open.</summary>
public sealed class RemoteReplClient : IAsyncDisposable
{
	private readonly JsonRpc _rpc;
	private RemoteTerminal? _terminal;
	private readonly Stream? _ownedStream;

	/// <summary>Creates a client over a borrowed duplex stream.</summary>
	public RemoteReplClient(Stream stream) : this(stream, stream) { }

	/// <summary>Creates a client over borrowed input and output streams.</summary>
	public RemoteReplClient(Stream input, Stream output) : this(input, output, ownedStream: null) { }

	internal RemoteReplClient(Stream input, Stream output, Stream? ownedStream)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(output);
		_ownedStream = ownedStream;
		_rpc = RemoteProtocol.CreateRpc(input, output);
		RemoteProtocol.RegisterTarget(_rpc, this);
		_rpc.StartListening();
		_ = ObserveConnectionAsync();
	}

	/// <summary>Reads the peer's OpenRPC document and checks the CLI profile major version.</summary>
	public async Task<JsonElement> DiscoverAsync(CancellationToken cancellationToken = default)
	{
		var document = await _rpc.InvokeWithParameterObjectAsync<JsonElement>("rpc.discover", new { }, cancellationToken).ConfigureAwait(false);
		if (!document.TryGetProperty("x-cli", out var profile) ||
			!profile.TryGetProperty("version", out var version) || !string.Equals(version.GetString()?.Split('.')[0], "1", StringComparison.Ordinal))
		{
			throw new NotSupportedException("The peer does not support CLI profile version 1.");
		}
		return document;
	}

	/// <summary>Reads the exposed command graph.</summary>
	public Task<RemoteCommandGraph> DescribeAsync(CancellationToken cancellationToken = default) =>
		_rpc.InvokeWithParameterObjectAsync<RemoteCommandGraph>("cli.describe", new { }, cancellationToken);

	/// <summary>Executes once; a lost response is never retried automatically.</summary>
	public Task<RemoteExecutionResult> ExecuteAsync(RemoteInvocationRequest request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		request.Validate();
		return _rpc.InvokeWithParameterObjectAsync<RemoteExecutionResult>("cli.execute", request, cancellationToken);
	}

	/// <summary>Opens a terminal; consume its output until completion.</summary>
	public async Task<RemoteTerminal> OpenTerminalAsync(RemoteTerminalOptions? options = null, CancellationToken cancellationToken = default)
	{
		options ??= new();
		options.Validate();
		var terminal = new RemoteTerminal(_rpc);
		if (Interlocked.CompareExchange(ref _terminal, terminal, comparand: null) is not null) { throw new InvalidOperationException("A terminal is already open."); }
		try
		{
			await _rpc.InvokeWithParameterObjectAsync("terminal.open", new { sessionId = terminal.SessionId, options }, cancellationToken).ConfigureAwait(false);
			return terminal;
		}
		catch (Exception ex) { Interlocked.CompareExchange(ref _terminal, value: null, comparand: terminal); terminal.Finish(ex); throw; }
	}

	[JsonRpcMethod("terminal.output")]
	private void Output(string sessionId, string text)
	{
		if (_terminal is { } terminal && string.Equals(terminal.SessionId, sessionId, StringComparison.Ordinal)) { terminal.AcceptOutput(text); }
	}

	[JsonRpcMethod("terminal.exited")]
	private void Exited(string sessionId, int exitCode)
	{
		if (_terminal is { } terminal && string.Equals(terminal.SessionId, sessionId, StringComparison.Ordinal))
		{
			Interlocked.CompareExchange(ref _terminal, value: null, comparand: terminal);
			terminal.Finish(exitCode);
		}
	}

	private async Task ObserveConnectionAsync()
	{
#pragma warning disable VSTHRD003 // Transport completion has no thread affinity.
		try { await _rpc.Completion.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
		catch (Exception ex) { Interlocked.Exchange(ref _terminal, value: null)?.Finish(ex); return; }
		Interlocked.Exchange(ref _terminal, value: null)?.Finish(new IOException("The peer disconnected before terminal.exited."));
	}

	/// <summary>Disconnects this client, disposing only a stream created by its named-pipe factory.</summary>
	public async ValueTask DisposeAsync()
	{
		_rpc.Dispose();
		if (_ownedStream is not null) { await _ownedStream.DisposeAsync().ConfigureAwait(false); }
	}
}
