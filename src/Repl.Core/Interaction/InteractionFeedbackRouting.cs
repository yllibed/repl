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
	/// a test harness — and they are treated as terminals; their colour keeps following standard output's
	/// redirection, which is what it has always followed. The console session of a protocol-passthrough
	/// command is the exception: both its writers are the process's standard error, so that stream's own
	/// redirection applies.
	/// <para>
	/// A protocol-passthrough command's protocol owns the command output, which its session splits off
	/// where it can (<c>CoreReplApp.OpenPassthroughSession</c>). Feedback never shares that writer: it moves
	/// to the session's error writer, and is not written at all when that is the protocol's too, as in a
	/// hosted session whose transport has a single stream.
	/// </para>
	/// </remarks>
	public static FeedbackTarget Resolve(OutputOptions? outputOptions, bool isOutputRedirected, bool isErrorRedirected)
	{
		var isProcessConsole = ReplSessionIO.IsProcessConsoleSession;
		var isLocal = !ReplSessionIO.IsSessionActive || isProcessConsole;
		var toError = WritesToError(
			outputOptions?.FeedbackStream ?? ReplFeedbackStream.Auto,
			ReplSessionIO.IsMachineReadableOutput,
			isLocalOutputRedirected: isLocal && isOutputRedirected)
			|| SharesTheProtocol(ReplSessionIO.Output);
		var writesToStandardError = isLocal && (toError || isProcessConsole);
		var writer = toError ? ReplSessionIO.Error : ReplSessionIO.Output;
		return new FeedbackTarget(
			SharesTheProtocol(writer) ? TextWriter.Null : writer,
			IsError: toError,
			IsSessionWriter: !isLocal,
			IsStreamRedirected: writesToStandardError ? isErrorRedirected : isOutputRedirected);
	}

	/// <summary>
	/// Whether an interactive prompt may draw on the session's output, as the arrow-key menus and the Spectre
	/// prompts do: only where its feedback goes too, so a menu never lands in a payload a program reads.
	/// Otherwise the text prompts take over, whose label and echo follow the feedback.
	/// </summary>
	public static bool PromptsMayDrawOnOutput(OutputOptions? outputOptions) =>
		ReferenceEquals(Resolve(outputOptions).Writer, ReplSessionIO.Output);

	private static bool SharesTheProtocol(TextWriter writer) =>
		ReplSessionIO.IsProtocolPassthrough && ReferenceEquals(writer, ReplSessionIO.CommandOutput);

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
