namespace Repl;

/// <summary>
/// Shared coordination primitive for console input access.
/// ConsoleLineReader, CancelKeyHandler, and ConsoleKeyReader all acquire
/// this gate before reading keys to avoid conflicts.
/// </summary>
internal static class ConsoleInputGate
{
	internal static readonly SemaphoreSlim Gate = new(1, 1);
}
