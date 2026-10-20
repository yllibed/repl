namespace Repl;

/// <summary>
/// Where one piece of interaction feedback goes. <paramref name="IsError"/> says whether it was moved to
/// the error stream; <paramref name="IsTerminal"/> says whether that stream reaches a terminal, which can
/// rewrite a progress line in place and show colour, rather than a file or a pipe, which cannot.
/// </summary>
internal readonly record struct FeedbackTarget(TextWriter Writer, bool IsError, bool IsTerminal)
{
	/// <summary>
	/// Whether feedback may be styled. <paramref name="ansiEnabledForOutput"/> is the output stream's own
	/// colour rule, which already weighs its redirection and the colour opt-ins; the error stream is only
	/// styled when it is itself a terminal.
	/// </summary>
	public bool CanStyle(bool ansiEnabledForOutput) => ansiEnabledForOutput && (!IsError || IsTerminal);
}
