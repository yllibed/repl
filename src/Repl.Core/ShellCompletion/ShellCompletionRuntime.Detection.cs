using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Repl.ShellCompletion;

internal sealed partial class ShellCompletionRuntime
{
	private ShellDetectionResult DetectShellKind()
	{
		if (_options.ShellCompletion.PreferredShell is ShellKind preferred)
		{
			if (IsShellCompletionSupportedShell(preferred))
			{
				return new ShellDetectionResult(
					preferred,
					"preferred override",
					ParentLooksLikeWindowsPowerShell: preferred == ShellKind.PowerShell && IsWindowsPowerShellInProcessChain());
			}

			return new ShellDetectionResult(ShellKind.Unknown, "preferred override was not a supported shell");
		}

		var environment = DetectShellFromEnvironment();
		var parent = DetectShellFromParentProcess();
		var powershellScore = environment.PowershellScore + parent.PowershellScore;
		var bashScore = environment.BashScore + parent.BashScore;
		var zshScore = environment.ZshScore + parent.ZshScore;
		if (powershellScore == 0 && bashScore == 0 && zshScore == 0)
		{
			return environment.HasKnownUnsupported
				? new ShellDetectionResult(ShellKind.Unsupported, environment.Reason)
				: new ShellDetectionResult(ShellKind.Unknown, "no shell signal");
		}

		var scoreByShell = new[]
		{
			(Shell: ShellKind.PowerShell, Score: powershellScore),
			(Shell: ShellKind.Bash, Score: bashScore),
			(Shell: ShellKind.Zsh, Score: zshScore),
		};
		var best = scoreByShell.OrderByDescending(static item => item.Score).First();
		var hasTie = scoreByShell
			.Where(item => item.Score == best.Score)
			.Skip(1)
			.Any();
		var reason = BuildShellDetectionReason(environment.Reason, parent.Reason);
		if (hasTie)
		{
			return new ShellDetectionResult(ShellKind.Unknown, reason);
		}

		return best.Shell == ShellKind.PowerShell
			? new ShellDetectionResult(
				ShellKind.PowerShell,
				reason,
				parent.ParentLooksLikeWindowsPowerShell)
			: new ShellDetectionResult(best.Shell, reason);
	}

	private static string BuildShellDetectionReason(string environmentReason, string parentReason)
	{
		if (string.IsNullOrWhiteSpace(environmentReason))
		{
			return string.IsNullOrWhiteSpace(parentReason) ? "unknown" : parentReason;
		}

		return string.IsNullOrWhiteSpace(parentReason)
			? environmentReason
			: $"{environmentReason}; {parentReason}";
	}

	private static ShellSignal DetectShellFromEnvironment()
	{
		var shellVar = Environment.GetEnvironmentVariable("SHELL") ?? string.Empty;
		var bashVersion = Environment.GetEnvironmentVariable("BASH_VERSION");
		var zshVersion = Environment.GetEnvironmentVariable("ZSH_VERSION");
		var psModulePath = Environment.GetEnvironmentVariable("PSModulePath");
		var psDistribution = Environment.GetEnvironmentVariable("POWERSHELL_DISTRIBUTION_CHANNEL");
		var powershellHint = !string.IsNullOrWhiteSpace(psDistribution)
			|| !string.IsNullOrWhiteSpace(psModulePath)
				&& psModulePath.Contains("PowerShell", StringComparison.OrdinalIgnoreCase);
		var bashHint = !string.IsNullOrWhiteSpace(bashVersion)
			|| shellVar.EndsWith("bash", StringComparison.OrdinalIgnoreCase)
			|| shellVar.EndsWith("bash.exe", StringComparison.OrdinalIgnoreCase);
		var zshHint = !string.IsNullOrWhiteSpace(zshVersion)
			|| shellVar.EndsWith("zsh", StringComparison.OrdinalIgnoreCase)
			|| shellVar.EndsWith("zsh.exe", StringComparison.OrdinalIgnoreCase);
		var unsupportedHint = shellVar.EndsWith("fish", StringComparison.OrdinalIgnoreCase)
			|| shellVar.EndsWith("sh", StringComparison.OrdinalIgnoreCase)
				&& !bashHint
				&& !zshHint;
		var reason = string.Empty;
		if (powershellHint)
		{
			reason = "env suggests PowerShell";
		}
		else if (bashHint)
		{
			reason = "env suggests bash";
		}
		else if (zshHint)
		{
			reason = "env suggests zsh";
		}
		else if (unsupportedHint)
		{
			reason = $"env shell '{shellVar}' is currently unsupported";
		}

		return new ShellSignal(
			PowershellScore: powershellHint ? 3 : 0,
			BashScore: bashHint ? 3 : 0,
			ZshScore: zshHint ? 3 : 0,
			HasKnownUnsupported: unsupportedHint,
			Reason: reason,
			ParentLooksLikeWindowsPowerShell: false);
	}

