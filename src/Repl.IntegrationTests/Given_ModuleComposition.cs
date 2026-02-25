using Microsoft.Extensions.DependencyInjection;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ModuleComposition
{
	[TestMethod]
	[Description("Regression guard: verifies a shared module instance can be mounted in multiple contexts so that commands behave contextually per scope.")]
	public void When_MountingSameModuleAcrossContexts_Then_NearestContextValueIsInjected()
	{
		var insights = new SharedInsightsModule();
		var sut = ReplApp.Create();
		sut.Context("client {clientId}", client => client.MapModule(insights));
		sut.Context("contact {contactId}", contact => contact.MapModule(insights));
		sut.Context("invoice {invoiceId}", invoice => invoice.MapModule(insights));

		var clientOutput = ConsoleCaptureHelper.Capture(
			() => sut.Run(["client", "acme", "insights", "summary", "--no-logo"]));
		var contactOutput = ConsoleCaptureHelper.Capture(
			() => sut.Run(["contact", "42", "insights", "summary", "--no-logo"]));
		var invoiceOutput = ConsoleCaptureHelper.Capture(
			() => sut.Run(["invoice", "inv-100", "insights", "summary", "--no-logo"]));

		clientOutput.ExitCode.Should().Be(0);
		contactOutput.ExitCode.Should().Be(0);
		invoiceOutput.ExitCode.Should().Be(0);
		clientOutput.Text.Should().Contain("acme");
		contactOutput.Text.Should().Contain("42");
		invoiceOutput.Text.Should().Contain("inv-100");
	}

	[TestMethod]
	[Description("Regression guard: verifies generic module registration resolves through DI so that MapModule<TModule>() is usable as a first-class composition path.")]
	public void When_MappingModuleByGenericType_Then_ModuleIsResolvedFromServicesAndMapped()
	{
		var sut = ReplApp.Create(services =>
		{
			services.AddSingleton<RootPingModule>();
		});
		sut.MapModule<RootPingModule>();

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["ops", "ping", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("pong");
	}

	[TestMethod]
	[Description("Regression guard: verifies generic module registration resolves through DI in nested contexts so that MapModule<TModule>() is usable below root.")]
	public void When_MappingModuleByGenericTypeInsideContext_Then_ModuleIsResolvedFromServicesAndMapped()
	{
		var sut = ReplApp.Create(services =>
		{
			services.AddSingleton<ScopedOpsModule>();
		});
		sut.Context("client {clientId}", client =>
		{
			client.MapModule<ScopedOpsModule>();
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["client", "acme", "ops", "ping", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("scoped-pong");
	}

	[TestMethod]
	[Description("Regression guard: verifies all-ancestor context lookup so that [FromContext(All = true)] returns nearest-to-root ordered values.")]
	public void When_RequestingAllContextAncestors_Then_CollectionIsOrderedNearestToRoot()
	{
		var sut = ReplApp.Create();
		sut.Context("client {clientId}", client =>
			client.Context("invoice {invoiceId}", invoice =>
				invoice.MapModule(new SharedInsightsModule())));

		var output = ConsoleCaptureHelper.Capture(
			() => sut.Run(["client", "acme", "invoice", "inv-100", "insights", "notes", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("inv-100>acme");
	}

	[TestMethod]
	[Description("Regression guard: verifies module handlers follow ambiguity rules so that context-versus-service collisions fail unless explicit attributes are used.")]
	public void When_ContextAndServiceCanBindSameType_Then_AmbiguityErrorsAndExplicitBindingWorks()
	{
		var sut = ReplApp.Create(services =>
		{
			services.AddSingleton("service-scope");
		});
		sut.Context("client {scope}", client => client.MapModule(new AmbiguousScopeModule()));

		var ambiguous = ConsoleCaptureHelper.Capture(
			() => sut.Run(["client", "context-scope", "insights", "ambiguous", "--no-logo"]));
		var explicitContext = ConsoleCaptureHelper.Capture(
			() => sut.Run(["client", "context-scope", "insights", "from-context", "--no-logo"]));
		var explicitService = ConsoleCaptureHelper.Capture(
			() => sut.Run(["client", "context-scope", "insights", "from-services", "--no-logo"]));

		ambiguous.ExitCode.Should().Be(1);
		ambiguous.Text.Should().Contain("Ambiguous binding");
		explicitContext.ExitCode.Should().Be(0);
		explicitContext.Text.Should().Contain("context-scope");
		explicitService.ExitCode.Should().Be(0);
		explicitService.Text.Should().Contain("service-scope");
	}

	private sealed class SharedInsightsModule : IReplModule
	{
		public void Map(IReplMap map)
		{
			map.Context("insights", insights =>
			{
				insights.Map("summary", ([FromContext] string scopeId) => scopeId);
				insights.Map("notes", ([FromContext(All = true)] IReadOnlyList<string> scopes) => string.Join('>', scopes));
			});
		}
	}

	private sealed class RootPingModule : IReplModule
	{
		public void Map(IReplMap map)
		{
			map.Context("ops", ops =>
			{
				ops.Map("ping", () => "pong");
			});
		}
	}

	private sealed class AmbiguousScopeModule : IReplModule
	{
		public void Map(IReplMap map)
		{
			map.Context("insights", insights =>
			{
				insights.Map("ambiguous", (string value) => value);
				insights.Map("from-context", ([FromContext] string value) => value);
				insights.Map("from-services", ([FromServices] string value) => value);
			});
		}
	}

	private sealed class ScopedOpsModule : IReplModule
	{
		public void Map(IReplMap map)
		{
			map.Context("ops", ops =>
			{
				ops.Map("ping", () => "scoped-pong");
			});
		}
	}
}
