using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace Repl.ShellCompletion;

internal sealed partial class ShellCompletionRuntime : IShellCompletionRuntime
{
	private readonly ReplOptions _options;
	private readonly Func<string> _resolveEntryAssemblyName;
	private readonly Func<string> _resolveCommandName;
	private readonly Func<string, int, string[]> _resolveCandidates;

	public ShellCompletionRuntime(
		ReplOptions options,
		Func<string> resolveEntryAssemblyName,
		Func<string> resolveCommandName,
		Func<string, int, string[]> resolveCandidates)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_resolveEntryAssemblyName = resolveEntryAssemblyName ?? throw new ArgumentNullException(nameof(resolveEntryAssemblyName));
		_resolveCommandName = resolveCommandName ?? throw new ArgumentNullException(nameof(resolveCommandName));
		_resolveCandidates = resolveCandidates ?? throw new ArgumentNullException(nameof(resolveCandidates));
	}

	public object HandleBridgeRoute(string? shell, string? line, string? cursor)
	{
		if (!ShellCompletionHostValidator.IsSupportedHostProcess(Environment.ProcessPath, _resolveEntryAssemblyName()))
		{
			return Results.Error("shell_completion_unsupported_host", ShellCompletionHostValidator.UnsupportedHostMessage);
		}

		if (string.IsNullOrWhiteSpace(shell)
			|| !TryParseShellKind(shell, out var shellKind)
			|| !IsShellCompletionSupportedShell(shellKind))
		{
			return CreateShellCompletionProtocolUsageError(
				$"{ShellCompletionConstants.BridgeCommandName} requires --shell <{BuildSupportedShellList()}>.");
		}

		if (line is null)
		{
			return CreateShellCompletionProtocolUsageError(
				$"{ShellCompletionConstants.BridgeCommandName} requires --line <input>.");
		}

		if (string.IsNullOrWhiteSpace(cursor)
			|| !int.TryParse(
				cursor,
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out var cursorPosition)
			|| cursorPosition < 0)
		{
			return CreateShellCompletionProtocolUsageError(
				$"{ShellCompletionConstants.BridgeCommandName} requires --cursor <position> with a non-negative integer.");
		}

		var candidates = _resolveCandidates(line, cursorPosition);
		return string.Join(Environment.NewLine, candidates);
	}

	public object HandleStatusRoute()
	{
		var availabilityError = ValidateShellCompletionManagementAvailability();
		if (availabilityError is not null)
		{
			return availabilityError;
		}

		var detection = DetectShellKind();
		return BuildShellCompletionStatusModel(detection);
	}

	public object HandleDetectShellRoute()
	{
		var availabilityError = ValidateShellCompletionManagementAvailability();
		if (availabilityError is not null)
		{
			return availabilityError;
		}

		var detection = DetectShellKind();
		return new ShellCompletionDetectShellModel
		{
			DetectedShell = FormatShellKind(detection.Kind),
			DetectionReason = detection.Reason,
			Detected = $"{FormatShellKind(detection.Kind)} ({detection.Reason})",
		};
	}

	public async ValueTask<object> HandleInstallRouteAsync(
		string? shell,
		bool? force,
		bool? silent,
		CancellationToken cancellationToken)
	{
		var isSilent = silent ?? false;
		var availabilityError = ValidateShellCompletionManagementAvailability();
		if (availabilityError is not null)
		{
			return isSilent
				? Results.Exit(1)
				: availabilityError;
		}

		var detection = DetectShellKind();
		if (!TryResolveCompletionShell(shell, detection, out var shellKind, out var shellError))
		{
			return isSilent
				? Results.Exit(1)
				: Results.Validation(shellError);
		}

		var operation = await InstallShellCompletionAsync(
				shellKind,
				detection,
				force ?? false,
				cancellationToken)
			.ConfigureAwait(false);
		if (!operation.Success)
		{
			return isSilent
				? Results.Exit(1)
				: Results.Error("shell_completion_install_failed", operation.Message);
		}

		var state = LoadShellCompletionState();
		state.LastDetectedShell = detection.Kind.ToString();
		TryAddInstalledShell(state, shellKind);
		if (_options.ShellCompletion.SetupMode == ShellCompletionSetupMode.Prompt)
		{
			state.PromptShown = true;
		}

		TrySaveShellCompletionState(state);
		if (isSilent)
		{
			return Results.Exit(0);
		}

		return new ShellCompletionInstallModel
		{
			Success = operation.Success,
			Changed = operation.Changed,
			Shell = FormatShellKind(shellKind),
			ProfilePath = operation.ProfilePath,
			Message = operation.Message,
		};
	}

	public async ValueTask<object> HandleUninstallRouteAsync(
		string? shell,
		bool? silent,
		CancellationToken cancellationToken)
	{
		var isSilent = silent ?? false;
		var availabilityError = ValidateShellCompletionManagementAvailability();
		if (availabilityError is not null)
		{
			return isSilent
				? Results.Exit(1)
				: availabilityError;
		}

		var detection = DetectShellKind();
		if (!TryResolveCompletionShell(shell, detection, out var shellKind, out var shellError))
		{
			return isSilent
				? Results.Exit(1)
				: Results.Validation(shellError);
		}

		var operation = await UninstallShellCompletionAsync(shellKind, detection, cancellationToken).ConfigureAwait(false);
		if (!operation.Success)
		{
			return isSilent
				? Results.Exit(1)
				: Results.Error("shell_completion_uninstall_failed", operation.Message);
		}

		var state = LoadShellCompletionState();
		state.LastDetectedShell = detection.Kind.ToString();
		state.InstalledShells.RemoveAll(item => string.Equals(item, shellKind.ToString(), StringComparison.OrdinalIgnoreCase));
		TrySaveShellCompletionState(state);
		if (isSilent)
		{
			return Results.Exit(0);
		}

		return new ShellCompletionUninstallModel
		{
			Success = operation.Success,
			Changed = operation.Changed,
			Shell = FormatShellKind(shellKind),
			ProfilePath = operation.ProfilePath,
			Message = operation.Message,
		};
	}

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Startup flow intentionally keeps Manual/Prompt/Auto branches explicit.")]
	public async ValueTask HandleStartupAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
	{
		if (!_options.ShellCompletion.Enabled
			|| _options.ShellCompletion.SetupMode == ShellCompletionSetupMode.Manual)
		{
			return;
		}

		if (!ShellCompletionHostValidator.IsSupportedHostProcess(Environment.ProcessPath, _resolveEntryAssemblyName()))
		{
			return;
		}

		var detection = DetectShellKind();
		if (!IsShellCompletionSupportedShell(detection.Kind))
		{
			return;
		}

		var state = LoadShellCompletionState();
		state.LastDetectedShell = detection.Kind.ToString();
		if (_options.ShellCompletion.SetupMode == ShellCompletionSetupMode.Prompt)
		{
			if (_options.ShellCompletion.PromptOnce && state.PromptShown)
			{
				return;
			}

			if (IsShellCompletionInstalled(detection.Kind, detection))
			{
				state.PromptShown = true;
				TrySaveShellCompletionState(state);
				return;
			}

			var installRequested = false;
			if (serviceProvider.GetService(typeof(IReplInteractionChannel)) is IReplInteractionChannel interaction)
			{
				installRequested = await interaction.AskConfirmationAsync(
						"shell_completion_install",
						$"Install shell completion for {FormatShellKind(detection.Kind)} now?",
						defaultValue: true,
						new AskOptions(cancellationToken))
					.ConfigureAwait(false);
			}
			else
			{
				await ReplSessionIO.Output.WriteLineAsync(
						$"Tip: run '{ShellCompletionConstants.SetupCommandName} install --shell {FormatShellKindToken(detection.Kind)}' to enable shell completion.")
					.ConfigureAwait(false);
			}

			state.PromptShown = true;
			TrySaveShellCompletionState(state);
			if (!installRequested)
			{
				return;
			}

			_ = await InstallShellCompletionAsync(detection.Kind, detection, force: false, cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		if (IsShellCompletionInstalled(detection.Kind, detection))
		{
			state.PromptShown = true;
			TrySaveShellCompletionState(state);
			return;
		}

		var autoInstall = await InstallShellCompletionAsync(detection.Kind, detection, force: false, cancellationToken)
			.ConfigureAwait(false);
		if (autoInstall.Success)
		{
			state.PromptShown = true;
			TryAddInstalledShell(state, detection.Kind);
			TrySaveShellCompletionState(state);
		}
	}

	public bool IsBridgeInvocation(IReadOnlyList<string> tokens) =>
		tokens.Count >= 2
		&& string.Equals(tokens[0], ShellCompletionConstants.SetupCommandName, StringComparison.OrdinalIgnoreCase)
		&& string.Equals(tokens[1], ShellCompletionConstants.ProtocolSubcommandName, StringComparison.OrdinalIgnoreCase);

	private IReplResult? ValidateShellCompletionManagementAvailability()
	{
		if (!_options.ShellCompletion.Enabled)
		{
			return Results.Error(
				"shell_completion_disabled",
				"shell completion setup is disabled. Enable options.ShellCompletion.Enabled to use 'completion'.");
		}

		if (!ShellCompletionHostValidator.IsSupportedHostProcess(Environment.ProcessPath, _resolveEntryAssemblyName()))
		{
			return Results.Error(
				"shell_completion_unsupported_host",
				ShellCompletionHostValidator.UnsupportedHostMessage);
		}

		return null;
	}

	private ShellCompletionStatusModel BuildShellCompletionStatusModel(ShellDetectionResult detection)
	{
		var bashInstalled = IsShellCompletionInstalled(ShellKind.Bash, detection);
		var powershellInstalled = IsShellCompletionInstalled(ShellKind.PowerShell, detection);
		var zshInstalled = IsShellCompletionInstalled(ShellKind.Zsh, detection);
		var fishInstalled = IsShellCompletionInstalled(ShellKind.Fish, detection);
		var nuInstalled = IsShellCompletionInstalled(ShellKind.Nu, detection);
		return new ShellCompletionStatusModel
		{
			Enabled = _options.ShellCompletion.Enabled,
			SetupMode = _options.ShellCompletion.SetupMode.ToString(),
			DetectedShell = FormatShellKind(detection.Kind),
			DetectionReason = detection.Reason,
			Detected = $"{FormatShellKind(detection.Kind)} ({detection.Reason})",
			BashProfilePath = ResolveShellProfilePath(ShellKind.Bash, detection),
			BashInstalled = bashInstalled,
			PowerShellProfilePath = ResolveShellProfilePath(ShellKind.PowerShell, detection),
			PowerShellInstalled = powershellInstalled,
			ZshProfilePath = ResolveShellProfilePath(ShellKind.Zsh, detection),
			ZshInstalled = zshInstalled,
			FishProfilePath = ResolveShellProfilePath(ShellKind.Fish, detection),
			FishInstalled = fishInstalled,
			NuProfilePath = ResolveShellProfilePath(ShellKind.Nu, detection),
			NuInstalled = nuInstalled,
		};
	}

	private static bool TryResolveCompletionShell(
		string? shellOption,
		ShellDetectionResult detection,
		out ShellKind shellKind,
		out string error)
	{
		error = string.Empty;
		shellKind = ShellKind.Unknown;
		if (!string.IsNullOrWhiteSpace(shellOption))
		{
			if (!TryParseShellKind(shellOption, out shellKind) || !IsShellCompletionSupportedShell(shellKind))
			{
				error = $"option --shell must be one of {BuildSupportedShellList()}.";
				return false;
			}

			return true;
		}

		if (IsShellCompletionSupportedShell(detection.Kind))
		{
			shellKind = detection.Kind;
			return true;
		}

		error = $"could not detect a supported shell. Specify --shell {BuildSupportedShellList()}.";
		return false;
	}

	private static bool IsShellCompletionSupportedShell(ShellKind shellKind) =>
		ShellCompletionAdapterRegistry.TryGetByKind(shellKind, out _);

	private static bool TryParseShellKind(string shell, out ShellKind shellKind) =>
		ShellCompletionAdapterRegistry.TryParseShellKind(shell, out shellKind);

	private static string FormatShellKind(ShellKind shellKind)
	{
		if (ShellCompletionAdapterRegistry.TryGetByKind(shellKind, out var adapter))
		{
			return adapter.Token;
		}

		return shellKind switch
		{
			ShellKind.Unsupported => "unsupported",
			_ => "unknown",
		};
	}

	private static string FormatShellKindToken(ShellKind shellKind) =>
		ShellCompletionAdapterRegistry.TryGetByKind(shellKind, out var adapter)
			? adapter.Token
			: "unknown";

	private static string BuildShellReloadHint(ShellKind shellKind)
	{
		if (ShellCompletionAdapterRegistry.TryGetByKind(shellKind, out var adapter))
		{
			return adapter.BuildReloadHint();
		}

		return "Restart the shell to activate completions.";
	}

	private static string BuildSupportedShellList() =>
		ShellCompletionAdapterRegistry.BuildSupportedShellList();

	private static IReplResult CreateShellCompletionProtocolUsageError(string message) =>
		Results.Error(
			"shell_completion_protocol_usage",
			$"{message}{Environment.NewLine}{BuildShellBridgeUsage()}");

	private static string BuildShellBridgeUsage() =>
		$"usage: {ShellCompletionConstants.SetupCommandName} {ShellCompletionConstants.ProtocolSubcommandName} --shell <{BuildSupportedShellList()}> --line <input> --cursor <position>";
}
