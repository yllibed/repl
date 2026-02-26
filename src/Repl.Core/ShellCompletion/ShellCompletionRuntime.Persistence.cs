using System.Reflection;

namespace Repl.ShellCompletion;

internal sealed partial class ShellCompletionRuntime
{
	private async ValueTask<ShellCompletionOperationResult> InstallShellCompletionAsync(
		ShellKind shellKind,
		ShellDetectionResult detection,
		bool force,
		CancellationToken cancellationToken)
	{
		var profilePath = ResolveShellProfilePath(shellKind, detection);
		var appId = ResolveShellCompletionAppId();
		var commandName = _resolveCommandName();
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
					Message:
						$"Shell completion is already installed for {FormatShellKind(shellKind)} in '{profilePath}'. Use --force to rewrite. "
						+ BuildShellReloadHint(shellKind));
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
				Message:
					$"Installed shell completion for {FormatShellKind(shellKind)} in '{profilePath}'. "
					+ BuildShellReloadHint(shellKind));
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
		if (!ShellCompletionAdapterRegistry.TryGetByKind(shellKind, out var adapter))
		{
			throw new InvalidOperationException($"Unsupported shell kind '{shellKind}'.");
		}

		return adapter.ResolveProfilePath(
			_options.ShellCompletion,
			detection.ParentLooksLikeWindowsPowerShell,
			ResolveUserHomePath());
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
			return Path.Combine(root, appName, ShellCompletionConstants.StateFileName);
		}

		var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		var configRoot = !string.IsNullOrWhiteSpace(xdgConfig)
			? xdgConfig
			: Path.Combine(ResolveUserHomePath(), ".config");
		return Path.Combine(configRoot, appName, ShellCompletionConstants.StateFileName);
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

	private static bool ContainsShellCompletionManagedBlock(string content, ShellKind shellKind, string appId)
	{
		var startMarker = ShellCompletionScriptBuilder.BuildManagedBlockStartMarker(appId, shellKind);
		var endMarker = ShellCompletionScriptBuilder.BuildManagedBlockEndMarker(appId, shellKind);
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
		var startMarker = ShellCompletionScriptBuilder.BuildManagedBlockStartMarker(appId, shellKind);
		var endMarker = ShellCompletionScriptBuilder.BuildManagedBlockEndMarker(appId, shellKind);
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
		string appId)
	{
		if (!ShellCompletionAdapterRegistry.TryGetByKind(shellKind, out var adapter))
		{
			throw new InvalidOperationException($"Unsupported shell kind '{shellKind}'.");
		}

		return adapter.BuildManagedBlock(commandName, appId);
	}

	private string ResolveShellCompletionAppId()
	{
		var entryAssembly = Assembly.GetEntryAssembly();
		var appName = entryAssembly?.GetName().Name;
		if (string.IsNullOrWhiteSpace(appName))
		{
			appName = _resolveCommandName();
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
}
