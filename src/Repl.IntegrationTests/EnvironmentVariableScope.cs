namespace Repl.IntegrationTests;

/// <summary>
/// Saves, overrides, and restores process environment variables around a test.
/// Test classes using this scope must be marked [DoNotParallelize] because
/// environment variables are process-global. (Local copy of the Repl.Tests helper —
/// the two test projects deliberately share no code.)
/// </summary>
internal sealed class EnvironmentVariableScope : IDisposable
{
	private readonly (string Name, string? PreviousValue)[] _previousValues;

	public EnvironmentVariableScope(params (string Name, string? Value)[] variables)
	{
		_previousValues = variables
			.Select(static variable => (variable.Name, Environment.GetEnvironmentVariable(variable.Name)))
			.ToArray();

		foreach (var (name, value) in variables)
		{
			Environment.SetEnvironmentVariable(name, value);
		}
	}

	public void Dispose()
	{
		foreach (var (name, previousValue) in _previousValues)
		{
			Environment.SetEnvironmentVariable(name, previousValue);
		}
	}
}
