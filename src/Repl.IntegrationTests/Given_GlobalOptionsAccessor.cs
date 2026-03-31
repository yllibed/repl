using Microsoft.Extensions.DependencyInjection;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_GlobalOptionsAccessor
{
	[TestMethod]
	[Description("Global option is accessible in handler via IGlobalOptionsAccessor parameter.")]
	public void When_GlobalOptionProvided_Then_HandlerCanReadItViaAccessor()
	{
		var sut = ReplApp.Create();
		sut.Options(o => o.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("show", (IGlobalOptionsAccessor globals) => globals.GetValue<string>("tenant") ?? "none");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("Global option is accessible in middleware via DI.")]
	public void When_GlobalOptionProvided_Then_MiddlewareCanReadIt()
	{
		string? captured = null;
		var sut = ReplApp.Create();
		sut.Options(o => o.Parsing.AddGlobalOption<string>("tenant"));
		sut.Use(async (ctx, next) =>
		{
			var globals = ctx.Services.GetRequiredService<IGlobalOptionsAccessor>();
			captured = globals.GetValue<string>("tenant");
			await next().ConfigureAwait(false);
		});
		sut.Map("ping", () => "pong");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["ping", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		captured.Should().Be("acme");
	}

	[TestMethod]
	[Description("Global option is accessible in DI factory via lazy resolution.")]
	public void When_GlobalOptionProvided_Then_DiFactoryCanReadIt()
	{
		var sut = ReplApp.Create(services =>
		{
			services.AddSingleton(sp =>
			{
				var globals = sp.GetRequiredService<IGlobalOptionsAccessor>();
				return new TenantConfig(globals.GetValue<string>("tenant") ?? "default");
			});
		});
		sut.Options(o => o.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("show", (TenantConfig cfg) => cfg.Name);

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("Global option defaults are returned when option not provided.")]
	public void When_GlobalOptionNotProvided_Then_DefaultIsReturned()
	{
		var sut = ReplApp.Create();
		sut.Options(o => o.Parsing.AddGlobalOption<int>("port", defaultValue: 3000));
		sut.Map("show", (IGlobalOptionsAccessor globals) => globals.GetValue<int>("port"));

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("3000");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> registers typed class accessible via DI.")]
	public void When_UsingTypedGlobalOptions_Then_ClassIsPopulatedFromParsedValues()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();
		sut.Map("show", (TestGlobalOptions opts) => $"{opts.Tenant}:{opts.Port}");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--port", "9090", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("acme:9090");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> properties without values keep defaults.")]
	public void When_UsingTypedGlobalOptionsWithoutValues_Then_DefaultsAreKept()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();
		sut.Map("show", (TestGlobalOptions opts) => $"{opts.Tenant ?? "none"}:{opts.Port}");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("none:8080");
	}

	[TestMethod]
	[Description("Global option registered with string type name works end to end.")]
	public void When_GlobalOptionRegisteredWithStringTypeName_Then_TypedAccessWorks()
	{
		var sut = ReplApp.Create();
		sut.Options(o => o.Parsing.AddGlobalOption("port", "int"));
		sut.Map("show", (IGlobalOptionsAccessor globals) => globals.GetValue<int>("port"));

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--port", "4000", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("4000");
	}

	[TestMethod]
	[Description("CoreReplApp (no MS DI) also provides IGlobalOptionsAccessor.")]
	public void When_UsingCoreReplApp_Then_AccessorIsAvailableInHandler()
	{
		var sut = CoreReplApp.Create();
		sut.Options(o => o.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("show", (IGlobalOptionsAccessor globals) => globals.GetValue<string>("tenant") ?? "none");

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("acme");
	}

	[TestMethod]
	[Description("UseGlobalOptions<T> returns fresh values on each resolution (not stale singleton).")]
	public async Task When_TypedOptionsResolvedMultipleTimes_Then_ReflectsLatestValues()
	{
		var sut = ReplApp.Create();
		sut.UseGlobalOptions<TestGlobalOptions>();
		var results = new List<string>();
		sut.Map("show", (TestGlobalOptions opts) =>
		{
			results.Add($"{opts.Tenant}:{opts.Port}");
			return "ok";
		});

		// First invocation
		ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "first", "--port", "1111", "--no-logo"]));

		// Second invocation with different values
		ConsoleCaptureHelper.Capture(
			() => sut.Run(["show", "--tenant", "second", "--port", "2222", "--no-logo"]));

		results.Should().HaveCount(2);
		results[0].Should().Be("first:1111");
		results[1].Should().Be("second:2222");
	}

	private sealed record TenantConfig(string Name);

	private sealed class TestGlobalOptions
	{
		public string? Tenant { get; set; }

		public int Port { get; set; } = 8080;
	}
}
