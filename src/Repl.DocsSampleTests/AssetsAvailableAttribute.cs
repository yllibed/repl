namespace Repl.DocsSampleTests;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class AssetsAvailableAttribute : ConditionBaseAttribute
{
	internal AssetsAvailableAttribute() : base(ConditionMode.Include)
	{
		IgnoreMessage = "Set DOCS_TEST_ASSETS to the verified sample assets directory to run native integration tests.";
	}
	public override string GroupName => nameof(AssetsAvailableAttribute);
	public override bool IsConditionMet => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCS_TEST_ASSETS"));
}
