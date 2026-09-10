using System.Globalization;
using Repl.Parameters;
using Repl.ShellCompletion;

namespace Repl.ShellCompletionTestHost;

internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		var app = ReplApp.Create();
		ConfigureScenario(app, Environment.GetEnvironmentVariable("REPL_TEST_SCENARIO"));
		ConfigureShellCompletionOptions(app);
		if (TryReadBoolean("REPL_TEST_USE_DEFAULT_INTERACTIVE", out var useInteractive) && useInteractive)
		{
			app.UseDefaultInteractive();
		}

		ReplRunOptions? runOptions = null;
		if (TryReadEnum<ProcessSignalHandlingMode>("REPL_TEST_SIGNAL_HANDLING", out var signalHandling))
		{
			runOptions = new ReplRunOptions { ProcessSignalHandling = signalHandling };
		}

		if (TryReadBoolean("REPL_TEST_USE_SYNC_RUN", out var useSynchronousRun) && useSynchronousRun)
		{
			return app.Run(args, runOptions);
		}

		return await app.RunAsync(args, runOptions).ConfigureAwait(false);
	}

	private static void ConfigureScenario(ReplApp app, string? scenario)
	{
		ArgumentNullException.ThrowIfNull(app);
		var normalized = string.IsNullOrWhiteSpace(scenario)
			? "completion"
			: scenario.Trim();
		switch (normalized.ToLowerInvariant())
		{
			case "completion":
			case "setup":
				ConfigureCompletionScenario(app);
				return;
			case "process-signal":
				ConfigureProcessSignalScenario(app);
				return;
			case "process-signal-exit-code":
				ConfigureProcessSignalExitCodeScenario(app);
				return;
			default:
				throw new InvalidOperationException(
					$"Unknown REPL test scenario '{scenario}'. Supported values: completion, setup, process-signal, process-signal-exit-code.");
		}
	}

	private static void ConfigureProcessSignalScenario(ReplApp app)
	{
		app.UseCliProfile();
		app.Map("wait {marker}", async (string marker, CancellationToken cancellationToken) =>
		{
			await File.WriteAllTextAsync(marker, "READY\n", CancellationToken.None).ConfigureAwait(false);
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				await File.AppendAllTextAsync(marker, "FINALLY\n", CancellationToken.None).ConfigureAwait(false);
				if (int.TryParse(
						Environment.GetEnvironmentVariable("REPL_TEST_SIGNAL_CLEANUP_DELAY_MS"),
						NumberStyles.Integer,
						CultureInfo.InvariantCulture,
						out var cleanupDelayMs)
					&& cleanupDelayMs > 0)
				{
					await Task.Delay(TimeSpan.FromMilliseconds(cleanupDelayMs), CancellationToken.None).ConfigureAwait(false);
					await File.AppendAllTextAsync(
						marker,
						"CLEANUP-COMPLETED\n",
						CancellationToken.None).ConfigureAwait(false);
				}
			}
		});
	}

	private static void ConfigureProcessSignalExitCodeScenario(ReplApp app)
	{
		app.UseCliProfile();
		app.Map("wait {marker}", async (string marker, CancellationToken cancellationToken) =>
		{
			await File.WriteAllTextAsync(marker, "READY\n", CancellationToken.None).ConfigureAwait(false);
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				await File.AppendAllTextAsync(
					marker,
					"HANDLER-RETURNED\n",
					CancellationToken.None).ConfigureAwait(false);
			}

			return Results.Exit(7);
		});
	}

	private static void ConfigureCompletionScenario(ReplApp app)
	{
		app.Map("contact list", () => "ok");
		app.Map("contact remove", () => "ok");
		app.Map(
			"contact show {id:int}",
			(Func<int, bool, string?, string>)((id, verbose, label) =>
				$"{id}-{verbose}-{label ?? string.Empty}"));
		app.Map("contact inspect", () => "ok")
			.WithCompletion(
				"clientId",
				static (_, input, _) =>
					ValueTask.FromResult<IReadOnlyList<string>>([$"{input}A", $"{input}B"]));

		// A shell-scoped positional provider whose values exercise the bridge's shell
		// encoding: a command-substitution string (must stay literal on acceptance) and a
		// value containing whitespace (must round-trip as ONE argument). The handler echoes
		// the bound value verbatim so the real-shell smoke can assert accept-to-argv.
		app.Map("deploy {target}", static string (string target) => target)
			.WithCompletion(
				"target",
				static (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(
					["$(printf PWNED)", "New York"]),
				CompletionProviderScope.InteractiveAndShell)
			.WithDescription("Deploy a target.");

		app.Map("config set", () => "ok");
		app.Map(
			"render",
			([ReplOption(Aliases = ["-m"])] CompletionRenderMode mode = CompletionRenderMode.Fast) =>
				mode.ToString());
		app.Map("send", () => "ok");
		app.Map("secret ping", () => "ok").Hidden();
		app.Map("ping", () => "pong");

		app.Context("admin", admin =>
		{
			admin.Map("reset", () => "ok");
			admin.Map("status", () => "ok");
		}).Hidden();

		app.Context("client", client =>
		{
			client.Context("{id}", scoped =>
			{
				scoped.Map(
					"show",
					(Func<string, string>)(id => id));
			});
		});
	}

	private enum CompletionRenderMode
	{
		Fast,
		Slow,
	}

	private static void ConfigureShellCompletionOptions(ReplApp app)
	{
		app.Options(options =>
		{
			if (TryReadBoolean("REPL_TEST_SHELL_COMPLETION_ENABLED", out var enabled))
			{
				options.ShellCompletion.Enabled = enabled;
			}

			if (TryReadEnum<ShellCompletionSetupMode>(
				"REPL_TEST_SHELL_COMPLETION_SETUP_MODE",
				out var setupMode))
			{
				options.ShellCompletion.SetupMode = setupMode;
			}

			if (TryReadEnum<ShellKind>(
				"REPL_TEST_SHELL_COMPLETION_PREFERRED_SHELL",
				out var preferredShell))
			{
				options.ShellCompletion.PreferredShell = preferredShell;
			}

			AssignIfPresent(
				"REPL_TEST_SHELL_COMPLETION_STATE_FILE_PATH",
				value => options.ShellCompletion.StateFilePath = value);
			AssignIfPresent(
				"REPL_TEST_SHELL_COMPLETION_BASH_PROFILE_PATH",
				value => options.ShellCompletion.BashProfilePath = value);
			AssignIfPresent(
				"REPL_TEST_SHELL_COMPLETION_POWERSHELL_PROFILE_PATH",
				value => options.ShellCompletion.PowerShellProfilePath = value);
			AssignIfPresent(
				"REPL_TEST_SHELL_COMPLETION_ZSH_PROFILE_PATH",
				value => options.ShellCompletion.ZshProfilePath = value);
			AssignIfPresent(
				"REPL_TEST_SHELL_COMPLETION_FISH_PROFILE_PATH",
				value => options.ShellCompletion.FishProfilePath = value);
			AssignIfPresent(
				"REPL_TEST_SHELL_COMPLETION_NU_PROFILE_PATH",
				value => options.ShellCompletion.NuProfilePath = value);
		});
	}

	private static bool TryReadBoolean(string variableName, out bool value)
	{
		value = default;
		var raw = Environment.GetEnvironmentVariable(variableName);
		return !string.IsNullOrWhiteSpace(raw)
			&& bool.TryParse(raw, out value);
	}

	private static bool TryReadEnum<TEnum>(
		string variableName,
		out TEnum value)
		where TEnum : struct, Enum
	{
		value = default;
		var raw = Environment.GetEnvironmentVariable(variableName);
		return !string.IsNullOrWhiteSpace(raw)
			&& Enum.TryParse<TEnum>(raw, ignoreCase: true, out value);
	}

	private static void AssignIfPresent(
		string variableName,
		Action<string> assign)
	{
		var value = Environment.GetEnvironmentVariable(variableName);
		if (string.IsNullOrWhiteSpace(value))
		{
			return;
		}

		assign(value);
	}
}
