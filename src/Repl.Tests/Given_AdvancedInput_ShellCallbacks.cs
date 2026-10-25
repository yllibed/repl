using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Repl.Tests;

[TestClass]
public sealed class Given_AdvancedInput_ShellCallbacks
{
	[TestMethod]
	[DataRow("adapter")]
	[DataRow("validator")]
	[DataRow("service")]
	[DataRow("cancellation")]
	[Description("Callback failures remain observable on stderr while shell protocol stdout stays empty, including cancellation not requested by the caller.")]
	public async Task When_ACallbackFails_Then_OnlyTheErrorStreamReceivesADiagnostic(string failure)
	{
		var app = ReplApp.Create();
		app.Map("query", () => "unused");
		if (failure is "adapter" or "cancellation")
		{
			app.UseArgumentPreprocessor((_, _) => throw failure switch
			{
				"cancellation" => new OperationCanceledException("unrelated cancellation"),
				_ => new InvalidOperationException("adapter failure"),
			});
		}
		else if (failure is "service")
		{
			app.UseInputValidator((ParsedInputContext _, [FromServices] MissingDependency dependency) => InputValidationResult.Valid());
		}
		else { app.UseInputValidator(InputValidationResult (ParsedInputContext _) => throw new InvalidOperationException("validator failure")); }
		using var output = new StringWriter();
		using var error = new StringWriter();
		using var io = ReplSessionIO.SetSession(output, TextReader.Null, error: error);
		var candidates = await CompleteAsync(app.Core, app.Services);
		candidates.Should().BeEmpty();
		output.ToString().Should().BeEmpty();
		error.ToString().Should().Contain("Shell completion input callbacks failed:");
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("The shared callback deadline bounds synchronous and asynchronous stalls even when callback code ignores cancellation.")]
	public async Task When_AnAdapterStalls_Then_TheInjectedClockEndsCompletion(bool synchronous)
	{
		var clock = new DeadlineClock();
		var app = ReplApp.Create(services => services.AddSingleton<TimeProvider>(clock));
		app.Options(options => options.ShellCompletion.ProviderTimeout = TimeSpan.FromSeconds(1));
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<ArgumentPreprocessingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var syncRelease = new ManualResetEventSlim();
		CancellationToken callbackToken = default;
		app.UseArgumentPreprocessor((_, token) =>
		{
			callbackToken = token;
			started.TrySetResult();
			if (!synchronous) { return new(release.Task); }
			try
			{
				// Deliberately block before returning a ValueTask to exercise invocation isolation.
				syncRelease.Wait(CancellationToken.None);
				throw new InvalidOperationException("late callback fault");
			}
			finally { finished.TrySetResult(); }
		});
		using var output = new StringWriter();
		using var error = new StringWriter();
		using var io = ReplSessionIO.SetSession(output, TextReader.Null, error: error);
		var completion = CompleteAsync(app.Core, app.Services);
		try
		{
			await Task.WhenAll(started.Task, clock.Armed.Task).WaitAsync(TimeSpan.FromSeconds(5));
			clock.Advance(TimeSpan.FromSeconds(2));
			(await completion.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeEmpty();
			error.ToString().Should().Contain("timed out");
			output.ToString().Should().BeEmpty();
			await WaitForCancellationAsync(callbackToken);
		}
		finally
		{
			if (synchronous)
			{
				syncRelease.Set();
				await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
			}
			else { release.TrySetResult(ArgumentPreprocessingResult.Continue()); }
		}
	}

	[TestMethod]
	[Description("Adapter and validator work share one input budget; validation must not reset the deadline after a slow adapter.")]
	public async Task When_ValidationStallsAfterAdaptation_Then_ItUsesTheRemainingBudget()
	{
		var clock = new DeadlineClock();
		var app = ReplApp.Create(services => services.AddSingleton<TimeProvider>(clock));
		var releaseAdapter = new TaskCompletionSource<ArgumentPreprocessingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		var validatorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseValidator = new TaskCompletionSource<InputValidationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		app.Map("query", () => "unused");
		app.UseArgumentPreprocessor((_, _) => new(releaseAdapter.Task));
		app.UseInputValidator((ParsedInputContext _) =>
		{
			validatorStarted.TrySetResult();
			return new ValueTask<InputValidationResult>(releaseValidator.Task);
		});
		using var error = new StringWriter();
		using var io = ReplSessionIO.SetSession(TextWriter.Null, TextReader.Null, error: error);
		var completion = CompleteAsync(app.Core, app.Services);
		try
		{
			await clock.Armed.Task.WaitAsync(TimeSpan.FromSeconds(5));
			clock.Advance(TimeSpan.FromMilliseconds(750));
			releaseAdapter.SetResult(ArgumentPreprocessingResult.Continue());
			await validatorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			clock.Advance(TimeSpan.FromMilliseconds(251));
			(await completion.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeEmpty();
			error.ToString().Should().Contain("timed out");
		}
		finally
		{
			releaseAdapter.TrySetResult(ArgumentPreprocessingResult.Continue());
			releaseValidator.TrySetResult(InputValidationResult.Valid());
		}
	}

	[TestMethod]
	[Description("A caller-cancelled completion propagates cancellation rather than reporting an application callback failure.")]
	public async Task When_TheCallerCancels_Then_CancellationPropagatesWithoutADiagnostic()
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(ArgumentPreprocessingResult.Continue()));
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();
		using var error = new StringWriter();
		using var io = ReplSessionIO.SetSession(TextWriter.Null, TextReader.Null, error: error);
		Func<Task> completing = () => CompleteAsync(app, app.CurrentServiceProvider, cancellation.Token);
		await completing.Should().ThrowAsync<OperationCanceledException>();
		error.ToString().Should().BeEmpty();
	}

	[TestMethod]
	[Description("Handled completion input is terminal and must not construct validator services or offer canonical suggestions.")]
	public async Task When_AnAdapterHandlesCompletion_Then_ValidatorsDoNotRun()
	{
		var app = CoreReplApp.Create();
		app.UseArgumentPreprocessor((_, _) => ValueTask.FromResult(ArgumentPreprocessingResult.Handled()));
		app.UseInputValidator(InputValidationResult (ParsedInputContext _) => throw new InvalidOperationException("must not run"));
		using var error = new StringWriter();
		using var io = ReplSessionIO.SetSession(TextWriter.Null, TextReader.Null, error: error);
		(await CompleteAsync(app, app.CurrentServiceProvider)).Should().BeEmpty();
		error.ToString().Should().BeEmpty();
	}

	private static Task<string[]> CompleteAsync(CoreReplApp app, IServiceProvider services, CancellationToken cancellationToken = default) =>
		new ShellCompletionEngine(app).ResolveShellCompletionCandidatesAsync("app query", 9, ShellKind.Bash, services, cancellationToken).AsTask();

	private static async Task WaitForCancellationAsync(CancellationToken token)
	{
		var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = token.Register(() => cancelled.TrySetResult());
		await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
	}

	private sealed class MissingDependency;

	private sealed class DeadlineClock : TimeProvider
	{
		private readonly FakeTimeProvider _clock = new();
		public TaskCompletionSource Armed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			var timer = _clock.CreateTimer(callback, state, dueTime, period);
			Armed.TrySetResult();
			return timer;
		}
		public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);
	}
}
