using System.Net;
using DocsMcpSample;
using Microsoft.Extensions.Configuration;

namespace Repl.DocsSampleTests;

[TestClass]
public sealed class Given_Settings
{
	[TestMethod]
	[Description("Configuration providers supply typed ports and application paths without creating the index or requiring runtime assets.")]
	public void When_ConfigurationProvidesSettings_Then_TheApplicationUsesTypedValues()
	{
		using var fixture = new TestCorpus();
		var indexPath = Path.Combine(fixture.Root, "another-index");
		using var configuration = new ConfigurationManager
		{
			["DOCUMENTS"] = fixture.Settings.Documents,
			["INDEX"] = indexPath,
			["ASSETS"] = "configured-assets",
			["PIPE"] = "configured-pipe",
			["ADMIN_ADDRESS"] = "::1",
			["MCP_ADDRESS"] = "0.0.0.0",
			["MCP_PORT"] = "9100",
			["ADMIN_PORT"] = "9101",
			["ADMIN_ORIGIN"] = "https://admin.example.com",
		};

		var settings = Settings.FromConfiguration(configuration);

		settings.Documents.Should().Be(fixture.Settings.Documents);
		settings.Index.Should().Be(indexPath);
		settings.Assets.Should().Be(Path.GetFullPath("configured-assets"));
		settings.Pipe.Should().Be("configured-pipe");
		settings.AdminAddress.Should().Be(IPAddress.IPv6Loopback);
		settings.McpPort.Should().Be(9100);
		settings.AdminPort.Should().Be(9101);
		settings.AdminOrigin.Should().Be("https://admin.example.com");
		settings.McpAddress.Should().Be(IPAddress.Any);
		Directory.Exists(indexPath).Should().BeFalse();
	}

	[TestMethod]
	[Description("Omitting the optional configuration file preserves the local-only administration defaults and standard application locations.")]
	public void When_ConfigurationIsEmpty_Then_DefaultsRemainUsable()
	{
		using var configuration = new ConfigurationManager();

		var settings = Settings.FromConfiguration(configuration);

		settings.Documents.Should().Be(Path.GetFullPath("documents"));
		settings.Index.Should().Be(Path.GetFullPath(".index"));
		settings.Assets.Should().Be(Path.GetFullPath(".assets"));
		settings.Pipe.Should().Be("repl-docs-index");
		settings.AdminAddress.Should().Be(IPAddress.Loopback);
		settings.McpAddress.Should().Be(IPAddress.Loopback);
		settings.McpPort.Should().Be(8080);
		settings.AdminPort.Should().Be(8081);
		settings.AdminOrigin.Should().BeNull();
	}

	[TestMethod]
	[DataRow("MCP_PORT")]
	[DataRow("ADMIN_PORT")]
	[Description("A malformed port is rejected by the configuration conversion with its key in the diagnostic, before server construction.")]
	public void When_APortIsMalformed_Then_ConfigurationReportsTheSetting(string key)
	{
		using var configuration = new ConfigurationManager { [key] = "not-a-port" };

		var load = () => Settings.FromConfiguration(configuration);

		load.Should().Throw<InvalidOperationException>().WithMessage("*" + key + "*");
	}

	[TestMethod]
	[DataRow("ADMIN_ADDRESS", "not-an-address")]
	[DataRow("MCP_ADDRESS", "")]
	[DataRow("DOCUMENTS", "")]
	[DataRow("INDEX", " ")]
	[DataRow("ASSETS", "")]
	[Description("Explicit invalid configuration is rejected with its key; an empty path never falls back to another corpus or index.")]
	public void When_APathOrAddressIsInvalid_Then_TheDiagnosticNamesItsKey(string key, string value)
	{
		using var configuration = new ConfigurationManager { [key] = value };

		var load = () => Settings.FromConfiguration(configuration);

		load.Should().Throw<ArgumentException>().WithMessage("*DOCS_" + key + "*");
	}
}
