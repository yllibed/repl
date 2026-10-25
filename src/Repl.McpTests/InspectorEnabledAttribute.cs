namespace Repl.McpTests;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class InspectorEnabledAttribute : ConditionBaseAttribute
{
	internal const string EnvironmentVariable = "REPL_RUN_MCP_INSPECTOR_TESTS";

	public InspectorEnabledAttribute() : base(ConditionMode.Include)
	{
		IgnoreMessage = $"Set {EnvironmentVariable}=1 to run the MCP Inspector external-toolchain tests.";
	}

	public override string GroupName => nameof(InspectorEnabledAttribute);

	public override bool IsConditionMet
	{
		get
		{
			var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
			return string.Equals(value, "1", StringComparison.Ordinal)
				|| string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
		}
	}
}