	private static ShellSignal DetectShellFromParentProcess()
	{
		var names = GetParentProcessNames();
		if (names.Length == 0)
		{
			return new ShellSignal(0, 0, 0, HasKnownUnsupported: false, Reason: string.Empty, ParentLooksLikeWindowsPowerShell: false);
		}

		var powershellHint = names.Any(name =>
			string.Equals(name, "pwsh", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "powershell", StringComparison.OrdinalIgnoreCase));
		var bashHint = names.Any(name =>
			string.Equals(name, "bash", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "bash.exe", StringComparison.OrdinalIgnoreCase));
		var zshHint = names.Any(name =>
			string.Equals(name, "zsh", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "zsh.exe", StringComparison.OrdinalIgnoreCase));
		var legacyPowerShell = names.Any(name =>
			string.Equals(name, "powershell", StringComparison.OrdinalIgnoreCase));
		var reason = powershellHint
			? "parent process suggests PowerShell"
			: bashHint
				? "parent process suggests bash"
				: zshHint
					? "parent process suggests zsh"
					: $"parent process chain: {string.Join(" -> ", names)}";
		return new ShellSignal(
			PowershellScore: powershellHint ? 2 : 0,
			BashScore: bashHint ? 2 : 0,
			ZshScore: zshHint ? 2 : 0,
			HasKnownUnsupported: false,
			Reason: reason,
			ParentLooksLikeWindowsPowerShell: legacyPowerShell);
	}

	private static bool IsWindowsPowerShellInProcessChain() =>
		GetParentProcessNames().Any(name => string.Equals(name, "powershell", StringComparison.OrdinalIgnoreCase));

	private static string[] GetParentProcessNames()
	{
		try
		{
			var currentProcessId = Environment.ProcessId;
			if (!TryGetParentProcessId(currentProcessId, out var parentProcessId))
			{
				return [];
			}

			var names = new List<string>();
			var guard = 0;
			while (parentProcessId > 0 && guard++ < 8)
			{
				string? processName;
				if (TryGetProcessNameById(parentProcessId, out processName))
				{
					if (!string.IsNullOrWhiteSpace(processName))
					{
						names.Add(processName);
					}
				}
				else
				{
					break;
				}

				if (!TryGetParentProcessId(parentProcessId, out parentProcessId))
				{
					break;
				}
			}

			return [.. names];
		}
		catch
		{
			return [];
		}
	}

	private static bool TryGetProcessNameById(int processId, out string? processName)
	{
		processName = null;
		if (processId <= 0)
		{
			return false;
		}

		try
		{
			using var process = Process.GetProcessById(processId);
			processName = process.ProcessName;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetParentProcessId(int processId, out int parentProcessId)
	{
		parentProcessId = 0;
		if (OperatingSystem.IsWindows())
		{
			return TryGetParentProcessIdWindows(processId, out parentProcessId);
		}

		if (OperatingSystem.IsLinux())
		{
			return TryGetParentProcessIdLinux(processId, out parentProcessId);
		}

		if (OperatingSystem.IsMacOS())
		{
			return TryGetParentProcessIdMac(processId, out parentProcessId);
		}

		return false;
	}

	private static bool TryGetParentProcessIdLinux(int processId, out int parentProcessId)
	{
		parentProcessId = 0;
		try
		{
			var statPath = $"/proc/{processId}/stat";
			if (!File.Exists(statPath))
			{
				return false;
			}

			var stat = File.ReadAllText(statPath);
			var endCommand = stat.LastIndexOf(')');
			if (endCommand < 0 || endCommand + 2 >= stat.Length)
			{
				return false;
			}

			var rest = stat[(endCommand + 2)..];
			var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2)
			{
				return false;
			}

			return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parentProcessId);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetParentProcessIdMac(int processId, out int parentProcessId)
	{
		parentProcessId = 0;
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "ps",
				ArgumentList = { "-o", "ppid=", "-p", processId.ToString(CultureInfo.InvariantCulture) },
				RedirectStandardOutput = true,
				RedirectStandardError = false,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			using var process = Process.Start(psi);
			if (process is null)
			{
				return false;
			}

			var output = process.StandardOutput.ReadToEnd();
			process.WaitForExit(milliseconds: 250);
			return int.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parentProcessId);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetParentProcessIdWindows(int processId, out int parentProcessId)
	{
		const uint TH32CS_SNAPPROCESS = 0x00000002;
		parentProcessId = 0;
		var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, th32ProcessID: 0);
		if (snapshot == InvalidHandleValue || snapshot == IntPtr.Zero)
		{
			return false;
		}

		try
		{
			var entry = new ProcessEntry32
			{
				dwSize = (uint)Marshal.SizeOf<ProcessEntry32>(),
			};
			if (!Process32First(snapshot, ref entry))
			{
				return false;
			}

			do
			{
				if (entry.th32ProcessID != processId)
				{
					continue;
				}

				parentProcessId = entry.th32ParentProcessID;
				return parentProcessId > 0;
			}
			while (Process32Next(snapshot, ref entry));

			return false;
		}
		finally
		{
			_ = CloseHandle(snapshot);
		}
	}

	private static readonly IntPtr InvalidHandleValue = new(-1);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ProcessEntry32
	{
		public uint dwSize;
		public uint cntUsage;
		public int th32ProcessID;
		public IntPtr th32DefaultHeapID;
		public uint th32ModuleID;
		public uint cntThreads;
		public int th32ParentProcessID;
		public int pcPriClassBase;
		public uint dwFlags;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string szExeFile;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 processEntry);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 processEntry);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}
