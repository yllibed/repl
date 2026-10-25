using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Repl.Tests;

[TestClass]
public sealed class Given_StartupIsolation
{
	[TestMethod]
	[Description("Prepared runs sharing one application keep their globals and scoped dependencies independent across asynchronous startup hooks.")]
	public async Task When_TwoRunsOverlap_Then_StartupValuesAndServicesRemainLocal()
	{
		var builder = ReplStartup.CreateBuilder().UseGlobalOptions<Settings>();
		var first = await builder.PrepareAsync(["query", "3", "--count", "3"]);
		var second = await builder.PrepareAsync(["query", "7", "--count", "7"]);
		var app = ReplApp.Create(first, services => services.AddScoped(provider =>
			new Dependency { Count = provider.GetRequiredService<Settings>().Count }));
		var arrived = 0;
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var scopes = new System.Collections.Concurrent.ConcurrentBag<Dependency>();
		app.UseStartupHook(async (ReplStartupContext context, Settings settings, Dependency dependency, CancellationToken token) =>
		{
			scopes.Add(dependency);
			if (Interlocked.Increment(ref arrived) == 2) { release.TrySetResult(); }
			await release.Task.WaitAsync(token).ConfigureAwait(false);
			dependency.Count.Should().Be(settings.Count);
			context.GetOptions<Settings>().Count.Should().Be(settings.Count);
			context.Arguments.Should().Contain(settings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
			return ArgumentPreprocessingResult.Continue();
		});
		app.Map("query {expected}", (int expected, Settings settings, Dependency dependency) =>
		{
			settings.Count.Should().Be(expected);
			dependency.Count.Should().Be(expected);
		});
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var codes = await Task.WhenAll(app.RunAsync(first, cancellationToken: cancellation.Token).AsTask(),
			app.RunAsync(second, cancellationToken: cancellation.Token).AsTask());
		codes.Should().Equal(0, 0);
		scopes.Distinct().Should().HaveCount(2);
	}

	[TestMethod]
	[Description("A later prepared invocation with omitted globals uses its own defaults rather than the previous run's explicit values.")]
	public async Task When_PreparedInputIsReusedWithNewArguments_Then_DefaultsDoNotLeak()
	{
		var builder = ReplStartup.CreateBuilder().UseGlobalOptions<Settings>();
		var first = await builder.PrepareAsync(["query", "--count", "8"]);
		var second = await builder.PrepareAsync(["query"]);
		var app = CoreReplApp.Create(first);
		var seen = new List<int>();
		app.UseStartupHook((Settings settings) => { seen.Add(settings.Count); return ArgumentPreprocessingResult.Continue(); });
		app.Map("query", (Settings settings) => { seen.Add(settings.Count); });
		(await app.RunAsync(first)).Should().Be(0);
		(await app.RunAsync(second)).Should().Be(0);
		seen.Should().Equal(8, 8, 1, 1);
	}

	[TestMethod]
	[Description("Startup dependencies resolve options from the current run rather than the preceding session's baseline.")]
	public async Task When_AReusedRunOmitsAGlobal_Then_StartupDependenciesReceiveItsDefault()
	{
		var builder = ReplStartup.CreateBuilder().UseGlobalOptions<Settings>();
		var first = await builder.PrepareAsync(["query", "--count", "8"]);
		var second = await builder.PrepareAsync(["query"]);
		var hostedValues = new List<int>();
		var app = ReplApp.Create(first, services =>
		{
			services.AddScoped(provider => new Dependency { Count = provider.GetRequiredService<Settings>().Count });
			services.AddSingleton<IHostedService>(provider => new HostedGlobals(provider.GetRequiredService<IGlobalOptionsAccessor>(), hostedValues));
		});
		var seen = new List<int>();
		app.UseStartupHook((Settings settings, Dependency dependency) =>
		{
			dependency.Count.Should().Be(settings.Count);
			seen.Add(dependency.Count);
			return ArgumentPreprocessingResult.Continue();
		});
		app.Map("query", () => { });
		var options = new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.Head };
		(await app.RunAsync(first, options)).Should().Be(0);
		(await app.RunAsync(second, options)).Should().Be(0);
		seen.Should().Equal(8, 1);
		hostedValues.Should().Equal(8, 1);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("A later hook failure retains earlier rewrites, requested JSON output and application-failure classification.")]
	public async Task When_AHookFailsAfterARewrite_Then_TheEffectiveErrorFormatIsPreserved(bool preparation)
	{
		ArgumentPreprocessingResult Rewrite() => ArgumentPreprocessingResult.Replace(["query", "--output:json"]);
		ArgumentPreprocessingResult Fail() => throw new InvalidOperationException("startup failed");
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		if (preparation)
		{
			var prepared = await ReplStartup.CreateBuilder().UsePreparationHook(Rewrite).UsePreparationHook(Fail).PrepareAsync([]);
			prepared.Arguments.Should().Contain("--output:json");
			(await prepared.CompleteAsync()).Should().Be(1);
		}
		else
		{
			var app = CoreReplApp.Create();
			app.UseStartupHook(Rewrite).UseStartupHook(Fail);
			(await app.RunAsync([])).Should().Be(1);
		}
		using var document = JsonDocument.Parse(writer.ToString());
		document.RootElement.ToString().Should().Contain("startup_error").And.Contain("startup failed");
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("Caller cancellation during either preparation or startup propagates by default and never dispatches a command.")]
	public async Task When_TheCallerCancelsAHook_Then_TheCancellationPolicyIsPreserved(bool preparation)
	{
		using var cancellation = new CancellationTokenSource();
		ArgumentPreprocessingResult Cancel(CancellationToken token)
		{
			cancellation.Cancel();
			token.ThrowIfCancellationRequested();
			return ArgumentPreprocessingResult.Continue();
		}
		Func<Task> run = preparation
			? async () => { await ReplStartup.CreateBuilder().UsePreparationHook(Cancel).PrepareAsync([], cancellation.Token).ConfigureAwait(false); }
			: async () => { await CoreReplApp.Create().UseStartupHook(Cancel).RunAsync([], cancellation.Token).ConfigureAwait(false); };
		await run.Should().ThrowAsync<OperationCanceledException>();
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("Caller cancellation follows the configured exit policy, while an unrelated cancellation exception remains a startup failure.")]
	public async Task When_AStartupHookCancels_Then_OnlyCallerCancellationUsesTheCancelledCode(bool callerCancellation)
	{
		using var cancellation = new CancellationTokenSource();
		var app = CoreReplApp.Create().Options(options => options.ExitCodes.Cancelled = 130);
		app.UseStartupHook(ArgumentPreprocessingResult () =>
		{
			if (callerCancellation) { cancellation.Cancel(); }
			throw new OperationCanceledException("hook cancellation");
		});
		(await app.RunAsync(["query"], cancellation.Token)).Should().Be(callerCancellation ? 130 : 1);
	}

