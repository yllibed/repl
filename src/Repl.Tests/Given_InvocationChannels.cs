using Microsoft.Extensions.DependencyInjection;
using Repl.Parameters;

namespace Repl.Tests;

[TestClass]
public sealed class Given_InvocationChannels
{
	[TestMethod]
	[DataRow("plain", false)]
	[DataRow("help", false)]
	[DataRow("interactive", false)]
	[DataRow("inactive", false)]
	[DataRow("plain", true)]
	[DataRow("help", true)]
	[DataRow("interactive", true)]
	[Description("Structured context targets are refused as noncommands, including inactive routes; an earlier hook can still replace them with an exact active command.")]
	public async Task When_AStructuredTargetNamesAContext_Then_OnlyExplicitRepairCanDispatch(string mode, bool repair)
	{
		var app = CoreReplApp.Create();
		var ran = false;
		app.Context("tools", scope => scope.Map("run", () => { ran = true; }));
		if (mode is "inactive") { app.MapModule(new ContextNamedCommand(), _ => false); }
		if (repair) { app.UseInvocationHook(() => ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("tools run"))); }
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		var globals = new Dictionary<string, object?>(StringComparer.Ordinal) { ["no-interactive"] = true };
		if (mode is "help") { globals["help"] = true; }
		if (mode is "interactive") { globals.Remove("no-interactive"); globals["interactive"] = true; }

		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		(await app.RunAsync(ReplInvocationRequest.ForCommand("tools", globalValues: globals), cancellation.Token)).Should().Be(repair ? 0 : 2);

