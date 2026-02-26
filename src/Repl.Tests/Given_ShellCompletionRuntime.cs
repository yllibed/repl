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
	[Description("Regression guard: verifies nushell script escapes command head using double-quoted literal when special characters are present.")]
	public void When_CommandNameContainsQuotes_Then_NushellScriptUsesEscapedDoubleQuotedLiteral()
	{
		var script = NuShellCompletionAdapter.Instance.BuildManagedBlock("my'app\"tool", appId: "test-app");

		script.Should().Contain("const __repl_completion_command = \"my'app\\\"tool\"");
	}

	private static ShellCompletionRuntime CreateRuntime(
		ReplOptions? options = null,
		Func<string, int, string[]>? resolveCandidates = null)
	{
		options ??= new ReplOptions();
		resolveCandidates ??= static (_, _) => [];
		return new ShellCompletionRuntime(
			options,
			resolveEntryAssemblyName: static () => Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty,
			resolveCommandName: static () => "app",
			resolveCandidates: resolveCandidates);
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
