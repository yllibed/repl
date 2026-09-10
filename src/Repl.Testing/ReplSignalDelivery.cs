namespace Repl.Testing;

/// <summary>
/// What the framework decided about one delivered signal.
/// </summary>
public enum ReplSignalDelivery
{
	/// <summary>
	/// Nothing claimed the signal, so a real process would have taken the operating-system default.
	/// A signal that arrives with no run in flight lands here, as does <see cref="ReplProcessSignal.Break"/>
	/// on a declared platform where Ctrl+Break is not a signal.
	/// </summary>
	NotHandled,

	/// <summary>
	/// The signal claimed the run cooperatively: every run in flight was asked to cancel, and a real
	/// process would have kept running to finish its cleanup.
	/// </summary>
	CancellationRequested,

	/// <summary>
	/// A signal arrived after one was already claimed, so the framework stepped aside and a real
	/// process would have been terminated by the operating system.
	/// <para>
	/// This reports the framework's decision, which is all an in-process test can observe. It does not
	/// mean anything stopped: no process dies here, so a run still in flight keeps running and code
	/// after the delivery still executes. Only a spawned process can show that termination happened.
	/// </para>
	/// </summary>
	WouldTerminateProcess,
}