		ran.Should().Be(repair);
	}

	[TestMethod]
	[Description("Shell candidate routing retains native null globals from structured replacements and restores the surrounding input afterward.")]
	public async Task When_ShellHookReplacesNativeGlobals_Then_CandidatesUseTheNativePresenceDecision()
	{
		var app = CoreReplApp.Create().UseGlobalOptions<Globals>();
		app.MapModule(new NativeCompletionCommand(), _ => app.GlobalOptionsAccessor.GetValue<string>("tenant") is null);
		app.UseInvocationHook(() => ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("run", globalValues:
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = null })));
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		const string line = "app legacy ";

		var candidates = await new ShellCompletionEngine(app).ResolveShellCompletionCandidatesAsync(
			line, line.Length, ShellKind.Bash, app.CurrentServiceProvider, CancellationToken.None);

		candidates.Should().Contain("--count");
		app.GlobalOptionsAccessor.GetValue<string>("tenant").Should().Be("default");
	}

	private sealed class NativeCompletionCommand : IReplModule
	{
		public void Map(IReplMap map) => map.Map("run", (int count) => count);
	}
	private sealed class ContextNamedCommand : IReplModule
	{
		public void Map(IReplMap map) => map.Map("tools", () => Assert.Fail("Inactive commands must not run."));
	}

	[TestMethod]
	[Description("Legacy text adaptation must precede startup and parsed hooks, with validation and middleware applied once afterward.")]
	public async Task When_TextIsAdapted_Then_StartupAndInvocationCallbacksHaveSeparateOrdering()
	{
		var events = new List<string>();
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((_, _) =>
		{
			events.Add("adapter");
			return ValueTask.FromResult(ArgumentPreprocessingResult.Replace(["run", "--count=3"]));
		});
		app.UseStartupHook(() => { events.Add("startup"); return ArgumentPreprocessingResult.Continue(); });
		app.UseInvocationHook((ReplInvocationContext context, int count) =>
		{
			context.OriginalRequest.Arguments.Should().Equal("legacy");
			count.Should().Be(3);
			events.Add("hook");
			return ReplInvocationResult.Continue();
		});
		app.UseInputValidator((ParsedInputContext _) => { events.Add("validator"); return InputValidationResult.Valid(); });
		app.Use(async (_, next) => { events.Add("middleware"); await next().ConfigureAwait(false); });
		app.Map("run", (int count) => { events.Add("handler"); });
		(await app.RunAsync(["legacy"])).Should().Be(0);
		events.Should().Equal("adapter", "startup", "hook", "validator", "middleware", "handler");
	}

	[TestMethod]
	[Description("External actions and native rewrites must preserve interactive context and leave the session available for another command.")]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_InteractiveHookHandlesOrRewrites_Then_TheNextCommandStillRuns(bool hosted)
	{
		var app = CoreReplApp.Create();
		var received = new List<int>();
		var channels = new List<ReplInvocationChannel>();
		app.Context("tools", scope => scope.Map("next", () => { received.Add(1); }));
		app.Map("other", (int count) => { received.Add(count); });
		app.UseInvocationHook((ReplInvocationContext context) =>
		{
			if (!context.Parsed.Input.IsInteractive) { return ReplInvocationResult.Continue(); }
			channels.Add(context.Channel);
			return (context.Request.Arguments.Count == 0 ? null : context.Request.Arguments[0]) switch
			{
				"external" => ReplInvocationResult.Handled("external done"),
				"rewrite" => ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("other", new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = 9 })),
				_ => ReplInvocationResult.Continue(),
			};
		});
		using var writer = new StringWriter();
		using var reader = new StringReader("tools\nexternal\nrewrite\nnext\nexit\n");
		using var io = ReplSessionIO.SetSession(writer, reader, isHostedSession: hosted);
		(await app.RunAsync([])).Should().Be(0);
		received.Should().Equal(9, 1);
		channels.Should().OnlyContain(channel => channel == (hosted ? ReplInvocationChannel.HostedInteractive : ReplInvocationChannel.Interactive));
		writer.ToString().Should().Contain("external done");
	}

	[TestMethod]
	[Description("Background analysis must not trigger hook side effects, and handled completion must not render a result.")]
	public async Task When_TabOrHelpRuns_Then_HooksRunButBackgroundPreviewDoesNot()
	{
		var app = CoreReplApp.Create();
		app.Map("run", (int count) => count);
		var purposes = new List<InputPurpose>();
		app.UseInvocationHook((ReplInvocationContext context) =>
		{
			purposes.Add(context.Parsed.Input.Purpose);
			return ReplInvocationResult.Handled("must not render during completion");
		});
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		await app.Autocomplete.ResolveAutocompleteAsync(new("run --count", 11, MenuRequested: false), [], app.CurrentServiceProvider, CancellationToken.None);
		purposes.Should().BeEmpty();
		var tab = await app.Autocomplete.ResolveAutocompleteAsync(new("run --count", 11, MenuRequested: false, ExplicitCompletion: true), [], app.CurrentServiceProvider, CancellationToken.None);
		tab!.Value.Suggestions.Should().BeEmpty();
		writer.ToString().Should().BeEmpty();
		(await app.RunAsync(["run", "--help"])).Should().Be(0);
		purposes.Should().Equal(InputPurpose.Completion, InputPurpose.Help);
	}

	[TestMethod]
	[Description("The shell bridge exposes its user payload as completion input without leaking handled output to stdout.")]
	public async Task When_ShellPayloadIsAnalyzed_Then_HookSeesCompletionMode()
	{
		var app = CoreReplApp.Create();
		var calls = 0;
		app.Map("run", (int count) => count);
		app.UseInvocationHook((ReplInvocationContext context) =>
		{
			calls++;
			context.Request.Arguments.Should().Equal("run", "--count");
			context.Parsed.Input.Mode.Should().Be(InputProcessingMode.ShellCompletion);
			return ReplInvocationResult.Handled("must not render");
		});
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		(await new ShellCompletionEngine(app).ResolveShellCompletionCandidatesAsync("app run --count", 15, ShellKind.Bash, app.CurrentServiceProvider, CancellationToken.None)).Should().BeEmpty();
		calls.Should().Be(1);
		writer.ToString().Should().BeEmpty();
	}

	[TestMethod]
	[Description("A literal route value resembling a flag must not change help or global-option behavior.")]
	public async Task When_NativeRouteContainsAnOptionLikeValue_Then_ItRemainsARouteValue()
	{
		var app = CoreReplApp.Create();
		string? received = null;
		app.Map("run {text}", (string text) => { received = text; });
		(await app.RunAsync(ReplInvocationRequest.ForCommand("run {text}", new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = "--help" }))).Should().Be(0);
		received.Should().Be("--help");
	}

	[TestMethod]
	[Description("Malformed scalar input must refuse the typed hook instead of allowing actions based on a default integer.")]
	public async Task When_RequiredHookInputIsInvalid_Then_ItReceivesABindingDiagnostic()
	{
		var app = CoreReplApp.Create();
		app.Map("run", (int count) => Assert.Fail("Invalid input must not execute."));
		app.UseInvocationHook((int count) => { Assert.Fail("Invalid input must not become a default integer."); return ReplInvocationResult.Continue(); });
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		(await app.RunAsync(["run", "--count=bad"])).Should().Be(2);
		writer.ToString().Should().Contain("Cannot bind invocation hook input 'count'");
	}

	[TestMethod]
	[Description("Group values, typed globals and keyed services must agree with the effective request and its service provider.")]
	public async Task When_HookReceivesOptionsAndKeyedServices_Then_ItUsesTheCurrentInvocation()
	{
		var marker = new object();
		var app = ReplApp.Create(services => services.AddKeyedSingleton("chosen", marker)).UseGlobalOptions<Globals>();
		app.Map("run", (Group group, Globals globals) => { group.Count.Should().Be(4); globals.Tenant.Should().Be("native"); });
		app.UseInvocationHook((Group group, Globals globals, [FromServices("chosen")] object selected) =>
		{
			group.Count.Should().Be(4);
			globals.Tenant.Should().Be("native");
			selected.Should().BeSameAs(marker);
			return ReplInvocationResult.Continue();
		});
		(await app.RunAsync(ReplInvocationRequest.ForCommand("run", new Dictionary<string, object?>(StringComparer.Ordinal) { ["Count"] = 4 },
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = "native" }))).Should().Be(0);
	}

	[TestMethod]
	[Description("A public child invocation must own its submission without consuming the native parent waiting to execute.")]
	public async Task When_HookInvokesANestedRun_Then_TheChildDoesNotInheritTheParentRequest()
	{
		var app = CoreReplApp.Create();
		var received = new List<string>();
		app.Map("outer", (int count) => { received.Add($"outer:{count}"); });
		app.Map("inner", () => { received.Add("inner"); });
		app.UseInvocationHook(async (ReplInvocationContext context) =>
		{
			if (string.Equals(context.Parsed.Command, "outer", StringComparison.Ordinal)) { (await app.RunAsync(["inner"]).ConfigureAwait(false)).Should().Be(0); }
			return ReplInvocationResult.Continue();
		});
		(await app.RunAsync(ReplInvocationRequest.ForCommand("outer", new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = 8 }))).Should().Be(0);
		received.Should().Equal("inner", "outer:8");
	}

	[TestMethod]
	[Description("Concurrent async hooks must retain their own native values and globals across an await boundary.")]
	public async Task When_RunsOverlap_Then_ValuesAndGlobalsRemainIndependent()
	{
		var app = ReplApp.Create().UseGlobalOptions<Globals>();
		var arrived = 0;
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		app.Map("run", (string expected, Globals globals) => { globals.Tenant.Should().Be(expected); });
		app.UseInvocationHook(async (ReplInvocationContext context, Globals globals, CancellationToken token) =>
		{
			if (Interlocked.Increment(ref arrived) == 2) { release.TrySetResult(); }
			await release.Task.WaitAsync(token).ConfigureAwait(false);
			context.Parsed.TryGetValue<string>("expected", out var expected).Should().BeTrue();
			globals.Tenant.Should().Be(expected);
			return ReplInvocationResult.Continue();
		});
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var codes = await Task.WhenAll(RunAsync("first"), RunAsync("second"));
		codes.Should().Equal(0, 0);
		Task<int> RunAsync(string value) => app.RunAsync(ReplInvocationRequest.ForCommand("run",
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["expected"] = value },
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = value }), cancellationToken: cts.Token).AsTask();
	}

	[TestMethod]
	[Description("Early interactive rendering must retain native null globals for accessors and DI, then restore the session even after a rendering failure.")]
	[DataRow(false, false)]
	[DataRow(true, false)]
	[DataRow(false, true)]
	[DataRow(true, true)]
	public async Task When_InteractiveHandledRenderingUsesNativeGlobals_Then_TheSessionIsRestored(bool hosted, bool renderFails)
	{
		var app = ReplApp.Create(services => services.AddTransient<RenderDependency>()).UseGlobalOptions<Globals>();
		var direct = new List<string?>();
		var injected = new List<string?>();
		var next = new List<string?>();
		app.Options(options => options.Output.AddTransformer("native", new InteractiveGlobalsTransformer(app.Core, direct, injected, renderFails)));
		app.Map("run", () => Assert.Fail("Handled input must not reach the handler."));
		app.Map("next", (Globals globals, RenderDependency dependency) =>
		{
			next.Add(globals.Tenant);
			next.Add(dependency.Tenant);
		});
		app.UseInvocationHook((ReplInvocationContext context) => context.Request.Arguments is ["external"]
			? ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("run", globalValues:
				new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = null, ["output"] = "native" }))
			: ReplInvocationResult.Continue());
		app.UseInvocationHook((ReplInvocationContext context) => context.Parsed.Command is "run"
			? ReplInvocationResult.Handled("payload") : ReplInvocationResult.Continue());
		using var writer = new StringWriter();
		using var reader = new StringReader("external\nnext\nexit\n");
		using var io = ReplSessionIO.SetSession(writer, reader, isHostedSession: hosted);

		var code = await app.RunAsync(["--tenant=session"]);

		code.Should().Be(0);
		direct.Should().Equal(new string?[] { null });
		injected.Should().Equal(new string?[] { null });
		next.Should().Equal("session", "session");
		writer.ToString().Should().Contain(renderFails ? "render failure" : "native rendered");
	}

	private sealed class RenderDependency(Globals globals) { public string? Tenant { get; } = globals.Tenant; }
	private sealed class InteractiveGlobalsTransformer(CoreReplApp app, List<string?> direct, List<string?> injected, bool renderFails) : IOutputTransformer
	{
		public string Name => "native";
		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
		{
			direct.Add(app.GlobalOptionsAccessor.GetValue<string>("tenant"));
			injected.Add(app.CurrentServiceProvider.GetRequiredService<RenderDependency>().Tenant);
			if (renderFails) { throw new InvalidOperationException("render failure"); }
			return ValueTask.FromResult("native rendered");
		}
	}

	[TestMethod]
	[Description("Required group input must not trigger an external action with a fabricated default before validation.")]
	[DataRow(false, "missing")]
	[DataRow(true, "missing")]
	[DataRow(false, "invalid")]
	[DataRow(true, "invalid")]
	[DataRow(false, "present")]
	[DataRow(true, "present")]
	public async Task When_HookRequiresAGroupProperty_Then_OnlyAvailableInputReachesIt(bool native, string state)
	{
		var app = CoreReplApp.Create();
		var calls = new List<int>();
		app.Map("run", (RequiredGroup group) => Assert.Fail("Handled or refused input must not execute."));
		app.UseInvocationHook((RequiredGroup group) =>
		{
			calls.Add(group.Count);
			return ReplInvocationResult.Handled();
		});
		var values = new Dictionary<string, object?>(StringComparer.Ordinal);
		if (state is not "missing") { values["Count"] = state is "invalid" ? "bad" : 4; }
		string[] arguments = state is "missing" ? ["run"] : ["run", state is "invalid" ? "--count=bad" : "--count=4"];
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);

		var code = await (native ? app.RunAsync(ReplInvocationRequest.ForCommand("run", values)) : app.RunAsync(arguments));

		code.Should().Be(state is "present" ? 0 : 2);
		if (state is "present") { calls.Should().Equal(4); }
		else
		{
			calls.Should().BeEmpty();
			writer.ToString().Should().Contain("Cannot bind invocation hook input 'Count'");
		}
	}

	[TestMethod]
	[Description("OneOrMore group properties must be supplied before a typed hook can perform an external action.")]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_RequiredGroupCollectionIsMissing_Then_TheHookIsRefused(bool native)
	{
		var app = CoreReplApp.Create();
		var hookCalled = false;
		app.Map("run", (RequiredCollectionGroup group) => Assert.Fail("Missing required collection must not execute."));
		app.UseInvocationHook((RequiredCollectionGroup group) => { hookCalled = true; return ReplInvocationResult.Handled(); });
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);

		var code = await (native ? app.RunAsync(ReplInvocationRequest.ForCommand("run")) : app.RunAsync(["run"]));

		code.Should().Be(2);
		hookCalled.Should().BeFalse();
		writer.ToString().Should().Contain("Cannot bind invocation hook input 'Items'");
	}

	[TestMethod]
	[Description("A context hook can repair an absent required group value before a later typed hook binds it.")]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_ContextRepairsARequiredGroup_Then_TheNextHookAndHandlerReceiveIt(bool native)
	{
		var app = CoreReplApp.Create();
		var received = new List<int>();
		app.Map("run", (RequiredGroup group) => { received.Add(group.Count); });
		app.UseInvocationHook((ReplInvocationContext context) =>
		{
			context.Parsed.Values["Count"].State.Should().Be(InputValueState.Missing);
			return ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("run",
				new Dictionary<string, object?>(StringComparer.Ordinal) { ["Count"] = 6 }));
		});
		app.UseInvocationHook((RequiredGroup group) => { received.Add(group.Count); return ReplInvocationResult.Continue(); });

		var code = await (native ? app.RunAsync(ReplInvocationRequest.ForCommand("run")) : app.RunAsync(["run"]));

		code.Should().Be(0);
		received.Should().Equal(6, 6);
	}

	[TestMethod]
	[Description("An omitted optional group property retains its initializer rather than being treated as unavailable required input.")]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_OptionalGroupPropertyIsMissing_Then_ItsInitializerIsPreserved(bool native)
	{
		var app = CoreReplApp.Create();
		var received = new List<int>();
		app.Map("run", (OptionalGroup group) => { received.Add(group.Count); });
		app.UseInvocationHook((OptionalGroup group) => { received.Add(group.Count); return ReplInvocationResult.Continue(); });

		var code = await (native ? app.RunAsync(ReplInvocationRequest.ForCommand("run")) : app.RunAsync(["run"]));

		code.Should().Be(0);
		received.Should().Equal(42, 42);
	}

	[ReplOptionsGroup]
	private sealed class RequiredGroup
	{
		[ReplOption(Arity = ReplArity.ExactlyOne)]
		public int Count { get; set; }
	}
	[ReplOptionsGroup]
	private sealed class RequiredCollectionGroup
	{
		[ReplOption(Arity = ReplArity.OneOrMore)]
		public string[] Items { get; set; } = [];
	}
	[ReplOptionsGroup]
	private sealed class OptionalGroup { public int Count { get; set; } = 42; }

	[ReplOptionsGroup]
	private sealed class Group { public int Count { get; set; } }
	private sealed class Globals { public string Tenant { get; set; } = "default"; }
}
