namespace Repl;

/// <summary>A parsed snapshot before final validation, with services injected separately into the hook.</summary>
public sealed class ReplInvocationContext
{
	internal ReplInvocationContext(ReplInvocationRequest original, ReplInvocationRequest request,
		ParsedInputContext parsed, ReplInvocationChannel channel)
	{
		OriginalRequest = original.Copy();
		Request = request.Copy();
		Parsed = parsed;
		Channel = channel;
	}

	/// <summary>Gets the request at the start of this hook chain.</summary>
	public ReplInvocationRequest OriginalRequest { get; }
	/// <summary>Gets the effective request, including earlier replacements.</summary>
	public ReplInvocationRequest Request { get; }
	/// <summary>Gets parsed command and global values, processing mode, purpose and diagnostics.</summary>
	public ParsedInputContext Parsed { get; }
	/// <summary>Gets the submitting input surface.</summary>
	public ReplInvocationChannel Channel { get; }
}
