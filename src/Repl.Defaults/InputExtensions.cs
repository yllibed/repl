namespace Repl;

/// <summary>Registers advanced input callbacks on a DI-enabled application.</summary>
public static class InputExtensions
{
	/// <summary>Adds an argument adapter, including for preview and completion requests.</summary>
	/// <param name="app">Application to configure.</param>
	/// <param name="preprocessor">Callback returning an explicit argument decision.</param>
	/// <returns>The same application.</returns>
	public static ReplApp UseArgumentPreprocessor(this ReplApp app,
		Func<ArgumentPreprocessingContext, CancellationToken, ValueTask<ArgumentPreprocessingResult>> preprocessor)
	{
		ArgumentNullException.ThrowIfNull(app);
		app.Core.UseArgumentPreprocessor(preprocessor);
		return app;
	}

	/// <summary>Adds a validator returning InputValidationResult, with parameters resolved from the current scope.</summary>
	/// <param name="app">Application to configure.</param>
	/// <param name="validator">Validator accepting ParsedInputContext, services and/or CancellationToken.</param>
	/// <returns>The same application.</returns>
	public static ReplApp UseInputValidator(this ReplApp app, Delegate validator)
	{
		ArgumentNullException.ThrowIfNull(app);
		app.Core.UseInputValidator(validator);
		return app;
	}
}
