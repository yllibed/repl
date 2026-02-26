namespace Repl.ShellCompletion;

internal static class ShellCompletionScriptBuilder
{
	public static string BuildManagedBlockStartMarker(string appId, ShellKind shellKind) =>
		$"{ShellCompletionConstants.ManagedBlockStartPrefix}appId={appId};shell={FormatShellToken(shellKind)}{ShellCompletionConstants.ManagedBlockStartSuffix}";

	public static string BuildManagedBlockEndMarker(string appId, ShellKind shellKind) =>
		$"{ShellCompletionConstants.ManagedBlockEndPrefix}appId={appId};shell={FormatShellToken(shellKind)}{ShellCompletionConstants.ManagedBlockEndSuffix}";

	internal static string[] BuildPowerShellCompletionCommandNames(string commandName)
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

	internal static string EscapePowerShellSingleQuotedLiteral(string value) =>
		value.Replace("'", "''", StringComparison.Ordinal);

	internal static string EscapeNuDoubleQuotedLiteral(string value)
	{
		System.Text.StringBuilder? builder = null;
		for (var index = 0; index < value.Length; index++)
		{
			var replacement = value[index] switch
			{
				'\\' => "\\\\",
				'"' => "\\\"",
				'\r' => "\\r",
				'\n' => "\\n",
				'\t' => "\\t",
				_ => null,
			};
			if (replacement is null)
			{
				builder?.Append(value[index]);
				continue;
			}

			builder ??= new System.Text.StringBuilder(value.Length + 8);
			if (builder.Length == 0 && index > 0)
			{
				builder.Append(value, 0, index);
			}

			builder.Append(replacement);
		}

		return builder is null ? value : builder.ToString();
	}

	internal static string BuildShellFunctionName(string commandName)
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
