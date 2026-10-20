namespace Repl;

/// <summary>
/// Where one piece of interaction feedback goes. <paramref name="IsError"/> says whether it was moved to
/// the error stream; <paramref name="IsSessionWriter"/> whether the writer belongs to a session rather than
/// to the process's console; <paramref name="IsStreamRedirected"/> is the redirection the colour and
/// terminal-sequence rules read for that stream.
/// </summary>
internal readonly record struct FeedbackTarget(TextWriter Writer, bool IsError, bool IsSessionWriter, bool IsStreamRedirected)
{
	/// <summary>
	/// Whether the stream can rewrite a progress line in place: a session's writer always can, a console
	/// stream only when it is not redirected to a file or a pipe.
	/// </summary>
	public bool IsTerminal => IsSessionWriter || !IsStreamRedirected;
}
