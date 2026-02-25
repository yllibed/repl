using System.Globalization;
using System.Reflection;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Repl;

public sealed partial class CoreReplApp
{
	private const string ShellCompletionSetupCommandName = "completion";
	private const string ShellCompletionProtocolSubcommandName = "__complete";
	private const string ShellCompletionBridgeCommandName =
		ShellCompletionSetupCommandName + " " + ShellCompletionProtocolSubcommandName;
	private const string ShellCompletionUsage =
		"usage: completion __complete --shell <bash|powershell> --line <input> --cursor <position>";
	private const string ShellCompletionManagedBlockStartPrefix = "# >>> repl completion [";
	private const string ShellCompletionManagedBlockEndPrefix = "# <<< repl completion [";
	private const string ShellCompletionManagedBlockStartSuffix = "] >>>";
	private const string ShellCompletionManagedBlockEndSuffix = "] <<<";
	private const string ShellCompletionStateFileName = "shell-completion-state.json";

	private int HandleShellCompletionCommand(IReadOnlyList<string> commandTokens)
	{
		if (!ShellCompletionHostValidator.IsSupportedHostProcess(Environment.ProcessPath, ResolveEntryAssemblyName()))
		{
			Console.Error.WriteLine($"Error: {ShellCompletionHostValidator.UnsupportedHostMessage}");
			return 1;
		}

		var parsed = InvocationOptionParser.Parse(commandTokens);
		if (parsed.PositionalArguments.Count > 0)
		{
			return WriteShellCompletionUsageError(
				$"Error: {ShellCompletionBridgeCommandName} does not accept positional arguments.");
		}

		var unsupportedOption = parsed.NamedOptions.Keys.FirstOrDefault(option =>
			!string.Equals(option, "shell", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(option, "line", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(option, "cursor", StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrWhiteSpace(unsupportedOption))
		{
			return WriteShellCompletionUsageError(
				$"Error: unsupported option '--{unsupportedOption}' for {ShellCompletionBridgeCommandName}.");
		}

		if (!TryGetSingleNamedOption(parsed.NamedOptions, "shell", out var shell)
			|| !TryParseShellCompletionShell(shell))
		{
			return WriteShellCompletionUsageError(
				$"Error: {ShellCompletionBridgeCommandName} requires --shell <bash|powershell>.");
		}

		if (!TryGetSingleNamedOption(parsed.NamedOptions, "line", out var line))
		{
			return WriteShellCompletionUsageError(
				$"Error: {ShellCompletionBridgeCommandName} requires --line <input>.");
		}

		if (!TryGetSingleNamedOption(parsed.NamedOptions, "cursor", out var cursorText)
			|| !int.TryParse(
				cursorText,
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out var cursor)
			|| cursor < 0)
		{
			return WriteShellCompletionUsageError(
				$"Error: {ShellCompletionBridgeCommandName} requires --cursor <position> with a non-negative integer.");
		}

		var candidates = ResolveShellCompletionCandidates(line, cursor);
		foreach (var candidate in candidates)
		{
			ReplSessionIO.Output.WriteLine(candidate);
		}

		return 0;
	}

	private CommandExit HandleShellCompletionBridgeRoute(string? shell, string? line, string? cursor)
	{
		var commandTokens = new List<string>(capacity: 6);
		if (!string.IsNullOrWhiteSpace(shell))
		{
			commandTokens.Add("--shell");
			commandTokens.Add(shell);
		}

		if (line is not null)
		{
			commandTokens.Add("--line");
			commandTokens.Add(line);
		}

		if (!string.IsNullOrWhiteSpace(cursor))
		{
			commandTokens.Add("--cursor");
			commandTokens.Add(cursor);
		}

		var exitCode = HandleShellCompletionCommand(commandTokens);
		return new CommandExit(exitCode);
	}

	private object HandleShellCompletionSetupRoute()
	{
		var availabilityError = ValidateShellCompletionManagementAvailability();
		return availabilityError is not null
			? availabilityError
			: BuildShellCompletionSetupUsage();
	}

	private object HandleShellCompletionStatusRoute()
	{
		var availabilityError = ValidateShellCompletionManagementAvailability();
		if (availabilityError is not null)
		{
			return availabilityError;
		}

		var detection = DetectShellKind();
		return BuildShellCompletionStatusModel(detection);
	}

	private object HandleShellCompletionDetectShellRoute()
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

	private object HandleShellCompletionUnknownSubcommandRoute(string subCommand)
	{
		var availabilityError = ValidateShellCompletionManagementAvailability();
		if (availabilityError is not null)
		{
			return availabilityError;
		}

		if (string.Equals(subCommand, "help", StringComparison.OrdinalIgnoreCase))
		{
			return BuildShellCompletionSetupUsage();
		}

		return Results.Validation(
			$"Unknown completion subcommand '{subCommand}'.{Environment.NewLine}{BuildShellCompletionSetupUsage()}");
	}

	private async ValueTask<object> HandleShellCompletionInstallRouteAsync(
		string? shell,
		bool? force,
		CancellationToken cancellationToken)
	{
		var availabilityError = ValidateShellCompletionManagementAvailability();
		if (availabilityError is not null)
		{
			return availabilityError;
		}

		var detection = DetectShellKind();
		if (!TryResolveCompletionShell(shell, detection, out var shellKind, out var shellError))
		{
			return Results.Validation(shellError);
		}

		var operation = await InstallShellCompletionAsync(
				shellKind,
				detection,
				force ?? false,
				cancellationToken)
			.ConfigureAwait(false);
		if (!operation.Success)
		{
			return Results.Error("shell_completion_install_failed", operation.Message);
		}

		var state = LoadShellCompletionState();
		state.LastDetectedShell = detection.Kind.ToString();
		TryAddInstalledShell(state, shellKind);
		if (_options.ShellCompletion.SetupMode == ShellCompletionSetupMode.Prompt)
		{
			state.PromptShown = true;
		}

		TrySaveShellCompletionState(state);
		return new ShellCompletionInstallModel
		{
			Success = operation.Success,
			Changed = operation.Changed,
			Shell = FormatShellKind(shellKind),
			ProfilePath = operation.ProfilePath,
			Message = operation.Message,
		};
	}

	private async ValueTask<object> HandleShellCompletionUninstallRouteAsync(
		string? shell,
		CancellationToken cancellationToken)
	{
		var availabilityError = ValidateShellCompletionManagementAvailability();
		if (availabilityError is not null)
		{
			return availabilityError;
		}

		var detection = DetectShellKind();
		if (!TryResolveCompletionShell(shell, detection, out var shellKind, out var shellError))
		{
			return Results.Validation(shellError);
		}

		var operation = await UninstallShellCompletionAsync(shellKind, detection, cancellationToken).ConfigureAwait(false);
		if (!operation.Success)
		{
			return Results.Error("shell_completion_uninstall_failed", operation.Message);
		}

		var state = LoadShellCompletionState();
		state.LastDetectedShell = detection.Kind.ToString();
		state.InstalledShells.RemoveAll(item => string.Equals(item, shellKind.ToString(), StringComparison.OrdinalIgnoreCase));
		TrySaveShellCompletionState(state);
		return new ShellCompletionUninstallModel
		{
			Success = operation.Success,
			Changed = operation.Changed,
			Shell = FormatShellKind(shellKind),
			ProfilePath = operation.ProfilePath,
			Message = operation.Message,
		};
	}

	private string[] ResolveShellCompletionCandidates(string line, int cursor)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		var state = AnalyzeShellCompletionInput(line, cursor);
		if (state.PriorTokens.Length == 0)
		{
			return [];
		}

		var priorInvocationTokens = state.PriorTokens.Skip(1).ToArray();
		var parsed = InvocationOptionParser.Parse(priorInvocationTokens);
		var commandPrefix = parsed.PositionalArguments.ToArray();
		var currentTokenPrefix = state.CurrentTokenPrefix;
		var currentTokenIsOption = IsGlobalOptionToken(currentTokenPrefix);
		var routeMatch = Resolve(commandPrefix, activeGraph.Routes);
		var hasTerminalRoute = routeMatch is not null && routeMatch.RemainingTokens.Count == 0;
		var commandCandidates = currentTokenIsOption
			? []
			: CollectShellCommandCandidates(commandPrefix, currentTokenPrefix, activeGraph.Routes);
		var optionCandidates = currentTokenIsOption || (string.IsNullOrEmpty(currentTokenPrefix) && hasTerminalRoute)
			? CollectShellOptionCandidates(hasTerminalRoute ? routeMatch!.Route : null, currentTokenPrefix)
			: [];

		return commandCandidates
			.Concat(optionCandidates)
			.Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private IEnumerable<string> CollectShellCommandCandidates(
		string[] commandPrefix,
		string currentTokenPrefix,
		IReadOnlyList<RouteDefinition> routes)
	{
		var matchingRoutes = CollectVisibleMatchingRoutes(commandPrefix, StringComparison.OrdinalIgnoreCase, routes);
		foreach (var route in matchingRoutes)
		{
			if (commandPrefix.Length >= route.Template.Segments.Count
				|| route.Template.Segments[commandPrefix.Length] is not LiteralRouteSegment literal
				|| !literal.Value.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			yield return literal.Value;
		}
	}

	private IEnumerable<string> CollectShellOptionCandidates(RouteDefinition? route, string currentTokenPrefix)
	{
		foreach (var option in CollectGlobalShellOptionCandidates(currentTokenPrefix))
		{
			yield return option;
		}

		if (route is null)
		{
			yield break;
		}

		foreach (var option in CollectRouteShellOptionCandidates(route, currentTokenPrefix))
		{
			yield return option;
		}
	}

	private IEnumerable<string> CollectGlobalShellOptionCandidates(string currentTokenPrefix)
	{
		var staticOptions = new[]
		{
			"--help",
			"--interactive",
			"--no-interactive",
			"--no-logo",
			"--output:",
		};

		foreach (var option in staticOptions)
		{
			if (option.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				yield return option;
			}
		}

		foreach (var alias in _options.Output.Aliases.Keys)
		{
			var option = $"--{alias}";
			if (option.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				yield return option;
			}
		}

		foreach (var format in _options.Output.Transformers.Keys)
		{
			var option = $"--output:{format}";
			if (option.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				yield return option;
			}
		}
	}

	private static IEnumerable<string> CollectRouteShellOptionCandidates(RouteDefinition route, string currentTokenPrefix)
	{
		var routeParameterNames = route.Template.Segments
			.OfType<DynamicRouteSegment>()
			.Select(segment => segment.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var parameter in route.Command.Handler.Method.GetParameters())
		{
			if (string.IsNullOrWhiteSpace(parameter.Name)
				|| parameter.ParameterType == typeof(CancellationToken)
				|| routeParameterNames.Contains(parameter.Name)
				|| IsFrameworkInjectedParameter(parameter.ParameterType)
				|| parameter.GetCustomAttribute<FromContextAttribute>() is not null
				|| parameter.GetCustomAttribute<FromServicesAttribute>() is not null)
			{
				continue;
			}

			var option = $"--{parameter.Name}";
			if (option.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				yield return option;
			}
		}
	}

	private static ShellCompletionInputState AnalyzeShellCompletionInput(string input, int cursor)
	{
		input ??= string.Empty;
		cursor = Math.Clamp(cursor, 0, input.Length);
		var tokens = TokenizeInputSpans(input);
		for (var i = 0; i < tokens.Count; i++)
		{
			var token = tokens[i];
			if (cursor < token.Start || cursor > token.End)
			{
				continue;
			}

			var prior = tokens.Take(i).Select(static tokenSpan => tokenSpan.Value).ToArray();
			var prefix = input[token.Start..cursor];
			return new ShellCompletionInputState(prior, prefix);
		}

		var trailingPrior = tokens
			.Where(token => token.End <= cursor)
			.Select(static token => token.Value)
			.ToArray();
		return new ShellCompletionInputState(trailingPrior, CurrentTokenPrefix: string.Empty);
	}

	private readonly record struct ShellCompletionInputState(
		string[] PriorTokens,
		string CurrentTokenPrefix);

	private static bool TryGetSingleNamedOption(
		IReadOnlyDictionary<string, IReadOnlyList<string>> namedOptions,
		string name,
		out string value)
	{
		value = string.Empty;
		if (!namedOptions.TryGetValue(name, out var values) || values.Count != 1)
		{
			return false;
		}

		value = values[0];
		return true;
	}

	private static bool TryParseShellCompletionShell(string shell) =>
		string.Equals(shell, "bash", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase);

	private static int WriteShellCompletionUsageError(string message)
	{
		Console.Error.WriteLine(message);
		Console.Error.WriteLine(ShellCompletionUsage);
		return 2;
	}

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Startup flow needs explicit mode handling for manual/prompt/auto.")]
	private async ValueTask TryHandleShellCompletionStartupAsync(
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		if (!_options.ShellCompletion.Enabled
			|| _options.ShellCompletion.SetupMode == ShellCompletionSetupMode.Manual)
		{
			return;
		}

		if (!ShellCompletionHostValidator.IsSupportedHostProcess(Environment.ProcessPath, ResolveEntryAssemblyName()))
		{
			return;
		}

		var detection = DetectShellKind();
		if (detection.Kind is not ShellKind.Bash and not ShellKind.PowerShell)
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
						$"Tip: run '{ShellCompletionSetupCommandName} install --shell {FormatShellKindToken(detection.Kind)}' to enable shell completion.")
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

	private IReplResult? ValidateShellCompletionManagementAvailability()
	{
		if (!_options.ShellCompletion.Enabled)
		{
			return Results.Error(
				"shell_completion_disabled",
				"shell completion setup is disabled. Enable options.ShellCompletion.Enabled to use 'completion'.");
		}

		if (!ShellCompletionHostValidator.IsSupportedHostProcess(Environment.ProcessPath, ResolveEntryAssemblyName()))
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
			if (!TryParseShellKind(shellOption, out shellKind) || shellKind is not ShellKind.Bash and not ShellKind.PowerShell)
			{
				error = "option --shell must be one of bash|powershell.";
				return false;
			}

			return true;
		}

		if (detection.Kind is ShellKind.Bash or ShellKind.PowerShell)
		{
			shellKind = detection.Kind;
			return true;
		}

		error = "could not detect a supported shell. Specify --shell bash|powershell.";
		return false;
	}

	private async ValueTask<ShellCompletionOperationResult> InstallShellCompletionAsync(
		ShellKind shellKind,
		ShellDetectionResult detection,
		bool force,
		CancellationToken cancellationToken)
	{
		var profilePath = ResolveShellProfilePath(shellKind, detection);
		var appId = ResolveShellCompletionAppId();
		var commandName = ResolveShellCompletionCommandName();
		var block = BuildShellCompletionManagedBlock(shellKind, commandName, appId);
		try
		{
			var existing = File.Exists(profilePath)
				? await File.ReadAllTextAsync(profilePath, cancellationToken).ConfigureAwait(false)
				: string.Empty;
			var alreadyInstalled = ContainsShellCompletionManagedBlock(existing, shellKind, appId);
			if (alreadyInstalled && !force)
			{
				return new ShellCompletionOperationResult(
					Success: true,
					Changed: false,
					ProfilePath: profilePath,
					Message: $"Shell completion is already installed for {FormatShellKind(shellKind)} in '{profilePath}'. Use --force to rewrite.");
			}

			var updated = UpsertShellCompletionManagedBlock(existing, block, shellKind, appId);
			var directory = Path.GetDirectoryName(profilePath);
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}

			await File.WriteAllTextAsync(profilePath, updated, cancellationToken).ConfigureAwait(false);
			return new ShellCompletionOperationResult(
				Success: true,
				Changed: true,
				ProfilePath: profilePath,
				Message: $"Installed shell completion for {FormatShellKind(shellKind)} in '{profilePath}'.");
		}
		catch (Exception ex)
		{
			return new ShellCompletionOperationResult(
				Success: false,
				Changed: false,
				ProfilePath: profilePath,
				Message: $"Failed to install shell completion in '{profilePath}': {ex.Message}");
		}
	}

	private async ValueTask<ShellCompletionOperationResult> UninstallShellCompletionAsync(
		ShellKind shellKind,
		ShellDetectionResult detection,
		CancellationToken cancellationToken)
	{
		var profilePath = ResolveShellProfilePath(shellKind, detection);
		var appId = ResolveShellCompletionAppId();
		try
		{
			if (!File.Exists(profilePath))
			{
				return new ShellCompletionOperationResult(
					Success: true,
					Changed: false,
					ProfilePath: profilePath,
					Message: $"Shell completion is not installed for {FormatShellKind(shellKind)} (profile does not exist: '{profilePath}').");
			}

			var existing = await File.ReadAllTextAsync(profilePath, cancellationToken).ConfigureAwait(false);
			if (!TryRemoveShellCompletionManagedBlock(existing, shellKind, appId, out var updated))
			{
				return new ShellCompletionOperationResult(
					Success: true,
					Changed: false,
					ProfilePath: profilePath,
					Message: $"Shell completion block was not found in '{profilePath}'.");
			}

			await File.WriteAllTextAsync(profilePath, updated, cancellationToken).ConfigureAwait(false);
			return new ShellCompletionOperationResult(
				Success: true,
				Changed: true,
				ProfilePath: profilePath,
				Message: $"Removed shell completion for {FormatShellKind(shellKind)} from '{profilePath}'.");
		}
		catch (Exception ex)
		{
			return new ShellCompletionOperationResult(
				Success: false,
				Changed: false,
				ProfilePath: profilePath,
				Message: $"Failed to uninstall shell completion from '{profilePath}': {ex.Message}");
		}
	}

	private ShellDetectionResult DetectShellKind()
	{
		if (_options.ShellCompletion.PreferredShell is ShellKind preferred)
		{
			if (preferred is ShellKind.Bash or ShellKind.PowerShell)
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
		if (powershellScore == 0 && bashScore == 0)
		{
			return environment.HasKnownUnsupported
				? new ShellDetectionResult(ShellKind.Unsupported, environment.Reason)
				: new ShellDetectionResult(ShellKind.Unknown, "no shell signal");
		}

		if (powershellScore > bashScore)
		{
			return new ShellDetectionResult(
				ShellKind.PowerShell,
				BuildShellDetectionReason(environment.Reason, parent.Reason),
				parent.ParentLooksLikeWindowsPowerShell);
		}

		if (bashScore > powershellScore)
		{
			return new ShellDetectionResult(
				ShellKind.Bash,
				BuildShellDetectionReason(environment.Reason, parent.Reason));
		}

		return new ShellDetectionResult(
			ShellKind.Unknown,
			BuildShellDetectionReason(environment.Reason, parent.Reason));
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
		var psModulePath = Environment.GetEnvironmentVariable("PSModulePath");
		var psDistribution = Environment.GetEnvironmentVariable("POWERSHELL_DISTRIBUTION_CHANNEL");
		var powershellHint = !string.IsNullOrWhiteSpace(psDistribution)
			|| !string.IsNullOrWhiteSpace(psModulePath)
				&& psModulePath.Contains("PowerShell", StringComparison.OrdinalIgnoreCase);
		var bashHint = !string.IsNullOrWhiteSpace(bashVersion)
			|| shellVar.EndsWith("bash", StringComparison.OrdinalIgnoreCase)
			|| shellVar.EndsWith("bash.exe", StringComparison.OrdinalIgnoreCase);
		var unsupportedHint = shellVar.EndsWith("zsh", StringComparison.OrdinalIgnoreCase)
			|| shellVar.EndsWith("fish", StringComparison.OrdinalIgnoreCase)
			|| shellVar.EndsWith("sh", StringComparison.OrdinalIgnoreCase)
				&& !bashHint;
		var reason = string.Empty;
		if (powershellHint)
		{
			reason = "env suggests PowerShell";
		}
		else if (bashHint)
		{
			reason = "env suggests bash";
		}
		else if (unsupportedHint)
		{
			reason = $"env shell '{shellVar}' is currently unsupported";
		}

		return new ShellSignal(
			PowershellScore: powershellHint ? 3 : 0,
			BashScore: bashHint ? 3 : 0,
			HasKnownUnsupported: unsupportedHint,
			Reason: reason,
			ParentLooksLikeWindowsPowerShell: false);
	}

	private static ShellSignal DetectShellFromParentProcess()
	{
		var names = GetParentProcessNames();
		if (names.Length == 0)
		{
			return new ShellSignal(0, 0, HasKnownUnsupported: false, Reason: string.Empty, ParentLooksLikeWindowsPowerShell: false);
		}

		var powershellHint = names.Any(name =>
			string.Equals(name, "pwsh", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "powershell", StringComparison.OrdinalIgnoreCase));
		var bashHint = names.Any(name =>
			string.Equals(name, "bash", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "bash.exe", StringComparison.OrdinalIgnoreCase));
		var legacyPowerShell = names.Any(name =>
			string.Equals(name, "powershell", StringComparison.OrdinalIgnoreCase));
		var reason = powershellHint
			? "parent process suggests PowerShell"
			: bashHint
				? "parent process suggests bash"
				: $"parent process chain: {string.Join(" -> ", names)}";
		return new ShellSignal(
			PowershellScore: powershellHint ? 2 : 0,
			BashScore: bashHint ? 2 : 0,
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
			if (TryGetProcessName(parentProcessId, out var parentName))
			{
				names.Add(parentName);
			}

			if (TryGetParentProcessId(parentProcessId, out var grandParentProcessId)
				&& TryGetProcessName(grandParentProcessId, out var grandParentName))
			{
				names.Add(grandParentName);
			}

			return names.ToArray();
		}
		catch
		{
			return [];
		}
	}

	private static bool TryGetProcessName(int processId, out string name)
	{
		name = string.Empty;
		try
		{
			using var process = Process.GetProcessById(processId);
			name = process.ProcessName;
			return !string.IsNullOrWhiteSpace(name);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetParentProcessId(int processId, out int parentProcessId)
	{
		if (OperatingSystem.IsWindows())
		{
			return TryGetParentProcessIdWindows(processId, out parentProcessId);
		}

		if (TryGetParentProcessIdProcFs(processId, out parentProcessId))
		{
			return true;
		}

		return TryGetParentProcessIdFromPs(processId, out parentProcessId);
	}

	private static bool TryGetParentProcessIdProcFs(int processId, out int parentProcessId)
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
			var closingParen = stat.LastIndexOf(')');
			if (closingParen < 0 || closingParen + 2 >= stat.Length)
			{
				return false;
			}

			var tail = stat[(closingParen + 2)..];
			var parts = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2)
			{
				return false;
			}

			return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parentProcessId)
				&& parentProcessId > 0;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetParentProcessIdFromPs(int processId, out int parentProcessId)
	{
		parentProcessId = 0;
		try
		{
			var info = new ProcessStartInfo
			{
				FileName = "ps",
				Arguments = $"-o ppid= -p {processId}",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			using var process = Process.Start(info);
			if (process is null)
			{
				return false;
			}

			var output = process.StandardOutput.ReadToEnd();
			process.WaitForExit(milliseconds: 2000);
			return int.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parentProcessId)
				&& parentProcessId > 0;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetParentProcessIdWindows(int processId, out int parentProcessId)
	{
		parentProcessId = 0;
		const uint SnapshotFlag = 0x00000002;
		var snapshot = CreateToolhelp32Snapshot(SnapshotFlag, 0);
		if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
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

	private bool IsShellCompletionInstalled(ShellKind shellKind, ShellDetectionResult detection)
	{
		var profilePath = ResolveShellProfilePath(shellKind, detection);
		var appId = ResolveShellCompletionAppId();
		if (!File.Exists(profilePath))
		{
			return false;
		}

		try
		{
			var content = File.ReadAllText(profilePath);
			return ContainsShellCompletionManagedBlock(content, shellKind, appId);
		}
		catch
		{
			return false;
		}
	}

	private string ResolveShellProfilePath(ShellKind shellKind, ShellDetectionResult detection)
	{
		if (shellKind == ShellKind.Bash)
		{
			if (!string.IsNullOrWhiteSpace(_options.ShellCompletion.BashProfilePath))
			{
				return _options.ShellCompletion.BashProfilePath;
			}

			var home = ResolveUserHomePath();
			var bashRc = Path.Combine(home, ".bashrc");
			var bashProfile = Path.Combine(home, ".bash_profile");
			return File.Exists(bashRc) || !File.Exists(bashProfile)
				? bashRc
				: bashProfile;
		}

		if (!string.IsNullOrWhiteSpace(_options.ShellCompletion.PowerShellProfilePath))
		{
			return _options.ShellCompletion.PowerShellProfilePath;
		}

		var userHome = ResolveUserHomePath();
		if (OperatingSystem.IsWindows())
		{
			var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			var root = detection.ParentLooksLikeWindowsPowerShell
				? Path.Combine(documents, "WindowsPowerShell")
				: Path.Combine(documents, "PowerShell");
			return Path.Combine(root, "Microsoft.PowerShell_profile.ps1");
		}

		var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		var configRoot = !string.IsNullOrWhiteSpace(xdgConfig)
			? xdgConfig
			: Path.Combine(userHome, ".config");
		return Path.Combine(configRoot, "powershell", "Microsoft.PowerShell_profile.ps1");
	}

	private string ResolveShellCompletionStateFilePath()
	{
		if (!string.IsNullOrWhiteSpace(_options.ShellCompletion.StateFilePath))
		{
			return _options.ShellCompletion.StateFilePath;
		}

		var appName = ResolveShellCompletionAppId();
		if (OperatingSystem.IsWindows())
		{
			var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			var root = string.IsNullOrWhiteSpace(appData)
				? Path.Combine(ResolveUserHomePath(), "AppData", "Roaming")
				: appData;
			return Path.Combine(root, appName, ShellCompletionStateFileName);
		}

		var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		var configRoot = !string.IsNullOrWhiteSpace(xdgConfig)
			? xdgConfig
			: Path.Combine(ResolveUserHomePath(), ".config");
		return Path.Combine(configRoot, appName, ShellCompletionStateFileName);
	}

	private ShellCompletionState LoadShellCompletionState()
	{
		var path = ResolveShellCompletionStateFilePath();
		try
		{
			if (!File.Exists(path))
			{
				return new ShellCompletionState();
			}

			var state = new ShellCompletionState();
			var lines = File.ReadAllLines(path);
			foreach (var line in lines)
			{
				if (line.StartsWith("promptShown=", StringComparison.OrdinalIgnoreCase))
				{
					state.PromptShown = bool.TryParse(line["promptShown=".Length..], out var promptShown) && promptShown;
					continue;
				}

				if (line.StartsWith("lastDetectedShell=", StringComparison.OrdinalIgnoreCase))
				{
					state.LastDetectedShell = line["lastDetectedShell=".Length..];
					continue;
				}

				if (!line.StartsWith("installedShells=", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var shells = line["installedShells=".Length..]
					.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				state.InstalledShells = shells.ToList();
			}

			return state;
		}
		catch
		{
			return new ShellCompletionState();
		}
	}

	private void TrySaveShellCompletionState(ShellCompletionState state)
	{
		var path = ResolveShellCompletionStateFilePath();
		try
		{
			var directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}

			var lines = new[]
			{
				$"promptShown={(state.PromptShown ? "true" : "false")}",
				$"lastDetectedShell={state.LastDetectedShell ?? string.Empty}",
				$"installedShells={string.Join(',', state.InstalledShells)}",
			};
			File.WriteAllLines(path, lines);
		}
		catch
		{
			// Best-effort state persistence only.
		}
	}

	private static string BuildShellCompletionManagedBlockStartMarker(string appId, ShellKind shellKind) =>
		$"{ShellCompletionManagedBlockStartPrefix}appId={appId};shell={FormatShellKindToken(shellKind)}{ShellCompletionManagedBlockStartSuffix}";

	private static string BuildShellCompletionManagedBlockEndMarker(string appId, ShellKind shellKind) =>
		$"{ShellCompletionManagedBlockEndPrefix}appId={appId};shell={FormatShellKindToken(shellKind)}{ShellCompletionManagedBlockEndSuffix}";

	private static bool ContainsShellCompletionManagedBlock(string content, ShellKind shellKind, string appId)
	{
		var startMarker = BuildShellCompletionManagedBlockStartMarker(appId, shellKind);
		var endMarker = BuildShellCompletionManagedBlockEndMarker(appId, shellKind);
		return TryFindShellCompletionManagedBlock(content, startMarker, endMarker, out _, out _);
	}

	private static string UpsertShellCompletionManagedBlock(
		string content,
		string block,
		ShellKind shellKind,
		string appId)
	{
		var cleaned = RemoveShellCompletionManagedBlocks(
			content,
			shellKind,
			appId,
			out _);
		var builder = new System.Text.StringBuilder();
		if (!string.IsNullOrWhiteSpace(cleaned))
		{
			builder.Append(cleaned.TrimEnd());
			builder.AppendLine();
			builder.AppendLine();
		}

		builder.Append(block.TrimEnd());
		builder.AppendLine();
		return builder.ToString();
	}

	private static bool TryRemoveShellCompletionManagedBlock(
		string content,
		ShellKind shellKind,
		string appId,
		out string updated)
	{
		updated = RemoveShellCompletionManagedBlocks(content, shellKind, appId, out var removed);
		return removed;
	}

	private static string RemoveShellCompletionManagedBlocks(
		string content,
		ShellKind shellKind,
		string appId,
		out bool removedAny)
	{
		var startMarker = BuildShellCompletionManagedBlockStartMarker(appId, shellKind);
		var endMarker = BuildShellCompletionManagedBlockEndMarker(appId, shellKind);
		var updated = content;
		removedAny = false;
		while (TryFindShellCompletionManagedBlock(updated, startMarker, endMarker, out var start, out var end))
		{
			updated = updated[..start] + updated[end..];
			removedAny = true;
		}

		if (!removedAny)
		{
			return content;
		}

		updated = updated.TrimEnd();
		return updated.Length == 0
			? string.Empty
			: updated + Environment.NewLine;
	}

	private static bool TryFindShellCompletionManagedBlock(
		string content,
		string startMarker,
		string endMarker,
		out int blockStart,
		out int blockEnd)
	{
		blockStart = content.IndexOf(startMarker, StringComparison.Ordinal);
		blockEnd = 0;
		if (blockStart < 0)
		{
			return false;
		}

		var endMarkerIndex = content.IndexOf(endMarker, blockStart, StringComparison.Ordinal);
		if (endMarkerIndex < 0)
		{
			return false;
		}

		blockEnd = endMarkerIndex + endMarker.Length;
		while (blockEnd < content.Length && (content[blockEnd] == '\r' || content[blockEnd] == '\n'))
		{
			blockEnd++;
		}

		return true;
	}

	private static string BuildShellCompletionManagedBlock(
		ShellKind shellKind,
		string commandName,
		string appId) =>
		shellKind == ShellKind.Bash
			? BuildBashCompletionManagedBlock(commandName, appId)
			: BuildPowerShellCompletionManagedBlock(commandName, appId);

	private static string BuildBashCompletionManagedBlock(string commandName, string appId)
	{
		var functionName = BuildBashFunctionName(commandName);
		var startMarker = BuildShellCompletionManagedBlockStartMarker(appId, ShellKind.Bash);
		var endMarker = BuildShellCompletionManagedBlockEndMarker(appId, ShellKind.Bash);
		return string.Join(
			Environment.NewLine,
			startMarker,
			$"{functionName}() {{",
			"  local line cursor",
			"  local candidate",
			"  line=\"$COMP_LINE\"",
			"  cursor=\"$COMP_POINT\"",
			"  COMPREPLY=()",
			"  while IFS= read -r candidate; do",
			"    COMPREPLY+=(\"$candidate\")",
			$"  done < <({commandName} {ShellCompletionSetupCommandName} {ShellCompletionProtocolSubcommandName} --shell bash --line \"$line\" --cursor \"$cursor\" --no-interactive --no-logo)",
			"}",
			string.Empty,
			$"complete -F {functionName} {commandName}",
			endMarker);
	}

	private static string BuildPowerShellCompletionManagedBlock(string commandName, string appId)
	{
		var escapedCommandName = EscapePowerShellSingleQuotedLiteral(commandName);
		var escapedCommandNames = BuildPowerShellCompletionCommandNames(commandName)
			.Select(EscapePowerShellSingleQuotedLiteral)
			.ToArray();
		var escapedCommandNamesArrayLiteral = string.Join(", ", escapedCommandNames.Select(static name => $"'{name}'"));
		var escapedExpectedCommandPath = EscapePowerShellSingleQuotedLiteral(Environment.ProcessPath ?? string.Empty);
		var startMarker = BuildShellCompletionManagedBlockStartMarker(appId, ShellKind.PowerShell);
		var endMarker = BuildShellCompletionManagedBlockEndMarker(appId, ShellKind.PowerShell);
		return string.Join(
			Environment.NewLine,
			startMarker,
			$"$__replCompletionCommandNames = @({escapedCommandNamesArrayLiteral})",
			$"$__replExpectedCommandPath = '{escapedExpectedCommandPath}'",
			"$__replCompleter = {",
			"    param($wordToComplete, $commandAst, $cursorPosition)",
			string.Empty,
			"    $invokedCommand = if ($commandAst.CommandElements.Count -gt 0 -and $commandAst.CommandElements[0] -is [System.Management.Automation.Language.StringConstantExpressionAst]) {",
			"        $commandAst.CommandElements[0].Value",
			"    } else {",
			$"        '{escapedCommandName}'",
			"    }",
			string.Empty,
			"    $resolvedCommand = Get-Command $invokedCommand -ErrorAction SilentlyContinue",
			"    if ($null -eq $resolvedCommand) {",
			"        return",
			"    }",
			"    $resolvedPath = $resolvedCommand.Source",
			"    if ([string]::IsNullOrWhiteSpace($resolvedPath)) {",
			"        return",
			"    }",
			"    if (-not [string]::IsNullOrWhiteSpace($__replExpectedCommandPath) -and -not [string]::Equals($resolvedPath, $__replExpectedCommandPath, [System.StringComparison]::OrdinalIgnoreCase)) {",
			"        return",
			"    }",
			string.Empty,
			$"    & $invokedCommand {ShellCompletionSetupCommandName} {ShellCompletionProtocolSubcommandName} --shell powershell --line $commandAst.ToString() --cursor $cursorPosition --no-interactive --no-logo |",
			"        ForEach-Object {",
			"            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)",
			"        }",
			"}",
			string.Empty,
			"if ((Get-Command Register-ArgumentCompleter).Parameters.ContainsKey('Native')) {",
			"    Register-ArgumentCompleter -Native -CommandName $__replCompletionCommandNames -ScriptBlock $__replCompleter",
			"} else {",
			"    Register-ArgumentCompleter -CommandName $__replCompletionCommandNames -ScriptBlock $__replCompleter",
			"}",
			endMarker);
	}

	private static string[] BuildPowerShellCompletionCommandNames(string commandName)
	{
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			commandName,
		};

		var processPath = Environment.ProcessPath;
		var processFileName = string.IsNullOrWhiteSpace(processPath)
			? string.Empty
			: Path.GetFileName(processPath);
		if (!string.IsNullOrWhiteSpace(processFileName))
		{
			names.Add(processFileName);
			if (OperatingSystem.IsWindows())
			{
				names.Add($".\\{processFileName}");
			}
		}

		if (!string.IsNullOrWhiteSpace(processPath))
		{
			names.Add(processPath);
		}

		if (OperatingSystem.IsWindows()
			&& string.IsNullOrWhiteSpace(Path.GetExtension(commandName)))
		{
			names.Add($"{commandName}.exe");
		}

		return names.ToArray();
	}

	private static string EscapePowerShellSingleQuotedLiteral(string value) =>
		value.Replace("'", "''", StringComparison.Ordinal);

	private static string BuildBashFunctionName(string commandName)
	{
		var normalized = new string(commandName
			.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
			.ToArray());
		if (string.IsNullOrWhiteSpace(normalized))
		{
			normalized = "repl_complete";
		}

		if (char.IsDigit(normalized[0]))
		{
			normalized = $"_{normalized}";
		}

		return $"_{normalized}_complete";
	}

	private static string ResolveUserHomePath()
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (!string.IsNullOrWhiteSpace(home))
		{
			return home;
		}

		home = Environment.GetEnvironmentVariable("HOME");
		return string.IsNullOrWhiteSpace(home) ? "." : home;
	}

	private string ResolveShellCompletionCommandName()
	{
		var processPath = Environment.ProcessPath;
		if (!string.IsNullOrWhiteSpace(processPath))
		{
			var name = Path.GetFileNameWithoutExtension(processPath);
			if (!string.IsNullOrWhiteSpace(name))
			{
				return name;
			}
		}

		var app = BuildDocumentationApp();
		return string.IsNullOrWhiteSpace(app.Name) ? "repl" : app.Name;
	}

	private string ResolveShellCompletionAppId()
	{
		var entryAssembly = Assembly.GetEntryAssembly();
		var appName = entryAssembly?.GetName().Name;
		if (string.IsNullOrWhiteSpace(appName))
		{
			appName = ResolveShellCompletionCommandName();
		}

		return SanitizePathSegment(appName);
	}

	private static string SanitizePathSegment(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "repl";
		}

		var chars = value
			.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
			.ToArray();
		return new string(chars).Trim('-');
	}

	private static void TryAddInstalledShell(ShellCompletionState state, ShellKind shellKind)
	{
		if (state.InstalledShells.Exists(item => string.Equals(item, shellKind.ToString(), StringComparison.OrdinalIgnoreCase)))
		{
			return;
		}

		state.InstalledShells.Add(shellKind.ToString());
	}

	private static bool TryParseShellKind(string shell, out ShellKind shellKind)
	{
		if (string.Equals(shell, "bash", StringComparison.OrdinalIgnoreCase))
		{
			shellKind = ShellKind.Bash;
			return true;
		}

		if (string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(shell, "pwsh", StringComparison.OrdinalIgnoreCase))
		{
			shellKind = ShellKind.PowerShell;
			return true;
		}

		shellKind = ShellKind.Unknown;
		return false;
	}

	private static string FormatShellKind(ShellKind shellKind) => shellKind switch
	{
		ShellKind.Bash => "bash",
		ShellKind.PowerShell => "powershell",
		ShellKind.Unsupported => "unsupported",
		_ => "unknown",
	};

	private static string FormatShellKindToken(ShellKind shellKind) => shellKind switch
	{
		ShellKind.Bash => "bash",
		_ => "powershell",
	};

	private static string BuildShellCompletionSetupUsage() =>
		$"usage: {ShellCompletionSetupCommandName} <install|uninstall|status|detect-shell> [options]{Environment.NewLine}"
		+ $"  {ShellCompletionSetupCommandName} install [--shell bash|powershell] [--force]{Environment.NewLine}"
		+ $"  {ShellCompletionSetupCommandName} uninstall [--shell bash|powershell]{Environment.NewLine}"
		+ $"  {ShellCompletionSetupCommandName} status{Environment.NewLine}"
		+ $"  {ShellCompletionSetupCommandName} detect-shell";

	private static bool IsShellCompletionBridgeInvocation(IReadOnlyList<string> tokens) =>
		tokens.Count >= 2
		&& string.Equals(tokens[0], ShellCompletionSetupCommandName, StringComparison.OrdinalIgnoreCase)
		&& string.Equals(tokens[1], ShellCompletionProtocolSubcommandName, StringComparison.OrdinalIgnoreCase);

	private sealed class ShellCompletionStatusModel
	{
		[Display(Order = 1)]
		public bool Enabled { get; init; }

		[Display(Name = "Setup mode", Order = 2)]
		public string SetupMode { get; init; } = string.Empty;

		[Display(Name = "Detected shell", Order = 3)]
		public string Detected { get; init; } = string.Empty;

		[Display(Name = "Bash profile", Order = 4)]
		public string BashProfilePath { get; init; } = string.Empty;

		[Display(Name = "Bash installed", Order = 5)]
		public bool BashInstalled { get; init; }

		[Display(Name = "PowerShell profile", Order = 6)]
		public string PowerShellProfilePath { get; init; } = string.Empty;

		[Display(Name = "PowerShell installed", Order = 7)]
		public bool PowerShellInstalled { get; init; }

		[Browsable(false)]
		public string DetectedShell { get; init; } = string.Empty;

		[Browsable(false)]
		public string DetectionReason { get; init; } = string.Empty;
	}

	private sealed class ShellCompletionDetectShellModel
	{
		[Display(Name = "Detected shell", Order = 1)]
		public string Detected { get; init; } = string.Empty;

		[Browsable(false)]
		public string DetectedShell { get; init; } = string.Empty;

		[Browsable(false)]
		public string DetectionReason { get; init; } = string.Empty;
	}

	private sealed class ShellCompletionInstallModel
	{
		[Display(Order = 1)]
		public bool Success { get; init; }

		[Display(Order = 2)]
		public bool Changed { get; init; }

		[Display(Order = 3)]
		public string Shell { get; init; } = string.Empty;

		[Display(Name = "Profile path", Order = 4)]
		public string ProfilePath { get; init; } = string.Empty;

		[Display(Order = 5)]
		public string Message { get; init; } = string.Empty;
	}

	private sealed class ShellCompletionUninstallModel
	{
		[Display(Order = 1)]
		public bool Success { get; init; }

		[Display(Order = 2)]
		public bool Changed { get; init; }

		[Display(Order = 3)]
		public string Shell { get; init; } = string.Empty;

		[Display(Name = "Profile path", Order = 4)]
		public string ProfilePath { get; init; } = string.Empty;

		[Display(Order = 5)]
		public string Message { get; init; } = string.Empty;
	}

	private readonly record struct ShellCompletionOperationResult(
		bool Success,
		bool Changed,
		string ProfilePath,
		string Message);

	private readonly record struct ShellDetectionResult(
		ShellKind Kind,
		string Reason,
		bool ParentLooksLikeWindowsPowerShell = false);

	private readonly record struct ShellSignal(
		int PowershellScore,
		int BashScore,
		bool HasKnownUnsupported,
		string Reason,
		bool ParentLooksLikeWindowsPowerShell);

	private sealed class ShellCompletionState
	{
		public bool PromptShown { get; set; }

		public string? LastDetectedShell { get; set; }

		public List<string> InstalledShells { get; set; } = [];
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

