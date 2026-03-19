namespace Repl.Tests;

[TestClass]
public sealed class Given_CommandAnnotations
{
	[TestMethod]
	[Description("Verifies annotation record defaults to all-false.")]
	public void When_DefaultConstructed_Then_AllFlagsAreFalse()
	{
		var annotations = new CommandAnnotations();

		annotations.Destructive.Should().BeFalse();
		annotations.ReadOnly.Should().BeFalse();
		annotations.Idempotent.Should().BeFalse();
		annotations.OpenWorld.Should().BeFalse();
		annotations.LongRunning.Should().BeFalse();
		annotations.AutomationHidden.Should().BeFalse();
	}

	[TestMethod]
	[Description("Verifies init setters work for individual flags.")]
	public void When_CreatedWithInitSetters_Then_FlagsAreSet()
	{
		var annotations = new CommandAnnotations
		{
			Destructive = true,
			ReadOnly = true,
		};

		annotations.Destructive.Should().BeTrue();
		annotations.ReadOnly.Should().BeTrue();
		annotations.Idempotent.Should().BeFalse();
	}

	[TestMethod]
	[Description("Verifies with-expression preserves existing flags when setting new ones.")]
	public void When_UsingWithExpression_Then_ExistingFlagsArePreserved()
	{
		var original = new CommandAnnotations { Destructive = true };
		var modified = original with { OpenWorld = true };

		modified.Destructive.Should().BeTrue();
		modified.OpenWorld.Should().BeTrue();
	}

	[TestMethod]
	[Description("Verifies builder produces matching annotations.")]
	public void When_UsingBuilder_Then_AnnotationsMatchFlags()
	{
		var builder = new CommandAnnotationsBuilder();
		builder.Destructive().LongRunning().OpenWorld();

		var annotations = builder.Build();

		annotations.Destructive.Should().BeTrue();
		annotations.LongRunning.Should().BeTrue();
		annotations.OpenWorld.Should().BeTrue();
		annotations.ReadOnly.Should().BeFalse();
		annotations.Idempotent.Should().BeFalse();
		annotations.AutomationHidden.Should().BeFalse();
	}

	[TestMethod]
	[Description("Verifies builder supports toggling flags off.")]
	public void When_BuilderTogglesOff_Then_FlagIsCleared()
	{
		var builder = new CommandAnnotationsBuilder();
		builder.Destructive().Destructive(value: false);

		var annotations = builder.Build();

		annotations.Destructive.Should().BeFalse();
	}
}
