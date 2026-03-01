using System.Globalization;
using Repl.Parameters;

namespace Repl.ShellCompletionTestHost;

internal static class Program
{
	private static int Main(string[] args)
	{
		var app = ReplApp.Create();
		ConfigureScenario(app, Environment.GetEnvironmentVariable("REPL_TEST_SCENARIO"));
		ConfigureShellCompletionOptions(app);
		if (TryReadBoolean("REPL_TEST_USE_DEFAULT_INTERACTIVE", out var useInteractive) && useInteractive)
		{
			app.UseDefaultInteractive();
		}

		return app.Run(args);
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
			default:
				throw new InvalidOperationException(
					$"Unknown REPL test scenario '{scenario}'. Supported values: completion, setup.");
		}
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
