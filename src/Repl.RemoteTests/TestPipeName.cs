namespace Repl.RemoteTests;

internal static class TestPipeName
{
	// Unix sockets also include TMPDIR and CoreFxPipe_ in their bounded path.
	internal static string Create() => Guid.NewGuid().ToString("N")[..20];
}
