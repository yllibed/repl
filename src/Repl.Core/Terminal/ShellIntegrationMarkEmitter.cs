using System.Buffers;
using System.Globalization;
using System.Text;

namespace Repl;

/// <summary>
/// Emits shell-integration lifecycle marks (OSC 133 generic backend, OSC 633 for
/// VS Code) around the interactive prompt and command execution. Enablement and
/// backend are resolved once per interactive session; a small phase state machine
/// guarantees at most one command-end mark per prompt cycle.
/// </summary>
internal sealed class ShellIntegrationMarkEmitter
{
	private enum Phase
	{
		Idle,
		Prompt,
		Input,
		Executing,
	}

	private const string Bell = "\x07";

	// Declared to VS Code before the first prompt on a local Windows console (see
	// ReportVsCodePromptContextAsync). Internal so a test can pin the exact bytes: the
	// emission branch itself only runs on a local console, which the hosted test harness
	// cannot exercise, so a typo here would otherwise ship undetected.
	internal const string WindowsPtyProperty = "\x1b]633;P;IsWindows=True\x07";

	// The A/B/C/D-no-code marks are constant per backend; precomputed so the per-prompt
	// path allocates no throw-away interpolated strings. Only the variable-payload marks
	// still format per call: D-with-exit-code and the 633;E command-line report.
	private static readonly MarkSet Osc133 = new("\x1b]133;A\x07", "\x1b]133;B\x07", "\x1b]133;C\x07", "\x1b]133;D\x07");
	private static readonly MarkSet Osc633 = new("\x1b]633;A\x07", "\x1b]633;B\x07", "\x1b]633;C\x07", "\x1b]633;D\x07");

	private static readonly SearchValues<char> EscapedCommandLineChars = CreateEscapedCommandLineChars();

	private readonly TerminalIntegrationOptions? _options;
	private readonly OutputOptions _outputOptions;
	private bool _enabled;
	private bool _isVsCodeBackend;
	private MarkSet _marks = Osc133;
	private Phase _phase;
	private bool _windowsPtyReported;
	private string? _reportedPrompt;
	private bool _sessionOpened;
	private readonly ShellIntegrationStatusAmbient.Slot? _statusSlot;
	private ShellIntegrationGate? _publishedGate;
	private bool _publishedVsCodeBackend;

	private readonly record struct MarkSet(string PromptStart, string InputStart, string OutputStart, string CommandEndNoCode);

	private ShellIntegrationMarkEmitter(
		TerminalIntegrationOptions? options,
		OutputOptions outputOptions,
		ShellIntegrationStatusAmbient.Slot? statusSlot)
	{
		_options = options;
		_outputOptions = outputOptions;
		_statusSlot = statusSlot;
	}

	public static ShellIntegrationMarkEmitter Create(
		TerminalIntegrationOptions? options,
		OutputOptions outputOptions,
		ShellIntegrationStatusAmbient.Slot? statusSlot = null)
	{
		ArgumentNullException.ThrowIfNull(outputOptions);
		return new ShellIntegrationMarkEmitter(options, outputOptions, statusSlot);
	}

