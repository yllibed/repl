using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Repl.Remote;

[SupportedOSPlatform("windows")]
internal static partial class WindowsConsoleMode
{
	internal static IDisposable Enter()
	{
		const int standardOutputHandle = -11;
		const uint enableVirtualTerminalProcessing = 0x0004;
		var handle = GetStdHandle(standardOutputHandle);
		if (!GetConsoleMode(handle, out var original) || !SetConsoleMode(handle, original | enableVirtualTerminalProcessing))
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError());
		}
		return new RestoreMode(handle, original);
	}

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial nint GetStdHandle(int handle);
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetConsoleMode(nint handle, out uint mode);
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleMode(nint handle, uint mode);

	private sealed class RestoreMode(nint handle, uint mode) : IDisposable
	{
		public void Dispose()
		{
			if (!SetConsoleMode(handle, mode)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
		}
	}
}
