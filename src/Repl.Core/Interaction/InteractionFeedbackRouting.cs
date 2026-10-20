namespace Repl;

/// <summary>
/// Decides which stream receives interaction feedback, so a program reading standard output gets the
/// payload alone.
/// </summary>
internal static class InteractionFeedbackRouting
{
	/// <summary>
	/// Resolves the target of the next piece of feedback. Evaluated per write rather than once, because
	/// the output format is chosen per command and the session can change between two commands.
	/// </summary>
	public static FeedbackTarget Resolve(OutputOptions? outputOptions) =>
		Resolve(outputOptions, Console.IsOutputRedirected, Console.IsErrorRedirected);

	/// <summary>
	/// <see cref="Resolve(OutputOptions?)"/> with the process's console redirection passed in, which is
	/// the only part of the decision a test cannot otherwise control.
	/// </summary>
	/// <remarks>
	/// The redirection only counts outside a session. A session supplies its own writers — a transport,
	/// a test harness, the protocol-passthrough split — and they are treated as terminals; their colour
	/// keeps following standard output's redirection, which is what it has always followed.
	/// </remarks>
	public static FeedbackTarget Resolve(OutputOptions? outputOptions, bool isOutputRedirected, bool isErrorRedirected)
	{
		var isLocal = !ReplSessionIO.IsSessionActive;
		var toError = WritesToError(
			outputOptions?.FeedbackStream ?? ReplFeedbackStream.Auto,
			ReplSessionIO.IsMachineReadableOutput,
			isLocalOutputRedirected: isLocal && isOutputRedirected);
		return new FeedbackTarget(
			toError ? ReplSessionIO.Error : ReplSessionIO.Output,
			IsError: toError,
			IsSessionWriter: !isLocal,
			IsStreamRedirected: (isLocal && toError) ? isErrorRedirected : isOutputRedirected);
	}

	/// <summary>
	/// The routing rule: with <see cref="ReplFeedbackStream.Auto"/>, feedback leaves the output stream
	/// whenever a program reads it.
	/// </summary>
	public static bool WritesToError(
		ReplFeedbackStream stream,
		bool isMachineReadableOutput,
		bool isLocalOutputRedirected) =>
		stream switch
		{
			ReplFeedbackStream.Output => false,
			ReplFeedbackStream.Error => true,
			_ => isMachineReadableOutput || isLocalOutputRedirected,
		};
}
