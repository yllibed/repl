#if NET11_0_OR_GREATER
namespace Repl.Tests;

[TestClass]
public sealed class Given_UnionResults
{
	[TestMethod]
	[Description("A handler returning a union gives the command pipeline the active case, not the union wrapper.")]
	public async Task When_HandlerReturnsAUnion_Then_TheActiveCaseIsReturned()
	{
		var result = await CommandInvoker.InvokeAsync(
			(Func<UnionLookup>)(() => new UnionContact("Ada")),
			[]);

		result.Should().Be(new UnionContact("Ada"));
	}

	[TestMethod]
	[Description("The active case of a union is read after the awaited Task<T> completes.")]
	public async Task When_HandlerReturnsATaskOfAUnion_Then_TheActiveCaseIsReturned()
	{
		var result = await CommandInvoker.InvokeAsync(
			(Func<Task<UnionLookup>>)(async () =>
			{
				await Task.Yield();
				return new UnionMissing("7");
			}),
			[]);

		result.Should().Be(new UnionMissing("7"));
	}

	[TestMethod]
	[Description("The active case of a union is read after the awaited ValueTask<T> completes.")]
	public async Task When_HandlerReturnsAValueTaskOfAUnion_Then_TheActiveCaseIsReturned()
	{
		var result = await CommandInvoker.InvokeAsync(
			(Func<ValueTask<UnionLookup>>)(async () =>
			{
				await Task.Yield();
				return new UnionContact("Grace");
			}),
			[]);

		result.Should().Be(new UnionContact("Grace"));
	}

	[TestMethod]
	[Description("A value-type case comes out as its boxed value, so the output pipeline sees an int rather than the union struct.")]
	public async Task When_AUnionHoldsAValueTypeCase_Then_TheBoxedValueIsReturned()
	{
		var result = await CommandInvoker.InvokeAsync(
			(Func<UnionCount>)(() => 42),
			[]);

		result.Should().Be(42);
	}

	[TestMethod]
	[Description("A union with no active case behaves like a handler that returned null.")]
	public async Task When_AUnionHasNoActiveCase_Then_ResultIsNull()
	{
		var result = await CommandInvoker.InvokeAsync(
			(Func<UnionLookup>)(() => default),
			[]);

		result.Should().BeNull();
	}

	[TestMethod]
	[Description("A union holding another union is unwrapped down to the case that carries the value.")]
	public async Task When_AUnionHoldsAnotherUnion_Then_TheInnerCaseIsReturned()
	{
		var result = await CommandInvoker.InvokeAsync(
			(Func<UnionNested>)(() => (UnionLookup)new UnionContact("Ada")),
			[]);

		result.Should().Be(new UnionContact("Ada"));
	}

	[TestMethod]
	[Description("A case that carries an exit code sets the process exit code, as it does when returned directly.")]
	public void When_TheActiveCaseIsAnExitResult_Then_ItsExitCodeIsTheProcessExitCode()
	{
		var app = CreateApp();
		app.Map("run", () => (UnionOutcome)new UnionExit(7, "done"));

		var (exitCode, output) = RunCapturing(app, ["run"]);

		exitCode.Should().Be(7);
		output.Should().Contain("done");
	}

	[TestMethod]
	[Description("A case that is an error result is classified and rendered as one, not as an object named after the union.")]
	public void When_TheActiveCaseIsAnErrorResult_Then_TheRunFailsWithItsMessage()
	{
		var app = CreateApp();
		app.Map("run", () => (UnionOutcome)new UnionRefusal("not allowed"));

		var (exitCode, output) = RunCapturing(app, ["run"]);

		exitCode.Should().NotBe(0);
		output.Should().Contain("not allowed");
	}

	[TestMethod]
	[Description("An ordinary payload case renders exactly as the same payload returned directly, in a structured format.")]
	public void When_TheActiveCaseIsAPayload_Then_ItRendersAsTheBarePayload()
	{
		var viaUnion = CreateApp();
		viaUnion.Map("run", () => (UnionLookup)new UnionContact("Ada"));
		var direct = CreateApp();
		direct.Map("run", () => new UnionContact("Ada"));

		var wrapped = RunCapturing(viaUnion, ["run", "--output:json"]);
		var bare = RunCapturing(direct, ["run", "--output:json"]);

		wrapped.ExitCode.Should().Be(0);
		wrapped.Output.Should().Be(bare.Output);
		wrapped.Output.Should().Contain("Ada");
	}

	[TestMethod]
	[Description("An ordinary payload case also renders as the bare payload in human output, not as an object named after the union.")]
	public void When_TheActiveCaseIsAPayload_Then_HumanOutputIsTheBarePayloadToo()
	{
		var viaUnion = CreateApp();
		viaUnion.Map("run", () => (UnionLookup)new UnionContact("Ada"));
		var direct = CreateApp();
		direct.Map("run", () => new UnionContact("Ada"));

		var wrapped = RunCapturing(viaUnion, ["run"]);
		var bare = RunCapturing(direct, ["run"]);

		wrapped.ExitCode.Should().Be(0);
		wrapped.Output.Should().Be(bare.Output);
		wrapped.Output.Should().Contain("Ada");
	}

	[TestMethod]
	[Description("A union returned as an element of a tuple is unwrapped like a top-level one, so the last element still decides the exit code.")]
	public void When_ALastTupleElementIsAUnion_Then_ItsActiveCaseDecidesTheExitCode()
	{
		var app = CreateApp();
		app.Map("run", () => ("head", (UnionOutcome)new UnionExit(7, "done")));

		var (exitCode, output) = RunCapturing(app, ["run"]);

		exitCode.Should().Be(7);
		output.Should().Contain("head").And.Contain("done");
	}

	[TestMethod]
	[Description("A union is not a bindable input type: a handler parameter of a union type is refused as a usage error rather than crashing the run.")]
	public void When_AHandlerParameterIsAUnion_Then_TheRunReportsAUsageError()
	{
		var app = CreateApp();
		app.Map("run {value}", (UnionCount value) => "got");

		var (exitCode, output) = RunCapturing(app, ["run", "5"]);

		exitCode.Should().Be(2);
		output.Should().StartWith("Error:");
	}

	private sealed record UnionContact(string Name);

	private sealed record UnionMissing(string Id);

	private sealed record UnionExit(int ExitCode, object? Payload) : IExitResult;

	private sealed record UnionRefusal(string Message) : IReplResult
	{
		public string Kind => "error";

		public string? Code => "refused";

		public object? Details => null;
	}

	private union UnionLookup(UnionContact, UnionMissing);

	private union UnionCount(int, string);

	private union UnionOutcome(UnionContact, UnionExit, UnionRefusal);

	private union UnionNested(UnionLookup, int);

	private static ReplApp CreateApp()
	{
		var app = ReplApp.Create();
		app.Options(options =>
		{
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.BannerEnabled = false;
		});
		return app;
	}

	private static (int ExitCode, string Output) RunCapturing(ReplApp sut, string[] args)
	{
		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);
		var exitCode = sut.Run(args);
		return (exitCode, writer.ToString());
	}
}
#endif
