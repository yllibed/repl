namespace Repl.ShellCompletion;

internal static class ShellCompletionAdapterRegistry
{
	private static readonly IReadOnlyDictionary<ShellKind, IShellCompletionAdapter> s_adapters =
		new Dictionary<ShellKind, IShellCompletionAdapter>
		{
			[ShellKind.Bash] = BashShellCompletionAdapter.Instance,
			[ShellKind.PowerShell] = PowerShellShellCompletionAdapter.Instance,
			[ShellKind.Zsh] = ZshShellCompletionAdapter.Instance,
		};

	public static bool TryGetByKind(ShellKind kind, out IShellCompletionAdapter adapter) =>
		s_adapters.TryGetValue(kind, out adapter!);

	public static bool TryParseShellKind(string shell, out ShellKind shellKind)
	{
		shellKind = ShellKind.Unknown;
		if (string.IsNullOrWhiteSpace(shell))
		{
			return false;
		}

		var normalized = shell.Trim();
		if (string.Equals(normalized, "pwsh", StringComparison.OrdinalIgnoreCase))
		{
			shellKind = ShellKind.PowerShell;
			return true;
		}

		foreach (var adapter in s_adapters.Values)
		{
			if (string.Equals(normalized, adapter.Token, StringComparison.OrdinalIgnoreCase))
			{
				shellKind = adapter.Kind;
				return true;
			}
		}

		return false;
	}
}
