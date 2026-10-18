using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Repl.Tests;

[TestClass]
public sealed class Given_InvocationStartup
{
	[TestMethod]
	[Description("Startup decisions govern native dispatch while original values remain available for provenance across Core and the facade.")]
	[DataRow(false, "continue")]
	[DataRow(true, "continue")]
	[DataRow(false, "replace")]
	[DataRow(true, "replace")]
	[DataRow(false, "handled")]
	[DataRow(true, "handled")]
	public async Task When_StartupDecidesANativeRequest_Then_DispatchUsesItsEffectiveRequest(bool facade, string decision)
	{
		var app = facade ? ReplApp.Create().UseGlobalOptions<Globals>() : null;
		var core = app?.Core ?? CoreReplApp.Create().UseGlobalOptions<Globals>();
		var seen = new List<string>();
		var hookCalls = 0;
		core.UseArgumentPreprocessor((_, _) => throw new InvalidOperationException("Native requests must not run token adapters."));
		core.Map("original", (int count, Globals globals) => { count.Should().Be(8); seen.Add(globals.Tenant); });
		core.Map("replacement", (Globals globals) => { seen.Add(globals.Tenant); });
		core.UseStartupHook((Globals globals) =>
		{
			globals.Tenant.Should().Be("native");
			return decision switch
			{
				"replace" => ArgumentPreprocessingResult.Replace(["replacement", "--tenant=rewritten"]),
				"handled" => ArgumentPreprocessingResult.Handled(),
				_ => ArgumentPreprocessingResult.Continue(),
			};
		});
		core.UseInvocationHook((ReplInvocationContext context) =>
		{
			hookCalls++;
			context.OriginalRequest.Command.Should().Be("original");
			context.Parsed.Command.Should().Be(string.Equals(decision, "replace", StringComparison.Ordinal) ? "replacement" : "original");
			return ReplInvocationResult.Continue();
		});
		var request = ReplInvocationRequest.ForCommand("original", new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = 8 },
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = "native" });
		(await (app is null ? core.RunAsync(request) : app.RunAsync(request))).Should().Be(0);
		if (string.Equals(decision, "handled", StringComparison.Ordinal)) { seen.Should().BeEmpty(); hookCalls.Should().Be(0); }
		else { seen.Should().Equal(string.Equals(decision, "replace", StringComparison.Ordinal) ? "rewritten" : "native"); hookCalls.Should().Be(1); }
	}

	[TestMethod]
	[Description("Invalid built-in flags must remain inspectable and repairable instead of failing before invocation hooks.")]
	[DataRow(false, false)]
	[DataRow(true, false)]
	[DataRow(false, true)]
	[DataRow(true, true)]
	public async Task When_ABuiltinGlobalIsInvalid_Then_AContextHookCanRepairIt(bool facade, bool replaceFirst)
	{
		var app = facade ? ReplApp.Create() : null;
		var core = app?.Core ?? CoreReplApp.Create();
		var ran = false;
		var invalid = ReplInvocationRequest.ForCommand("run", globalValues: new Dictionary<string, object?>(StringComparer.Ordinal) { ["help"] = "bad" });
		core.Map("run", () => { ran = true; });
		core.UseInvocationHook(() => replaceFirst ? ReplInvocationResult.Replace(invalid) : ReplInvocationResult.Continue());
		core.UseInvocationHook((ReplInvocationContext context) =>
		{
			context.Parsed.GlobalValues["help"].State.Should().Be(InputValueState.Invalid);
			context.Parsed.Validation.State.Should().Be(InputValidationState.Invalid);
			return ReplInvocationResult.Replace(ReplInvocationRequest.ForCommand("run"));
		});
		var request = replaceFirst ? ReplInvocationRequest.ForCommand("run") : invalid;
		(await (app is null ? core.RunAsync(request) : app.RunAsync(request))).Should().Be(0);
		ran.Should().BeTrue();
	}

	[TestMethod]
	[Description("Unrepaired native flags must be rejected before command execution rather than silently ignored.")]
	public async Task When_AnInvalidGlobalIsNotRepaired_Then_FinalValidationRefusesIt()
	{
		var app = CoreReplApp.Create();
		app.Map("run", () => Assert.Fail("Invalid globals must be refused."));
		(await app.RunAsync(ReplInvocationRequest.ForCommand("run", globalValues:
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["help"] = "bad" }))).Should().Be(2);
	}

	private sealed class Globals { public string Tenant { get; set; } = "default"; }

	[TestMethod]
	[Description("Nested textual runs from startup must not consume the pending parent, including when the child fails.")]
	[DataRow(false, false)]
	[DataRow(true, false)]
	[DataRow(false, true)]
	[DataRow(true, true)]
	public async Task When_StartupRunsATextualChild_Then_ItCannotConsumeThePendingNativeParent(bool facade, bool childFails)
	{
		var app = facade ? ReplApp.Create().UseGlobalOptions<Globals>() : null;
		var core = app?.Core ?? CoreReplApp.Create().UseGlobalOptions<Globals>();
		var seen = new List<string>();
		core.Map("inner", (Globals globals) =>
		{
			globals.Tenant.Should().Be("default");
			seen.Add("inner");
			if (childFails) { throw new InvalidOperationException("child failure"); }
		});
		core.Map("outer", (int count, Globals globals) => { count.Should().Be(8); globals.Tenant.Should().Be("parent"); seen.Add("outer"); });
		core.UseStartupHook(async (ReplStartupContext context, Globals globals) =>
		{
			if (context.RemainingArguments is ["outer"])
			{
				var childCode = app is null ? await core.RunAsync(["inner"]).ConfigureAwait(false) : await app.RunAsync(["inner"]).ConfigureAwait(false);
				childCode.Should().Be(childFails ? 1 : 0);
				globals.Tenant.Should().Be("parent");
				core.GlobalOptionsAccessor.GetValue<string>("tenant").Should().Be("parent");
			}
			return ArgumentPreprocessingResult.Continue();
		});
		var request = ReplInvocationRequest.ForCommand("outer", new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = 8 },
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = "parent" });
		(await (app is null ? core.RunAsync(request) : app.RunAsync(request))).Should().Be(0);
		seen.Should().Equal("inner", "outer");
	}

	[TestMethod]
	[Description("Dependencies created during startup must observe the same native null and restored parent globals as direct injection.")]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_NativeStartupCreatesDependencies_Then_DirectAndDiGlobalsAgree(bool childFails)
	{
		var app = ReplApp.Create(services => services.AddTransient<Dependency>()).UseGlobalOptions<Globals>();
		var seen = new List<string?>();
		app.UseStartupHook((Globals globals, Dependency dependency) =>
		{
			dependency.Tenant.Should().Be(globals.Tenant);
			seen.Add(globals.Tenant);
			return ArgumentPreprocessingResult.Continue();
		});
		app.Map("inner", (Globals globals, Dependency dependency) =>
		{
			globals.Tenant.Should().BeNull();
			dependency.Tenant.Should().BeNull();
			if (childFails) { throw new InvalidOperationException("child failure"); }
		});
		app.Map("outer", (Globals globals, Dependency dependency) => { globals.Tenant.Should().Be("parent"); dependency.Tenant.Should().Be("parent"); });
		var child = ReplInvocationRequest.ForCommand("inner", globalValues: new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = null });
		app.UseInvocationHook(async (ReplInvocationContext context, Globals globals) =>
		{
			if (string.Equals(context.Parsed.Command, "outer", StringComparison.Ordinal))
			{
				(await app.RunAsync(child).ConfigureAwait(false)).Should().Be(childFails ? 1 : 0);
				globals.Tenant.Should().Be("parent");
			}
			return ReplInvocationResult.Continue();
		});
		(await app.RunAsync(child)).Should().Be(childFails ? 1 : 0);
		(await app.RunAsync(ReplInvocationRequest.ForCommand("outer", globalValues:
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = "parent" }))).Should().Be(0);
		seen.Should().Equal(null, "parent", null);
	}

	[TestMethod]
	[Description("Head service startup must see native globals even when no startup hook was registered.")]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_HostedServicesStart_Then_TheySeeNativeGlobals(bool startupHook)
	{
		var seen = new List<string?>();
		var app = ReplApp.Create(services => services.AddSingleton<IHostedService>(provider => new HostedProbe(provider.GetRequiredService<Globals>(), seen)))
			.UseGlobalOptions<Globals>();
		app.Map("run", () => { });
		if (startupHook) { app.UseStartupHook(() => ArgumentPreprocessingResult.Continue()); }
		var request = ReplInvocationRequest.ForCommand("run", globalValues: new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = null });
		(await app.RunAsync(request, new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head })).Should().Be(0);
		seen.Should().ContainSingle().Which.Should().BeNull();
	}

	[TestMethod]
	[Description("A terminal startup result must retain native globals until its output transformer finishes.")]
	public async Task When_StartupHandlesANativeRequest_Then_TerminalRenderingSeesItsGlobals()
	{
		var app = CoreReplApp.Create().UseGlobalOptions<Globals>();
		app.Options(options => options.Output.AddTransformer("native", new NativeGlobalTransformer(app.GlobalOptionsAccessor)));
		app.UseStartupHook(() => ArgumentPreprocessingResult.Handled("payload"));
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		(await app.RunAsync(ReplInvocationRequest.ForCommand("run", globalValues:
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = null, ["output"] = "native" }))).Should().Be(0);
		writer.ToString().Should().Contain("done");
	}

	[TestMethod]
	[Description("Native nullable globals must remain native across interactive entry, text overrides and subsequent independent runs.")]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_ANativeRequestEntersInteractiveMode_Then_CommandsInheritNativeNulls(bool hosted)
	{
		var app = ReplApp.Create().UseGlobalOptions<NullableGlobals>();
		var seen = new List<string?>();
		app.Map("start", () => { });
		app.Map("check", (NullableGlobals globals) =>
		{
			seen.Add(globals.Tenant);
			seen.Add(globals.Limit?.ToString(System.Globalization.CultureInfo.InvariantCulture));
		});
		using var writer = new StringWriter();
		using var reader = new StringReader("check\ncheck --tenant=override --limit=9\ncheck\nexit\n");
		using var io = ReplSessionIO.SetSession(writer, reader, isHostedSession: hosted);
		var request = ReplInvocationRequest.ForCommand("start", globalValues:
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = null, ["limit"] = null, ["interactive"] = true });

		(await app.RunAsync(request)).Should().Be(0);
		(await app.RunAsync(["check"])).Should().Be(0);

		seen.Should().Equal(null, null, "override", "9", null, null, "default", "3");
	}

	private sealed class NullableGlobals
	{
		public string? Tenant { get; set; } = "default";
		public int? Limit { get; set; } = 3;
	}

	private sealed class Dependency(Globals globals) { public string? Tenant { get; } = globals.Tenant; }
	private sealed class HostedProbe(Globals globals, List<string?> seen) : IHostedService
	{
		public Task StartAsync(CancellationToken cancellationToken) { seen.Add(globals.Tenant); return Task.CompletedTask; }
		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}

	[TestMethod]
	[Description("Hosted startup callbacks must not consume their native parent when they run a successful or failing textual child.")]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_HeadStartupRunsATextualChild_Then_TheParentSubmissionIsRestored(bool childFails)
	{
		CoreReplApp? core = null;
		var seen = new List<string>();
		var app = ReplApp.Create(services => services.AddSingleton<IHostedService>(new NestedHostedProbe(async token =>
		{
			(await core!.RunAsync(["inner"], token).ConfigureAwait(false)).Should().Be(childFails ? 1 : 0);
			core.GlobalOptionsAccessor.GetValue<string>("tenant").Should().Be("parent");
		}))).UseGlobalOptions<Globals>();
		core = app.Core;
		app.Map("inner", (Globals globals) =>
		{
			globals.Tenant.Should().Be("default");
			seen.Add("inner");
			if (childFails) { throw new InvalidOperationException("child failure"); }
		});
		app.Map("outer", (int count, Globals globals) => { count.Should().Be(8); globals.Tenant.Should().Be("parent"); seen.Add("outer"); });
		var request = ReplInvocationRequest.ForCommand("outer", new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = 8 },
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = "parent" });
		(await app.RunAsync(request, new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head })).Should().Be(0);
		seen.Should().Equal("inner", "outer");
	}

	private sealed class NestedHostedProbe(Func<CancellationToken, Task> start) : IHostedService
	{
		public Task StartAsync(CancellationToken cancellationToken) => start(cancellationToken);
		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}
	private sealed class NativeGlobalTransformer(IGlobalOptionsAccessor accessor) : IOutputTransformer
	{
		public string Name => "native";
		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
		{
			accessor.GetValue<string>("tenant").Should().BeNull();
			return ValueTask.FromResult("done");
		}
	}

	[TestMethod]
	[Description("Independent public submissions use configured defaults and restore their enclosing invocation afterward.")]
	public async Task When_APublicChildOmitsGlobals_Then_ItUsesDefaultsAndRestoresItsParent()
	{
		var app = CoreReplApp.Create().UseGlobalOptions<Globals>();
		app.Map("outer", (Globals globals) => { globals.Tenant.Should().Be("parent"); });
		app.Map("inner", (Globals globals) => { globals.Tenant.Should().Be("default"); });
		app.UseInvocationHook(async (ReplInvocationContext context, Globals globals) =>
		{
			if (string.Equals(context.Parsed.Command, "outer", StringComparison.Ordinal))
			{
				(await app.RunAsync(ReplInvocationRequest.ForCommand("inner")).ConfigureAwait(false)).Should().Be(0);
				globals.Tenant.Should().Be("parent");
			}
			else { globals.Tenant.Should().Be("default"); }
			return ReplInvocationResult.Continue();
		});
		(await app.RunAsync(ReplInvocationRequest.ForCommand("outer", globalValues:
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant"] = "parent" }))).Should().Be(0);
	}
}
