using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Repl.Tests;

[TestClass]
public sealed class Given_InvocationHooks
{
	[TestMethod]
	[Description("Repair must refresh typed values without replaying earlier hooks or losing the original submission.")]
	public async Task When_InvalidInputIsReplaced_Then_NextHookSeesFreshParsedValues()
	{
		var events = new List<string>();
		var app = CoreReplApp.Create();
		app.Map("run", (int count) => { events.Add($"handler:{count}"); });
		app.UseInvocationHook((ReplInvocationContext context) =>
		{
			context.Parsed.Values["count"].State.Should().Be(InputValueState.Invalid);
			events.Add("first");
			return ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("run", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["count"] = 7 }));
		});
		app.UseInvocationHook((ReplInvocationContext context, int count) =>
		{
			context.OriginalRequest.Arguments.Should().Equal("run", "--count=bad");
			context.Parsed.TryGetValue<int>("count", out var parsed).Should().BeTrue();
			parsed.Should().Be(count);
			events.Add($"second:{count}");
			return ReplInvocationResult.Continue();
		});
		(await app.RunAsync(["run", "--count=bad"])).Should().Be(0);
		events.Should().Equal("first", "second:7", "handler:7");
	}

	[TestMethod]
	[Description("Native collections preserve literal option-like strings and explicit emptiness without CLI token interpretation.")]
	public async Task When_StructuredValuesContainEmptyAndOptionLikeStrings_Then_BindingPreservesThem()
	{
		var app = CoreReplApp.Create();
		string[]? received = null;
		app.Map("run", (string[] items) => { received = items; });
		var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["items"] = new[] { "", "--help", "a b", "x=y" } };
		(await app.RunAsync(ReplInvocationRequest.ForCommand("run", values))).Should().Be(0);
		received.Should().Equal("", "--help", "a b", "x=y");
		values["items"] = Array.Empty<string>();
		(await app.RunAsync(ReplInvocationRequest.ForCommand("run", values))).Should().Be(0);
		received.Should().BeEmpty();
	}

	[TestMethod]
	[Description("Native decimals must retain their value when the active text parsing culture uses a different separator.")]
	public async Task When_NativeNumbersUseDifferentCulture_Then_NoTextRoundTripOccurs()
	{
		using var culture = new CultureScope(CultureInfo.GetCultureInfo("fr-CA"));
		var app = CoreReplApp.Create().Options(options => options.Parsing.NumericCulture = NumericParsingCulture.Current);
		decimal received = 0;
		app.Map("run", (decimal number) => { received = number; });
		(await app.RunAsync(ReplInvocationRequest.ForCommand("run", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["number"] = 1.25m }))).Should().Be(0);
		received.Should().Be(1.25m);
	}

	[TestMethod]
	[Description("External actions can terminate invocation processing before later hooks or the command produce side effects.")]
	public async Task When_HookHandlesRequest_Then_HandlerAndFollowingHooksDoNotRun()
	{
		var app = CoreReplApp.Create();
		app.Map("run", () => Assert.Fail("Handler must not run."));
		app.UseInvocationHook(() => ReplInvocationResult.Handled());
		app.UseInvocationHook(() => { Assert.Fail("Following hook must not run."); return ReplInvocationResult.Continue(); });
		(await app.RunAsync(["run"])).Should().Be(0);
	}

	[TestMethod]
	[Description("Async hooks share invocation services and caller cancellation with ordinary command execution.")]
	public async Task When_HookUsesServices_Then_ItReceivesTheRunScopeAndCancellation()
	{
		var service = new Marker();
		var app = ReplApp.Create(services => services.AddSingleton(service));
		var called = false;
		app.Map("run", () => { });
		app.UseInvocationHook(async ValueTask<ReplInvocationResult> (ReplInvocationContext context, Marker marker, CancellationToken token) =>
		{
			await Task.Yield();
			marker.Should().BeSameAs(service);
			context.Channel.Should().Be(ReplInvocationChannel.Programmatic);
			token.CanBeCanceled.Should().BeTrue();
			called = true;
			return ReplInvocationResult.Continue();
		});
		using var cts = new CancellationTokenSource();
		(await app.RunAsync(ReplInvocationRequest.ForCommand("run"), cancellationToken: cts.Token)).Should().Be(0);
		called.Should().BeTrue();
	}

	[TestMethod]
	[Description("A rewrite cannot bypass the final command schema by introducing undeclared values.")]
	public async Task When_ReplacementHasUnknownValues_Then_ItIsRejected()
	{
		var app = CoreReplApp.Create();
		app.Map("run", () => Assert.Fail("Invalid final request must not execute."));
		app.UseInvocationHook(() => ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("run", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["unknown"] = true })));
		(await app.RunAsync(["run"])).Should().NotBe(0);
	}

	[TestMethod]
	[Description("Request copies isolate mutable native collections so repeated submissions remain independent.")]
	public async Task When_RequestIsReused_Then_HookMutationsDoNotChangeItsValues()
	{
		var app = CoreReplApp.Create();
		var received = new List<string>();
		app.Map("run", (string[] items) => { received.Add(items[0]); });
		app.UseInvocationHook((ReplInvocationContext context) =>
		{
			((string[])context.Request.Values["items"]!)[0] = "changed";
			return ReplInvocationResult.Continue();
		});
		string[] items = ["original"];
		var request = ReplInvocationRequest.ForCommand("run", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["items"] = items });
		(await app.RunAsync(request)).Should().Be(0);
		(await app.RunAsync(request)).Should().Be(0);
		received.Should().Equal("original", "original");
	}

	private sealed class Marker;

	private sealed class CultureScope : IDisposable
	{
		private readonly CultureInfo _previous = CultureInfo.CurrentCulture;
		internal CultureScope(CultureInfo culture) => CultureInfo.CurrentCulture = culture;
		public void Dispose() => CultureInfo.CurrentCulture = _previous;
	}
}
