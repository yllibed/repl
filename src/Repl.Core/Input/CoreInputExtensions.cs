using System.Runtime.CompilerServices;

namespace Repl;

/// <summary>Registers advanced input callbacks on a core application.</summary>
public static class CoreInputExtensions
{
	/// <summary>Adds an argument adapter, including for preview and completion requests.</summary>
	/// <param name="app">Application to configure.</param>
	/// <param name="preprocessor">Callback returning an explicit argument decision.</param>
	/// <returns>The same application.</returns>
	// Keep null and throw-only callbacks bound to the original asynchronous overload.
	[OverloadResolutionPriority(1)]
	public static CoreReplApp UseArgumentPreprocessor(this CoreReplApp app,
		Func<ArgumentPreprocessingContext, CancellationToken, ValueTask<ArgumentPreprocessingResult>> preprocessor)
	{
		ArgumentNullException.ThrowIfNull(app);
		ArgumentNullException.ThrowIfNull(preprocessor);
		app.Options(options => options.Input.Preprocessors.Add(preprocessor));
		return app;
	}

	/// <inheritdoc cref="UseArgumentPreprocessor(CoreReplApp, Func{ArgumentPreprocessingContext, CancellationToken, ValueTask{ArgumentPreprocessingResult}})"/>
	public static CoreReplApp UseArgumentPreprocessor(this CoreReplApp app,
		Func<ArgumentPreprocessingContext, CancellationToken, ArgumentPreprocessingResult> preprocessor)
	{
		ArgumentNullException.ThrowIfNull(app);
		ArgumentNullException.ThrowIfNull(preprocessor);
		return app.UseArgumentPreprocessor((input, ct) => ValueTask.FromResult(preprocessor(input, ct)));
	}

	/// <summary>Adds a validator returning InputValidationResult, with parameters resolved from the current scope.</summary>
	/// <param name="app">Application to configure.</param>
	/// <param name="validator">Validator accepting ParsedInputContext, services and/or CancellationToken.</param>
	/// <returns>The same application.</returns>
	public static CoreReplApp UseInputValidator(this CoreReplApp app, Delegate validator)
	{
		ArgumentNullException.ThrowIfNull(app);
		ArgumentNullException.ThrowIfNull(validator);
		app.Options(options => options.Input.Validators.Add(validator));
		return app;
	}
}
