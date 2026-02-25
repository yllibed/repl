using System.Text.Json;
using AwesomeAssertions;
using Repl.Protocol;

namespace Repl.ProtocolTests;

[TestClass]
public sealed class Given_ProtocolContracts
{
	[TestMethod]
	[Description("Regression guard: verifies creating help document so that command metadata is preserved.")]
	public void When_CreatingHelpDocument_Then_CommandMetadataIsPreserved()
	{
		var commands = new[]
		{
			new HelpCommand("list", "List contacts", "contact list"),
		};

		var document = ProtocolContracts.CreateHelpDocument("contact", commands);

		document.Scope.Should().Be("contact");
		document.Commands.Should().ContainSingle();
		document.Commands[0].Name.Should().Be("list");
	}

	[TestMethod]
	[Description("Regression guard: verifies serializing help document so that scope is machine readable.")]
	public void When_SerializingHelpDocument_Then_ScopeIsMachineReadable()
	{
		var document = ProtocolContracts.CreateHelpDocument(
			"contact",
			[
				new HelpCommand("show", "Show contact", "contact 42 show"),
			]);

		var json = JsonSerializer.Serialize(document);

		json.Should().Contain("\"Scope\":\"contact\"");
		json.Should().Contain("\"Commands\"");
	}

	[TestMethod]
	[Description("Regression guard: verifies creating error contract so that code and message are set.")]
	public void When_CreatingErrorContract_Then_CodeAndMessageAreSet()
	{
		var error = ProtocolContracts.CreateError("not_found", "Contact not found.");

		error.Code.Should().Be("not_found");
		error.Message.Should().Be("Contact not found.");
	}

	[TestMethod]
	[Description("Regression guard: verifies mapping help command to MCP tool so that future MCP integration has a stable bridge.")]
	public void When_CreatingMcpToolFromHelpCommand_Then_NameAndDescriptionAreMapped()
	{
		var command = new HelpCommand("contact list", "List contacts", "contact list");

		var tool = ProtocolContracts.CreateMcpTool(command);

		tool.Name.Should().Be("contact list");
		tool.Description.Should().Be("List contacts");
		tool.InputSchema.Should().NotBeNull();
	}

	[TestMethod]
	[Description("Regression guard: verifies creating MCP manifest so that tools and resources are packaged with metadata.")]
	public void When_CreatingMcpManifest_Then_MetadataAndEntriesArePreserved()
	{
		var tools = new[]
		{
			new McpTool("contact list", "List contacts", new { type = "object" }),
		};
		var resources = new[]
		{
			new McpResource("repl://contacts", "Contacts", "Discoverable contacts"),
		};

		var manifest = ProtocolContracts.CreateMcpManifest("repl", "0.1.0", tools, resources);

		manifest.Name.Should().Be("repl");
		manifest.Version.Should().Be("0.1.0");
		manifest.Tools.Should().ContainSingle();
		manifest.Resources.Should().ContainSingle();
	}

	[TestMethod]
	[Description("Regression guard: verifies creating MCP tools from help document so that command discovery can be projected to MCP shape.")]
	public void When_CreatingMcpToolsFromHelpDocument_Then_CommandsAreMappedToTools()
	{
		var help = ProtocolContracts.CreateHelpDocument(
			"root",
			[
				new HelpCommand("contact list", "List contacts", "contact list"),
				new HelpCommand("contact show", "Show contact", "contact show"),
			]);

		var tools = ProtocolContracts.CreateMcpTools(help);

		tools.Should().HaveCount(2);
		tools.Select(tool => tool.Name).Should().Contain(["contact list", "contact show"]);
	}
}






