namespace Repl.Remote;

internal interface IRemoteConsole
{
	IDisposable Enter();
	(int Columns, int Rows) Size { get; }
	ValueTask<ConsoleKeyInfo?> ReadKeyAsync(CancellationToken cancellationToken);
	ValueTask WriteAsync(string text, CancellationToken cancellationToken);
}
