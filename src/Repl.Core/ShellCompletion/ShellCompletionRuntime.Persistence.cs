using System.Reflection;
using System.Collections.Concurrent;

namespace Repl.ShellCompletion;

internal sealed partial class ShellCompletionRuntime
{
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathMutationLocks = new(StringComparer.Ordinal);

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
			var mutationLock = await AcquirePathMutationLockAsync(profilePath, cancellationToken).ConfigureAwait(false);
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

				await WriteTextFileAtomicallyAsync(profilePath, updated, cancellationToken).ConfigureAwait(false);
				return new ShellCompletionOperationResult(
					Success: true,
					Changed: true,
					ProfilePath: profilePath,
					Message:
						$"Installed shell completion for {FormatShellKind(shellKind)} in '{profilePath}'. "
						+ BuildShellReloadHint(shellKind));
			}
			finally
			{
				mutationLock.Release();
			}
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
			var mutationLock = await AcquirePathMutationLockAsync(profilePath, cancellationToken).ConfigureAwait(false);
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

				await WriteTextFileAtomicallyAsync(profilePath, updated, cancellationToken).ConfigureAwait(false);
				return new ShellCompletionOperationResult(
					Success: true,
					Changed: true,
					ProfilePath: profilePath,
					Message: $"Removed shell completion for {FormatShellKind(shellKind)} from '{profilePath}'.");
			}
			finally
			{
				mutationLock.Release();
			}
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
		var path = ResolveExistingShellCompletionStateFilePath(ResolveShellCompletionStateFilePath());
		try
		{
			if (string.IsNullOrWhiteSpace(path))
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
		var mutationLock = GetPathMutationLock(path);
		var lockTaken = false;
		try
		{
			mutationLock.Wait();
			lockTaken = true;
			var directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}

			var content = string.Join(
				'\n',
				$"promptShown={(state.PromptShown ? "true" : "false")}",
				$"lastDetectedShell={state.LastDetectedShell ?? string.Empty}",
				$"installedShells={string.Join(',', state.InstalledShells)}");
			WriteTextFileAtomically(path, content);
		}
		catch
		{
			// Best-effort state persistence only.
		}
		finally
		{
			if (lockTaken)
			{
				mutationLock.Release();
			}
		}
	}

	private static SemaphoreSlim GetPathMutationLock(string path)
	{
		var normalizedPath = Path.GetFullPath(path);
		return PathMutationLocks.GetOrAdd(normalizedPath, static _ => new SemaphoreSlim(1, 1));
	}

	private static async ValueTask<SemaphoreSlim> AcquirePathMutationLockAsync(string path, CancellationToken cancellationToken)
	{
		var semaphore = GetPathMutationLock(path);
		await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
		return semaphore;
	}

	private static string? ResolveExistingShellCompletionStateFilePath(string preferredPath)
	{
		if (File.Exists(preferredPath))
		{
			return preferredPath;
		}

		var legacyPath = ResolveLegacyShellCompletionStateFilePath(preferredPath);
		return !string.IsNullOrWhiteSpace(legacyPath) && File.Exists(legacyPath) ? legacyPath : null;
	}

	private static string? ResolveLegacyShellCompletionStateFilePath(string preferredPath)
	{
		var fileName = Path.GetFileName(preferredPath);
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return null;
		}

		var directory = Path.GetDirectoryName(preferredPath);
		if (string.Equals(fileName, ShellCompletionConstants.StateFileName, StringComparison.OrdinalIgnoreCase))
		{
			return string.IsNullOrWhiteSpace(directory)
				? ShellCompletionConstants.LegacyStateFileName
				: Path.Combine(directory, ShellCompletionConstants.LegacyStateFileName);
		}

		return fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
			? Path.ChangeExtension(preferredPath, ".json")
			: null;
	}

	private static async Task WriteTextFileAtomicallyAsync(
		string path,
		string content,
		CancellationToken cancellationToken)
	{
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var tempDirectory = string.IsNullOrWhiteSpace(directory)
			? Directory.GetCurrentDirectory()
			: directory;
		var tempPath = Path.Combine(
			tempDirectory,
			$".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
		try
		{
			await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
			File.Move(tempPath, path, overwrite: true);
		}
		finally
		{
			try
			{
				if (File.Exists(tempPath))
				{
					File.Delete(tempPath);
				}
			}
			catch
			{
				// Best-effort temp cleanup only.
			}
		}
	}

	private static void WriteTextFileAtomically(string path, string content)
	{
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var tempDirectory = string.IsNullOrWhiteSpace(directory)
			? Directory.GetCurrentDirectory()
			: directory;
		var tempPath = Path.Combine(
			tempDirectory,
			$".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
		try
		{
			File.WriteAllText(tempPath, content);
			File.Move(tempPath, path, overwrite: true);
		}
		finally
		{
			try
			{
				if (File.Exists(tempPath))
				{
					File.Delete(tempPath);
				}
			}
			catch
			{
				// Best-effort temp cleanup only.
			}
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
