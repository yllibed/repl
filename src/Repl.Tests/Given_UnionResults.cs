#if NET11_0_OR_GREATER
using Microsoft.Extensions.DependencyInjection;

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
	[Description("A case that holds null behaves like a handler that returned null.")]
	public async Task When_TheActiveCaseHoldsNull_Then_ResultIsNull()
	{
		var result = await CommandInvoker.InvokeAsync(
			(Func<UnionLookup>)(() => (UnionContact)null!),
			[]);

		result.Should().BeNull();
	}

	[TestMethod]
	[Description("A case that is a Task<T> is awaited, as the same task returned directly would be.")]
	public async Task When_TheActiveCaseIsATask_Then_ItIsAwaited()
	{
		var result = await CommandInvoker.InvokeAsync(
			(Func<UnionPending>)(() => Task.FromResult(new UnionContact("Ada"))),
			[]);

		result.Should().Be(new UnionContact("Ada"));
	}

	[TestMethod]
	[Description("A case that is a ValueTask<T> is awaited, as the same value task returned directly would be.")]
	public async Task When_TheActiveCaseIsAValueTask_Then_ItIsAwaited()
	{
		var result = await CommandInvoker.InvokeAsync(
			(Func<UnionPending>)(() => new ValueTask<UnionMissing>(new UnionMissing("7"))),
			[]);

		result.Should().Be(new UnionMissing("7"));
	}

	[TestMethod]
	[Description("A Task case carrying an exit code sets the process exit code once awaited, as the task returned directly does.")]
	public void When_TheActiveCaseIsATaskOfAnExitResult_Then_ItsExitCodeIsTheProcessExitCode()
	{
		var app = CreateApp();
		app.Map("run", () => (UnionPending)Task.FromResult(new UnionExit(7, "done")));

		var (exitCode, output) = RunCapturing(app, ["run"]);

		exitCode.Should().Be(7);
		output.Should().Contain("done");
	}

	[TestMethod]
	[Description("A faulted Task case fails the run exactly as the same faulted task returned directly, instead of rendering the task as data.")]
	public void When_TheActiveCaseIsAFaultedTask_Then_TheRunFailsAsTheTaskReturnedDirectly()
	{
		var viaUnion = CreateApp();
		viaUnion.Map("run", () => (UnionPending)Task.FromException<UnionContact>(new InvalidOperationException("boom")));
		var direct = CreateApp();
		direct.Map("run", () => Task.FromException<UnionContact>(new InvalidOperationException("boom")));

		var wrapped = RunCapturing(viaUnion, ["run"]);
		var bare = RunCapturing(direct, ["run"]);

		bare.ExitCode.Should().NotBe(0);
		wrapped.Should().Be(bare);
	}

	[TestMethod]
	[Description("A custom IUnion whose Value returns itself cannot hang the run: unwrapping stops after a bounded number of steps.")]
	public async Task When_AUnionLeadsBackToItself_Then_UnwrappingStops()
	{
		// Every step completes synchronously, so the invocation runs on another thread for the timeout to fire.
		var result = await Task.Run(() => CommandInvoker.InvokeAsync((Func<SelfUnion>)(() => new SelfUnion()), []).AsTask())
			.WaitAsync(TimeSpan.FromSeconds(10));

		result.Should().BeOfType<SelfUnion>();
	}

	[TestMethod]
	[Description("A middleware reading the result after next() sees the active case of the handler's union, not the union.")]
	public void When_AMiddlewareReadsTheResult_Then_ItSeesTheActiveCase()
	{
		object? observed = null;
		var app = CreateApp();
		app.Use(async (context, next) =>
		{
			await next().ConfigureAwait(false);
			observed = context.Result;
		});
		app.Map("run", () => (UnionOutcome)new UnionExit(7, "done"));

		var (exitCode, _) = RunCapturing(app, ["run"]);

		exitCode.Should().Be(7);
		observed.Should().Be(new UnionExit(7, "done"));
	}

	[TestMethod]
	[Description("A union a middleware sets as the result is unwrapped too, so no union reaches the output.")]
	public void When_AMiddlewareSetsAUnionResult_Then_ItsActiveCaseIsProcessed()
	{
		var app = CreateApp();
		app.Use(async (context, next) =>
		{
			await next().ConfigureAwait(false);
			context.Result = (UnionOutcome)new UnionExit(7, "done");
		});
		app.Map("run", () => "replaced");

		var (exitCode, output) = RunCapturing(app, ["run"]);

		exitCode.Should().Be(7);
		output.Should().Contain("done");
	}

	[TestMethod]
	[Description("A tuple element that is a union whose case is a task is awaited as a top-level one is.")]
	public void When_ATupleElementIsAUnionOfATask_Then_ItIsAwaited()
	{
		var app = CreateApp();
		app.Map("run", () => ("head", (UnionPending)Task.FromResult(new UnionExit(7, "done"))));

		var (exitCode, output) = RunCapturing(app, ["run"]);

		exitCode.Should().Be(7);
		output.Should().Contain("head").And.Contain("done");
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
	[Description("A case that is an error result is classified and rendered as the same result returned directly, not as an object named after the union.")]
	public void When_TheActiveCaseIsAnErrorResult_Then_TheRunFailsAsTheResultReturnedDirectly()
	{
		var viaUnion = CreateApp();
		viaUnion.Map("run", () => (UnionOutcome)new UnionRefusal("not allowed"));
		var direct = CreateApp();
		direct.Map("run", () => new UnionRefusal("not allowed"));

		var wrapped = RunCapturing(viaUnion, ["run"]);
		var bare = RunCapturing(direct, ["run"]);

		bare.ExitCode.Should().NotBe(0);
		wrapped.Should().Be(bare);
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
	[DataRow("run {value}", "run", "5")]
	[DataRow("run", "run", "--value", "5")]
	[Description("A union gets no special conversion from command input: like any type Repl cannot convert, a value given for it is refused as a usage error rather than crashing the run.")]
	public void When_CommandInputIsGivenForAUnionParameter_Then_TheRunReportsAUsageError(string route, params string[] args)
	{
		var app = CreateApp();
		app.Map(route, (UnionCount value) => "got");

		var (exitCode, output) = RunCapturing(app, args);

		exitCode.Should().Be(2);
		output.Should().StartWith("Error:");
	}

	[TestMethod]
	[Description("A union parameter can come from the services, implicitly or with [FromServices], like any other type.")]
	public void When_AUnionParameterComesFromServices_Then_ItIsInjected()
	{
		var app = ReplApp.Create(services => services.AddSingleton(typeof(UnionCount), (UnionCount)42));
		app.Options(options =>
		{
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.BannerEnabled = false;
		});
		app.Map("run", (UnionCount value) => value);
		app.Map("explicit", ([FromServices] UnionCount value) => value);

		var implicitly = RunCapturing(app, ["run"]);
		var explicitly = RunCapturing(app, ["explicit"]);

		implicitly.ExitCode.Should().Be(0);
		implicitly.Output.Should().Contain("42");
		explicitly.Should().Be(implicitly);
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

	private union UnionPending(Task<UnionContact>, Task<UnionExit>, ValueTask<UnionMissing>);

	// A hand-written IUnion that never reaches a case, which a compiler-generated union cannot do.
	private sealed class SelfUnion : System.Runtime.CompilerServices.IUnion
	{
		public object? Value => this;
	}

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
