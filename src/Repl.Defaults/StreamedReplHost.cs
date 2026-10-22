namespace Repl;

/// <summary>
/// Generic session-layer host (L5) that is transport-agnostic.
/// The transport provides a <see cref="TextWriter"/> for output and pushes
/// raw text through <see cref="EnqueueInput"/>. This host handles the
/// session lifecycle including optional VT probing and delegates to
/// <see cref="ReplApp.RunAsync(string[], IReplHost, ReplRunOptions?, CancellationToken)"/>.
/// </summary>
public sealed class StreamedReplHost : IReplSessionHost, IAsyncDisposable
{
	private readonly ChannelTextReader _input = new();
	private readonly IWindowSizeProvider? _windowSizeProvider;
	private readonly Lock _registration = new();
	private bool _disposed;
	private string? _transportName;
	private string? _remotePeer;

	/// <summary>
	/// Initializes a new instance of the <see cref="StreamedReplHost"/> class.
	/// </summary>
	/// <param name="output">Transport-provided output writer.</param>
	/// <param name="windowSizeProvider">
	/// Optional window size provider. When <c>null</c>, a <see cref="DttermWindowSizeProvider"/>
	/// is created automatically for DTTERM in-band VT detection.
	/// </param>
	public StreamedReplHost(TextWriter output, IWindowSizeProvider? windowSizeProvider = null)
	{
		ArgumentNullException.ThrowIfNull(output);
		Output = output;
		_windowSizeProvider = windowSizeProvider;
		SessionId = $"session-{Guid.NewGuid():N}";
		ReplSessionIO.EnsureSession(SessionId);
	}

	/// <inheritdoc />
	public TextReader Input => _input;

	/// <inheritdoc />
	public TextWriter Output { get; }

	/// <inheritdoc />
	public string SessionId { get; }

	/// <summary>
	/// Gets or sets the transport name (e.g. "websocket", "telnet", "signalr").
	/// Set this before calling <see cref="RunSessionAsync"/> to make it available
	/// via <see cref="IReplSessionInfo.TransportName"/>.
	/// </summary>
	public string? TransportName
	{
		get => _transportName;
		set => _transportName = value;
	}

	/// <summary>
	/// Gets or sets remote peer details for the active connection (for example "203.0.113.7:50124").
	/// </summary>
	public string? RemotePeer
	{
		get => _remotePeer;
		set => _remotePeer = value;
	}

	/// <summary>
	/// Gets or sets a deferred resolver for the terminal identity (e.g. "xterm-256color").
	/// Called after the initial detection phase when terminal-type negotiation
	/// (e.g. Telnet TERMINAL-TYPE) is expected to have completed.
	/// </summary>
	public Func<string?>? TerminalIdentityResolver { get; set; }

	/// <summary>
	/// Pushes a raw text chunk from the transport layer into the input reader.
	/// </summary>
	/// <param name="text">Text chunk to enqueue.</param>
	public void EnqueueInput(string text) => _input.Enqueue(text);

	/// <summary>
	/// Signals that no more input will arrive (connection closed).
	/// </summary>
	public void Complete() => _input.Complete();

	/// <summary>
	/// Updates the known terminal window size for this session.
	/// </summary>
	public void UpdateWindowSize(int width, int height)
	{
		if (width <= 0 || height <= 0)
		{
			return;
		}

		UpdateSession(
			session => session.WithExplicitCapabilities(TerminalCapabilities.ResizeReporting)
				with { WindowSize = (width, height), });
	}

	/// <summary>
	/// Updates the known terminal identity for this session.
	/// </summary>
	public void UpdateTerminalIdentity(string? terminalIdentity)
	{
		if (string.IsNullOrWhiteSpace(terminalIdentity))
		{
			return;
		}

		// Same replace-inferred semantics as ReplSessionIO.TerminalIdentity: a downgrade
		// (e.g. Windows Terminal → dumb) revokes the previously inferred capabilities.
		UpdateSession(
			session => session.WithTerminalIdentity(terminalIdentity));
	}

	/// <summary>
	/// Updates the known ANSI support for this session.
	/// </summary>
	public void UpdateAnsiSupport(bool? ansiSupported)
	{
		if (ansiSupported is null)
		{
			return;
		}

		var isAnsiSupported = ansiSupported.Value;
		UpdateSession(
			session =>
			{
				var updated = isAnsiSupported
					? session.WithExplicitCapabilities(TerminalCapabilities.Ansi)
					: session.WithoutCapabilities(TerminalCapabilities.Ansi);
				return updated with { AnsiSupport = isAnsiSupported };
			});
	}

	/// <summary>
	/// Updates the known terminal capability flags for this session.
	/// </summary>
	public void UpdateTerminalCapabilities(TerminalCapabilities capabilities)
	{
		UpdateSession(
			session => session.WithExplicitCapabilities(capabilities));
	}

	/// <summary>
	/// Applies a parsed terminal control message to the current session metadata.
	/// </summary>
	public void ApplyControlMessage(TerminalControlMessage message)
	{
		ArgumentNullException.ThrowIfNull(message);

		if (message.WindowSize is { } size)
		{
			UpdateWindowSize(size.Width, size.Height);
		}

		if (!string.IsNullOrWhiteSpace(message.TerminalIdentity))
		{
			UpdateTerminalIdentity(message.TerminalIdentity);
		}

		if (message.AnsiSupported is { } ansi)
		{
			UpdateAnsiSupport(ansi);
		}

		if (message.TerminalCapabilities is { } caps)
		{
			UpdateTerminalCapabilities(caps);
		}
	}

