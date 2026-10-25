using System.Runtime.CompilerServices;

namespace Repl;

/// <summary>Registers advanced input callbacks on a DI-enabled application.</summary>
public static class InputExtensions
{
	/// <inheritdoc cref="CoreInputExtensions.UseArgumentPreprocessor(CoreReplApp, Func{ArgumentPreprocessingContext, CancellationToken, ValueTask{ArgumentPreprocessingResult}})"/>
	[OverloadResolutionPriority(1)]
	public static ReplApp UseArgumentPreprocessor(this ReplApp app,
		Func<ArgumentPreprocessingContext, CancellationToken, ValueTask<ArgumentPreprocessingResult>> preprocessor)
	{
		ArgumentNullException.ThrowIfNull(app);
		app.Core.UseArgumentPreprocessor(preprocessor);
		return app;
	}

	/// <inheritdoc cref="CoreInputExtensions.UseArgumentPreprocessor(CoreReplApp, Func{ArgumentPreprocessingContext, CancellationToken, ArgumentPreprocessingResult})"/>
	public static ReplApp UseArgumentPreprocessor(this ReplApp app,
		Func<ArgumentPreprocessingContext, CancellationToken, ArgumentPreprocessingResult> preprocessor)
	{
		ArgumentNullException.ThrowIfNull(app);
		app.Core.UseArgumentPreprocessor(preprocessor);
		return app;
	}

	/// <inheritdoc cref="CoreInputExtensions.UseInputValidator"/>
	public static ReplApp UseInputValidator(this ReplApp app, Delegate validator)
	{
		ArgumentNullException.ThrowIfNull(app);
		app.Core.UseInputValidator(validator);
		return app;
	}
}
