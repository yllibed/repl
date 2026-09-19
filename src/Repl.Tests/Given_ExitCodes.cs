using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Repl.Tests;

[TestClass]
public sealed class Given_ExitCodes
{
	[TestMethod]
	[Description("Regression guard: verifies a text-returning handler is classified Success so that the process exits 0.")]
	public void When_HandlerReturnsText_Then_KindIsSuccessAndExitCodeIsZero()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("hello", () => "world");

		var exitCode = Run(sut, ["hello"], out _);

		exitCode.Should().Be(0);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.Success);
	}

	[TestMethod]
	[Description("Regression guard: verifies --help is classified Help so that scripted callers can tell a no-op from real work.")]
	public void When_HelpIsRequested_Then_KindIsHelpAndExitCodeIsZero()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("hello", () => "world");

		var exitCode = Run(sut, ["--help"], out _);

		exitCode.Should().Be(0);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.Help);
	}

	[TestMethod]
	[Description("Regression guard: verifies a bare invocation that prints help is classified Help and keeps exiting 0 by default.")]
	public void When_BareInvocationPrintsHelp_Then_KindIsHelpAndExitCodeIsZero()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("hello", () => "world");

		var exitCode = Run(sut, [], out var output);

		exitCode.Should().Be(0);
		output.Should().Contain("hello");
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.Help);
	}

	[TestMethod]
	[Description("Regression guard: verifies the Help code is configurable so that a bare invocation can fail a CI step that ran the tool with no arguments.")]
	public void When_HelpIsMappedToNonZero_Then_BareInvocationReturnsMappedCode()
	{
		var sut = CreateApp(recorder: null, options => options.ExitCodes.Help = 64);
		sut.Map("hello", () => "world");

		var exitCode = Run(sut, [], out _);

		exitCode.Should().Be(64);
	}

	[TestMethod]
	[DataRow("unknown command", new[] { "nope" })]
	[DataRow("ambiguous prefix", new[] { "contact", "l" })]
	[DataRow("unknown command option", new[] { "hello", "--bogus", "x" })]
	[DataRow("global option missing its value", new[] { "hello", "--output" })]
	[DataRow("unknown output format", new[] { "hello", "--output:toml" })]
	[Description("Regression guard: verifies every framework refusal is a UsageError with exit code 2 so that misuse stays distinguishable from a handler failure.")]
	public void When_InvocationIsRefused_Then_KindIsUsageErrorAndExitCodeIsTwo(string refusal, string[] args)
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("hello", (string? name) => name ?? "world");
		sut.Map("contact list", () => "list");
		sut.Map("contact load", () => "load");

		var exitCode = Run(sut, args, out _);

		exitCode.Should().Be(2, refusal);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.UsageError, refusal);
	}

	[TestMethod]
	[Description("Regression guard: verifies a routing refusal hands the rendered refusal result to the resolver so that a consumer can map on the framework's own diagnostic instead of parsing text.")]
	public void When_CommandIsUnknown_Then_OutcomeCarriesTheRenderedRefusal()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("hello", () => "world");

		_ = Run(sut, ["nope"], out _);

		recorder.Last!.Result.Should().BeAssignableTo<IReplResult>();
	}

	[TestMethod]
	[Description("Regression guard: verifies a missing required parameter is a BindingError with exit code 2 so that binding failures are distinct from handler failures.")]
	public void When_RequiredParameterIsMissing_Then_KindIsBindingErrorAndExitCodeIsTwo()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("set", (int value) => value);

		var exitCode = Run(sut, ["set"], out _);

		exitCode.Should().Be(2);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.BindingError);
		recorder.Last.Exception.Should().NotBeNull();
	}

	[TestMethod]
	[Description("Regression guard: verifies a parameter conversion failure is a BindingError so that invalid values report the binding code.")]
	public void When_ParameterConversionFails_Then_KindIsBindingError()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("set", (int value) => value);

		var exitCode = Run(sut, ["set", "abc"], out _);

		exitCode.Should().Be(2);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.BindingError);
	}

	[TestMethod]
	[DataRow("error")]
	[DataRow("validation")]
	[DataRow("not_found")]
	[DataRow("cancelled")]
	[Description("Regression guard: verifies handler-returned failure results are HandlerError with exit code 1 so that existing handler contracts keep their code.")]
	public void When_HandlerReturnsFailureResult_Then_KindIsHandlerErrorAndExitCodeIsOne(string kind)
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		// Every row named explicitly: a fall-through arm would let a mistyped [DataRow] pass while
		// asserting a different result kind.
		sut.Map("fail", () => kind switch
		{
			"error" => Results.Error("boom", "failed"),
			"validation" => Results.Validation("invalid"),
			"not_found" => Results.NotFound("missing"),
			"cancelled" => Results.Cancelled("stopped"),
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unmapped DataRow"),
		});

		var exitCode = Run(sut, ["fail"], out _);

		exitCode.Should().Be(1);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerError);
		recorder.Last.Result.Should().BeAssignableTo<IReplResult>();
	}

	[TestMethod]
	[Description("Regression guard: verifies an explicit IExitResult bypasses the table so that handler-owned codes are never remapped silently.")]
	public void When_HandlerReturnsExitResult_Then_CodePassesThroughAndKindIsHandlerExitCode()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder, options => options.ExitCodes.HandlerError = 7);
		sut.Map("quit", () => Results.Exit(42));

		var exitCode = Run(sut, ["quit"], out _);

		exitCode.Should().Be(42);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerExitCode);
		recorder.Last.ExitCode.Should().Be(42);
	}

	[TestMethod]
	[Description("Regression guard: verifies an unhandled handler exception is HandlerException and exposes the unwrapped exception to the resolver.")]
	public void When_HandlerThrows_Then_KindIsHandlerExceptionAndExceptionIsExposed()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("boom", Boom);

		var exitCode = Run(sut, ["boom"], out _);

		exitCode.Should().Be(1);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
		recorder.Last.Exception!.Message.Should().Be("boom");

		static string Boom() => throw new FormatException("boom");
	}

	[TestMethod]
	[Description("Regression guard: verifies a handler-thrown InvalidOperationException is HandlerException, not BindingError, so that binder failures stay distinguishable.")]
	public void When_HandlerThrowsInvalidOperationException_Then_KindIsHandlerExceptionNotBindingError()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder, options => options.ExitCodes.BindingError = 9);
		sut.Map("boom", Boom);

		var exitCode = Run(sut, ["boom"], out var output);

		exitCode.Should().Be(1);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
		output.Should().Contain("boom");

		static string Boom() => throw new InvalidOperationException("boom");
	}

	[TestMethod]
	[Description("Regression guard: verifies cancellation still propagates as an exception when an application asked for neither a Cancelled code nor a resolver, so existing callers keep their contract.")]
	public async Task When_TokenIsCancelledAndNoCancellationPolicyIsSet_Then_OperationCanceledExceptionPropagates()
	{
		using var cts = new CancellationTokenSource();
		var sut = CreateApp(recorder: null);
		sut.Map("work", (CancellationToken ct) =>
		{
			cts.Cancel();
			ct.ThrowIfCancellationRequested();
			return "unreachable";
		});
		using var session = OpenSession(out _);

		var act = async () => await sut.RunAsync(["work"], cts.Token).ConfigureAwait(false);

		await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Regression guard: verifies a resolver alone makes cancellation observable so that the single interception point issue #81 asks for covers every final outcome, not only the ones with a table entry.")]
	public async Task When_TokenIsCancelledAndOnlyResolverIsSet_Then_ResolverSeesCancelledOutcome()
	{
		using var cts = new CancellationTokenSource();
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("work", (CancellationToken ct) =>
		{
			cts.Cancel();
			ct.ThrowIfCancellationRequested();
			return "unreachable";
		});
		using var session = OpenSession(out _);

		var exitCode = await sut.RunAsync(["work"], cts.Token).ConfigureAwait(false);

		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.Cancelled);
		recorder.Last.Exception.Should().BeAssignableTo<OperationCanceledException>();

		// The literal, not recorder.Last.ExitCode: the recorder returns the code it was handed, so
		// comparing the two proves only that the resolver ran. 130 is the pre-resolver code a
		// resolver-only application is handed for a cancellation.
		recorder.Last.ExitCode.Should().Be(130);
		exitCode.Should().Be(130);
	}

	[TestMethod]
	[Description("Regression guard: verifies the exit ambient command handled in one-shot mode is classified Success so that a CI-oriented Help mapping never marks it as failed.")]
	public void When_ExitAmbientCommandRunsInOneShotMode_Then_KindIsSuccess()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder, options => options.ExitCodes.Help = 3);
		sut.Map("hello", () => "world");

		var exitCode = Run(sut, ["exit"], out _);

		exitCode.Should().Be(0);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.Success);
	}

	[TestMethod]
	[Description("Regression guard: verifies an exception thrown by a middleware is HandlerException so that pipeline failures share the handler-failure code.")]
	public void When_MiddlewareThrows_Then_KindIsHandlerException()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Use((_, _) => throw new FormatException("middleware boom"));
		sut.Map("hello", () => "world");

		var exitCode = Run(sut, ["hello"], out _);

		exitCode.Should().Be(1);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
		recorder.Last.Exception!.Message.Should().Be("middleware boom");
	}

	[TestMethod]
	[Description("Regression guard: verifies a handler that cancels itself in one-shot mode is a HandlerException with a rendered message, so a mapped Cancelled code cannot make a real failure look like an operator abort.")]
	public void When_HandlerThrowsOperationCanceledWithoutCallerCancellation_Then_KindIsHandlerExceptionAndErrorIsRendered()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder, options => options.ExitCodes.Cancelled = 130);
		sut.Map("boom", string () => throw new OperationCanceledException("handler gave up"));

		var exitCode = Run(sut, ["boom"], out var output);

		exitCode.Should().Be(1);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
		output.Should().Contain("handler gave up");
	}

	[TestMethod]
	[Description("Regression guard: verifies mapped cancellation returns the configured code and Kind Cancelled so that headless tools get an integer for cancellation.")]
	public async Task When_TokenIsCancelledAndCancelledIsMapped_Then_ExitCodeIs130AndKindIsCancelled()
	{
		using var cts = new CancellationTokenSource();
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder, options => options.ExitCodes.Cancelled = 130);
		sut.Map("work", (CancellationToken ct) =>
		{
			cts.Cancel();
			ct.ThrowIfCancellationRequested();
			return "unreachable";
		});
		using var session = OpenSession(out _);

		var exitCode = await sut.RunAsync(["work"], cts.Token).ConfigureAwait(false);

		exitCode.Should().Be(130);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.Cancelled);
		recorder.Last.Exception.Should().BeAssignableTo<OperationCanceledException>();
	}

	[TestMethod]
	[Description("Regression guard: verifies a token cancelled before the run is subject to the same Cancelled mapping so that early cancellation is not a special case.")]
	public async Task When_PreCancelledTokenAndCancelledIsMapped_Then_ExitCodeIs130()
	{
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync().ConfigureAwait(false);
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder, options => options.ExitCodes.Cancelled = 130);
		sut.Map("work", () => "never");
		using var session = OpenSession(out _);

		var exitCode = await sut.RunAsync(["work"], cts.Token).ConfigureAwait(false);

		exitCode.Should().Be(130);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.Cancelled);
	}

	[TestMethod]
	[Description("Regression guard: verifies the UsageError code is configurable so that applications can publish their own exit-code contract.")]
	public void When_UsageErrorIsRemapped_Then_ConfiguredCodeIsReturned()
	{
		var sut = CreateApp(recorder: null, options => options.ExitCodes.UsageError = 64);
		sut.Map("hello", () => "world");

		var exitCode = Run(sut, ["nope"], out _);

		exitCode.Should().Be(64);
	}

	[TestMethod]
	[Description("Regression guard: verifies the resolver receives the table-mapped code and that its return value is final.")]
	public void When_ResolverIsSet_Then_ItReceivesMappedCodeAndItsReturnWins()
	{
		var seen = new List<int>();
		var sut = CreateApp(recorder: null, options => options.ExitCodes.Resolver = outcome =>
		{
			seen.Add(outcome.ExitCode);
			return outcome.ExitCode + 10;
		});
		sut.Map("hello", () => "world");

		var exitCode = Run(sut, ["nope"], out _);

		exitCode.Should().Be(12);
		seen.Should().Equal(2);
	}

	[TestMethod]
	[Description("Regression guard: verifies the resolver can override an explicit IExitResult so that one interception point governs every final outcome.")]
	public void When_ResolverSeesExitResult_Then_ItCanOverrideIt()
	{
		var sut = CreateApp(recorder: null, options => options.ExitCodes.Resolver = outcome =>
			outcome.Kind == ReplExecutionOutcomeKind.HandlerExitCode ? 99 : outcome.ExitCode);
		sut.Map("quit", () => Results.Exit(5));

		var exitCode = Run(sut, ["quit"], out _);

		exitCode.Should().Be(99);
	}

	[TestMethod]
	[Description("Regression guard: verifies a dependency the binder cannot resolve is a BindingError so that the classification of service-resolution failures is deliberate, not incidental.")]
	public void When_FromServicesDependencyIsMissing_Then_KindIsBindingError()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("show", ([FromServices] IMissingDependency dependency) => dependency.ToString());

		var exitCode = Run(sut, ["show"], out _);

		exitCode.Should().Be(2);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.BindingError);
	}

	[TestMethod]
	[Description("Regression guard: verifies a service factory that cancels during binding is a BindingError, not a HandlerException: the handler never ran, so the binding policy must still apply.")]
	public void When_ServiceFactoryCancelsDuringBinding_Then_KindIsBindingError()
	{
		var recorder = new OutcomeRecorder();
		var sut = ReplApp.Create(services =>
			services.AddSingleton<IMissingDependency>(_ => throw new OperationCanceledException("factory gave up")));
		sut.Options(options =>
		{
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.BannerEnabled = false;
			options.ExitCodes.BindingError = 9;
			options.ExitCodes.Resolver = recorder.Record;
		});
		sut.Map("show", ([FromServices] IMissingDependency dependency) => dependency.ToString());

		var exitCode = Run(sut, ["show"], out _);

		exitCode.Should().Be(9);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.BindingError);
	}

	[TestMethod]
	[Description("Regression guard: verifies a binding refusal hands the rendered result to the resolver alongside the exception, so the documented Result contract holds for every refusal and not only routing ones.")]
	public void When_BindingFails_Then_OutcomeCarriesBothTheRenderedResultAndTheException()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("set", (int value) => value);

		_ = Run(sut, ["set"], out _);

		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.BindingError);
		recorder.Last.Result.Should().BeAssignableTo<IReplResult>()
			.Which.Kind.Should().Be("validation");
		recorder.Last.Exception.Should().NotBeNull();
	}

	[TestMethod]
	[Description("Regression guard: verifies every outcome kind maps to its own configured entry so that swapping two arms of the table, or adding a kind without an entry, cannot pass unnoticed.")]
	public void When_EveryKindIsMapped_Then_EachKindReturnsItsOwnConfiguredCode()
	{
		var table = new ExitCodeOptions
		{
			Success = 10,
			Help = 11,
			UsageError = 12,
			BindingError = 13,
			HandlerError = 14,
			HandlerException = 15,
			Cancelled = 16,
			Interrupted = 17,
			FrameworkError = 18,
		};
		const int carried = 99;

		// Spelled out rather than derived from the value under test: this table IS the assertion, so a
		// reordered switch arm in ExitCodeOptions.Map has to disagree with it.
		var expectedByKind = new Dictionary<ReplExecutionOutcomeKind, int>
		{
			[ReplExecutionOutcomeKind.Success] = 10,
			[ReplExecutionOutcomeKind.Help] = 11,
			[ReplExecutionOutcomeKind.UsageError] = 12,
			[ReplExecutionOutcomeKind.BindingError] = 13,
			[ReplExecutionOutcomeKind.HandlerError] = 14,
			[ReplExecutionOutcomeKind.HandlerExitCode] = carried,
			[ReplExecutionOutcomeKind.HandlerException] = 15,
			[ReplExecutionOutcomeKind.Cancelled] = 16,
			[ReplExecutionOutcomeKind.Interrupted] = 17,
			[ReplExecutionOutcomeKind.FrameworkError] = 18,
		};

		expectedByKind.Keys.Should().BeEquivalentTo(
			Enum.GetValues<ReplExecutionOutcomeKind>(),
			"a new kind must be given an expected code here before it can ship");

		foreach (var (kind, expected) in expectedByKind)
		{
			table.Map(kind, carried).Should().Be(expected, $"{kind} must map through its own arm");
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies Cancelled and Interrupted keep the conventional code the outcome carries and otherwise fall back to 130, never to the framework-error code, so an aborted run stays distinguishable from a broken one.")]
	public void When_CancellationCodesAreUnset_Then_TheCarriedConventionalCodeIsUsedElseOneThirty()
	{
		var table = new ExitCodeOptions();

		table.Map(ReplExecutionOutcomeKind.Cancelled, carriedExitCode: 130).Should().Be(130);
		table.Map(ReplExecutionOutcomeKind.Interrupted, carriedExitCode: 143).Should().Be(143);

		// The fallback both kinds share. Asserted as the literal, not as a reference to the constant
		// under test, so renumbering it has to disagree with this line.
		table.Map(ReplExecutionOutcomeKind.Cancelled, carriedExitCode: null).Should().Be(130);
		table.Map(ReplExecutionOutcomeKind.Interrupted, carriedExitCode: null).Should().Be(130);
		table.FrameworkError.Should().Be(1, "the fallback must not collide with a handler failure");
	}

	[TestMethod]
	[Description("Regression guard: verifies a handler returning an int renders it as data and exits 0 so that scalar results never become exit codes.")]
	public void When_HandlerReturnsInt_Then_ValueIsRenderedAndKindIsSuccess()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("count", () => 3);

		var exitCode = Run(sut, ["count"], out var output);

		exitCode.Should().Be(0);
		output.Should().Contain("3");
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.Success);
		recorder.Last.Result.Should().Be(3);
	}

	[TestMethod]
	[Description("Regression guard: verifies the last tuple element decides the outcome so that tuple rendering follows the single-result rules.")]
	public void When_LastTupleElementIsError_Then_KindIsHandlerErrorAndResultIsLastElement()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("pair", () => ("first", Results.Error("boom", "failed")));

		var exitCode = Run(sut, ["pair"], out _);

		exitCode.Should().Be(1);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerError);
		recorder.Last.Result.Should().BeAssignableTo<IReplResult>()
			.Which.Kind.Should().Be("error");
	}

	[TestMethod]
	[Description("Regression guard: verifies middleware can observe the handler result after next() so that cross-cutting concerns can inspect outcomes.")]
	public void When_MiddlewareObservesResultAfterNext_Then_ContextResultHoldsHandlerReturn()
	{
		object? observed = null;
		var sut = CreateApp(recorder: null);
		sut.Use(async (context, next) =>
		{
			await next().ConfigureAwait(false);
			observed = context.Result;
		});
		sut.Map("hello", () => "world");

		var exitCode = Run(sut, ["hello"], out _);

		exitCode.Should().Be(0);
		observed.Should().Be("world");
	}

	[TestMethod]
	[Description("Regression guard: verifies a middleware-replaced result is rendered and classified so that middleware can transform outcomes.")]
	public void When_MiddlewareReplacesResultAfterNext_Then_ReplacementIsRenderedAndClassified()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Use(async (context, next) =>
		{
			await next().ConfigureAwait(false);
			context.Result = Results.Error("replaced", "middleware failed it");
		});
		sut.Map("hello", () => "world");

		var exitCode = Run(sut, ["hello"], out var output);

		exitCode.Should().Be(1);
		output.Should().Contain("middleware failed it");
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerError);
	}

	[TestMethod]
	[Description("Regression guard: verifies a short-circuiting middleware can supply a result so that it is rendered in place of the handler's.")]
	public void When_MiddlewareShortCircuitsAndSetsResult_Then_ResultIsRendered()
	{
		var handlerCalled = false;
		var sut = CreateApp(recorder: null);
		sut.Use((context, _) =>
		{
			context.Result = "from-middleware";
			return ValueTask.CompletedTask;
		});
		sut.Map("hello", () =>
		{
			handlerCalled = true;
			return "world";
		});

		var exitCode = Run(sut, ["hello"], out var output);

		exitCode.Should().Be(0);
		handlerCalled.Should().BeFalse();
		output.Should().Contain("from-middleware");
	}

	[TestMethod]
	[Description("Regression guard: verifies an ambiguous-prefix refusal survives a throwing output transformer. Every framework refusal is rendered through the requested format, so an application transformer that fails used to escape the pipeline from a site outside any catch, leaving the run with no classified outcome.")]
	public void When_AnAmbiguousPrefixRefusalCannotBeRendered_Then_KindIsStillUsageError()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder, options => options.Output.AddTransformer("broken", new ThrowingTransformer()));
		sut.Map("contact list", () => "list");
		sut.Map("contact load", () => "load");
		using var session = OpenSession(out _);

		var exitCode = sut.Run(["contact", "l", "--output:broken"]);

		exitCode.Should().Be(2);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.UsageError);
	}

	[TestMethod]
	[Description("Regression guard: verifies an option-parse refusal survives a throwing output transformer, on the second of the six refusal sites that sat outside any catch before the failure reporter was centralised.")]
	public void When_AnOptionRefusalCannotBeRendered_Then_KindIsStillUsageError()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder, options => options.Output.AddTransformer("broken", new ThrowingTransformer()));
		sut.Map("hello", (string? name) => name ?? "world");
		using var session = OpenSession(out _);

		var exitCode = sut.Run(["hello", "--bogus", "x", "--output:broken"]);

		exitCode.Should().Be(2);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.UsageError);
	}

	[TestMethod]
	[Description("Regression guard: verifies a scoped-context invocation that does not enter interactive mode rejects an unknown --output format. It writes human help directly, the sibling of the bare-invocation path, and used to report Help with the format never mentioned.")]
	public void When_ScopedContextHelpRequestsAnUnknownFormat_Then_KindIsUsageError()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Context("contact", contact => contact.Map("list", () => "list"));
		using var session = OpenSplitSession(out var output, out var error);

		var exitCode = sut.Run(["contact", "--output:toml"]);

		exitCode.Should().Be(2);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.UsageError);
		error.ToString().Should().Contain("unknown output format 'toml'");
		output.ToString().Should().NotContain("list", "the scoped help must not be printed as if the format were accepted");
	}

	[TestMethod]
	[Description("Regression guard: verifies a transformer that raises OperationCanceledException on its own account, with the caller token untouched, is treated as a failing transformer rather than escaping. The cancellation policy cannot convert it, so letting it through left the run with no outcome at all.")]
	public void When_TheTransformerRaisesCancellationItself_Then_TheRunIsStillClassified()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder, options => options.Output.AddTransformer("selfcancel", new SelfCancellingTransformer()));
		sut.Map("work", () => "payload");
		using var session = OpenSplitSession(out _, out var error);

		var exitCode = sut.Run(["work", "--output:selfcancel"]);

		exitCode.Should().Be(1);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
		error.ToString().Should().Contain("the output transformer also failed");
	}

	[TestMethod]
	[Description("Regression guard: verifies a bare non-interactive invocation rejects an unknown --output format instead of printing help and exiting Help. That path writes human help directly, bypassing the renderer that reports the refusal everywhere else.")]
	public void When_BareInvocationRequestsAnUnknownFormat_Then_KindIsUsageError()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("hello", () => "world");
		using var session = OpenSplitSession(out var output, out var error);

		var exitCode = sut.Run(["--output:toml"]);

		exitCode.Should().Be(2);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.UsageError);
		error.ToString().Should().Contain("unknown output format 'toml'");
		output.ToString().Should().NotContain("hello", "the help must not be printed as if the format were accepted");
	}

	[TestMethod]
	[Description("Regression guard: verifies a bare non-interactive invocation still prints human help for a valid --output format, since --output selects a format for a command result and a bare invocation produces none.")]
	public void When_BareInvocationRequestsAKnownFormat_Then_HumanHelpIsStillPrinted()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("hello", () => "world");

		var exitCode = Run(sut, ["--output:json"], out var output);

		exitCode.Should().Be(0);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.Help);
		output.Should().Contain("hello");
	}

	[TestMethod]
	[Description("Regression guard: verifies a cancellation raised while the framework reports a failure propagates to the cancellation policy instead of being reported as a handler failure, so ExitCodes.Cancelled still governs a run that was asked to stop.")]
	public async Task When_TheFallbackRenderIsCancelled_Then_CancellationWins()
	{
		using var cts = new CancellationTokenSource();
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(
			recorder,
			options =>
			{
				options.ExitCodes.Cancelled = 75;
				options.Output.AddTransformer("cancelling", new CancellingTransformer(cts));
			});
		sut.Map("work", () => "payload");
		using var session = OpenSession(out _);

		var exitCode = await sut.RunAsync(["work", "--output:cancelling"], cts.Token).ConfigureAwait(false);

		exitCode.Should().Be(75);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.Cancelled);
	}

	[TestMethod]
	[Description("Regression guard: verifies a HandlerException outcome carries the IReplResult the framework rendered on the handler's behalf, so a resolver can map on the framework's own diagnostic for a thrown failure exactly as it can for a refused one.")]
	public void When_HandlerThrows_Then_OutcomeCarriesTheRenderedFailure()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("boom", string () => throw new FormatException("handler blew up"));

		var exitCode = Run(sut, ["boom"], out _);

		exitCode.Should().Be(1);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);
		recorder.Last.Exception.Should().BeOfType<FormatException>();
		recorder.Last.Result.Should().BeAssignableTo<IReplResult>()
			.Which.Message.Should().Contain("handler blew up");
	}

	[TestMethod]
	[Description("Regression guard: verifies a consistently throwing output transformer cannot escape the pipeline. Reporting a failure re-invokes the very transformer that produced it, so a second throw used to leave the run with no classified outcome and no exit code at all.")]
	public void When_TheOutputTransformerAlsoThrows_Then_TheRunStillReportsHandlerException()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder, options => options.Output.AddTransformer("broken", new ThrowingTransformer()));
		sut.Map("work", () => "payload");
		using var session = OpenSplitSession(out var output, out var error);

		var exitCode = sut.Run(["work", "--output:broken"]);

		exitCode.Should().Be(1);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerException);

		// The caller still learns what happened, unformatted, and stdout stays clean.
		error.ToString().Should().Contain("the output transformer also failed");
		output.ToString().Should().NotContain("the output transformer also failed");
	}

	[TestMethod]
	[Description("Regression guard: verifies the hosted protocol-passthrough refusal is a FrameworkError when reached through the IReplHost facade, which builds its own session rather than inheriting an ambient one.")]
	public void When_ProtocolPassthroughIsRefusedViaReplHost_Then_KindIsFrameworkError()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("mcp start", () => Results.Exit(0))
			.AsProtocolPassthrough();
		using var input = new StringReader(string.Empty);
		using var output = new StringWriter();
		var host = new InMemoryHost(input, output);

		var exitCode = sut.Run(["mcp", "start"], host);

		exitCode.Should().Be(1);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.FrameworkError);
	}

	[TestMethod]
	[Description("Regression guard: verifies a resolver that throws degrades to the table-mapped code and reports itself once on the error stream, so a faulty exit-code hook cannot replace the run's own outcome.")]
	public void When_ResolverThrows_Then_TableCodeIsUsedAndOneDiagnosticIsWritten()
	{
		var sut = CreateApp(recorder: null, options =>
		{
			options.ExitCodes.HandlerError = 9;
			options.ExitCodes.Resolver = _ => throw new InvalidOperationException("resolver boom");
		});
		sut.Map("fail", () => Results.Error("boom", "failed"));
		using var session = OpenSplitSession(out var output, out var error);

		var exitCode = sut.Run(["fail"]);

		exitCode.Should().Be(9);
		error.ToString().Should().Contain("ExitCodes.Resolver threw InvalidOperationException", Exactly.Once());
		error.ToString().Should().Contain("resolver boom");
		output.ToString().Should().NotContain("ExitCodes.Resolver threw");
	}

	[TestMethod]
	[Description("Regression guard: verifies the resolver fallback survives a failing error stream, so reporting a resolver failure cannot itself become the failure that ends the run.")]
	public void When_ResolverThrowsAndTheErrorStreamAlsoThrows_Then_TheTableCodeIsStillReturned()
	{
		var sut = CreateApp(recorder: null, options =>
		{
			options.ExitCodes.HandlerError = 9;
			options.ExitCodes.Resolver = _ => throw new InvalidOperationException("resolver boom");
		});
		sut.Map("fail", () => Results.Error("boom", "failed"));
		using var output = new StringWriter();
		using var error = new ThrowingWriter();
		using var session = ReplSessionIO.SetSession(
			output,
			TextReader.Null,
			commandOutput: output,
			error: error,
			isHostedSession: false);

		var exitCode = sut.Run(["fail"]);

		exitCode.Should().Be(9);
	}

	[TestMethod]
	[DataRow("binding failure", new[] { "set", "--output:toml" })]
	[DataRow("handler exception", new[] { "boom", "--output:toml" })]
	[Description("Regression guard: verifies an unknown output format outranks the failure being reported, so a diagnostic the caller never saw is a usage mistake rather than a silent binding or handler failure.")]
	public void When_AFailureCannotBeRendered_Then_KindIsUsageError(string failure, string[] args)
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("set", (int value) => value);
		sut.Map("boom", string () => throw new FormatException("boom"));

		var exitCode = Run(sut, args, out _);

		exitCode.Should().Be(2, failure);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.UsageError, failure);
	}

	[TestMethod]
	[Description("Regression guard: verifies an EnterInteractive payload that cannot be rendered is a UsageError and does not enter the loop, so a refused output never leaves the caller waiting at a prompt.")]
	public void When_EnterInteractivePayloadCannotBeRendered_Then_KindIsUsageErrorAndTheLoopIsNotEntered()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("shell", () => Results.EnterInteractive(new { Name = "world" }));

		var exitCode = Run(sut, ["shell", "--output:toml"], out _);

		exitCode.Should().Be(2);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.UsageError);
	}

	[TestMethod]
	[Description("Regression guard: verifies a one-shot run reports Scope.Process so that a resolver can tell the process exit code from a per-command shell-integration mark.")]
	public void When_OneShotRunResolves_Then_ScopeIsProcess()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("hello", () => "world");

		_ = Run(sut, ["hello"], out _);

		recorder.Count.Should().Be(1);
		recorder.Last!.Scope.Should().Be(ReplExitCodeScope.Process);
	}

	[TestMethod]
	[Description("Regression guard: verifies an IReplResult carrying an unrecognized kind still fails so that a result the framework cannot classify never reports success to a pipeline.")]
	public void When_ResultKindIsUnrecognized_Then_KindIsHandlerErrorAndExitCodeIsOne()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("odd", () => new ReplResult("mystery", Code: null, Message: "something happened", Details: null));

		var exitCode = Run(sut, ["odd"], out _);

		exitCode.Should().Be(1);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.HandlerError);
	}

	[TestMethod]
	[Description("Regression guard: verifies a configured ExitCodes.Cancelled still outranks the conventional fallback so the 130 default never silently overrides an application's own code.")]
	public async Task When_CancelledIsConfigured_Then_ItWinsOverTheConventionalFallback()
	{
		using var cts = new CancellationTokenSource();
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder, options => options.ExitCodes.Cancelled = 75);
		sut.Map("work", (CancellationToken ct) =>
		{
			cts.Cancel();
			ct.ThrowIfCancellationRequested();
			return "unreachable";
		});
		using var session = OpenSession(out _);

		var exitCode = await sut.RunAsync(["work"], cts.Token).ConfigureAwait(false);

		exitCode.Should().Be(75);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.Cancelled);
	}

	[TestMethod]
	[Description("Regression guard: verifies an unknown --output format that displaces a handler exception still carries that exception on the outcome, so a caller-chosen format cannot erase the cause of a failed run from the resolver.")]
	public void When_HandlerThrowsAndOutputFormatIsUnknown_Then_UsageErrorCarriesTheHandlerException()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("boom", string () => throw new FormatException("handler blew up"));

		var exitCode = Run(sut, ["boom", "--output:toml"], out _);

		exitCode.Should().Be(2);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.UsageError);
		recorder.Last.Exception.Should().BeOfType<FormatException>()
			.Which.Message.Should().Be("handler blew up");

		// The handler's un-rendered failure, not the format refusal: the caller saw neither, and the
		// one worth reporting is the reason the run ended.
		recorder.Last.Result.Should().BeAssignableTo<IReplResult>();
	}

	[TestMethod]
	[Description("Regression guard: verifies the hosted protocol-passthrough refusal is a FrameworkError when the caller saw it, so a hosting-capability mismatch keeps reporting as a framework problem rather than a usage mistake.")]
	public void When_HostedPassthroughLacksIoContextAndRefusalIsRendered_Then_KindIsFrameworkError()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("serve", () => "payload").AsProtocolPassthrough();

		var exitCode = Run(sut, ["serve"], out var output);

		exitCode.Should().Be(1);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.FrameworkError);
		output.Should().Contain("requires a handler parameter of type IReplIoContext");
	}

	[TestMethod]
	[Description("Regression guard: verifies an unknown --output format outranks the hosted passthrough refusal, so the last FrameworkError site that ignored its render result can no longer report a diagnostic the caller never saw.")]
	public void When_HostedPassthroughRefusalCannotBeRendered_Then_KindIsUsageError()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder);
		sut.Map("serve", () => "payload").AsProtocolPassthrough();

		var exitCode = Run(sut, ["serve", "--output:toml"], out var output);

		exitCode.Should().Be(2);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.UsageError);
		output.Should().NotContain("requires a handler parameter of type IReplIoContext");
	}

	[TestMethod]
	[Description("Regression guard: verifies an unrenderable element of a tuple result is a usage error and prevents the interactive transition, so an EnterInteractive earlier in the tuple cannot open a session whose payload was never shown.")]
	public void When_TupleResultCannotBeRendered_Then_KindIsUsageErrorAndInteractiveIsNotEntered()
	{
		var recorder = new OutcomeRecorder();
		var sut = CreateApp(recorder, options => options.Interactive.InteractivePolicy = InteractivePolicy.Auto);
		sut.Map("open", () => (Results.EnterInteractive(), Results.Success("ready")));

		var exitCode = Run(sut, ["open", "--output:toml"], out var output);

		exitCode.Should().Be(2);
		recorder.Last!.Kind.Should().Be(ReplExecutionOutcomeKind.UsageError);
		output.Should().NotContain("ready");
	}

	private static ReplApp CreateApp(OutcomeRecorder? recorder, Action<ReplOptions>? configure = null)
	{
		var app = ReplApp.Create();
		app.Options(options =>
		{
			options.Interactive.InteractivePolicy = InteractivePolicy.Prevent;
			options.Output.BannerEnabled = false;
			configure?.Invoke(options);

			// After configure, so a test can observe outcomes while configure installs its own table
			// entries; a configure that sets its own Resolver keeps it.
			if (recorder is not null && options.ExitCodes.Resolver is null)
			{
				options.ExitCodes.Resolver = recorder.Record;
			}
		});
		return app;
	}

	private static int Run(ReplApp sut, string[] args, out string output)
	{
		using var session = OpenSession(out var writer);
		var exitCode = sut.Run(args);
		output = writer.ToString();
		return exitCode;
	}

	[TestMethod]
	[Description("An activation failure must still tell the operator why. Marking what escapes application code during binding exists so a remote host can withhold it, and the marker is a wrapper — rendering the wrapper's own message here would leave a console operator with the parameter's name and nothing about the cause, which is the diagnostic they came for.")]
	public async Task When_ADependencyFactoryThrows_Then_TheLocalDiagnosticNamesTheCause()
	{
		var sut = ReplApp.Create(services => services.AddSingleton<IFailingDependency>(
			implementationFactory: static _ => throw new InvalidOperationException("factory-cause-detail")));
		sut.Map("work", (IFailingDependency dependency) => dependency.ToString() ?? "ok");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		using var session = OpenSession(out var writer);
		await sut.RunAsync(["work"], cts.Token).ConfigureAwait(false);

		writer.ToString().Should().Contain(
			"factory-cause-detail",
			because: "the operator is the reader here, and the cause is the whole content of the diagnostic");
	}

	[TestMethod]
	[Description("The same for an options-group property setter, which reaches the binder through reflection. Reflection wraps what the application threw in its own exception, so unwrapping a single layer would leave the operator with reflection's generic target-of-an-invocation message — true, and useless.")]
	public async Task When_AnOptionsGroupSetterThrows_Then_TheLocalDiagnosticNamesTheCause()
	{
		var sut = ReplApp.Create();
		sut.Map("work", (FailingOptions options) => options.Label ?? "ok");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

		using var session = OpenSession(out var writer);
		await sut.RunAsync(["work", "--label", "x"], cts.Token).ConfigureAwait(false);

		writer.ToString().Should().Contain(
			"setter-cause-detail",
			because: "reflection's own wrapper is not the diagnostic, it is what hides it");
	}

	[Repl.Parameters.ReplOptionsGroup]
	public sealed class FailingOptions
	{
		private string? _label;

		public string? Label
		{
			get => _label;
			set
			{
				_label = value;
				throw new InvalidOperationException("setter-cause-detail");
			}
		}
	}

	/// <summary>A dependency whose registration always fails; only its activation path matters.</summary>
	public interface IFailingDependency;

	private static IDisposable OpenSession(out StringWriter writer)
	{
		writer = new StringWriter();
		return ReplSessionIO.SetSession(writer, TextReader.Null, commandOutput: writer, error: writer);
	}

	// Splits the session's error stream from its output so a framework diagnostic can be asserted on its
	// own; modelled on Given_CancelKeyHandler's session setup.
	private static IDisposable OpenSplitSession(out StringWriter output, out StringWriter error)
	{
		output = new StringWriter();
		error = new StringWriter();
		return ReplSessionIO.SetSession(
			output,
			TextReader.Null,
			commandOutput: output,
			error: error,
			isHostedSession: false);
	}

	private sealed class OutcomeRecorder
	{
		private readonly List<ReplExecutionOutcome> _observed = [];

		public ReplExecutionOutcome? Last => _observed.Count == 0 ? null : _observed[^1];

		public int Count => _observed.Count;

		public int Record(ReplExecutionOutcome outcome)
		{
			_observed.Add(outcome);
			return outcome.ExitCode;
		}
	}

	private interface IMissingDependency;

	// Stands in for a torn-down transport: every write fails.
	private sealed class ThrowingWriter : StringWriter
	{
		public override void WriteLine(string? value) => throw new ObjectDisposedException(nameof(ThrowingWriter));

		public override void Write(string? value) => throw new ObjectDisposedException(nameof(ThrowingWriter));
	}

	// An application-supplied transformer that always fails, so reporting its own failure re-enters it.
	private sealed class ThrowingTransformer : IOutputTransformer
	{
		public string Name => "broken";

		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("transformer is broken");
	}

	// Raises cancellation on its own account, with nothing having asked the run to stop: the pipeline
	// cannot convert it, so the reporter has to treat it as an ordinary transformer failure.
	private sealed class SelfCancellingTransformer : IOutputTransformer
	{
		public string Name => "selfcancel";

		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default) =>
			throw new OperationCanceledException("transformer gave up");
	}

	// Fails the first render, then cancels the caller's token and observes it on the fallback render —
	// the window in which a blanket catch would have reported a handler failure for a cancelled run.
	private sealed class CancellingTransformer(CancellationTokenSource cts) : IOutputTransformer
	{
		private bool _firstCallDone;

		public string Name => "cancelling";

		public async ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
		{
			if (!_firstCallDone)
			{
				_firstCallDone = true;
				throw new InvalidOperationException("transformer is broken");
			}

			await cts.CancelAsync().ConfigureAwait(false);
			throw new OperationCanceledException(cts.Token);
		}
	}

	private sealed class InMemoryHost(TextReader input, TextWriter output) : IReplHost
	{
		public TextReader Input { get; } = input;

		public TextWriter Output { get; } = output;
	}
}
