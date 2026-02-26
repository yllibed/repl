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
	[Description("Regression guard: verifies nushell app block stores the command head in base64 metadata to support global dispatcher reconstruction.")]
	public void When_CommandNameContainsQuotes_Then_NushellScriptStoresBase64Metadata()
	{
		var script = NuShellCompletionAdapter.Instance.BuildManagedBlock("my'app\"tool", appId: "test-app");

		script.Should().Contain("# repl nu command-b64=");
		script.Should().Contain(";shell=nu] >>>");
	}

	[TestMethod]
	[Description("Regression guard: verifies nushell global dispatcher receives the spans list from the external completer closure.")]
	public void When_BuildingNushellDispatcher_Then_DispatcherUsesSpansListArgument()
	{
		var script = NuShellCompletionAdapter.BuildGlobalDispatcherManagedBlock(
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["sample-app"] = "sample",
			});

		script.Should().Contain("def _repl_nu_dispatch_completion [spans: list<string>]");
		script.Should().Contain("|spans| _repl_nu_dispatch_completion $spans");
		script.Should().Contain("$env.config.completions.external.completer = { |spans| _repl_nu_dispatch_completion $spans }");
		script.Should().NotContain("| upsert completions.external.completer { |spans| _repl_nu_dispatch_completion $spans }");
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

	[TestMethod]
	[Description("Regression guard: verifies completion status reports per-shell profile file existence.")]
	public void When_StatusIsRequested_Then_ProfileExistsFieldsReflectFilesystem()
	{
		var root = Path.Combine(Path.GetTempPath(), "repl-shell-completion-status-exists-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var existingProfilePath = Path.Combine(root, ".bashrc");
		var missingProfilePath = Path.Combine(root, "missing.profile");
		File.WriteAllText(existingProfilePath, string.Empty);
		try
		{
			var options = new ReplOptions();
			options.ShellCompletion.PreferredShell = ShellKind.Bash;
			options.ShellCompletion.BashProfilePath = existingProfilePath;
			options.ShellCompletion.PowerShellProfilePath = missingProfilePath;
			var runtime = CreateRuntime(
				options,
				tryReadProfileContent: _ => string.Empty);

			var status = runtime.HandleStatusRoute();

			ReadObjectBool(status, "BashProfileExists").Should().BeTrue();
			ReadObjectBool(status, "PowerShellProfileExists").Should().BeFalse();
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

	private static bool ReadObjectBool(object value, string name)
	{
		var property = value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
		property.Should().NotBeNull();
		return (bool)property!.GetValue(value)!;
	}
}