	/// <summary>
	/// Prompt start (mark A): call before writing the prompt text. Pass the prompt text
	/// the loop is about to render so the VS Code backend can declare it (see
	/// <see cref="ReportVsCodePromptContextAsync"/>).
	/// </summary>
	public async ValueTask WritePromptStartAsync(string? promptText = null)
	{
		// Hosted clients can advertise capabilities mid-session (Telnet TTYPE,
		// @@repl:* control messages), so enablement and backend are re-resolved at
		// each prompt and frozen for the cycle to keep its marks consistent.
		RefreshCycleConfiguration();
		if (!_enabled)
		{
			return;
		}

		// The opener below only fixes a process-start anchor, so the latch is taken on the
		// FIRST enabled prompt of the session whatever the backend: a mid-session backend
		// flip to VS Code (client re-identification) must not fire a stray aborted D
		// between live commands.
		var isFirstEnabledPrompt = !_sessionOpened;
		_sessionOpened = true;

		if (_isVsCodeBackend)
		{
			// Nested-terminal handshake: when this app was launched from an integrated
			// shell, that shell's command (this very process) is still open from VS Code's
			// point of view, and handlePromptStart would anchor our first prompt at that
			// command's stale end position — the first gutter decoration then lands on the
			// banner. A lone command-end (aborted form: the outer exit code is unknowable)
			// closes it at the true cursor position first. Real shells don't need this
			// because they are not nested; harmless when nothing was open.
			if (isFirstEnabledPrompt)
			{
				await ReplSessionIO.Output.WriteAsync(_marks.CommandEndNoCode).ConfigureAwait(false);
			}

			await ReportVsCodePromptContextAsync(promptText).ConfigureAwait(false);
		}

		_phase = Phase.Prompt;
		await ReplSessionIO.Output.WriteAsync(_marks.PromptStart).ConfigureAwait(false);
	}

	// VS Code anchors command decorations at parse-time cursor positions, which ConPTY
	// rewrites on a local Windows console (worst right at process start — the first
	// command's decoration lands on the banner). Real shells compensate by declaring
	// 633;P properties: IsWindows=True switches VS Code's command detection to its
	// marker-adjusting heuristics, and Prompt=<text> lets those heuristics recognize a
	// custom prompt line. Both must precede the first input-start (B) so they already
	// cover the very first command.
	private async ValueTask ReportVsCodePromptContextAsync(string? promptText)
	{
		if (!_windowsPtyReported)
		{
			_windowsPtyReported = true;
			if (ShouldReportWindowsConPty())
			{
				await ReplSessionIO.Output.WriteAsync(WindowsPtyProperty).ConfigureAwait(false);
			}
		}

		if (promptText is not null && !string.Equals(_reportedPrompt, promptText, StringComparison.Ordinal))
		{
			_reportedPrompt = promptText;
			await ReplSessionIO.Output.WriteAsync($"\x1b]633;P;Prompt={EscapeCommandLine(promptText)}{Bell}").ConfigureAwait(false);
		}
	}

	// Hosted transports deliver our bytes verbatim to the remote terminal (no ConPTY in
	// the path), so they keep VS Code's position-trusting default; only a local console
	// on Windows goes through ConPTY.
	internal static bool ShouldReportWindowsConPty() =>
		!ReplSessionIO.IsSessionActive && OperatingSystem.IsWindows();

	/// <summary>Prompt end / input start (mark B): call after the prompt text, before reading the line.</summary>
	public async ValueTask WriteInputStartAsync()
	{
		if (!_enabled || _phase != Phase.Prompt)
		{
			return;
		}

		_phase = Phase.Input;
		await ReplSessionIO.Output.WriteAsync(_marks.InputStart).ConfigureAwait(false);
	}

	/// <summary>
	/// Command-line report (mark E, VS Code backend only): call after the user commits a
	/// non-empty line, before <see cref="WriteOutputStartAsync"/>. Silent no-op on OSC 133.
	/// </summary>
	public async ValueTask WriteCommandLineAsync(string commandLine)
	{
		ArgumentNullException.ThrowIfNull(commandLine);
		if (!_enabled || !_isVsCodeBackend || _phase != Phase.Input)
		{
			return;
		}

		await ReplSessionIO.Output.WriteAsync($"\x1b]633;E;{EscapeCommandLine(commandLine)}{Bell}").ConfigureAwait(false);
	}

	/// <summary>Pre-execution / output start (mark C): call right before dispatching the command.</summary>
	public async ValueTask WriteOutputStartAsync()
	{
		if (!_enabled || _phase != Phase.Input)
		{
			return;
		}

		_phase = Phase.Executing;
		await ReplSessionIO.Output.WriteAsync(_marks.OutputStart).ConfigureAwait(false);
	}

