namespace Repl;

/// <summary>Prepared input and its configuration, reusable without replaying preparation hooks.</summary>
public sealed class PreparedReplStartup
{
	private readonly Action<CoreReplApp>[] _configuration;
	private readonly CoreReplApp _preparationApp;
	internal PreparedReplStartup(object configurationKey, Action<CoreReplApp>[] configuration, CoreReplApp app, StartupInvocation invocation)
	{
		ConfigurationKey = configurationKey;
		_configuration = configuration;
		_preparationApp = app;
		Invocation = invocation;
		Context = new(app, invocation.Input);
	}
	internal object ConfigurationKey { get; }
	internal StartupInvocation Invocation { get; }
	internal int AdapterCount => _preparationApp.OptionsSnapshot.Input.Preprocessors.Count;
	/// <summary>Gets the preparation snapshot.</summary>
	public ReplStartupContext Context { get; }
	/// <summary>Gets preparation diagnostics without rendering them.</summary>
	public InputValidationResult Validation => Context.Validation;
	/// <summary>Gets a preparation callback failure, if one occurred.</summary>
	public Exception? Exception => Invocation.Failure;
	/// <summary>Gets whether composition and normal startup may continue.</summary>
	public bool CanContinue => Invocation.Failure is null && !Invocation.Input.IsHandled && Context.Validation.State == InputValidationState.Valid;
	/// <summary>Gets original arguments.</summary>
	public IReadOnlyList<string> OriginalArguments => Context.OriginalArguments;
	/// <summary>Gets effective arguments.</summary>
	public IReadOnlyList<string> Arguments => Context.Arguments;
	/// <summary>Gets arguments not consumed by global parsing.</summary>
	public IReadOnlyList<string> RemainingArguments => Context.RemainingArguments;
	/// <summary>Creates a registered options object using prepared values.</summary>
	/// <typeparam name="T">Options type.</typeparam>
	/// <returns>A fresh options object.</returns>
	public T GetOptions<T>() where T : class => Context.GetOptions<T>();
	/// <summary>Renders a terminal preparation result without constructing application services.</summary>
	/// <param name="cancellationToken">Caller cancellation.</param>
	/// <returns>The process exit code under the preparation configuration.</returns>
	public ValueTask<int> CompleteAsync(CancellationToken cancellationToken = default)
	{
		if (CanContinue) { throw new InvalidOperationException("Preparation can continue; create an application and run the prepared input."); }
		return _preparationApp.CompleteStartupAsync(Invocation, cancellationToken);
	}
	internal void Apply(CoreReplApp app)
	{
		foreach (var configure in _configuration) { configure(app); }
		app.SetStartupConfiguration(ConfigurationKey);
		var globals = GlobalOptionParser.Parse(Arguments, app.OptionsSnapshot.Output, app.OptionsSnapshot.Parsing);
		app.GlobalOptionsSnapshotInstance.Update(globals.CustomGlobalNamedOptions);
	}
}
