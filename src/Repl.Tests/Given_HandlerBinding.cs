using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Repl.Tests;

[TestClass]
public sealed class Given_HandlerBinding
{
	[TestMethod]
	[Description("Regression guard: verifies binding route and positional parameters so that handler receives converted values.")]
	public void When_BindingRouteAndPositionalParameters_Then_HandlerReceivesConvertedValues()
	{
		var sut = ReplApp.Create();
		var capturedId = 0;
		var capturedName = string.Empty;

		sut.Map("contact {id:int} rename", (int id, string name) =>
		{
			capturedId = id;
			capturedName = name;
			return "ok";
		});

		var exitCode = sut.Run(["contact", "42", "rename", "alice"]);

		exitCode.Should().Be(0);
		capturedId.Should().Be(42);
		capturedName.Should().Be("alice");
	}

	[TestMethod]
	[Description("Regression guard: verifies binding named option so that option value is mapped by parameter name.")]
	public void When_BindingNamedOption_Then_OptionValueIsMappedByParameterName()
	{
		var sut = ReplApp.Create();
		var capturedLimit = 0;

		sut.Map("contact list", (int limit) =>
		{
			capturedLimit = limit;
			return "ok";
		});

		var exitCode = sut.Run(["contact", "list", "--limit", "5"]);

		exitCode.Should().Be(0);
		capturedLimit.Should().Be(5);
	}

	[TestMethod]
	[Description("Regression guard: verifies binding repeated named option so that list parameter contains all values.")]
	public void When_BindingRepeatedNamedOption_Then_ListParameterContainsAllValues()
	{
		var sut = ReplApp.Create();
		List<string>? captured = null;

		sut.Map("contact tag", (List<string> tag) =>
		{
			captured = tag;
			return "ok";
		});

		var exitCode = sut.Run(["contact", "tag", "--tag", "vip", "--tag", "priority"]);

		exitCode.Should().Be(0);
		captured.Should().NotBeNull();
		captured!.Should().ContainInOrder("vip", "priority");
	}

	[TestMethod]
	[Description("Regression guard: verifies binding variadic positional array so that all remaining tokens are collected.")]
	public void When_BindingVariadicPositionalArray_Then_AllRemainingTokensAreCollected()
	{
		var sut = ReplApp.Create();
		int[]? captured = null;

		sut.Map("delete", (int[] ids) =>
		{
			captured = ids;
			return "ok";
		});

		var exitCode = sut.Run(["delete", "1", "2", "3"]);

		exitCode.Should().Be(0);
		captured.Should().NotBeNull();
		captured!.Should().ContainInOrder(1, 2, 3);
	}

	[TestMethod]
	[Description("Regression guard: verifies binding service parameter so that dependency is resolved from container.")]
	public void When_BindingServiceParameter_Then_DependencyIsResolvedFromContainer()
	{
		var sut = ReplApp.Create(services =>
		{
			services.AddSingleton<ITestCounter>(new TestCounter(7));
		});
		var captured = 0;

		sut.Map("counter", (ITestCounter counter) =>
		{
			captured = counter.Value;
			return "ok";
		});

		var exitCode = sut.Run(["counter"]);

		exitCode.Should().Be(0);
		captured.Should().Be(7);
	}

	[TestMethod]
	[Description("Regression guard: verifies binding cancellation token so that handler receives execution token.")]
	public async Task When_BindingCancellationToken_Then_HandlerReceivesExecutionToken()
	{
		var sut = ReplApp.Create();
		CancellationToken captured = default;
		using var cancellationTokenSource = new CancellationTokenSource();

		sut.Map("work", async (CancellationToken ct) =>
		{
			captured = ct;
			await Task.Yield();
			return "ok";
		});

		var exitCode = await sut.RunAsync(["work"], cancellationTokenSource.Token).ConfigureAwait(false);

		exitCode.Should().Be(0);
		captured.CanBeCanceled.Should().BeTrue();
		captured.Should().Be(cancellationTokenSource.Token);
	}

	[TestMethod]
	[Description("Regression guard: verifies handler returns task of result so that exit code reflects resolved result.")]
	public void When_HandlerReturnsTaskOfResult_Then_ExitCodeReflectsResolvedResult()
	{
		var sut = ReplApp.Create();
		sut.Map("async-task", static async () =>
		{
			await Task.Yield();
			return Results.NotFound("missing");
		});

		var exitCode = sut.Run(["async-task", "--no-logo"]);

		exitCode.Should().Be(1);
	}

	[TestMethod]
	[Description("Regression guard: verifies handler returns value task of result so that exit code reflects resolved result.")]
	public void When_HandlerReturnsValueTaskOfResult_Then_ExitCodeReflectsResolvedResult()
	{
		var sut = ReplApp.Create();
		sut.Map("async-valuetask", static () =>
			ValueTask.FromResult<IReplResult>(Results.NotFound("missing")));

		var exitCode = sut.Run(["async-valuetask", "--no-logo"]);

		exitCode.Should().Be(1);
	}

	private interface ITestCounter
	{
		int Value { get; }
	}

	private sealed class TestCounter(int value) : ITestCounter
	{
		public int Value { get; } = value;
	}
}






