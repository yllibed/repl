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

	private static readonly SearchValues<char> EscapedCommandLineChars = CreateEscapedCommandLineChars();

	private readonly bool _enabled;
	private readonly bool _isVsCodeBackend;
	private readonly string _oscCode;
	private Phase _phase;

	private ShellIntegrationMarkEmitter(bool enabled, bool isVsCodeBackend)
	{
		_enabled = enabled;
		_isVsCodeBackend = isVsCodeBackend;
		_oscCode = isVsCodeBackend ? "633" : "133";
	}

	public static ShellIntegrationMarkEmitter Create(
		TerminalIntegrationOptions? options,
		OutputOptions outputOptions)
	{
		ArgumentNullException.ThrowIfNull(outputOptions);
		var enabled = ResolveEnabled(options, outputOptions);
		return new ShellIntegrationMarkEmitter(enabled, enabled && IsVsCodeBackend());
	}

	/// <summary>Prompt start (mark A): call before writing the prompt text.</summary>
	public async ValueTask WritePromptStartAsync()
	{
		if (!_enabled)
		{
			return;
		}

		_phase = Phase.Prompt;
		await ReplSessionIO.Output.WriteAsync($"\x1b]{_oscCode};A{Bell}").ConfigureAwait(false);
	}

	/// <summary>Prompt end / input start (mark B): call after the prompt text, before reading the line.</summary>
	public async ValueTask WriteInputStartAsync()
	{
		if (!_enabled || _phase != Phase.Prompt)
		{
			return;
		}

		_phase = Phase.Input;
		await ReplSessionIO.Output.WriteAsync($"\x1b]{_oscCode};B{Bell}").ConfigureAwait(false);
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
		await ReplSessionIO.Output.WriteAsync($"\x1b]{_oscCode};C{Bell}").ConfigureAwait(false);
	}

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
		var suffix = exitCode is { } code
			? $";{code.ToString(CultureInfo.InvariantCulture)}"
			: string.Empty;
		await ReplSessionIO.Output.WriteAsync($"\x1b]{_oscCode};D{suffix}{Bell}").ConfigureAwait(false);
	}

	/// <summary>
	/// Escapes a command line for the OSC 633;E payload per the VS Code shell-integration
	/// spec: <c>\</c> becomes <c>\\</c>, <c>;</c> becomes <c>\x3b</c>, and control
	/// characters below 0x20 become <c>\xHH</c> (lowercase hex).
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
			else if (ch < ' ')
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

	private static bool ResolveEnabled(TerminalIntegrationOptions? options, OutputOptions outputOptions)
	{
		// Same structural gates as advanced progress (OSC 9;4): marks must never reach
		// protocol streams, non-ANSI writers, or redirected local output.
		if (options is null
			|| ReplSessionIO.IsProtocolPassthrough
			|| !outputOptions.IsAnsiEnabled()
			|| (Console.IsOutputRedirected && !ReplSessionIO.IsSessionActive))
		{
			return false;
		}

		return options.ShellIntegration switch
		{
			ShellIntegrationMode.Always => true,
			ShellIntegrationMode.Never => false,
			_ => SessionAdvertisesShellIntegration() || TerminalEnvironmentClassifier.IsKnownShellIntegrationTerminal(),
		};
	}

	private static bool SessionAdvertisesShellIntegration() =>
		ReplSessionIO.IsSessionActive
		&& ReplSessionIO.TerminalCapabilities.HasFlag(TerminalCapabilities.ShellIntegrationMarks);

	private static bool IsVsCodeBackend() =>
		TerminalEnvironmentClassifier.IsVsCodeTerminal()
		|| (ReplSessionIO.IsSessionActive
			&& ReplSessionIO.TerminalIdentity?.Contains("vscode", StringComparison.OrdinalIgnoreCase) is true);

	// The escape set is backslash, semicolon, and every control character below 0x20;
	// built programmatically to keep raw control bytes out of the source file.
	private static SearchValues<char> CreateEscapedCommandLineChars()
	{
		Span<char> chars = stackalloc char[34];
		chars[0] = '\\';
		chars[1] = ';';
		for (var i = 0; i < 32; i++)
		{
			chars[i + 2] = (char)i;
		}

		return SearchValues.Create(chars);
	}
}
