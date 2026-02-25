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

		ReplSessionIO.UpdateSession(
			SessionId,
			session => session with
			{
				WindowSize = (width, height),
				TerminalCapabilities = session.TerminalCapabilities | TerminalCapabilities.ResizeReporting,
			});
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

		ReplSessionIO.UpdateSession(
			SessionId,
			session =>
			{
				var inferred = TerminalCapabilitiesClassifier.InferFromIdentity(terminalIdentity);
				return session with
				{
					TerminalIdentity = terminalIdentity,
					TerminalCapabilities = session.TerminalCapabilities | inferred,
				};
			});
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

		ReplSessionIO.UpdateSession(
			SessionId,
			session =>
			{
				var capabilities = ansiSupported.Value
					? session.TerminalCapabilities | TerminalCapabilities.Ansi
					: session.TerminalCapabilities & ~TerminalCapabilities.Ansi;
				return session with { AnsiSupport = ansiSupported.Value, TerminalCapabilities = capabilities };
			});
	}

	/// <summary>
	/// Updates the known terminal capability flags for this session.
	/// </summary>
	public void UpdateTerminalCapabilities(TerminalCapabilities capabilities)
	{
		ReplSessionIO.UpdateSession(
			SessionId,
			session => session with { TerminalCapabilities = session.TerminalCapabilities | capabilities });
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

		await DetectSizeAndAnsiAsync(provider, dttermProvider, runOptions, ansiMode, cancellationToken)
			.ConfigureAwait(false);

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
	public ValueTask DisposeAsync()
	{
		Complete();
		ReplSessionIO.RemoveSession(SessionId);
		return default;
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
		ReplSessionIO.UpdateSession(
			SessionId,
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
			ReplSessionIO.UpdateSession(
				SessionId,
				session => session with { TerminalCapabilities = forcedCapabilities });
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
