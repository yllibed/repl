using AwesomeAssertions;

namespace Repl.Tests;

[TestClass]
public sealed class Given_MiddlewarePipeline
{
	[TestMethod]
	[Description("Regression guard: verifies middleware wraps handler so that order is deterministic.")]
	public void When_MiddlewareWrapsHandler_Then_OrderIsDeterministic()
	{
		var sut = ReplApp.Create();
		var calls = new List<string>();

		sut.Use(async (_, next) =>
		{
			calls.Add("m1-before");
			await next().ConfigureAwait(false);
			calls.Add("m1-after");
		});
		sut.Use(async (_, next) =>
		{
			calls.Add("m2-before");
			await next().ConfigureAwait(false);
			calls.Add("m2-after");
		});
		sut.Map("hello", () =>
		{
			calls.Add("handler");
			return "ok";
		});

		var exitCode = sut.Run(["hello"]);

		exitCode.Should().Be(0);
		calls.Should().ContainInOrder(
			"m1-before",
			"m2-before",
			"handler",
			"m2-after",
			"m1-after");
	}

	[TestMethod]
	[Description("Regression guard: verifies middleware short circuits so that handler is not executed.")]
	public void When_MiddlewareShortCircuits_Then_HandlerIsNotExecuted()
	{
		var sut = ReplApp.Create();
		var handlerCalled = false;

		sut.Use((_, _) => ValueTask.CompletedTask);
		sut.Map("hello", () =>
		{
			handlerCalled = true;
			return "ok";
		});

		var exitCode = sut.Run(["hello"]);

		exitCode.Should().Be(0);
		handlerCalled.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies handler throws so that exit code is non zero.")]
	public void When_HandlerThrows_Then_ExitCodeIsNonZero()
	{
		var sut = ReplApp.Create();
		sut.Map("boom", Boom);

		var exitCode = sut.Run(["boom"]);

		exitCode.Should().Be(1);

		static string Boom() => throw new InvalidOperationException("boom");
	}
}