	/// <summary>
	/// Runs a full REPL session including optional VT probe detection.
	/// </summary>
	/// <param name="app">Configured REPL application.</param>
	/// <param name="options">Optional run options.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Execution exit code.</returns>
	/// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled, for example while the session waited for input, and neither <see cref="ExitCodeOptions.Cancelled"/> nor <see cref="ExitCodeOptions.Resolver"/> is set to map the cancellation to an exit code. A client that closes the session ends it normally instead.</exception>
	public async ValueTask<int> RunSessionAsync(
		ReplApp app,
		ReplRunOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(app);
		var runOptions = options ?? new ReplRunOptions();

		ApplyConfiguredMetadata(runOptions);
		var ansiMode = runOptions.AnsiSupport;
		var (provider, dttermProvider) = ResolveProvider();

		try
		{
			await DetectSizeAndAnsiAsync(provider, dttermProvider, runOptions, ansiMode, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// The run below reports the caller's cancellation as soon as it starts, under the configured cancellation
			// policy, as it does for a cancellation at any later point.
		}

		provider.SizeChanged += OnSizeChanged;
		SetupKeyReader(dttermProvider);

		try
		{
			return await app.RunAsync([], this, runOptions, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			provider.SizeChanged -= OnSizeChanged;
		}
	}

	/// <inheritdoc />
	/// <remarks>Once disposed, the host ignores further terminal metadata updates, so they cannot register the ended session again.</remarks>
	public ValueTask DisposeAsync()
	{
		Complete();
		lock (_registration)
		{
			_disposed = true;
			ReplSessionIO.RemoveSession(SessionId);
		}

		return default;
	}

	// ReplSessionIO.UpdateSession registers a session it does not know, and a transport may still report terminal
	// metadata once the session has ended, so an update after disposal must not register this session again.
	private void UpdateSession(Func<ReplSessionIO.SessionMetadata, ReplSessionIO.SessionMetadata> updater)
	{
		lock (_registration)
		{
			if (!_disposed)
			{
				ReplSessionIO.UpdateSession(SessionId, updater);
			}
		}
	}

	private (IWindowSizeProvider Provider, DttermWindowSizeProvider? Dtterm) ResolveProvider()
	{
		if (_windowSizeProvider is not null)
		{
			return (_windowSizeProvider, null);
		}

		var dtterm = new DttermWindowSizeProvider(this);
		return (dtterm, dtterm);
	}

	private async ValueTask DetectSizeAndAnsiAsync(
		IWindowSizeProvider provider,
		DttermWindowSizeProvider? dttermProvider,
		ReplRunOptions options,
		AnsiMode ansiMode,
		CancellationToken ct)
	{
		// Respect explicit overrides first.
		var overrideSize = options.TerminalOverrides?.WindowSize;
		if (overrideSize is { } forcedSize)
		{
			UpdateWindowSize(forcedSize.Width, forcedSize.Height);
		}
		else
		{
			// Always detect window size — the provider handles transport-specific logic
			// (DTTERM probe, NAWS wait, etc.).
			var size = await provider.GetSizeAsync(ct).ConfigureAwait(false);
			if (size is { } s)
			{
				UpdateWindowSize(s.Width, s.Height);
			}
		}

		if (options.TerminalOverrides?.AnsiSupported is { } forcedAnsi)
		{
			UpdateAnsiSupport(forcedAnsi);
			return;
		}

		if (ansiMode == AnsiMode.Always)
		{
			UpdateAnsiSupport(ansiSupported: true);
			return;
		}

		if (ansiMode == AnsiMode.Never)
		{
			UpdateAnsiSupport(ansiSupported: false);
			return;
		}

		// Auto: DTTERM uses probe result; other transports default to VT-capable.
		UpdateAnsiSupport(dttermProvider?.DetectedAnsiSupport ?? true);
	}

	private void ApplyConfiguredMetadata(ReplRunOptions runOptions)
	{
		var overrides = runOptions.TerminalOverrides;
		UpdateSession(
			session => session with
			{
				TransportName = !string.IsNullOrWhiteSpace(overrides?.TransportName)
					? overrides.TransportName
					: _transportName,
				RemotePeer = !string.IsNullOrWhiteSpace(overrides?.RemotePeer)
					? overrides.RemotePeer
					: _remotePeer,
			});

		var resolvedTerminalIdentity = !string.IsNullOrWhiteSpace(overrides?.TerminalIdentity)
			? overrides.TerminalIdentity
			: TerminalIdentityResolver?.Invoke();
		if (!string.IsNullOrWhiteSpace(resolvedTerminalIdentity))
		{
			UpdateTerminalIdentity(resolvedTerminalIdentity);
		}

		if (overrides?.TerminalCapabilities is { } forcedCapabilities)
		{
			UpdateSession(
				session => session with
				{
					TerminalCapabilities = forcedCapabilities,
					// Forced overrides are authoritative: nothing remains attributable to
					// identity inference until the next identity report.
					IdentityInferredCapabilities = TerminalCapabilities.None,
				});
		}
	}

	private void SetupKeyReader(DttermWindowSizeProvider? dttermProvider)
	{
		var keyReader = new VtKeyReader(_input);
		if (dttermProvider is not null)
		{
			keyReader.OnResize = (cols, rows) => dttermProvider.NotifyResize(cols, rows);
		}

		ReplSessionIO.KeyReader = keyReader;
	}

	private void OnSizeChanged(object? sender, WindowSizeEventArgs e) => UpdateWindowSize(e.Width, e.Height);
}
