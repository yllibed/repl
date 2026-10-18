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

	[TestMethod]
	[Description("Native CLR and JSON null values must reach nullable scalar and collection parameters identically.")]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_NativeValuesAreNull_Then_TheirRepresentationDoesNotChangeBinding(bool json)
	{
		var app = CoreReplApp.Create();
		var called = false;
		app.Map("run", (string? item, int[]? items, int? count) =>
		{
			item.Should().BeNull();
			items.Should().BeNull();
			count.Should().BeNull();
			called = true;
		});
		object? value = json ? System.Text.Json.JsonSerializer.SerializeToElement<object?>(value: null) : null;
		var request = ReplInvocationRequest.ForCommand("run", new Dictionary<string, object?>(StringComparer.Ordinal)
			{ ["item"] = value, ["items"] = value, ["count"] = value });

		(await app.RunAsync(request)).Should().Be(0);

		called.Should().BeTrue();
	}

	[TestMethod]
	[Description("Typed globals with malformed user values must be refused before factories run, while a context hook may repair them.")]
	[DataRow(false, false)]
	[DataRow(true, false)]
	[DataRow(false, true)]
	[DataRow(true, true)]
	public async Task When_TypedGlobalsAreInvalid_Then_HooksCanRepairOrReceiveAUsageDiagnostic(bool native, bool repair)
	{
		var app = ReplApp.Create().UseGlobalOptions<NumericGlobals>();
		var calls = new List<int>();
		app.Map("run", () => { calls.Add(1); });
		app.UseInvocationHook((ReplInvocationContext context) =>
		{
			context.Parsed.GlobalValues["limit"].State.Should().Be(InputValueState.Invalid);
			return repair ? ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("run", globalValues:
				new Dictionary<string, object?>(StringComparer.Ordinal) { ["limit"] = 7 })) : ReplInvocationResult.Continue();
		});
		app.UseInvocationHook((NumericGlobals globals) => { calls.Add(globals.Limit); return ReplInvocationResult.Continue(); });
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);

		var code = await (native ? app.RunAsync(ReplInvocationRequest.ForCommand("run", globalValues:
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["limit"] = "bad" })) : app.RunAsync(["run", "--limit=bad"]));

		code.Should().Be(repair ? 0 : 2);
		if (repair) { calls.Should().Equal(7, 1); }
		else { calls.Should().BeEmpty(); writer.ToString().Should().Contain("Cannot bind invocation hook input 'limit'").And.NotContain("input_callback_error"); }
	}

	[TestMethod]
	[Description("A caller-owned provider without availability metadata must resolve an implicit scalar once for hooks and the handler.")]
	public async Task When_ExternalServicesLackAvailabilityMetadata_Then_TheResolvedFallbackIsReused()
	{
		var app = ReplApp.Create();
		var services = new ScalarProvider();
		var received = new List<string>();
		app.Map("run", (string prefix, int count) => { received.Add($"{prefix}:{count}"); });
		app.UseInvocationHook((string prefix, int count) => { received.Add($"{prefix}:{count}"); return ReplInvocationResult.Continue(); });

		(await app.RunAsync(["run", "5"], services)).Should().Be(0);

		received.Should().Equal("service:5", "service:5");
		services.Resolutions.Should().Be(1);
	}

	[TestMethod]
	[Description("Context-only repair must run before a command fallback factory reads malformed global options.")]
	public async Task When_AContextHookRepairsGlobals_Then_ServiceFallbackActivationWaitsForTypedInput()
	{
		var app = ReplApp.Create(services => services.AddTransient<string>(provider =>
			provider.GetRequiredService<NumericGlobals>().Limit.ToString(CultureInfo.InvariantCulture))).UseGlobalOptions<NumericGlobals>();
		var received = new List<string>();
		app.Map("run", (string prefix) => { received.Add(prefix); });
		app.UseInvocationHook((ReplInvocationContext _) => ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("run", globalValues:
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["limit"] = 7 })));
		app.UseInvocationHook((string prefix) => { received.Add(prefix); return ReplInvocationResult.Continue(); });

		(await app.RunAsync(["run", "--limit=bad"])).Should().Be(0);

		received.Should().Equal("7", "7");
	}

	[TestMethod]
	[Description("Invalid globals must refuse typed-global hooks before any dependent command fallback factory is activated.")]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_InvalidGlobalsHaveDependentFactories_Then_TypedHooksReceiveAUsageDiagnostic(bool native)
	{
		var factoryCalls = 0;
		var app = ReplApp.Create(services => services.AddTransient<string>(provider =>
		{
			factoryCalls++;
			return provider.GetRequiredService<NumericGlobals>().Limit.ToString(CultureInfo.InvariantCulture);
		})).UseGlobalOptions<NumericGlobals>();
		app.Map("run", (string prefix) => Assert.Fail("Invalid input must not execute."));
		app.UseInvocationHook((NumericGlobals globals) => { Assert.Fail("Invalid typed globals must not reach the hook."); return ReplInvocationResult.Continue(); });
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);

		var code = await (native ? app.RunAsync(ReplInvocationRequest.ForCommand("run", globalValues:
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["limit"] = "bad" })) : app.RunAsync(["run", "--limit=bad"]));

		code.Should().Be(2);
		factoryCalls.Should().Be(0);
		writer.ToString().Should().Contain("Cannot bind invocation hook input 'limit'").And.NotContain("input_callback_error");
	}

	private sealed class NumericGlobals { public int Limit { get; set; } = 3; }
	private sealed class ScalarProvider : IServiceProvider
	{
		public int Resolutions { get; private set; }
		public object? GetService(Type serviceType)
		{
			if (serviceType != typeof(string)) { return null; }
			Resolutions++;
			return "service";
		}
	}

	private sealed class Marker;

	private sealed class CultureScope : IDisposable
	{
		private readonly CultureInfo _previous = CultureInfo.CurrentCulture;
		internal CultureScope(CultureInfo culture) => CultureInfo.CurrentCulture = culture;
		public void Dispose() => CultureInfo.CurrentCulture = _previous;
	}
}
