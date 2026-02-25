namespace Repl;

internal interface IReplExecutionObserver
{
	void OnResult(object? result);

	void OnInteractionEvent(ReplInteractionEvent evt);
}
