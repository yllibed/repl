namespace Repl;

/// <summary>Opt-in input adaptation and validation for advanced integration and CLI migration.</summary>
public sealed class InputOptions
{
	/// <summary>Gets adapters, invoked once each in registration order for every analysis.</summary>
	public IList<Func<ArgumentPreprocessingContext, CancellationToken, ValueTask<ArgumentPreprocessingResult>>> Preprocessors { get; } =
		new List<Func<ArgumentPreprocessingContext, CancellationToken, ValueTask<ArgumentPreprocessingResult>>>();

	/// <summary>Gets validators with injectable ParsedInputContext, services and CancellationToken parameters.</summary>
	public IList<Delegate> Validators { get; } = new List<Delegate>();

	internal bool IsEnabled => Preprocessors.Count != 0 || Validators.Count != 0;
}
