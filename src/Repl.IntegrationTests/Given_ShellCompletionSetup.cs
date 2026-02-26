using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ShellCompletionSetup
{
	[TestMethod]
	[Description("Regression guard: verifies completion install command writes managed bash block into configured profile path.")]
	public void When_CompletionInstallCommandIsUsed_Then_ManagedProfileBlockIsWritten()
	{
		var paths = CreateTempPaths();
		try
		{
			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Bash);
			var output = RunSetup(["completion", "install", "--shell", "bash", "--no-logo"], environment);

			output.ExitCode.Should().Be(0);
			File.Exists(paths.ProfilePath).Should().BeTrue();
			var text = File.ReadAllText(paths.ProfilePath);
			text.Should().Contain("# >>> repl completion [appId=");
			text.Should().Contain(";shell=bash] >>>");
			text.Should().Contain("completion __complete --shell bash");
			text.Should().Contain("--no-interactive --no-logo");
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies generated bash completion script avoids mapfile so it works on older bash versions (for example macOS bash 3.2).")]
	public void When_BashCompletionScriptIsGenerated_Then_ItUsesPortableReadLoop()
	{
		var paths = CreateTempPaths();
		try
		{
			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Bash);
			var output = RunSetup(["completion", "install", "--shell", "bash", "--no-logo"], environment);

			output.ExitCode.Should().Be(0);
			var text = File.ReadAllText(paths.ProfilePath);
			text.Should().Contain("while IFS= read -r candidate; do");
			text.Should().Contain("COMPREPLY[${#COMPREPLY[@]}]=\"$candidate\"");
			text.Should().NotContain("COMPREPLY+=(\"$candidate\")");
			text.Should().NotContain("mapfile -t COMPREPLY");
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies generated zsh completion script uses compdef and the zsh bridge shell token.")]
	public void When_ZshCompletionScriptIsGenerated_Then_ItUsesCompdefAndZshBridge()
	{
		var paths = CreateTempPaths();
		try
		{
			var zshProfilePath = Path.Combine(paths.RootPath, ".zshrc");
			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Zsh, zshProfilePath: zshProfilePath);
			var output = RunSetup(["completion", "install", "--shell", "zsh", "--no-logo"], environment);

			output.ExitCode.Should().Be(0);
			output.Text.Should().Contain("source ~/.zshrc");
			var text = File.ReadAllText(zshProfilePath);
			text.Should().Contain(";shell=zsh] >>>");
			text.Should().Contain("completion __complete --shell zsh");
			text.Should().Contain("compdef");
			text.Should().Contain("cursor=\"$CURSOR\"");
			text.Should().NotContain("cursor=$((CURSOR > 0 ? CURSOR - 1 : 0))");
			text.Should().Contain("--no-interactive --no-logo");
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies generated fish completion script uses complete registration and fish bridge shell token.")]
	public void When_FishCompletionScriptIsGenerated_Then_ItUsesCompleteAndFishBridge()
	{
		var paths = CreateTempPaths();
		try
		{
			var fishProfilePath = Path.Combine(paths.RootPath, "config.fish");
			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Fish, fishProfilePath: fishProfilePath);
			var output = RunSetup(["completion", "install", "--shell", "fish", "--no-logo"], environment);

			output.ExitCode.Should().Be(0);
			var text = File.ReadAllText(fishProfilePath);
			text.Should().Contain(";shell=fish] >>>");
			text.Should().Contain("completion __complete --shell fish");
			text.Should().Contain("complete -c");
			text.Should().Contain("set -l line (commandline -p)");
			text.Should().Contain("set -l cursor (commandline -C)");
			text.Should().NotContain("set -l line (commandline -cp)");
			text.Should().NotContain("set -l cursor (string length -- $line)");
			text.Should().Contain("--no-interactive --no-logo");
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies generated nushell completion script configures a global dispatcher completer and uses nu bridge shell token.")]
	public void When_NuCompletionScriptIsGenerated_Then_ItUsesExternalCompleterAndNuBridge()
	{
		var paths = CreateTempPaths();
		try
		{
			var nuProfilePath = Path.Combine(paths.RootPath, "config.nu");
			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Nu, nuProfilePath: nuProfilePath);
			var output = RunSetup(["completion", "install", "--shell", "nushell", "--no-logo"], environment);

			output.ExitCode.Should().Be(0);
			var text = File.ReadAllText(nuProfilePath);
			text.Should().Contain(";shell=nu] >>>");
			text.Should().Contain("[appId=__repl_nu_dispatcher__;shell=nu] >>>");
			text.Should().Contain("completion __complete --shell nu");
			text.Should().Contain("completions.external.completer");
			text.Should().Contain("const __repl_completion_entries = [");
			text.Should().Contain("def _repl_nu_dispatch_completion [spans: list<string>]");
			text.Should().Contain("| where { |item| $item.command == $head }");
			text.Should().Contain("| each { |line| { value: $line, description: \"\" } }");
			text.Should().Contain("--no-interactive --no-logo");
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies nushell install requires --force when a managed global dispatcher already exists for another app.")]
	public void When_NuGlobalDispatcherExistsForAnotherApp_Then_InstallWithoutForceFails()
	{
		var paths = CreateTempPaths();
		try
		{
			var nuProfilePath = Path.Combine(paths.RootPath, "config.nu");
			var foreignEntry = BuildNuGlobalEntryLine("foreign-app", "foreign-tool");
			var seed = string.Join(
				Environment.NewLine,
				"# >>> repl completion [appId=__repl_nu_dispatcher__;shell=nu] >>>",
				foreignEntry,
				"# <<< repl completion [appId=__repl_nu_dispatcher__;shell=nu] <<<",
				string.Empty);
			File.WriteAllText(nuProfilePath, seed);

			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Nu, nuProfilePath: nuProfilePath);
			var output = RunSetup(["completion", "install", "--shell", "nushell", "--no-logo"], environment);

			output.ExitCode.Should().Be(1);
			output.Text.Should().Contain("--force");
			var text = File.ReadAllText(nuProfilePath);
			CountOccurrences(text, "# repl nu entry appId=").Should().Be(1);
			text.Should().Contain("foreign-app");
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies nushell install --force merges into global dispatcher and uninstall removes only current app entry.")]
	public void When_NuInstallForceAndUninstall_Then_GlobalDispatcherPreservesForeignEntries()
	{
		var paths = CreateTempPaths();
		try
		{
			var nuProfilePath = Path.Combine(paths.RootPath, "config.nu");
			var foreignAppBlock = string.Join(
				Environment.NewLine,
				"# >>> repl completion [appId=foreign-app;shell=nu] >>>",
				BuildNuAppCommandLine("foreign-tool"),
				"# <<< repl completion [appId=foreign-app;shell=nu] <<<",
				string.Empty);
			var foreignGlobalBlock = string.Join(
				Environment.NewLine,
				"# >>> repl completion [appId=__repl_nu_dispatcher__;shell=nu] >>>",
				BuildNuGlobalEntryLine("foreign-app", "foreign-tool"),
				"# <<< repl completion [appId=__repl_nu_dispatcher__;shell=nu] <<<",
				string.Empty);
			File.WriteAllText(nuProfilePath, foreignAppBlock + Environment.NewLine + foreignGlobalBlock);

			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Nu, nuProfilePath: nuProfilePath);
			var install = RunSetup(["completion", "install", "--shell", "nushell", "--force", "--no-logo"], environment);
			install.ExitCode.Should().Be(0);
			var afterInstall = File.ReadAllText(nuProfilePath);
			CountOccurrences(afterInstall, "# repl nu entry appId=").Should().Be(2);
			afterInstall.Should().Contain("foreign-app");

			var uninstall = RunSetup(["completion", "uninstall", "--shell", "nushell", "--no-logo"], environment);
			uninstall.ExitCode.Should().Be(0);
			var afterUninstall = File.ReadAllText(nuProfilePath);
			CountOccurrences(afterUninstall, "# repl nu entry appId=").Should().Be(1);
			afterUninstall.Should().Contain("foreign-app");
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies completion install --silent emits no payload and returns success via exit code only.")]
	public void When_CompletionInstallIsSilent_Then_OutputIsSuppressedAndExitCodeIndicatesSuccess()
	{
		var paths = CreateTempPaths();
		try
		{
			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Bash);
			var output = RunSetup(["completion", "install", "--shell", "bash", "--silent", "--no-logo"], environment);

			output.ExitCode.Should().Be(0);
			output.Text.Should().BeNullOrWhiteSpace();
			File.Exists(paths.ProfilePath).Should().BeTrue();
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies completion install --silent emits no payload and returns non-zero exit code on errors.")]
	public void When_CompletionInstallIsSilentAndUnavailable_Then_OutputIsSuppressedAndExitCodeIndicatesFailure()
	{
		var output = RunSetup(
			["completion", "install", "--shell", "bash", "--silent", "--no-logo"],
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["REPL_TEST_SHELL_COMPLETION_ENABLED"] = bool.FalseString,
			});

		output.ExitCode.Should().Be(1);
		output.Text.Should().BeNullOrWhiteSpace();
	}

	[TestMethod]
	[Description("Regression guard: verifies generated PowerShell completion script uses -Native when available and falls back to non-native registration.")]
	public void When_PowerShellCompletionScriptIsGenerated_Then_ItIncludesNativeFallback()
	{
		var paths = CreateTempPaths();
		try
		{
			var powerShellProfilePath = Path.Combine(paths.RootPath, "Microsoft.PowerShell_profile.ps1");
			var environment = CreateEnvironment(paths, preferredShell: ShellKind.PowerShell, powerShellProfilePath: powerShellProfilePath);
			var output = RunSetup(["completion", "install", "--shell", "powershell", "--no-logo"], environment);

			output.ExitCode.Should().Be(0);
			output.Text.Should().Contain(". $PROFILE");
			var text = File.ReadAllText(powerShellProfilePath);
			text.Should().Contain("Parameters.ContainsKey('Native')");
			text.Should().Contain("$__replCompletionCommandNames = @(");
			text.Should().Contain("Register-ArgumentCompleter -Native -CommandName $__replCompletionCommandNames");
			text.Should().Contain("Register-ArgumentCompleter -CommandName $__replCompletionCommandNames");
			text.Should().Contain("$commandAst.CommandElements[0].Value");
			text.Should().Contain("& $invokedCommand completion __complete");
			text.Should().Contain("--no-interactive --no-logo");
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies explicit completion help exposes subcommand descriptions for discoverability.")]
	public void When_RequestingHelpForCompletionContext_Then_SubcommandsHaveDescriptions()
	{
		var output = RunSetup(["completion", "--help", "--no-logo"]);

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("install");
		output.Text.Should().Contain("Install shell completion into the selected shell profile.");
		output.Text.Should().Contain("uninstall");
		output.Text.Should().Contain("Remove shell completion from the selected shell profile.");
		output.Text.Should().Contain("status");
		output.Text.Should().Contain("Show completion setup status and managed profile locations.");
		output.Text.Should().Contain("detect-shell");
		output.Text.Should().Contain("Detect the current shell using environment and parent process signals.");
	}

	[TestMethod]
	[Description("Regression guard: verifies completion uninstall command removes managed block from configured profile path.")]
	public void When_CompletionUninstallCommandIsUsed_Then_ManagedProfileBlockIsRemoved()
	{
		var paths = CreateTempPaths();
		try
		{
			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Bash);
			_ = RunSetup(["completion", "install", "--shell", "bash", "--no-logo"], environment);
			var uninstall = RunSetup(["completion", "uninstall", "--shell", "bash", "--no-logo"], environment);

			uninstall.ExitCode.Should().Be(0);
			var text = File.Exists(paths.ProfilePath) ? File.ReadAllText(paths.ProfilePath) : string.Empty;
			text.Should().NotContain(";shell=bash] >>>");
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies install/uninstall updates only the current app shell block and leaves foreign managed blocks intact.")]
	public void When_ProfileContainsForeignManagedBlock_Then_InstallAndUninstallOnlyTouchCurrentAppBlock()
	{
		var paths = CreateTempPaths();
		try
		{
			var foreignBlock = string.Join(
				Environment.NewLine,
				"# >>> repl completion [appId=foreign-app;shell=bash] >>>",
				"# foreign_marker_keep_me",
				"# <<< repl completion [appId=foreign-app;shell=bash] <<<",
				string.Empty);
			File.WriteAllText(paths.ProfilePath, foreignBlock);

			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Bash);
			var install = RunSetup(["completion", "install", "--shell", "bash", "--no-logo"], environment);
			install.ExitCode.Should().Be(0);
			var afterInstall = File.ReadAllText(paths.ProfilePath);
			afterInstall.Should().Contain("foreign_marker_keep_me");
			afterInstall.Should().Contain("completion __complete --shell bash");
			CountOccurrences(afterInstall, "# >>> repl completion [appId=").Should().Be(2);

			var uninstall = RunSetup(["completion", "uninstall", "--shell", "bash", "--no-logo"], environment);
			uninstall.ExitCode.Should().Be(0);
			var afterUninstall = File.ReadAllText(paths.ProfilePath);
			afterUninstall.Should().Contain("foreign_marker_keep_me");
			afterUninstall.Should().NotContain("completion __complete --shell bash");
			CountOccurrences(afterUninstall, "# >>> repl completion [appId=").Should().Be(1);
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies manual setup mode does not auto-install completion during interactive startup.")]
	public void When_SetupModeIsManual_Then_InteractiveStartupDoesNotAutoInstall()
	{
		var paths = CreateTempPaths();
		try
		{
			var environment = CreateEnvironment(
				paths,
				preferredShell: ShellKind.Bash,
				setupMode: ShellCompletionSetupMode.Manual,
				useDefaultInteractive: true);
			var output = RunSetup([], environment, "exit\n");

			output.ExitCode.Should().Be(0);
			File.Exists(paths.ProfilePath).Should().BeFalse();
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies auto setup mode installs completion during interactive startup when preferred shell is supported.")]
	public void When_SetupModeIsAuto_Then_InteractiveStartupInstallsCompletion()
	{
		var paths = CreateTempPaths();
		try
		{
			var environment = CreateEnvironment(
				paths,
				preferredShell: ShellKind.Bash,
				setupMode: ShellCompletionSetupMode.Auto,
				useDefaultInteractive: true);
			var output = RunSetup([], environment, "exit\n");

			output.ExitCode.Should().Be(0);
			File.Exists(paths.ProfilePath).Should().BeTrue();
			File.ReadAllText(paths.ProfilePath).Should().Contain(";shell=bash] >>>");
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies auto setup mode never mutates shell profiles during one-shot terminal command execution.")]
	public void When_SetupModeIsAutoAndRunningTerminalCommand_Then_NoAutoInstallOccurs()
	{
		var paths = CreateTempPaths();
		try
		{
			var environment = CreateEnvironment(
				paths,
				preferredShell: ShellKind.Bash,
				setupMode: ShellCompletionSetupMode.Auto);
			var output = RunSetup(["ping", "--no-logo"], environment);

			output.ExitCode.Should().Be(0);
			output.Text.Should().Contain("pong");
			File.Exists(paths.ProfilePath).Should().BeFalse();
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies completion management commands are blocked when shell completion feature is disabled.")]
	public void When_ShellCompletionFeatureIsDisabled_Then_CompletionManagementReturnsError()
	{
		var output = RunSetup(
			["completion", "status", "--no-logo"],
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["REPL_TEST_SHELL_COMPLETION_ENABLED"] = bool.FalseString,
			});

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("shell completion setup is disabled");
	}

	[TestMethod]
	[Description("Regression guard: verifies completion management commands do not exist in interactive mode and are resolved as unknown commands.")]
	public void When_CompletionCommandIsInvokedInInteractiveMode_Then_CommandIsUnknown()
	{
		var output = RunSetup(
			[],
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["REPL_TEST_USE_DEFAULT_INTERACTIVE"] = bool.TrueString,
			},
			"client 42\ncompletion status\nexit\n");

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Unknown command");
		output.Text.Should().Contain("completion status");
	}

	[TestMethod]
	[Description("Regression guard: verifies completion management commands are absent in hosted session channel.")]
	public void When_CompletionCommandIsInvokedInHostedSession_Then_CommandIsUnknown()
	{
		var sut = ReplApp.Create();
		using var input = new StringReader(string.Empty);
		using var outputWriter = new StringWriter();
		var host = new InMemoryHost(input, outputWriter);

		var exitCode = sut.Run(["completion", "status", "--no-logo"], host);

		exitCode.Should().Be(1);
		outputWriter.ToString().Should().Contain("Unknown command");
	}

	[TestMethod]
	[Description("Regression guard: verifies detect-shell honors preferred shell override for deterministic app configuration.")]
	public void When_DetectShellIsCalledWithPreferredShell_Then_OutputUsesOverride()
	{
		var output = RunSetup(
			["completion", "detect-shell", "--no-logo"],
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["REPL_TEST_SHELL_COMPLETION_PREFERRED_SHELL"] = nameof(ShellKind.PowerShell),
			});

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Detected shell: powershell");
		output.Text.Should().Contain("preferred override");
	}

	[TestMethod]
	[Description("Regression guard: verifies detect-shell supports zsh preferred override for deterministic app configuration.")]
	public void When_DetectShellIsCalledWithPreferredZsh_Then_OutputUsesOverride()
	{
		var output = RunSetup(
			["completion", "detect-shell", "--no-logo"],
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["REPL_TEST_SHELL_COMPLETION_PREFERRED_SHELL"] = nameof(ShellKind.Zsh),
			});

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Detected shell: zsh");
		output.Text.Should().Contain("preferred override");
	}

	[TestMethod]
	[Description("Regression guard: verifies detect-shell supports fish preferred override for deterministic app configuration.")]
	public void When_DetectShellIsCalledWithPreferredFish_Then_OutputUsesOverride()
	{
		var output = RunSetup(
			["completion", "detect-shell", "--no-logo"],
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["REPL_TEST_SHELL_COMPLETION_PREFERRED_SHELL"] = nameof(ShellKind.Fish),
			});

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Detected shell: fish");
		output.Text.Should().Contain("preferred override");
	}

	[TestMethod]
	[Description("Regression guard: verifies detect-shell supports nushell preferred override for deterministic app configuration.")]
	public void When_DetectShellIsCalledWithPreferredNu_Then_OutputUsesOverride()
	{
		var output = RunSetup(
			["completion", "detect-shell", "--no-logo"],
			new Dictionary<string, string?>(StringComparer.Ordinal)
			{
				["REPL_TEST_SHELL_COMPLETION_PREFERRED_SHELL"] = nameof(ShellKind.Nu),
			});

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Detected shell: nu");
		output.Text.Should().Contain("preferred override");
	}

	[TestMethod]
	[Description("Regression guard: verifies completion status supports structured JSON output through global output alias routing.")]
	public void When_CompletionStatusIsRequestedWithJsonAlias_Then_OutputIsStructuredJson()
	{
		var paths = CreateTempPaths();
		try
		{
			File.WriteAllText(paths.ProfilePath, "# bash profile");
			var powerShellProfilePath = Path.Combine(paths.RootPath, "Microsoft.PowerShell_profile.ps1");
			var environment = CreateEnvironment(paths, preferredShell: ShellKind.PowerShell, powerShellProfilePath: powerShellProfilePath);
			var output = RunSetup(["completion", "status", "--json", "--no-logo"], environment);

			output.ExitCode.Should().Be(0);
			using var payload = JsonDocument.Parse(output.Text);
			var root = payload.RootElement;
			root.GetProperty("enabled").GetBoolean().Should().BeTrue();
			root.GetProperty("setupMode").GetString().Should().NotBeNullOrWhiteSpace();
			root.GetProperty("detectedShell").GetString().Should().Be("powershell");
			root.GetProperty("detectionReason").GetString().Should().Contain("preferred override");
			root.GetProperty("bashProfileExists").GetBoolean().Should().BeTrue();
			root.GetProperty("powerShellProfileExists").GetBoolean().Should().BeFalse();
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies completion status JSON includes zsh profile path and install status fields.")]
	public void When_CompletionStatusIsRequestedWithZshPreferred_Then_StatusContainsZshFields()
	{
		var paths = CreateTempPaths();
		try
		{
			var zshProfilePath = Path.Combine(paths.RootPath, ".zshrc");
			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Zsh, zshProfilePath: zshProfilePath);
			var output = RunSetup(["completion", "status", "--json", "--no-logo"], environment);

			output.ExitCode.Should().Be(0);
			using var payload = JsonDocument.Parse(output.Text);
			var root = payload.RootElement;
			root.GetProperty("detectedShell").GetString().Should().Be("zsh");
			root.GetProperty("zshProfilePath").GetString().Should().Be(zshProfilePath);
			root.GetProperty("zshProfileExists").GetBoolean().Should().BeFalse();
			root.GetProperty("zshInstalled").GetBoolean().Should().BeFalse();
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies completion status JSON includes fish profile path and install status fields.")]
	public void When_CompletionStatusIsRequestedWithFishPreferred_Then_StatusContainsFishFields()
	{
		var paths = CreateTempPaths();
		try
		{
			var fishProfilePath = Path.Combine(paths.RootPath, "config.fish");
			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Fish, fishProfilePath: fishProfilePath);
			var output = RunSetup(["completion", "status", "--json", "--no-logo"], environment);

			output.ExitCode.Should().Be(0);
			using var payload = JsonDocument.Parse(output.Text);
			var root = payload.RootElement;
			root.GetProperty("detectedShell").GetString().Should().Be("fish");
			root.GetProperty("fishProfilePath").GetString().Should().Be(fishProfilePath);
			root.GetProperty("fishProfileExists").GetBoolean().Should().BeFalse();
			root.GetProperty("fishInstalled").GetBoolean().Should().BeFalse();
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies completion status JSON includes nushell profile path and install status fields.")]
	public void When_CompletionStatusIsRequestedWithNuPreferred_Then_StatusContainsNuFields()
	{
		var paths = CreateTempPaths();
		try
		{
			var nuProfilePath = Path.Combine(paths.RootPath, "config.nu");
			var environment = CreateEnvironment(paths, preferredShell: ShellKind.Nu, nuProfilePath: nuProfilePath);
			var output = RunSetup(["completion", "status", "--json", "--no-logo"], environment);

			output.ExitCode.Should().Be(0);
			using var payload = JsonDocument.Parse(output.Text);
			var root = payload.RootElement;
			root.GetProperty("detectedShell").GetString().Should().Be("nu");
			root.GetProperty("nuProfilePath").GetString().Should().Be(nuProfilePath);
			root.GetProperty("nuProfileExists").GetBoolean().Should().BeFalse();
			root.GetProperty("nuInstalled").GetBoolean().Should().BeFalse();
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	private static (int ExitCode, string Text) RunSetup(
		IReadOnlyList<string> args,
		IReadOnlyDictionary<string, string?>? environment = null,
		string? standardInput = null) =>
		ShellCompletionTestHostRunner.Run("setup", args, environment, standardInput);

	private static Dictionary<string, string?> CreateEnvironment(
		(string RootPath, string ProfilePath, string StatePath) paths,
		ShellKind? preferredShell = null,
		ShellCompletionSetupMode? setupMode = null,
		bool? enabled = null,
		bool useDefaultInteractive = false,
		string? bashProfilePath = null,
		string? powerShellProfilePath = null,
		string? zshProfilePath = null,
		string? fishProfilePath = null,
		string? nuProfilePath = null)
	{
		var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
		{
			["REPL_TEST_SHELL_COMPLETION_STATE_FILE_PATH"] = paths.StatePath,
			["REPL_TEST_SHELL_COMPLETION_BASH_PROFILE_PATH"] = bashProfilePath ?? paths.ProfilePath,
			["REPL_TEST_SHELL_COMPLETION_POWERSHELL_PROFILE_PATH"] = powerShellProfilePath ?? Path.Combine(paths.RootPath, "Microsoft.PowerShell_profile.ps1"),
			["REPL_TEST_SHELL_COMPLETION_ZSH_PROFILE_PATH"] = zshProfilePath ?? Path.Combine(paths.RootPath, ".zshrc"),
			["REPL_TEST_SHELL_COMPLETION_FISH_PROFILE_PATH"] = fishProfilePath ?? Path.Combine(paths.RootPath, "config.fish"),
			["REPL_TEST_SHELL_COMPLETION_NU_PROFILE_PATH"] = nuProfilePath ?? Path.Combine(paths.RootPath, "config.nu"),
		};

		if (preferredShell is not null)
		{
			environment["REPL_TEST_SHELL_COMPLETION_PREFERRED_SHELL"] = preferredShell.Value.ToString();
		}

		if (setupMode is not null)
		{
			environment["REPL_TEST_SHELL_COMPLETION_SETUP_MODE"] = setupMode.Value.ToString();
		}

		if (enabled is not null)
		{
			environment["REPL_TEST_SHELL_COMPLETION_ENABLED"] = enabled.Value.ToString();
		}

		if (useDefaultInteractive)
		{
			environment["REPL_TEST_USE_DEFAULT_INTERACTIVE"] = bool.TrueString;
		}

		return environment;
	}

	private static (string RootPath, string ProfilePath, string StatePath) CreateTempPaths()
	{
		var root = Path.Combine(Path.GetTempPath(), "repl-shell-completion-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
		Directory.CreateDirectory(root);
		return (
			RootPath: root,
			ProfilePath: Path.Combine(root, ".bashrc"),
			StatePath: Path.Combine(root, "state.txt"));
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch
		{
			// Best-effort cleanup for temp test directories.
		}
	}

	private static int CountOccurrences(string source, string value)
	{
		if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value))
		{
			return 0;
		}

		return source.Split([value], StringSplitOptions.None).Length - 1;
	}

	private static string BuildNuAppCommandLine(string commandName)
	{
		var commandB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(commandName));
		return $"# repl nu command-b64={commandB64}";
	}

	private static string BuildNuGlobalEntryLine(string appId, string commandName)
	{
		var commandB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(commandName));
		return $"# repl nu entry appId={appId};command-b64={commandB64}";
	}

	private sealed class InMemoryHost(TextReader input, TextWriter output) : IReplHost
	{
		public TextReader Input { get; } = input;

		public TextWriter Output { get; } = output;
	}
}
