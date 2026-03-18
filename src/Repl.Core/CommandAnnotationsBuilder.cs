namespace Repl;

/// <summary>
/// Fluent builder for <see cref="CommandAnnotations"/>.
/// Use as an escape hatch via <see cref="CommandBuilder.WithAnnotations"/> when
/// multiple flags need to be set in a single expression.
/// </summary>
public sealed class CommandAnnotationsBuilder
{
	private bool _destructive;
	private bool _readOnly;
	private bool _idempotent;
	private bool _openWorld;
	private bool _longRunning;
	private bool _automationHidden;

	/// <summary>Marks the command as destructive (deletes, modifies state).</summary>
	public CommandAnnotationsBuilder Destructive(bool value = true)
	{
		_destructive = value;
		return this;
	}

	/// <summary>Marks the command as read-only (no side effects).</summary>
	public CommandAnnotationsBuilder ReadOnly(bool value = true)
	{
		_readOnly = value;
		return this;
	}

	/// <summary>Marks the command as safely retriable.</summary>
	public CommandAnnotationsBuilder Idempotent(bool value = true)
	{
		_idempotent = value;
		return this;
	}

	/// <summary>Marks the command as interacting with external systems.</summary>
	public CommandAnnotationsBuilder OpenWorld(bool value = true)
	{
		_openWorld = value;
		return this;
	}

	/// <summary>Marks the command as long-running (enables task-based execution).</summary>
	public CommandAnnotationsBuilder LongRunning(bool value = true)
	{
		_longRunning = value;
		return this;
	}

	/// <summary>Hides the command from programmatic/automation surfaces only.</summary>
	public CommandAnnotationsBuilder AutomationHidden(bool value = true)
	{
		_automationHidden = value;
		return this;
	}

	/// <summary>
	/// Builds the immutable <see cref="CommandAnnotations"/> instance.
	/// </summary>
	internal CommandAnnotations Build() => new()
	{
		Destructive = _destructive,
		ReadOnly = _readOnly,
		Idempotent = _idempotent,
		OpenWorld = _openWorld,
		LongRunning = _longRunning,
		AutomationHidden = _automationHidden,
	};
}
