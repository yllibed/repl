namespace Repl;

/// <summary>A decision made by a parsed invocation hook.</summary>
public sealed class ReplInvocationResult
{
	private ReplInvocationResult(ReplInvocationRequest? request = null, bool handled = false, object? result = null)
	{
		Request = request;
		IsHandled = handled;
		Result = result;
	}

	internal ReplInvocationRequest? Request { get; }
	internal bool IsHandled { get; }
	internal object? Result { get; }
	/// <summary>Continues to the next hook or normal execution.</summary>
	/// <returns>A continuation decision.</returns>
	public static ReplInvocationResult Continue() => new();
	/// <summary>Replaces the complete request; earlier hooks are not replayed.</summary>
	/// <param name="request">Replacement target, values and globals.</param>
	/// <returns>A replacement decision.</returns>
	public static ReplInvocationResult Replace(ReplInvocationRequest request) => new(request ?? throw new ArgumentNullException(nameof(request)));
	/// <summary>Stops dispatch and renders the supplied result using the normal result pipeline.</summary>
	/// <param name="result">A command result, including an explicit exit result if desired.</param>
	/// <returns>A terminal decision.</returns>
	public static ReplInvocationResult Handled(object? result = null) => new(handled: true, result: result);
}
