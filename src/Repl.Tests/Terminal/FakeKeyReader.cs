using System.Collections.Concurrent;

namespace Repl.Tests.TerminalSupport;

internal sealed class FakeKeyReader(IEnumerable<ConsoleKeyInfo> keys) : IReplKeyReader
{
	private readonly ConcurrentQueue<ConsoleKeyInfo> _keys = new(keys ?? throw new ArgumentNullException(nameof(keys)));

	public bool KeyAvailable => !_keys.IsEmpty;

	public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		if (_keys.TryDequeue(out var key))
		{
			return ValueTask.FromResult(key);
		}

		throw new InvalidOperationException("No key available in FakeKeyReader queue.");
	}
}
