namespace Repl;

/// <summary>
/// Internal contract for channels that accept a per-command ambient cancellation token.
/// </summary>
internal interface ICommandTokenReceiver
{
	void SetCommandToken(CancellationToken ct);
}
