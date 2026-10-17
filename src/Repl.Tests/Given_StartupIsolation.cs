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
	private sealed class Dependency { public int Count { get; set; } }
	private sealed class HostedGlobals(IGlobalOptionsAccessor globals, List<int> seen) : IHostedService
	{
		public Task StartAsync(CancellationToken cancellationToken) { seen.Add(globals.GetValue<int>("count")); return Task.CompletedTask; }
		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
