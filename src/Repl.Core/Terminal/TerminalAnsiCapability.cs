namespace Repl;

/// <summary>
/// Shared ANSI gates. <see cref="IsAnsiCapableForColor(OutputOptions)"/> answers whether styling may be written;
/// <see cref="IsAnsiCapableForControlSequences(OutputOptions)"/> answers whether the terminal may be driven
/// (screen clearing, cursor-driven prompts and pager, shell-integration marks, advanced progress). Colour forced
/// onto a redirected stream is wanted in a pipe or a file, but a control sequence there is noise for whatever
/// reads it, so the second gate is the stricter one.
/// A hosted client can advertise ANSI purely through capability flags
/// (identity inference, control messages, <c>TerminalSessionOverrides</c>) without ever
/// setting the <c>AnsiSupport</c> override; honoring that flag avoids falling back to
/// <see cref="OutputOptions.IsColorEnabled()"/>'s view of the server console's redirection
/// state. Explicit opt-outs (<c>AnsiSupport=false</c>, <see cref="AnsiMode.Never"/>)
/// still win.
/// </summary>
internal static class TerminalAnsiCapability
{
	public static bool IsAnsiCapableForColor(OutputOptions outputOptions) =>
		IsAnsiCapableForColor(outputOptions, Console.IsOutputRedirected);

	/// <summary>
	/// Whether styling may be written to a stream whose redirection is <paramref name="isStreamRedirected"/>.
	/// </summary>
	public static bool IsAnsiCapableForColor(OutputOptions outputOptions, bool isStreamRedirected) =>
		outputOptions.IsColorEnabled(isStreamRedirected) || IsHostedClientAnsiCapable(outputOptions);

	public static bool IsAnsiCapableForControlSequences(OutputOptions outputOptions) =>
		IsAnsiCapableForControlSequences(outputOptions, Console.IsOutputRedirected);

	/// <summary>
	/// <see cref="IsAnsiCapableForControlSequences(OutputOptions)"/> for sequences written to a stream whose
	/// redirection is <paramref name="isStreamRedirected"/>, rather than to standard output.
	/// </summary>
	public static bool IsAnsiCapableForControlSequences(OutputOptions outputOptions, bool isStreamRedirected) =>
		(outputOptions.IsColorEnabled(isStreamRedirected) && IsTerminalOrExplicit(outputOptions, isStreamRedirected))
		|| IsHostedClientAnsiCapable(outputOptions);

	/// <summary>
	/// The colour answer under its 0.12.0 name, which a Repl.Spectre 0.12.0 binary still calls to style its
	/// console when it runs against a newer Repl.Core. Repl's own code calls
	/// <see cref="IsAnsiCapableForColor(OutputOptions)"/>.
	/// </summary>
	public static bool IsAnsiCapableForTerminalSequences(OutputOptions outputOptions) =>
		IsAnsiCapableForColor(outputOptions);

	// Only an explicit grant (the hosted session's stated support, AnsiMode.Always) outranks the
	// environment opt-out here; FORCE_COLOR, which granted the colour, does not.
	private static bool IsTerminalOrExplicit(OutputOptions outputOptions, bool isStreamRedirected) =>
		(ReplSessionIO.IsSessionActive && ReplSessionIO.AnsiSupport is not null)
		|| outputOptions.AnsiMode == AnsiMode.Always
		|| (!isStreamRedirected && !TerminalEnvironmentClassifier.IsAnsiOptOutEnvironment());

	// The fallback only bypasses the server console's own state (redirection, host
	// detection) — never the explicit opt-outs: AnsiSupport=false and AnsiMode.Never
	// below, and the NO_COLOR / TERM=dumb escape hatches the docs promise to honor.
	private static bool IsHostedClientAnsiCapable(OutputOptions outputOptions) =>
		ReplSessionIO.IsSessionActive
		&& ReplSessionIO.AnsiSupport is null
		&& outputOptions.AnsiMode != AnsiMode.Never
		&& !TerminalEnvironmentClassifier.IsAnsiOptOutEnvironment()
		&& ReplSessionIO.TerminalCapabilities.HasFlag(TerminalCapabilities.Ansi);
}
