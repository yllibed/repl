using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Repl.Tests;

[TestClass]
public sealed class Given_StartupPreparation
{
	[TestMethod]
	[Description("Preparation shares typed declarations with Core and does not replay adapters or preparation hooks during execution.")]
	public async Task When_CoreRunsPreparedInput_Then_TypedOptionsAndOriginalArgumentsArePreserved()
	{
		var adapters = 0;
		var preparations = 0;
		var builder = ReplStartup.CreateBuilder().UseGlobalOptions<StartupSettings>()
			.UseArgumentPreprocessor((input, _) =>
			{
				adapters++;
				return ValueTask.FromResult(ArgumentPreprocessingResult.Replace(["query", "--count", "3"]));
			})
			.UsePreparationHook((ReplStartupContext context, StartupSettings settings) =>
			{
				preparations++;
				context.OriginalArguments.Should().Equal("/old");
				settings.Count.Should().Be(3);
				return ArgumentPreprocessingResult.Continue();
			});
		var prepared = await builder.PrepareAsync(["/old"]);
		prepared.CanContinue.Should().BeTrue();
		prepared.GetOptions<StartupSettings>().Count.Should().Be(3);
		prepared.RemainingArguments.Should().Equal("query");
		var app = CoreReplApp.Create(prepared);
		var seen = new List<int>();
		app.UseStartupHook((ReplStartupContext context, StartupSettings settings, CancellationToken _) =>
		{
			context.OriginalArguments.Should().Equal("/old");
			seen.Add(settings.Count);
			return ArgumentPreprocessingResult.Continue();
		});
		app.Map("query", (StartupSettings settings) => { seen.Add(settings.Count); });
		(await app.RunAsync(prepared)).Should().Be(0);
		(await app.RunAsync(prepared)).Should().Be(0);
		seen.Should().Equal(3, 3, 3, 3);
		adapters.Should().Be(1);
		preparations.Should().Be(1);
		Action mutate = () => builder.Options(options => options.Parsing.AllowUnknownOptions = true);
		mutate.Should().Throw<InvalidOperationException>();
	}

	[TestMethod]
	[Description("Synchronous, Task and ValueTask preparation hooks run in order; app adapters run once after preparation before startup hooks.")]
	public async Task When_PreparationMixesDelegateKinds_Then_OrderingAndAdapterReplayAreExplicit()
	{
		var events = new List<string>();
		var builder = ReplStartup.CreateBuilder()
			.UsePreparationHook(() => { events.Add("sync"); return ArgumentPreprocessingResult.Continue(); })
			.UsePreparationHook(async Task<ArgumentPreprocessingResult> () =>
			{
				await Task.Yield();
				events.Add("task");
				return ArgumentPreprocessingResult.Replace(["query"]);
			})
			.UsePreparationHook(ValueTask<ArgumentPreprocessingResult> () =>
			{
				events.Add("value-task");
				return ValueTask.FromResult(ArgumentPreprocessingResult.Continue());
			});
		var prepared = await builder.PrepareAsync([]);
		var app = CoreReplApp.Create(prepared);
		app.UseArgumentPreprocessor((input, _) =>
		{
			input.Arguments.Should().Equal("query");
			events.Add("adapter");
			return ValueTask.FromResult(ArgumentPreprocessingResult.Continue());
		});
		app.UseStartupHook(() => { events.Add("startup"); return ArgumentPreprocessingResult.Continue(); });
		app.Map("query", () => { events.Add("handler"); });
		(await app.RunAsync(prepared)).Should().Be(0);
		events.Should().Equal("sync", "task", "value-task", "adapter", "startup", "handler");
	}

