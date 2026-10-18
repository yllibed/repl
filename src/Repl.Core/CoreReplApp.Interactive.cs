namespace Repl;

public sealed partial class CoreReplApp
{
	private InteractiveSession? _interactiveSession;
	internal InteractiveSession Interactive => _interactiveSession ??= new(this);

	private bool ShouldEnterInteractive(GlobalInvocationOptions globalOptions, bool allowAuto) =>
		Interactive.ShouldEnterInteractive(globalOptions, allowAuto);

	private async ValueTask<ExecutionOutcome> RunInteractiveSessionAsync(
		IReadOnlyList<string> initialScopeTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		using var nativeSession = _globalOptionsSnapshot.PushNativeSessionBaseline(
			_currentInput.Value?.Request?.Copy().GlobalValues ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
		using var globalsScope = _globalOptionsSnapshot.PushInputValues(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
		using var inputScope = PushProcessedInput(input: null);
		await Interactive.RunInteractiveSessionAsync(initialScopeTokens, serviceProvider, cancellationToken)
			.ConfigureAwait(false);
		return ExecutionOutcome.Success;
	}

	private string[] GetDeepestContextScopePath(IReadOnlyList<string> matchedPathTokens) =>
		Interactive.GetDeepestContextScopePath(matchedPathTokens);

	private static ValueTask<AmbientCommandOutcome> HandleUpAmbientCommandAsync(
		List<string> scopeTokens,
		bool isInteractiveSession) =>
		InteractiveSession.HandleUpAmbientCommandAsync(scopeTokens, isInteractiveSession);

	private ValueTask<AmbientCommandOutcome> HandleExitAmbientCommandAsync() =>
		Interactive.HandleExitAmbientCommandAsync();

	private ValueTask<bool> HandleCompletionAmbientCommandAsync(
		IReadOnlyList<string> commandTokens,
		IReadOnlyList<string> scopeTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken) =>
		Interactive.HandleCompletionAmbientCommandAsync(commandTokens, scopeTokens, serviceProvider, cancellationToken);
}
