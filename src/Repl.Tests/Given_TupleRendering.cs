namespace Repl.Tests;

[TestClass]
public sealed class Given_TupleRendering
{
	[TestMethod]
	[Description("Verifies tuple elements are rendered as separate output lines.")]
	public void When_HandlerReturnsTuple_Then_EachElementIsRenderedSeparately()
	{
		var sut = ReplApp.Create();
		sut.Map("test", () => ("first line", "second line"));

		using var output = new StringWriter();
		using var scope = ReplSessionIO.SetSession(output, TextReader.Null);

		var exitCode = sut.Run(["test"]);

		exitCode.Should().Be(0);
		var text = output.ToString();
		text.Should().Contain("first line");
		text.Should().Contain("second line");
	}

	[TestMethod]
	[Description("Verifies three-element tuple renders all elements.")]
	public void When_HandlerReturnsThreeElementTuple_Then_AllElementsAreRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("test", () => ("one", 2, "three"));

		using var output = new StringWriter();
		using var scope = ReplSessionIO.SetSession(output, TextReader.Null);

		var exitCode = sut.Run(["test"]);

		exitCode.Should().Be(0);
		var text = output.ToString();
		text.Should().Contain("one");
		text.Should().Contain("2");
		text.Should().Contain("three");
	}

	[TestMethod]
	[Description("Verifies tuple with IReplResult element renders the result message.")]
	public void When_TupleContainsReplResult_Then_ResultMessageIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("test", () => ("status", Results.Success("done")));

		using var output = new StringWriter();
		using var scope = ReplSessionIO.SetSession(output, TextReader.Null);

		var exitCode = sut.Run(["test"]);

		exitCode.Should().Be(0);
		var text = output.ToString();
		text.Should().Contain("status");
		text.Should().Contain("done");
	}

	[TestMethod]
	[Description("Verifies exit code is determined by the last tuple element.")]
	public void When_LastTupleElementIsError_Then_ExitCodeIsNonZero()
	{
		var sut = ReplApp.Create();
		sut.Map("test", () => ("info", Results.Error("fail", "something broke")));

		using var output = new StringWriter();
		using var scope = ReplSessionIO.SetSession(output, TextReader.Null);

		var exitCode = sut.Run(["test"]);

		exitCode.Should().Be(1);
	}

	[TestMethod]
	[Description("Verifies async tuple handler renders all elements.")]
	public void When_HandlerReturnsAsyncTuple_Then_AllElementsAreRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("test", async () =>
		{
			await Task.Yield();
			return ("async-one", "async-two");
		});

		using var output = new StringWriter();
		using var scope = ReplSessionIO.SetSession(output, TextReader.Null);

		var exitCode = sut.Run(["test"]);

		exitCode.Should().Be(0);
		var text = output.ToString();
		text.Should().Contain("async-one");
		text.Should().Contain("async-two");
	}
}