	[TestMethod]
	[Description("Startup rewrites remain subject to ordinary command and input validation.")]
	public async Task When_StartupProducesAnInvalidCommand_Then_ItCannotBypassValidation()
	{
		var app = CoreReplApp.Create();
		var handled = false;
		app.UseStartupHook(() => ArgumentPreprocessingResult.Replace(["query", "--limit", "bad"]));
		app.Map("query", (int limit) => { handled = true; });
		app.UseInputValidator((ParsedInputContext _) => InputValidationResult.Valid());
		(await app.RunAsync(["original"])).Should().Be(2);
		handled.Should().BeFalse();
	}

	[TestMethod]
	[Description("Nested invocations do not execute the parent run's startup hooks.")]
	public async Task When_AHandlerInvokesAChild_Then_StartupIsNotRepeated()
	{
		var calls = 0;
		var app = CoreReplApp.Create();
		app.UseStartupHook(() => { calls++; return ArgumentPreprocessingResult.Continue(); });
		app.Map("inner", () => "done");
		app.Map("outer", async (IServiceProvider services) => { (await app.RunSubInvocationAsync(["inner"], services).ConfigureAwait(false)).Should().Be(0); });
		(await app.RunAsync(["outer"])).Should().Be(0);
		calls.Should().Be(1);
	}

	public sealed class Settings { public int Count { get; set; } = 1; }

