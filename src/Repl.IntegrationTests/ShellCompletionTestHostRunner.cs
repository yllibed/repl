using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Repl.IntegrationTests;

internal static class ShellCompletionTestHostRunner
{
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

	public static (int ExitCode, string Text) Run(
		string scenario,
		IReadOnlyList<string> args,
		IReadOnlyDictionary<string, string?>? environment = null,
		string? standardInput = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
		ArgumentNullException.ThrowIfNull(args);

		using var process = CreateProcess(scenario, args, environment);
		var stdout = new StringBuilder();
		var stderr = new StringBuilder();
		process.OutputDataReceived += (_, eventArgs) => AppendLine(eventArgs.Data, stdout);
		process.ErrorDataReceived += (_, eventArgs) => AppendLine(eventArgs.Data, stderr);

		if (!process.Start())
		{
			throw new InvalidOperationException($"Failed to start test host process '{process.StartInfo.FileName}'.");
		}

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		if (!string.IsNullOrEmpty(standardInput))
		{
			process.StandardInput.Write(standardInput);
		}

		process.StandardInput.Close();
		EnsureExitedWithinTimeout(process);
		process.WaitForExit();

		return (process.ExitCode, MergeOutput(stdout.ToString(), stderr.ToString()));
	}

	private static Process CreateProcess(
		string scenario,
		IReadOnlyList<string> args,
		IReadOnlyDictionary<string, string?>? environment)
	{
		var startInfo = new ProcessStartInfo(ResolveHostExecutablePath())
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		startInfo.Environment["REPL_TEST_SCENARIO"] = scenario;
		foreach (var argument in args)
		{
			startInfo.ArgumentList.Add(argument);
		}

		if (environment is not null)
		{
			foreach (var pair in environment)
			{
				startInfo.Environment[pair.Key] = pair.Value;
			}
		}

		return new Process { StartInfo = startInfo };
	}

	private static void AppendLine(string? line, StringBuilder builder)
	{
		if (line is null)
		{
			return;
		}

		if (builder.Length > 0)
		{
			builder.AppendLine();
		}

		builder.Append(line);
	}

	private static string MergeOutput(string output, string error) =>
		string.IsNullOrWhiteSpace(error)
			? output
			: string.IsNullOrWhiteSpace(output)
				? error
				: $"{output}{Environment.NewLine}{error}";

	private static void EnsureExitedWithinTimeout(Process process)
	{
		if (process.WaitForExit((int)DefaultTimeout.TotalMilliseconds))
		{
			return;
		}

		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch
		{
			// Best-effort process cleanup.
		}

		throw new TimeoutException(
			$"Shell completion test host timed out after {DefaultTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s.");
	}

	private static string ResolveHostExecutablePath()
	{
		var root = ResolveRepositoryRoot();
		var configuration = ResolveBuildConfiguration();
		var executableName = OperatingSystem.IsWindows()
			? "Repl.ShellCompletionTestHost.exe"
			: "Repl.ShellCompletionTestHost";
		var path = Path.Combine(
			root,
			"src",
			"Repl.ShellCompletionTestHost",
			"bin",
			configuration,
			"net10.0",
			executableName);
		if (File.Exists(path))
		{
			return path;
		}

		throw new FileNotFoundException(
			$"Shell completion test host executable was not found at '{path}'. Build the solution before running integration tests.");
	}

	private static string ResolveRepositoryRoot()
	{
		var current = AppContext.BaseDirectory;
		while (!string.IsNullOrWhiteSpace(current))
		{
			var candidate = Path.Combine(current, "src", "Repl.ShellCompletionTestHost");
			if (Directory.Exists(candidate))
			{
				return current;
			}

			var parent = Directory.GetParent(current);
			if (parent is null)
			{
				break;
			}

			current = parent.FullName;
		}

		throw new DirectoryNotFoundException(
			$"Could not resolve repository root from '{AppContext.BaseDirectory}'.");
	}

	private static string ResolveBuildConfiguration()
	{
		var parts = AppContext.BaseDirectory.Split(
			[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			StringSplitOptions.RemoveEmptyEntries);
		return parts.Any(part => string.Equals(part, "Release", StringComparison.OrdinalIgnoreCase))
			? "Release"
			: "Debug";
	}
}
