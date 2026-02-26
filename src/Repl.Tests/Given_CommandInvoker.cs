namespace Repl.Tests;

[TestClass]
public sealed class Given_CommandInvoker
{
	[TestMethod]
	[Description("Regression guard: verifies synchronous handler result is returned through command invoker.")]
	public async Task When_HandlerReturnsSyncValue_Then_ResultIsReturned()
	{
		var result = await CommandInvoker.InvokeAsync(
			(Func<int, int>)(value => value + 1),
			[41]);

		result.Should().Be(42);
	}

	[TestMethod]
	[Description("Regression guard: verifies synchronous void handler returns null after invocation.")]
	public async Task When_HandlerReturnsVoid_Then_ResultIsNull()
	{
		var invoked = false;
		var result = await CommandInvoker.InvokeAsync(
			(Action)(() => invoked = true),
			[]);

		invoked.Should().BeTrue();
		result.Should().BeNull();
	}

	[TestMethod]
	[Description("Regression guard: verifies Task-returning handler is awaited and produces a completion result object.")]
	public async Task When_HandlerReturnsTask_Then_TaskIsAwaitedAndCompletionResultIsReturned()
	{
		var invoked = false;
		var result = await CommandInvoker.InvokeAsync(
			(Func<Task>)(async () =>
			{
				await Task.Yield();
				invoked = true;
			}),
			[]);

		invoked.Should().BeTrue();
		result.Should().NotBeNull();
	}

	[TestMethod]
	[Description("Regression guard: verifies Task<T>-returning handler result is extracted after await.")]
	public async Task When_HandlerReturnsGenericTask_Then_ResultIsReturned()
	{
		var result = await CommandInvoker.InvokeAsync(
			(Func<Task<int>>)(async () =>
			{
				await Task.Yield();
				return 7;
			}),
			[]);

		result.Should().Be(7);
	}

	[TestMethod]
	[Description("Regression guard: verifies ValueTask-returning handler is awaited and returns null.")]
	public async Task When_HandlerReturnsValueTask_Then_ResultIsNull()
	{
		var invoked = false;
		var result = await CommandInvoker.InvokeAsync(
			(Func<ValueTask>)(async () =>
			{
				await Task.Yield();
				invoked = true;
			}),
			[]);

		invoked.Should().BeTrue();
		result.Should().BeNull();
	}

	[TestMethod]
	[Description("Regression guard: verifies ValueTask<T>-returning handler result is extracted after await.")]
	public async Task When_HandlerReturnsGenericValueTask_Then_ResultIsReturned()
	{
		var result = await CommandInvoker.InvokeAsync(
			(Func<ValueTask<int>>)(async () =>
			{
				await Task.Yield();
				return 9;
			}),
			[]);

		result.Should().Be(9);
	}

	[TestMethod]
	[Description("Regression guard: verifies instance handler invocation uses delegate target instance.")]
	public async Task When_HandlerIsInstanceMethod_Then_TargetInstanceIsUsed()
	{
		var handler = new InstanceHandler(offset: 5);
		var result = await CommandInvoker.InvokeAsync(
			(Func<int, int>)handler.AddOffset,
			[10]);

		result.Should().Be(15);
	}

	private sealed class InstanceHandler(int offset)
	{
		public int AddOffset(int value) => value + offset;
	}
}