	[TestMethod]
	[Description("Prepared globals guide registrations before a DI-resolved module materializes the provider; startup and handlers share their scoped dependency.")]
	public async Task When_ModuleBuildsTheProvider_Then_PreparationAlreadyRanAndStartupPrecedesHostedServices()
	{
		var log = new StartupLog();
		var prepared = await ReplStartup.CreateBuilder().UseGlobalOptions<StartupSettings>()
			.UsePreparationHook((StartupSettings settings) => { log.Events.Add("prepare"); return ArgumentPreprocessingResult.Continue(); })
			.PrepareAsync(["query", "--count", "3"]);
		var app = ReplApp.Create(prepared, services =>
		{
			log.Events.Add("register");
			services.AddSingleton(log);
			services.AddSingleton(new ChosenCount(prepared.GetOptions<StartupSettings>().Count));
			services.AddScoped<ScopedDependency>();
			services.AddSingleton<IHostedService, HostedDependency>();
		});
		app.MapModule<PreparedModule>();
		ScopedDependency? dependency = null;
		app.UseStartupHook((StartupSettings settings, ChosenCount choice, ScopedDependency scoped) =>
		{
			settings.Count.Should().Be(choice.Value).And.Be(3);
			dependency = scoped;
			log.Events.Add("hook");
			return ArgumentPreprocessingResult.Continue();
		});
		app.Map("query", (ScopedDependency scoped) => { scoped.Should().BeSameAs(dependency); log.Events.Add("handler"); });
		(await app.RunAsync(prepared, new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head })).Should().Be(0);
		log.Events.Should().Equal("prepare", "register", "module:3", "hook", "start", "handler", "dispose", "stop");
	}

	[TestMethod]
	[Description("A terminal startup hook prevents hosted service startup and handler dispatch while its scoped dependencies are disposed.")]
	public async Task When_StartupHandlesTheRun_Then_HostedServicesDoNotStart()
	{
		var log = new StartupLog();
		var app = ReplApp.Create(services =>
		{
			services.AddSingleton(log);
			services.AddScoped<ScopedDependency>();
			services.AddSingleton<IHostedService, HostedDependency>();
		});
		app.UseStartupHook((ScopedDependency _) => ArgumentPreprocessingResult.Handled(Results.Exit(7)));
		app.UseStartupHook(ArgumentPreprocessingResult () => throw new InvalidOperationException("must not run"));
		app.Map("query", () => { log.Events.Add("handler"); });
		(await app.RunAsync(["query"], new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head })).Should().Be(7);
		log.Events.Should().Equal("dispose");
	}

	[TestMethod]
	[Description("Each rewrite updates later hook values and final binding without changing the prepared configuration snapshot or replaying callbacks.")]
	public async Task When_StartupRewritesGlobals_Then_FinalValidationSeesTheNewValues()
	{
		var prepared = await ReplStartup.CreateBuilder().UseGlobalOptions<StartupSettings>().PrepareAsync(["--count", "1"]);
		var app = CoreReplApp.Create(prepared);
		var calls = 0;
		app.UseStartupHook((StartupSettings settings) => { calls++; settings.Count.Should().Be(1); return ArgumentPreprocessingResult.Replace(["query", "--count", "2"]); });
		app.UseStartupHook((StartupSettings settings) => { calls++; settings.Count.Should().Be(2); return ArgumentPreprocessingResult.Continue(); });
		app.Map("query", (StartupSettings settings) => { settings.Count.Should().Be(2); });
		(await app.RunAsync(prepared)).Should().Be(0);
		prepared.GetOptions<StartupSettings>().Count.Should().Be(1);
		calls.Should().Be(2);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("Invalid and handled preparation can complete in the requested format without constructing an application or its services.")]
	public async Task When_PreparationIsTerminal_Then_ItCompletesWithoutApplicationComposition(bool invalid)
	{
		var builder = ReplStartup.CreateBuilder().UseGlobalOptions<StartupSettings>();
		if (!invalid) { builder.UsePreparationHook((ReplStartupContext _) => ArgumentPreprocessingResult.Handled(Results.Exit(9))); }
		var prepared = await builder.PrepareAsync(invalid ? ["--count", "bad", "--output:json"] : ["--output:json"]);
		prepared.CanContinue.Should().BeFalse();
		var registered = false;
		Action create = () => ReplApp.Create(prepared, _ => registered = true);
		create.Should().Throw<InvalidOperationException>();
		registered.Should().BeFalse();
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		(await prepared.CompleteAsync()).Should().Be(invalid ? 2 : 9);
		if (invalid) { using var json = JsonDocument.Parse(writer.ToString()); json.RootElement.ValueKind.Should().Be(JsonValueKind.Object); }
	}

	[TestMethod]
	[DataRow(false, false)]
	[DataRow(false, true)]
	[DataRow(true, false)]
	[DataRow(true, true)]
	[Description("An existing application rejects handled or failed preparation before any runtime startup work.")]
	public async Task When_AReusedApplicationReceivesTerminalPreparation_Then_RunIsRejected(bool facade, bool failure)
	{
		var builder = ReplStartup.CreateBuilder().UsePreparationHook((ReplStartupContext context) =>
		{
			if (context.Arguments is ["terminal"])
			{
				if (failure) { throw new InvalidOperationException("preparation failed"); }
				return ArgumentPreprocessingResult.Handled(Results.Exit(9));
			}
			return ArgumentPreprocessingResult.Continue();
		});
		var successful = await builder.PrepareAsync(["query"]);
		var terminal = await builder.PrepareAsync(["terminal"]);
		var entered = false;
		ArgumentPreprocessingResult Hook() { entered = true; return ArgumentPreprocessingResult.Continue(); }
		if (facade)
		{
			var app = ReplApp.Create(successful);
			app.UseStartupHook(Hook);
			Func<Task> run = () => app.RunAsync(terminal).AsTask();
			await run.Should().ThrowAsync<InvalidOperationException>();
		}
		else
		{
			var app = CoreReplApp.Create(successful);
			app.UseStartupHook(Hook);
			Func<Task> run = () => app.RunAsync(terminal).AsTask();
			await run.Should().ThrowAsync<InvalidOperationException>();
		}
		entered.Should().BeFalse();
	}

	[TestMethod]
	[Description("Preparation hooks cannot request application services, even the service-provider abstraction.")]
	public async Task When_PreparationRequestsServices_Then_ItReportsAnApplicationFailure()
	{
		var prepared = await ReplStartup.CreateBuilder()
			.UsePreparationHook((IServiceProvider _) => ArgumentPreprocessingResult.Continue()).PrepareAsync([]);
		prepared.CanContinue.Should().BeFalse();
		(await prepared.CompleteAsync()).Should().Be(1);
	}

	[TestMethod]
	[Description("Externally owned services can be configured after preparation and supply modules and scoped hook dependencies without an intermediate application provider.")]
	public async Task When_TheHostOwnsServices_Then_PreparationRunsBeforeHostConstruction()
	{
		var prepared = await ReplStartup.CreateBuilder().UseGlobalOptions<StartupSettings>().PrepareAsync(["query", "--count", "4"]);
		var log = new StartupLog();
		var services = new ServiceCollection();
		services.AddRepl(prepared, collection =>
		{
			collection.AddSingleton(log);
			collection.AddScoped<ScopedDependency>();
			collection.AddSingleton<PreparedModule>();
		}, (provider, app) =>
		{
			app.MapModule(provider.GetRequiredService<PreparedModule>());
			app.UseStartupHook((ScopedDependency _) => { log.Events.Add("hook"); return ArgumentPreprocessingResult.Continue(); });
			app.Map("query", (StartupSettings settings) => { settings.Count.Should().Be(4); });
		});
		log.Events.Should().BeEmpty();
		await using var provider = services.BuildServiceProvider(validateScopes: true);
		var app = provider.GetRequiredService<ReplApp>();
		(await app.RunAsync(prepared, provider)).Should().Be(0);
		provider.GetRequiredService<StartupLog>().Should().BeSameAs(log);
		log.Events.Should().Equal("module:4", "hook", "dispose");
	}

	[TestMethod]
	[DataRow("--help", InputPurpose.Help)]
	[DataRow("completion", InputPurpose.Completion)]
	[Description("Preparation and startup expose help and completion intent without requiring a resolved user command.")]
	public async Task When_TheRequestIsDiscovery_Then_BothStagesReceiveItsPurpose(string request, InputPurpose expected)
	{
		var purposes = new List<InputPurpose>();
		var prepared = await ReplStartup.CreateBuilder().UsePreparationHook((ReplStartupContext context) =>
		{
			purposes.Add(context.Purpose);
			return ArgumentPreprocessingResult.Continue();
		}).PrepareAsync(request is "completion" ? ["completion", "__complete", "bash", "app q", "5"] : [request]);
		var app = CoreReplApp.Create(prepared);
		app.UseStartupHook((ReplStartupContext context) => { purposes.Add(context.Purpose); return ArgumentPreprocessingResult.Handled(); });
		(await app.RunAsync(prepared)).Should().Be(0);
		purposes.Should().Equal(expected, expected);
	}

	[TestMethod]
	[Description("Startup runs once for an interactive session and does not repeat for its command lines or nested invocations.")]
	public async Task When_TheSessionRunsSeveralCommands_Then_StartupRunsOnlyOnce()
	{
		var calls = 0;
		var app = CoreReplApp.Create();
		app.UseStartupHook(() => { calls++; return ArgumentPreprocessingResult.Continue(); });
		app.Map("query", () => "done");
		using var writer = new StringWriter();
		using var reader = new StringReader("query\nquery\nexit\n");
		using var io = ReplSessionIO.SetSession(writer, reader);
		(await app.RunAsync([])).Should().Be(0);
		calls.Should().Be(1);
	}

	public sealed class StartupSettings { public int Count { get; set; } = 1; }
	private sealed record ChosenCount(int Value);
	private sealed class StartupLog { public List<string> Events { get; } = []; }
	private sealed class ScopedDependency(StartupLog log) : IDisposable { public void Dispose() => log.Events.Add("dispose"); }
	private sealed class HostedDependency(StartupLog log) : IHostedService
	{
		public Task StartAsync(CancellationToken cancellationToken) { log.Events.Add("start"); return Task.CompletedTask; }
		public Task StopAsync(CancellationToken cancellationToken) { log.Events.Add("stop"); return Task.CompletedTask; }
	}
	private sealed class PreparedModule : IReplModule
	{
		public PreparedModule(StartupLog log, StartupSettings settings) { log.Events.Add($"module:{settings.Count}"); }
		public void Map(IReplMap map) { }
	}
}