	/// <summary>
	/// Closes the current cycle without writing anything. Used for protocol-passthrough
	/// commands, where a trailing D mark would land in the protocol stream; the next
	/// prompt-start mark implicitly aborts the unterminated cycle on the terminal side.
	/// </summary>
	public void AbandonCycle() => _phase = Phase.Idle;

	/// <summary>
	/// Command end (mark D): call once per prompt cycle. Pass <c>null</c> for aborted or
	/// empty input (FinalTerm "command aborted" form, no exit-code parameter). No-op when
	/// no cycle is open, so a double call can never emit two D marks.
	/// </summary>
	public async ValueTask WriteCommandEndAsync(int? exitCode)
	{
		if (!_enabled || _phase == Phase.Idle)
		{
			return;
		}

		_phase = Phase.Idle;
		var mark = exitCode is { } code
			? $"\x1b]{(_isVsCodeBackend ? "633" : "133")};D;{code.ToString(CultureInfo.InvariantCulture)}{Bell}"
			: _marks.CommandEndNoCode;
		await ReplSessionIO.Output.WriteAsync(mark).ConfigureAwait(false);
	}

	/// <summary>
	/// Escapes a command line for the OSC 633;E payload per the VS Code shell-integration
	/// contract: <c>\</c> becomes <c>\\</c>, <c>;</c> becomes <c>\x3b</c>, and every byte
	/// that could break out of the OSC string — space and C0 controls (&lt;= 0x20), DEL
	/// (0x7f), and the C1 controls (0x80–0x9f, which include the 8-bit ST/OSC/CSI
	/// introducers xterm.js and VTE act on) — becomes <c>\xHH</c> (lowercase hex).
	/// </summary>
	internal static string EscapeCommandLine(string commandLine)
	{
		var first = commandLine.AsSpan().IndexOfAny(EscapedCommandLineChars);
		if (first < 0)
		{
			return commandLine;
		}

		const string hexDigits = "0123456789abcdef";
		var builder = new StringBuilder(commandLine.Length + 8);
		builder.Append(commandLine, 0, first);
		foreach (var ch in commandLine.AsSpan(first))
		{
			if (ch == '\\')
			{
				builder.Append(@"\\");
			}
			else if (ch == ';')
			{
				builder.Append(@"\x3b");
			}
			else if (IsForbiddenControl(ch))
			{
				builder.Append(@"\x").Append(hexDigits[(ch >> 4) & 0xF]).Append(hexDigits[ch & 0xF]);
			}
			else
			{
				builder.Append(ch);
			}
		}

		return builder.ToString();
	}

	// Space + C0 controls, DEL, and C1 controls: all can terminate or forge the OSC
	// string on terminals that decode 8-bit control codes. The C1 clause needs an explicit
	// upper bound: this predicate runs on every char once the builder path opens, so
	// without it a legitimate char >= 0xa0 (e.g. 'é') that follows an escapable char would
	// be escaped too.
	private static bool IsForbiddenControl(char ch) => ch <= ' ' || ch == '\x7f' || (ch >= '\x80' && ch <= '\x9f');

	/// <summary>
	/// The gate that decided enablement for the current prompt cycle. Diagnostic-only:
	/// lets tests (and debugging sessions) see WHY marks are on or off instead of
	/// reverse-engineering the decision from the emitted bytes.
	/// </summary>
	internal ShellIntegrationGate LastGate { get; private set; } = ShellIntegrationGate.NotConfigured;

	// Resolves enablement and backend for the cycle that is about to start. Cheap by
	// design: environment variables are only consulted when no hosted session is active.
	private void RefreshCycleConfiguration()
	{
		LastGate = ResolveGate(_options, _outputOptions);
		_enabled = LastGate == ShellIntegrationGate.Enabled;
		_isVsCodeBackend = _enabled && IsVsCodeBackend();
		_marks = _isVsCodeBackend ? Osc633 : Osc133;
		PublishStatus();
	}

