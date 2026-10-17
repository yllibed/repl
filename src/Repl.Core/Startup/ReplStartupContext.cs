namespace Repl;

/// <summary>A snapshot of startup input and global values; no command route is required.</summary>
public sealed class ReplStartupContext
{
	private readonly CoreReplApp _app;
	internal ReplStartupContext(CoreReplApp app, ProcessedInput input)
	{
		_app = app;
		OriginalArguments = input.Context.OriginalArguments;
		Arguments = input.Context.Arguments;
		var globals = GlobalOptionParser.Parse(Arguments, app.OptionsSnapshot.Output, app.OptionsSnapshot.Parsing);
		RemainingArguments = Array.AsReadOnly(globals.RemainingTokens.ToArray());
		Purpose = input.Context.Purpose;
		var accessor = new GlobalOptionsSnapshot(app.OptionsSnapshot.Parsing);
		accessor.Update(globals.CustomGlobalNamedOptions);
		GlobalOptions = accessor;
		Validation = input.Validation;
	}

	/// <summary>Gets arguments before preparation.</summary>
	public IReadOnlyList<string> OriginalArguments { get; }
	/// <summary>Gets the current effective arguments.</summary>
	public IReadOnlyList<string> Arguments { get; }
	/// <summary>Gets arguments remaining after global option parsing.</summary>
	public IReadOnlyList<string> RemainingArguments { get; }
	/// <summary>Gets the currently recognizable invocation purpose.</summary>
	public InputPurpose Purpose { get; }
	/// <summary>Gets this snapshot's global values.</summary>
	public IGlobalOptionsAccessor GlobalOptions { get; }
	/// <summary>Gets preparation diagnostics.</summary>
	public InputValidationResult Validation { get; }
	/// <summary>Creates the declared options type from this snapshot.</summary>
	/// <typeparam name="T">A registered global options type.</typeparam>
	/// <returns>A fresh options object.</returns>
	public T GetOptions<T>() where T : class => (T)(_app.CreateGlobalOptions(typeof(T), GlobalOptions)
		?? throw new InvalidOperationException($"Global options type '{typeof(T).Name}' is not registered."));
}