	[TestMethod]
	[DataRow(false, false)]
	[DataRow(false, true)]
	[DataRow(true, false)]
	[DataRow(true, true)]
	[Description("Terminal rendering failures become diagnosed exit outcomes in preparation and runtime, including tuple payloads.")]
	public async Task When_ATerminalTransformerThrows_Then_StartupReturnsAFailure(bool preparation, bool tuple)
	{
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		var resolutions = 0;
		void Configure(ReplOptions options)
		{
			options.Output.AddTransformer("broken", new CallbackTransformer((_, _) => throw new InvalidOperationException("renderer failed")));
			options.ExitCodes.Resolver = context => { resolutions++; return context.ExitCode; };
		}
		ArgumentPreprocessingResult Handle() => ArgumentPreprocessingResult.Handled(tuple ? ("first", "second") : "payload");
		int code;
		if (preparation)
		{
			var prepared = await ReplStartup.CreateBuilder().Options(Configure).UsePreparationHook(Handle).PrepareAsync(["--output:broken"]);
			code = await prepared.CompleteAsync();
		}
		else
		{
			var app = CoreReplApp.Create().Options(Configure);
			app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(Handle()));
			app.UseStartupHook(() => ArgumentPreprocessingResult.Continue());
			code = await app.RunAsync(["--output:broken"]);
		}
		code.Should().Be(1);
		resolutions.Should().Be(1);
		writer.ToString().Should().Contain("renderer failed");
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("A completed adapter replacement survives a later adapter failure, preserving the original input and JSON diagnostics.")]
	public async Task When_AnAdapterFailsAfterARewrite_Then_StartupRetainsItsEffectiveInput(bool preparation)
	{
		var calls = 0;
		ValueTask<ArgumentPreprocessingResult> RewriteAsync(ArgumentPreprocessingContext input, CancellationToken token)
		{
			calls++;
			return ValueTask.FromResult(ArgumentPreprocessingResult.Replace(["query", "--output:json"]));
		}
		ValueTask<ArgumentPreprocessingResult> FailAsync(ArgumentPreprocessingContext input, CancellationToken token)
		{
			calls++;
			throw new InvalidOperationException("adapter failed");
		}
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		if (preparation)
		{
			var prepared = await ReplStartup.CreateBuilder().UseArgumentPreprocessor(RewriteAsync).UseArgumentPreprocessor(FailAsync).PrepareAsync(["original"]);
			prepared.OriginalArguments.Should().Equal("original");
			prepared.Arguments.Should().Equal("query", "--output:json");
			(await prepared.CompleteAsync()).Should().Be(1);
		}
		else
		{
			var prepared = await ReplStartup.CreateBuilder().PrepareAsync(["original"]);
			var app = CoreReplApp.Create(prepared).UseArgumentPreprocessor(RewriteAsync).UseArgumentPreprocessor(FailAsync);
			(await app.RunAsync(prepared)).Should().Be(1);
		}
		calls.Should().Be(2);
		using var document = JsonDocument.Parse(writer.ToString());
		document.RootElement.ToString().Should().Contain("startup_error").And.Contain("adapter failed");
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("Terminal success and failure renderers observe this invocation's globals, including omitted values after an earlier run.")]
	public async Task When_TerminalRunsAreReused_Then_RenderingUsesTheirEffectiveGlobals(bool failure)
	{
		IGlobalOptionsAccessor? accessor = null;
		var app = CoreReplApp.Create().UseGlobalOptions<Settings>().Options(options =>
			options.Output.AddTransformer("globals", new CallbackTransformer((_, _) => ValueTask.FromResult($"count={accessor?.GetValue<int>("count")}"))));
		app.Map("seed", (IGlobalOptionsAccessor globals) => { accessor = globals; });
		app.UseStartupHook((ReplStartupContext context) =>
		{
			if (context.RemainingArguments is ["seed"]) { return ArgumentPreprocessingResult.Continue(); }
			if (failure) { throw new InvalidOperationException("startup failed"); }
			return ArgumentPreprocessingResult.Handled("payload");
		});
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		(await app.RunAsync(["seed", "--count", "3"])).Should().Be(0);
		writer.GetStringBuilder().Clear();
		(await app.RunAsync(["--count", "7", "--output:globals"])).Should().Be(failure ? 1 : 0);
		writer.ToString().Should().Contain("count=7");
		writer.GetStringBuilder().Clear();
		(await app.RunAsync(["--output:globals"])).Should().Be(failure ? 1 : 0);
		writer.ToString().Should().Contain("count=1");
	}

	[TestMethod]
	[DataRow(false, true, false)]
	[DataRow(false, true, true)]
	[DataRow(false, false, true)]
	[DataRow(true, true, false)]
	[DataRow(true, true, true)]
	[DataRow(true, false, true)]
	[Description("Terminal rendering preserves caller cancellation and its configured exit mapping; unrelated cancellation remains an application failure.")]
	public async Task When_ATerminalRendererCancels_Then_TheCallerPolicyIsPreserved(bool preparation, bool callerCancellation, bool mapCancellation)
	{
		using var cancellation = new CancellationTokenSource();
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		void Configure(ReplOptions options)
		{
			if (mapCancellation) { options.ExitCodes.Cancelled = 130; }
			options.Output.AddTransformer("cancel", new CallbackTransformer(async (_, token) =>
			{
				if (callerCancellation) { await cancellation.CancelAsync().ConfigureAwait(false); token.ThrowIfCancellationRequested(); }
				throw new OperationCanceledException("renderer cancelled itself");
			}));
		}
		ArgumentPreprocessingResult Handle() => ArgumentPreprocessingResult.Handled("payload");
		Func<Task<int>> run;
		if (preparation)
		{
			var prepared = await ReplStartup.CreateBuilder().Options(Configure).UsePreparationHook(Handle).PrepareAsync(["--output:cancel"]);
			run = () => prepared.CompleteAsync(cancellation.Token).AsTask();
		}
		else
		{
			var app = CoreReplApp.Create().Options(Configure).UseStartupHook(Handle);
			run = () => app.RunAsync(["--output:cancel"], cancellation.Token).AsTask();
		}
		if (callerCancellation && !mapCancellation) { await run.Should().ThrowAsync<OperationCanceledException>(); }
		else { (await run()).Should().Be(callerCancellation ? 130 : 1); }
	}

	[TestMethod]
	[Description("Overlapping terminal renderers observe only their own invocation globals across asynchronous rendering.")]
	public async Task When_TerminalRenderersOverlap_Then_TheirGlobalFramesRemainIndependent()
	{
		var builder = ReplStartup.CreateBuilder().UseGlobalOptions<Settings>();
		var first = await builder.PrepareAsync(["--count", "3", "--output:globals"]);
		var second = await builder.PrepareAsync(["--count", "7", "--output:globals"]);
		var app = CoreReplApp.Create(first);
		var arrived = 0;
		var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var observed = new System.Collections.Concurrent.ConcurrentBag<int>();
		app.Options(options => options.Output.AddTransformer("globals", new CallbackTransformer(async (value, token) =>
		{
			if (Interlocked.Increment(ref arrived) == 2) { barrier.TrySetResult(); }
			await barrier.Task.WaitAsync(token).ConfigureAwait(false);
			var actual = app.GlobalOptionsAccessor.GetValue<int>("count");
			actual.Should().Be((int)value!);
			observed.Add(actual);
			return string.Empty;
		})));
		app.UseStartupHook((Settings settings) => ArgumentPreprocessingResult.Handled(settings.Count));
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var writer = new StringWriter();
		using var io = ReplSessionIO.SetSession(writer, TextReader.Null);
		var codes = await Task.WhenAll(app.RunAsync(first, cancellation.Token).AsTask(), app.RunAsync(second, cancellation.Token).AsTask());
		codes.Should().Equal(0, 0);
		observed.Should().BeEquivalentTo([3, 7]);
	}

	private sealed class CallbackTransformer(Func<object?, CancellationToken, ValueTask<string>> transform) : IOutputTransformer
	{
		public string Name => "test";
		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default) => transform(value, cancellationToken);
	}
	private sealed class Dependency { public int Count { get; set; } }
	private sealed class HostedGlobals(IGlobalOptionsAccessor globals, List<int> seen) : IHostedService
	{
		public Task StartAsync(CancellationToken cancellationToken) { seen.Add(globals.GetValue<int>("count")); return Task.CompletedTask; }
		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