	// Publishes the human-readable detection outcome for IReplSessionInfo consumers
	// (debug/sample commands); rebuilt only when the decision actually changed.
	private void PublishStatus()
	{
		if (_statusSlot is not { } slot
			|| (_publishedGate == LastGate && _publishedVsCodeBackend == _isVsCodeBackend))
		{
			return;
		}

		_publishedGate = LastGate;
		_publishedVsCodeBackend = _isVsCodeBackend;
		slot.Status = _enabled
			? (_isVsCodeBackend ? "OSC 633 (VS Code)" : "OSC 133")
			: $"off ({LastGate})";
	}

	// Gates are evaluated in ShellIntegrationGate member order; the first failing gate
	// names the decision. The structural gates mirror advanced progress (OSC 9;4): marks
	// must never reach protocol streams, non-ANSI writers, or redirected local output.
	private static ShellIntegrationGate ResolveGate(TerminalIntegrationOptions? options, OutputOptions outputOptions)
	{
		if (options is null)
		{
			return ShellIntegrationGate.NotConfigured;
		}

		if (ReplSessionIO.IsProtocolPassthrough)
		{
			return ShellIntegrationGate.ProtocolPassthrough;
		}

		if (!TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(outputOptions))
		{
			return ShellIntegrationGate.AnsiUnsupported;
		}

		if (Console.IsOutputRedirected && !ReplSessionIO.IsSessionActive)
		{
			return ShellIntegrationGate.OutputRedirected;
		}

		// Hosted sessions decide from what the remote client advertised, never from the
		// server's own environment: WT_SESSION/TERM_PROGRAM describe the terminal the
		// server runs in, not the WebSocket/Telnet client on the other end.
		return options.ShellIntegration switch
		{
			ShellIntegrationMode.Always => ShellIntegrationGate.Enabled,
			ShellIntegrationMode.Never => ShellIntegrationGate.ModeNever,
			_ when ReplSessionIO.IsSessionActive => SessionAdvertisesShellIntegration()
				? ShellIntegrationGate.Enabled
				: ShellIntegrationGate.SessionNotAdvertising,
			_ => TerminalEnvironmentClassifier.IsKnownShellIntegrationTerminal()
				? ShellIntegrationGate.Enabled
				: ShellIntegrationGate.EnvironmentUnknown,
		};
	}

	private static bool SessionAdvertisesShellIntegration() =>
		ReplSessionIO.TerminalCapabilities.HasFlag(TerminalCapabilities.ShellIntegrationMarks);

	// Same session-boundary rule as ResolveEnabled: the 633 dialect is chosen from the
	// client-reported identity for hosted sessions, from the environment locally.
	private static bool IsVsCodeBackend() =>
		ReplSessionIO.IsSessionActive
			? ReplSessionIO.TerminalIdentity?.Contains("vscode", StringComparison.OrdinalIgnoreCase) is true
			: TerminalEnvironmentClassifier.IsVsCodeTerminal();

	// The escape set is backslash, semicolon, space + C0 controls (0x00–0x20), DEL (0x7f),
	// and the C1 controls (0x80–0x9f); built programmatically to keep raw control bytes
	// out of the source file. The set never contains chars above 0x9f — but the
	// IsForbiddenControl predicate, which runs on EVERY char once the builder path opens,
	// still needs its explicit 0x9f ceiling (see its comment and the 'é' regression test);
	// only the SearchValues set itself is bound-free by construction.
	private static SearchValues<char> CreateEscapedCommandLineChars()
	{
		// 2 punctuation + 33 (0x00–0x20) + 1 (DEL) + 32 (0x80–0x9f) = 68.
		Span<char> chars = stackalloc char[68];
		chars[0] = '\\';
		chars[1] = ';';
		var next = 2;
		for (var i = 0; i <= 0x20; i++)
		{
			chars[next++] = (char)i;
		}

		chars[next++] = '\x7f';
		for (var i = 0x80; i <= 0x9f; i++)
		{
			chars[next++] = (char)i;
		}

		return SearchValues.Create(chars);
	}
}
