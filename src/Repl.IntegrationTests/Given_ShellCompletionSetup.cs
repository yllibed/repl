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
			var sut = ReplApp.Create();
			sut.Options(options =>
			{
				options.ShellCompletion.BashProfilePath = paths.ProfilePath;
				options.ShellCompletion.StateFilePath = paths.StatePath;
				options.ShellCompletion.PreferredShell = ShellKind.Bash;
			});

			var output = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "install", "--shell", "bash", "--no-logo"]));

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
			var sut = ReplApp.Create();
			sut.Options(options =>
			{
				options.ShellCompletion.BashProfilePath = paths.ProfilePath;
				options.ShellCompletion.StateFilePath = paths.StatePath;
				options.ShellCompletion.PreferredShell = ShellKind.Bash;
			});

			var output = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "install", "--shell", "bash", "--no-logo"]));

			output.ExitCode.Should().Be(0);
			var text = File.ReadAllText(paths.ProfilePath);
			text.Should().Contain("while IFS= read -r candidate; do");
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
			var sut = ReplApp.Create();
			sut.Options(options =>
			{
				options.ShellCompletion.ZshProfilePath = zshProfilePath;
				options.ShellCompletion.StateFilePath = paths.StatePath;
				options.ShellCompletion.PreferredShell = ShellKind.Zsh;
			});

			var output = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "install", "--shell", "zsh", "--no-logo"]));

			output.ExitCode.Should().Be(0);
			output.Text.Should().Contain("source ~/.zshrc");
			var text = File.ReadAllText(zshProfilePath);
			text.Should().Contain(";shell=zsh] >>>");
			text.Should().Contain("completion __complete --shell zsh");
			text.Should().Contain("compdef");
			text.Should().Contain("--no-interactive --no-logo");
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
			var sut = ReplApp.Create();
			sut.Options(options =>
			{
				options.ShellCompletion.BashProfilePath = paths.ProfilePath;
				options.ShellCompletion.StateFilePath = paths.StatePath;
				options.ShellCompletion.PreferredShell = ShellKind.Bash;
			});

			var output = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "install", "--shell", "bash", "--silent", "--no-logo"]));

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
		var sut = ReplApp.Create();
		sut.Options(options => options.ShellCompletion.Enabled = false);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "install", "--shell", "bash", "--silent", "--no-logo"]));

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
			var sut = ReplApp.Create();
			sut.Options(options =>
			{
				options.ShellCompletion.PowerShellProfilePath = powerShellProfilePath;
				options.ShellCompletion.StateFilePath = paths.StatePath;
				options.ShellCompletion.PreferredShell = ShellKind.PowerShell;
			});

			var output = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "install", "--shell", "powershell", "--no-logo"]));

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
		var sut = ReplApp.Create();

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "--help", "--no-logo"]));

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
			var sut = ReplApp.Create();
			sut.Options(options =>
			{
				options.ShellCompletion.BashProfilePath = paths.ProfilePath;
				options.ShellCompletion.StateFilePath = paths.StatePath;
				options.ShellCompletion.PreferredShell = ShellKind.Bash;
			});

			_ = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "install", "--shell", "bash", "--no-logo"]));
			var uninstall = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "uninstall", "--shell", "bash", "--no-logo"]));

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

			var sut = ReplApp.Create();
			sut.Options(options =>
			{
				options.ShellCompletion.BashProfilePath = paths.ProfilePath;
				options.ShellCompletion.StateFilePath = paths.StatePath;
				options.ShellCompletion.PreferredShell = ShellKind.Bash;
			});

			var install = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "install", "--shell", "bash", "--no-logo"]));
			install.ExitCode.Should().Be(0);
			var afterInstall = File.ReadAllText(paths.ProfilePath);
			afterInstall.Should().Contain("foreign_marker_keep_me");
			afterInstall.Should().Contain("completion __complete --shell bash");
			CountOccurrences(afterInstall, "# >>> repl completion [appId=").Should().Be(2);

			var uninstall = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "uninstall", "--shell", "bash", "--no-logo"]));
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
			var sut = ReplApp.Create().UseDefaultInteractive();
			sut.Options(options =>
			{
				options.ShellCompletion.SetupMode = ShellCompletionSetupMode.Manual;
				options.ShellCompletion.PreferredShell = ShellKind.Bash;
				options.ShellCompletion.BashProfilePath = paths.ProfilePath;
				options.ShellCompletion.StateFilePath = paths.StatePath;
			});

			var output = ConsoleCaptureHelper.CaptureWithInput("exit\n", () => sut.Run([]));

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
			var sut = ReplApp.Create().UseDefaultInteractive();
			sut.Options(options =>
			{
				options.ShellCompletion.SetupMode = ShellCompletionSetupMode.Auto;
				options.ShellCompletion.PreferredShell = ShellKind.Bash;
				options.ShellCompletion.BashProfilePath = paths.ProfilePath;
				options.ShellCompletion.StateFilePath = paths.StatePath;
			});

			var output = ConsoleCaptureHelper.CaptureWithInput("exit\n", () => sut.Run([]));

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
			var sut = ReplApp.Create();
			sut.Map("ping", () => "pong");
			sut.Options(options =>
			{
				options.ShellCompletion.SetupMode = ShellCompletionSetupMode.Auto;
				options.ShellCompletion.PreferredShell = ShellKind.Bash;
				options.ShellCompletion.BashProfilePath = paths.ProfilePath;
				options.ShellCompletion.StateFilePath = paths.StatePath;
			});

			var output = ConsoleCaptureHelper.Capture(() => sut.Run(["ping", "--no-logo"]));

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
		var sut = ReplApp.Create();
		sut.Options(options => options.ShellCompletion.Enabled = false);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "status", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("shell completion setup is disabled");
	}

	[TestMethod]
	[Description("Regression guard: verifies completion management commands do not exist in interactive mode and are resolved as unknown commands.")]
	public void When_CompletionCommandIsInvokedInInteractiveMode_Then_CommandIsUnknown()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Context("client {id}", client =>
		{
			client.Map("show", (Func<string, string>)(id => id));
		});

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"client 42\ncompletion status\nexit\n",
			() => sut.Run([]));

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
		var sut = ReplApp.Create();
		sut.Options(options => options.ShellCompletion.PreferredShell = ShellKind.PowerShell);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "detect-shell", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Detected shell: powershell");
		output.Text.Should().Contain("preferred override");
	}

	[TestMethod]
	[Description("Regression guard: verifies detect-shell supports zsh preferred override for deterministic app configuration.")]
	public void When_DetectShellIsCalledWithPreferredZsh_Then_OutputUsesOverride()
	{
		var sut = ReplApp.Create();
		sut.Options(options => options.ShellCompletion.PreferredShell = ShellKind.Zsh);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "detect-shell", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Detected shell: zsh");
		output.Text.Should().Contain("preferred override");
	}

	[TestMethod]
	[Description("Regression guard: verifies completion status supports structured JSON output through global output alias routing.")]
	public void When_CompletionStatusIsRequestedWithJsonAlias_Then_OutputIsStructuredJson()
	{
		var paths = CreateTempPaths();
		try
		{
			var sut = ReplApp.Create();
			sut.Options(options =>
			{
				options.ShellCompletion.PreferredShell = ShellKind.PowerShell;
				options.ShellCompletion.BashProfilePath = paths.ProfilePath;
				options.ShellCompletion.PowerShellProfilePath = Path.Combine(paths.RootPath, "Microsoft.PowerShell_profile.ps1");
				options.ShellCompletion.StateFilePath = paths.StatePath;
			});

			var output = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "status", "--json", "--no-logo"]));

			output.ExitCode.Should().Be(0);
			using var payload = JsonDocument.Parse(output.Text);
			var root = payload.RootElement;
			root.GetProperty("enabled").GetBoolean().Should().BeTrue();
			root.GetProperty("setupMode").GetString().Should().NotBeNullOrWhiteSpace();
			root.GetProperty("detectedShell").GetString().Should().Be("powershell");
			root.GetProperty("detectionReason").GetString().Should().Contain("preferred override");
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
			var sut = ReplApp.Create();
			sut.Options(options =>
			{
				options.ShellCompletion.PreferredShell = ShellKind.Zsh;
				options.ShellCompletion.ZshProfilePath = zshProfilePath;
				options.ShellCompletion.StateFilePath = paths.StatePath;
			});

			var output = ConsoleCaptureHelper.Capture(() => sut.Run(["completion", "status", "--json", "--no-logo"]));

			output.ExitCode.Should().Be(0);
			using var payload = JsonDocument.Parse(output.Text);
			var root = payload.RootElement;
			root.GetProperty("detectedShell").GetString().Should().Be("zsh");
			root.GetProperty("zshProfilePath").GetString().Should().Be(zshProfilePath);
			root.GetProperty("zshInstalled").GetBoolean().Should().BeFalse();
		}
		finally
		{
			TryDelete(paths.RootPath);
		}
	}

	private static (string RootPath, string ProfilePath, string StatePath) CreateTempPaths()
	{
		var root = Path.Combine(Path.GetTempPath(), "repl-shell-completion-tests", Guid.NewGuid().ToString("N"));
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

	private sealed class InMemoryHost(TextReader input, TextWriter output) : IReplHost
	{
		public TextReader Input { get; } = input;

		public TextWriter Output { get; } = output;
	}
}
