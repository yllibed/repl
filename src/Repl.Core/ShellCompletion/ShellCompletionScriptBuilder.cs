using System.Collections;

namespace Repl.ShellCompletion;

internal static class ShellCompletionScriptBuilder
{
	public static string BuildManagedBlockStartMarker(string appId, ShellKind shellKind) =>
		$"{ShellCompletionConstants.ManagedBlockStartPrefix}appId={appId};shell={FormatShellToken(shellKind)}{ShellCompletionConstants.ManagedBlockStartSuffix}";

	public static string BuildManagedBlockEndMarker(string appId, ShellKind shellKind) =>
		$"{ShellCompletionConstants.ManagedBlockEndPrefix}appId={appId};shell={FormatShellToken(shellKind)}{ShellCompletionConstants.ManagedBlockEndSuffix}";

	public static string BuildBashManagedBlock(string commandName, string appId)
	{
		var functionName = BuildShellFunctionName(commandName);
		var startMarker = BuildManagedBlockStartMarker(appId, ShellKind.Bash);
		var endMarker = BuildManagedBlockEndMarker(appId, ShellKind.Bash);
		return $$"""
{{startMarker}}
{{functionName}}() {
  local line cursor
  local candidate
  line="$COMP_LINE"
  cursor="$COMP_POINT"
  COMPREPLY=()
  while IFS= read -r candidate; do
    COMPREPLY+=("$candidate")
  done < <({{commandName}} {{ShellCompletionConstants.SetupCommandName}} {{ShellCompletionConstants.ProtocolSubcommandName}} --shell bash --line "$line" --cursor "$cursor" --no-interactive --no-logo)
}

complete -F {{functionName}} {{commandName}}
{{endMarker}}
""";
	}

	public static string BuildPowerShellManagedBlock(string commandName, string appId, string? expectedProcessPath)
	{
		var escapedCommandName = EscapePowerShellSingleQuotedLiteral(commandName);
		var escapedCommandNames = BuildPowerShellCompletionCommandNames(commandName)
			.Select(EscapePowerShellSingleQuotedLiteral)
			.ToArray();
		var escapedCommandNamesArrayLiteral = string.Join(", ", escapedCommandNames.Select(static name => $"'{name}'"));
		var escapedExpectedCommandPath = EscapePowerShellSingleQuotedLiteral(expectedProcessPath ?? string.Empty);
		var startMarker = BuildManagedBlockStartMarker(appId, ShellKind.PowerShell);
		var endMarker = BuildManagedBlockEndMarker(appId, ShellKind.PowerShell);
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

    & $invokedCommand {{ShellCompletionConstants.SetupCommandName}} {{ShellCompletionConstants.ProtocolSubcommandName}} --shell powershell --line $commandAst.ToString() --cursor $cursorPosition --no-interactive --no-logo |
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

	public static string BuildZshManagedBlock(string commandName, string appId)
	{
		var functionName = BuildShellFunctionName(commandName);
		var startMarker = BuildManagedBlockStartMarker(appId, ShellKind.Zsh);
		var endMarker = BuildManagedBlockEndMarker(appId, ShellKind.Zsh);
		return $$"""
{{startMarker}}
{{functionName}}() {
  local line cursor
  local candidate
  local -a reply
  line="$BUFFER"
  cursor=$((CURSOR - 1))
  reply=()
  while IFS= read -r candidate; do
    reply+=("$candidate")
  done < <({{commandName}} {{ShellCompletionConstants.SetupCommandName}} {{ShellCompletionConstants.ProtocolSubcommandName}} --shell zsh --line "$line" --cursor "$cursor" --no-interactive --no-logo)
  if (( ${#reply[@]} > 0 )); then
    compadd -- "${reply[@]}"
  fi
}

compdef {{functionName}} {{commandName}}
{{endMarker}}
""";
	}

	public static string BuildFishManagedBlock(string commandName, string appId)
	{
		var functionName = BuildShellFunctionName(commandName);
		var startMarker = BuildManagedBlockStartMarker(appId, ShellKind.Fish);
		var endMarker = BuildManagedBlockEndMarker(appId, ShellKind.Fish);
		return $$"""
{{startMarker}}
function {{functionName}}
  set -l line (commandline -cp)
  set -l cursor (string length -- $line)
  {{commandName}} {{ShellCompletionConstants.SetupCommandName}} {{ShellCompletionConstants.ProtocolSubcommandName}} --shell fish --line "$line" --cursor "$cursor" --no-interactive --no-logo
end

complete -c {{commandName}} -f -a "({{functionName}})"
{{endMarker}}
""";
	}

	public static string BuildNuManagedBlock(string commandName, string appId)
	{
		var functionName = BuildShellFunctionName(commandName);
		var escapedCommandName = EscapeNuSingleQuotedLiteral(commandName);
		var startMarker = BuildManagedBlockStartMarker(appId, ShellKind.Nu);
		var endMarker = BuildManagedBlockEndMarker(appId, ShellKind.Nu);
		return $$"""
{{startMarker}}
let __repl_completion_command = '{{escapedCommandName}}'
def --env {{functionName}} [spans: list<string>] {
  let line = ($spans | str join ' ')
  let cursor = ($line | str length)
  (
    ^$__repl_completion_command {{ShellCompletionConstants.SetupCommandName}} {{ShellCompletionConstants.ProtocolSubcommandName}} --shell nu --line $line --cursor $cursor --no-interactive --no-logo
    | lines
  )
}

$env.config = (
  $env.config
  | upsert completions.external.enable true
  | upsert completions.external.completer { |spans| {{functionName}} $spans }
)
{{endMarker}}
""";
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

	private static string EscapeNuSingleQuotedLiteral(string value) =>
		value.Replace("'", "''", StringComparison.Ordinal);

	private static string BuildShellFunctionName(string commandName)
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

	private static string FormatShellToken(ShellKind shellKind) =>
		ShellCompletionAdapterRegistry.TryGetByKind(shellKind, out var adapter)
			? adapter.Token
			: "unknown";
}
