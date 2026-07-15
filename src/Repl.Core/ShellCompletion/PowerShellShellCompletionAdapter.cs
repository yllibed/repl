namespace Repl.ShellCompletion;

internal sealed class PowerShellShellCompletionAdapter : IShellCompletionAdapter
{
	public static PowerShellShellCompletionAdapter Instance { get; } = new();

	private PowerShellShellCompletionAdapter()
	{
	}

	public ShellKind Kind => ShellKind.PowerShell;

	public string Token => "powershell";

	public string ResolveProfilePath(
		ShellCompletionOptions options,
		bool parentLooksLikeWindowsPowerShell,
		string userHomePath)
	{
		if (!string.IsNullOrWhiteSpace(options.PowerShellProfilePath))
		{
			return options.PowerShellProfilePath;
		}

		if (OperatingSystem.IsWindows())
		{
			var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			var root = parentLooksLikeWindowsPowerShell
				? Path.Combine(documents, "WindowsPowerShell")
				: Path.Combine(documents, "PowerShell");
			return Path.Combine(root, "Microsoft.PowerShell_profile.ps1");
		}

		var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		var configRoot = !string.IsNullOrWhiteSpace(xdgConfig)
			? xdgConfig
			: Path.Combine(userHomePath, ".config");
		return Path.Combine(configRoot, "powershell", "Microsoft.PowerShell_profile.ps1");
	}

	public string BuildManagedBlock(string commandName, string appId)
	{
		var escapedCommandName = ShellCompletionScriptBuilder.EscapePowerShellSingleQuotedLiteral(commandName);
		var escapedCommandNames = ShellCompletionScriptBuilder.BuildPowerShellCompletionCommandNames(commandName)
			.Select(ShellCompletionScriptBuilder.EscapePowerShellSingleQuotedLiteral)
			.ToArray();
		var escapedCommandNamesArrayLiteral = string.Join(", ", escapedCommandNames.Select(static name => $"'{name}'"));
		var escapedExpectedCommandPath = ShellCompletionScriptBuilder.EscapePowerShellSingleQuotedLiteral(Environment.ProcessPath ?? string.Empty);
		var startMarker = ShellCompletionScriptBuilder.BuildManagedBlockStartMarker(appId, ShellKind.PowerShell);
		var endMarker = ShellCompletionScriptBuilder.BuildManagedBlockEndMarker(appId, ShellKind.PowerShell);
		return $$"""
			{{startMarker}}
			$__replCompletionCommandNames = @({{escapedCommandNamesArrayLiteral}})
			$__replExpectedCommandPath = '{{escapedExpectedCommandPath}}'
			$__replCompleter = {
			    param($wordToComplete, $commandAst, $cursorPosition)

			    $invokedCommand = if ($commandAst.CommandElements.Count -gt 0 -and $commandAst.CommandElements[0] -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
			        $commandAst.CommandElements[0].Value
			    } else {
			        '{{escapedCommandName}}'
			    }

			    $resolvedCommand = Get-Command $invokedCommand -ErrorAction SilentlyContinue
			    if ($null -eq $resolvedCommand) {
			        return
			    }
			    $resolvedPath = $resolvedCommand.Source
			    if ([string]::IsNullOrWhiteSpace($resolvedPath)) {
			        return
			    }
			    if (-not [string]::IsNullOrWhiteSpace($__replExpectedCommandPath) -and -not [string]::Equals($resolvedPath, $__replExpectedCommandPath, [System.StringComparison]::OrdinalIgnoreCase)) {
			        return
			    }

			    # $commandAst drops trailing whitespace while $cursorPosition still counts it,
			    # so a line ending right after a token (an EMPTY value position, e.g. 'app deploy ')
			    # would otherwise be analyzed as the previous token. Rebuild the line relative to
			    # the command's start and pad it back out to the cursor so the empty position
			    # survives to the bridge.
			    $replLine = $commandAst.Extent.Text
			    $replCursor = $cursorPosition - $commandAst.Extent.StartOffset
			    if ($replCursor -lt 0) { $replCursor = 0 }
			    if ($replLine.Length -lt $replCursor) { $replLine = $replLine.PadRight($replCursor) }

			    & $invokedCommand {{ShellCompletionConstants.SetupCommandName}} {{ShellCompletionConstants.ProtocolSubcommandName}} --shell powershell --line $replLine --cursor $replCursor --no-interactive --no-logo |
			        ForEach-Object {
			            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
			        }
			}

			if ((Get-Command Register-ArgumentCompleter).Parameters.ContainsKey('Native')) {
			    Register-ArgumentCompleter -Native -CommandName $__replCompletionCommandNames -ScriptBlock $__replCompleter
			} else {
			    Register-ArgumentCompleter -CommandName $__replCompletionCommandNames -ScriptBlock $__replCompleter
			}
			{{endMarker}}
			""";
	}

	public string BuildReloadHint() =>
		"Reload your PowerShell profile (for example: '. $PROFILE') or restart the shell to activate completions.";
}
