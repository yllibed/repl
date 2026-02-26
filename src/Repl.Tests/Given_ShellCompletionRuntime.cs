using System.Reflection;
using Repl.ShellCompletion;

namespace Repl.Tests;

[TestClass]
public sealed class Given_ShellCompletionRuntime
{
	[TestMethod]
	[Description("Regression guard: verifies shell completion bridge joins candidates with LF so POSIX shells do not receive CR characters.")]
	public void When_BridgeReturnsCandidates_Then_CandidatesAreJoinedWithLf()
	{
		var runtime = CreateRuntime(
			resolveCandidates: static (_, _) => ["alpha", "beta"]);

		var result = runtime.HandleBridgeRoute(shell: "bash", line: "app a", cursor: "5");

		result.Should().BeOfType<string>();
		result.Should().Be("alpha\nbeta");
	}

	[TestMethod]
	[Description("Regression guard: verifies state file default extension matches key=value format rather than json.")]
	public void When_UsingDefaultStateFileName_Then_ExtensionIsNotJson()
	{
		ShellCompletionConstants.StateFileName.Should().Be("shell-completion-state.txt");
	}

	[TestMethod]
	[Description("Regression guard: verifies legacy .json state file is loaded when new default state file is absent.")]
	public void When_NewStateFileIsMissing_Then_LegacyJsonStateFileIsLoaded()
	{
		var root = Path.Combine(Path.GetTempPath(), "repl-shell-completion-runtime-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var statePath = Path.Combine(root, ShellCompletionConstants.StateFileName);
			var legacyStatePath = Path.Combine(root, ShellCompletionConstants.LegacyStateFileName);
			File.WriteAllLines(legacyStatePath, [
				"promptShown=true",
				"lastDetectedShell=bash",
				"installedShells=bash,nu",
			]);

			var options = new ReplOptions();
			options.ShellCompletion.StateFilePath = statePath;
			var runtime = CreateRuntime(options);
			var state = LoadState(runtime);

			ReadStateBool(state, "PromptShown").Should().BeTrue();
			ReadStateString(state, "LastDetectedShell").Should().Be("bash");
			ReadStateList(state, "InstalledShells").Should().ContainInOrder("bash", "nu");
		}
		finally
		{
			try
			{
				Directory.Delete(root, recursive: true);
			}
			catch
			{
				// Best-effort cleanup for temp test directories.
			}
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies nushell app block stores the command head in base64 metadata to support global dispatcher reconstruction.")]
	public void When_CommandNameContainsQuotes_Then_NushellScriptStoresBase64Metadata()
	{
		var script = NuShellCompletionAdapter.Instance.BuildManagedBlock("my'app\"tool", appId: "test-app");

		script.Should().Contain("# repl nu command-b64=");
		script.Should().Contain(";shell=nu] >>>");
	}

	[TestMethod]
	[Description("Regression guard: verifies path mutation lock key comparer is OS-aware so case-variant paths share a lock only on case-insensitive platforms.")]
	public void When_ResolvingPathMutationLock_Then_PathCaseBehaviorIsPlatformAware()
	{
		var getLock = typeof(ShellCompletionRuntime).GetMethod(
			"GetPathMutationLock",
			BindingFlags.Static | BindingFlags.NonPublic);
		getLock.Should().NotBeNull();

		var root = Path.Combine(Path.GetTempPath(), "repl-shell-completion-runtime-lock-tests", Guid.NewGuid().ToString("N"));
		var upperPath = Path.Combine(root, "StatePath.txt");
		var lowerPath = Path.Combine(root, "statepath.txt");
		var lockA = getLock!.Invoke(null, [upperPath]);
		var lockB = getLock.Invoke(null, [lowerPath]);

		lockA.Should().NotBeNull();
		lockB.Should().NotBeNull();
		if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
		{
			ReferenceEquals(lockA, lockB).Should().BeTrue();
		}
		else
		{
			ReferenceEquals(lockA, lockB).Should().BeFalse();
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies completion status reads each distinct profile path once per status call.")]
	public void When_StatusProfilesShareSamePath_Then_RuntimeReadsProfileContentOnce()
	{
		var profilePath = Path.Combine(Path.GetTempPath(), "repl-shell-completion-status-tests", $"{Guid.NewGuid():N}.profile");
		Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
		File.WriteAllText(profilePath, string.Empty);
		try
		{
			var readCount = 0;
			var options = new ReplOptions();
			options.ShellCompletion.BashProfilePath = profilePath;
			options.ShellCompletion.PowerShellProfilePath = profilePath;
			options.ShellCompletion.ZshProfilePath = profilePath;
			options.ShellCompletion.FishProfilePath = profilePath;
			options.ShellCompletion.NuProfilePath = profilePath;
			var runtime = CreateRuntime(
				options,
				tryReadProfileContent: _ =>
				{
					readCount++;
					return string.Empty;
				});

			_ = runtime.HandleStatusRoute();

			readCount.Should().Be(1);
		}
		finally
		{
			try
			{
				File.Delete(profilePath);
			}
			catch
			{
				// Best-effort cleanup for temp test files.
			}
		}
	}

	private static ShellCompletionRuntime CreateRuntime(
		ReplOptions? options = null,
		Func<string, int, string[]>? resolveCandidates = null,
		Func<string, string?>? tryReadProfileContent = null)
	{
		options ??= new ReplOptions();
		resolveCandidates ??= static (_, _) => [];
		return new ShellCompletionRuntime(
			options,
			resolveEntryAssemblyName: static () => Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty,
			resolveCommandName: static () => "app",
			resolveCandidates: resolveCandidates,
			tryReadProfileContent: tryReadProfileContent);
	}

	private static object LoadState(ShellCompletionRuntime runtime)
	{
		var method = typeof(ShellCompletionRuntime).GetMethod(
			"LoadShellCompletionState",
			BindingFlags.Instance | BindingFlags.NonPublic);
		method.Should().NotBeNull();
		return method!.Invoke(obj: runtime, parameters: null)!;
	}

	private static bool ReadStateBool(object state, string name)
	{
		var property = state.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
		property.Should().NotBeNull();
		return (bool)property!.GetValue(state)!;
	}

	private static string? ReadStateString(object state, string name)
	{
		var property = state.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
		property.Should().NotBeNull();
		return property!.GetValue(state) as string;
	}

	private static string[] ReadStateList(object state, string name)
	{
		var property = state.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
		property.Should().NotBeNull();
		return ((IEnumerable<string>)property!.GetValue(state)!).ToArray();
	}
}
