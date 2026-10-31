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
		var codePage = GetConsoleOutputCP();
		if (codePage == 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
		if (!GetConsoleMode(handle, out var original) || !SetConsoleMode(handle, original | enableVirtualTerminalProcessing))
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError());
		}
		if (!SetConsoleOutputCP(65001))
		{
			var error = new Win32Exception(Marshal.GetLastPInvokeError());
			SetConsoleMode(handle, original);
			throw error;
		}
		return new RestoreMode(handle, original, codePage);
	}

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial nint GetStdHandle(int handle);
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetConsoleMode(nint handle, out uint mode);
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleMode(nint handle, uint mode);
	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial uint GetConsoleOutputCP();
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetConsoleOutputCP(uint codePage);

	private sealed class RestoreMode(nint handle, uint mode, uint codePage) : IDisposable
	{
		public void Dispose()
		{
			Win32Exception? error = null;
			if (!SetConsoleOutputCP(codePage)) { error = new Win32Exception(Marshal.GetLastPInvokeError()); }
			if (!SetConsoleMode(handle, mode)) { error ??= new Win32Exception(Marshal.GetLastPInvokeError()); }
			if (error is not null) { throw error; }
		}
	}
}
