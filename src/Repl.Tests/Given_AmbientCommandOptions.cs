namespace Repl.Tests;

[TestClass]
public sealed class Given_AmbientCommandOptions
{
	[TestMethod]
	[Description("MapAmbient registers a custom command definition.")]
	public void When_MapAmbientIsCalled_Then_CommandIsRegistered()
	{
		var options = new AmbientCommandOptions();
		Delegate handler = () => { };
		options.MapAmbient("clear", handler, "Clear the screen");

		options.CustomCommands.Should().ContainKey("clear");
		options.CustomCommands["clear"].Name.Should().Be("clear");
		options.CustomCommands["clear"].Description.Should().Be("Clear the screen");
		options.CustomCommands["clear"].Handler.Should().BeSameAs(handler);
	}

	[TestMethod]
	[Description("MapAmbient returns the same instance for fluent chaining.")]
	public void When_MapAmbientIsCalled_Then_ReturnsSameInstance()
	{
		var options = new AmbientCommandOptions();
		var result = options.MapAmbient("test", () => { });

		result.Should().BeSameAs(options);
	}

	[TestMethod]
	[Description("MapAmbient is case-insensitive for command names.")]
	public void When_MapAmbientWithDifferentCase_Then_OverridesPrevious()
	{
		var options = new AmbientCommandOptions();
		options.MapAmbient("clear", () => { }, "first");
		options.MapAmbient("CLEAR", () => { }, "second");

		options.CustomCommands.Should().HaveCount(1);
		options.CustomCommands["clear"].Description.Should().Be("second");
	}

	[TestMethod]
	[Description("MapAmbient throws on null or empty name.")]
	public void When_MapAmbientWithNullName_Then_Throws()
	{
		var options = new AmbientCommandOptions();
		var act = () => options.MapAmbient(null!, () => { });
		act.Should().Throw<ArgumentException>();
	}

	[TestMethod]
	[Description("MapAmbient throws on null handler.")]
	public void When_MapAmbientWithNullHandler_Then_Throws()
	{
		var options = new AmbientCommandOptions();
		var act = () => options.MapAmbient("test", null!);
		act.Should().Throw<ArgumentNullException>();
	}
}
