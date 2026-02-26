namespace Repl.ShellCompletion;

internal static class ShellCompletionAdapterRegistry
{
	private static readonly IShellCompletionAdapter[] s_adapterList =
	[
		BashShellCompletionAdapter.Instance,
		PowerShellShellCompletionAdapter.Instance,
		ZshShellCompletionAdapter.Instance,
		FishShellCompletionAdapter.Instance,
		NuShellCompletionAdapter.Instance,
	];

	private static readonly Dictionary<ShellKind, IShellCompletionAdapter> s_adapters =
		s_adapterList.ToDictionary(adapter => adapter.Kind);

	public static bool TryGetByKind(ShellKind kind, out IShellCompletionAdapter adapter) =>
		s_adapters.TryGetValue(kind, out adapter!);

	public static string BuildSupportedShellList() =>
		string.Join('|', s_adapterList.Select(static adapter => adapter.Token));

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

		if (string.Equals(normalized, "nushell", StringComparison.OrdinalIgnoreCase))
		{
			shellKind = ShellKind.Nu;
			return true;
		}

		foreach (var adapter in s_adapterList)
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
