namespace Repl;

/// <summary>
/// Thrown when application code the binder invoked while supplying a handler argument raised. The
/// original failure is the inner exception.
/// </summary>
/// <remarks>
/// This exists to tell two binding failures apart that are otherwise identical: a diagnostic the
/// binder wrote for the caller ("cannot convert 'abc' to an int") and an exception that escaped
/// application code — a service factory, an options-group constructor, a property setter. Both surface
/// as a binding failure and both are commonly an <see cref="InvalidOperationException"/>, so neither
/// the outcome kind nor the exception type distinguishes them — yet the first is meant to be read and
/// the second can name a filesystem path, a connection string, or application state. A host that
/// publishes failures to someone other than the operator uses this type to withhold the second while
/// keeping the first; a local console keeps the inner cause, where the reader is the operator.
/// </remarks>
/// <param name="target">What the binder was supplying — a parameter or a property.</param>
/// <param name="innerException">Failure raised by the application code.</param>
public sealed class ReplBindingCallbackException(string target, Exception innerException)
	: InvalidOperationException($"Supplying '{target}' failed.", innerException)
{
	/// <summary>Gets what the binder was supplying when the application code raised.</summary>
	public string Target { get; } = target;
}
