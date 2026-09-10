namespace Repl;

/// <summary>
/// Provides default profile extension methods for common hosting scenarios.
/// </summary>
public static class ReplAppProfileExtensions
{
	/// <summary>
	/// Applies interactive defaults for console usage. This profile also takes process signal ownership
	/// for standalone runs — see <see cref="ProcessSignalHandlingMode.Automatic"/> for exactly what that
	/// means, including why an interactive session's own Ctrl+C behavior is unchanged. Set
	/// <see cref="ReplRunOptions.ProcessSignalHandling"/> to <see cref="ProcessSignalHandlingMode.None"/>
	/// to keep that ownership with the caller for one run.
	/// </summary>
	/// <param name="app">Target app.</param>
	/// <returns>The same app instance.</returns>
	public static ReplApp UseDefaultInteractive(this ReplApp app)
	{
		ArgumentNullException.ThrowIfNull(app);

		app.Options(options =>
		{
			options.Interactive.Prompt = ">";
			options.Interactive.InteractivePolicy = InteractivePolicy.Auto;
		});
		app.SetDefaultProcessSignalHandling(ProcessSignalHandlingMode.Automatic);

		return app;
	}

	/// <summary>
	/// Applies process-owning defaults suited for CLI one-shot execution. This profile takes process signal
	/// ownership for standalone runs — see <see cref="ProcessSignalHandlingMode.Automatic"/> for exactly
	/// what that means. Set <see cref="ReplRunOptions.ProcessSignalHandling"/> to
	/// <see cref="ProcessSignalHandlingMode.None"/> to keep that ownership with the caller for one run.
	/// </summary>
	/// <param name="app">Target app.</param>
	/// <returns>The same app instance.</returns>
	public static ReplApp UseCliProfile(this ReplApp app)
	{
		ArgumentNullException.ThrowIfNull(app);

		app.Options(options =>
		{
			options.Interactive.InteractivePolicy = InteractivePolicy.Auto;
			options.Output.DefaultFormat = "human";
			options.Output.BannerEnabled = true;
		});
		app.SetDefaultProcessSignalHandling(ProcessSignalHandlingMode.Automatic);

		return app;
	}

	/// <summary>
	/// Applies defaults suited for embedded host scenarios, leaving process signal ownership with the caller.
	/// </summary>
	/// <param name="app">Target app.</param>
	/// <returns>The same app instance.</returns>
	public static ReplApp UseEmbeddedConsoleProfile(this ReplApp app)
	{
		ArgumentNullException.ThrowIfNull(app);

		app.Options(options =>
		{
			options.AmbientCommands.ExitCommandEnabled = false;
			options.Interactive.InteractivePolicy = InteractivePolicy.Auto;
		});
		app.SetDefaultProcessSignalHandling(ProcessSignalHandlingMode.None);

		return app;
	}
}
