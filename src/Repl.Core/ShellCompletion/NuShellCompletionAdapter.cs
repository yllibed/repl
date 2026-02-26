using System.Text;

namespace Repl.ShellCompletion;

internal sealed class NuShellCompletionAdapter : IShellCompletionAdapter
{
	internal const string NuCommandMetadataPrefix = "# repl nu command-b64=";
	internal const string NuGlobalEntryMetadataPrefix = "# repl nu entry appId=";
	internal const string NuGlobalEntryCommandSeparator = ";command-b64=";

	public static NuShellCompletionAdapter Instance { get; } = new();

	private NuShellCompletionAdapter()
	{
	}

	public ShellKind Kind => ShellKind.Nu;

	public string Token => "nu";

	public string ResolveProfilePath(
		ShellCompletionOptions options,
		bool parentLooksLikeWindowsPowerShell,
		string userHomePath)
	{
		if (!string.IsNullOrWhiteSpace(options.NuProfilePath))
		{
			return options.NuProfilePath;
		}

		if (OperatingSystem.IsWindows())
		{
			var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			var root = string.IsNullOrWhiteSpace(appData)
				? Path.Combine(userHomePath, "AppData", "Roaming")
				: appData;
			return Path.Combine(root, "nushell", "config.nu");
		}

		var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		var configRoot = !string.IsNullOrWhiteSpace(xdgConfig)
			? xdgConfig
			: Path.Combine(userHomePath, ".config");
		return Path.Combine(configRoot, "nushell", "config.nu");
	}

	public string BuildManagedBlock(string commandName, string appId)
	{
		var commandB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(commandName));
		var startMarker = ShellCompletionScriptBuilder.BuildManagedBlockStartMarker(appId, ShellKind.Nu);
		var endMarker = ShellCompletionScriptBuilder.BuildManagedBlockEndMarker(appId, ShellKind.Nu);
		return $$"""
			{{startMarker}}
			{{NuCommandMetadataPrefix}}{{commandB64}}
			{{endMarker}}
			""";
	}

	internal static string BuildGlobalDispatcherManagedBlock(IReadOnlyDictionary<string, string> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);

		var orderedEntries = entries
			.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var entryMetadataLines = string.Join(
			Environment.NewLine,
			orderedEntries.Select(entry => BuildGlobalEntryMetadataLine(entry.Key, entry.Value)));
		var entryRecords = string.Join(
			$",{Environment.NewLine}  ",
			orderedEntries.Select(entry =>
			{
				var escapedAppId = ShellCompletionScriptBuilder.EscapeNuDoubleQuotedLiteral(entry.Key);
				var escapedCommand = ShellCompletionScriptBuilder.EscapeNuDoubleQuotedLiteral(entry.Value);
				return $$"""{ appId: "{{escapedAppId}}", command: "{{escapedCommand}}" }""";
			}));
		var startMarker = ShellCompletionScriptBuilder.BuildManagedBlockStartMarker(ShellCompletionConstants.NuDispatcherAppId, ShellKind.Nu);
		var endMarker = ShellCompletionScriptBuilder.BuildManagedBlockEndMarker(ShellCompletionConstants.NuDispatcherAppId, ShellKind.Nu);
		return $$"""
			{{startMarker}}
			{{entryMetadataLines}}
			const __repl_completion_entries = [
			  {{entryRecords}}
			]
			def _repl_nu_dispatch_completion [spans: list<string>] {
			  if (($spans | length) == 0) {
			    return []
			  }

			  let head = ($spans | get 0)
			  let matches = ($__repl_completion_entries | where { |item| $item.command == $head })
			  if (($matches | length) == 0) {
			    return []
			  }

			  let entry = ($matches | get 0)
			  let line = ($spans | str join ' ')
			  let cursor = ($line | str length)
			  (
			    ^$entry.command {{ShellCompletionConstants.SetupCommandName}} {{ShellCompletionConstants.ProtocolSubcommandName}} --shell nu --line $line --cursor $cursor --no-interactive --no-logo
			    | lines
			    | each { |line| { value: $line, description: "" } }
			  )
			}

			$env.config = (
			  $env.config
			  | upsert completions.external.enable true
			)
			$env.config.completions.external.completer = { |spans| _repl_nu_dispatch_completion $spans }
			{{endMarker}}
			""";
	}

	internal static bool TryParseCommandFromAppMetadataLine(string line, out string commandName)
	{
		commandName = string.Empty;
		if (!line.StartsWith(NuCommandMetadataPrefix, StringComparison.Ordinal))
		{
			return false;
		}

		var encoded = line[NuCommandMetadataPrefix.Length..];
		return TryDecodeBase64(encoded, out commandName);
	}

	internal static bool TryParseGlobalEntryMetadataLine(string line, out string appId, out string commandName)
	{
		appId = string.Empty;
		commandName = string.Empty;
		if (!line.StartsWith(NuGlobalEntryMetadataPrefix, StringComparison.Ordinal))
		{
			return false;
		}

		var payload = line[NuGlobalEntryMetadataPrefix.Length..];
		var separatorIndex = payload.IndexOf(NuGlobalEntryCommandSeparator, StringComparison.Ordinal);
		if (separatorIndex <= 0)
		{
			return false;
		}

		appId = payload[..separatorIndex];
		var encodedCommand = payload[(separatorIndex + NuGlobalEntryCommandSeparator.Length)..];
		return !string.IsNullOrWhiteSpace(appId)
			&& TryDecodeBase64(encodedCommand, out commandName);
	}

	internal static string BuildGlobalEntryMetadataLine(string appId, string commandName)
	{
		var encodedCommand = Convert.ToBase64String(Encoding.UTF8.GetBytes(commandName));
		return $"{NuGlobalEntryMetadataPrefix}{appId}{NuGlobalEntryCommandSeparator}{encodedCommand}";
	}

	public string BuildReloadHint() =>
		"Reload your Nushell config (for example: 'source ~/.config/nushell/config.nu') or restart the shell to activate completions.";

	private static bool TryDecodeBase64(string encoded, out string value)
	{
		value = string.Empty;
		if (string.IsNullOrWhiteSpace(encoded))
		{
			return false;
		}

		try
		{
			var bytes = Convert.FromBase64String(encoded);
			value = Encoding.UTF8.GetString(bytes);
			return true;
		}
		catch
		{
			return false;
		}
	}
}
