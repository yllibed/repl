using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpToolNameFlattener
{
	[TestMethod]
	[Description("Simple command without contexts produces the command name.")]
	public void When_SimpleRoute_Then_ReturnsSameWord()
	{
		McpToolNameFlattener.Flatten("greet", '_').Should().Be("greet");
	}

	[TestMethod]
	[Description("Context + command produces flattened name with separator.")]
	public void When_ContextAndCommand_Then_Flattened()
	{
		McpToolNameFlattener.Flatten("contact add", '_').Should().Be("contact_add");
	}

	[TestMethod]
	[Description("Dynamic segments are removed from the tool name.")]
	public void When_DynamicSegment_Then_Removed()
	{
		McpToolNameFlattener.Flatten("contact {id} notes", '_').Should().Be("contact_notes");
	}

	[TestMethod]
	[Description("Multiple dynamic segments are all removed.")]
	public void When_MultipleDynamicSegments_Then_AllRemoved()
	{
		McpToolNameFlattener.Flatten("project {pid} task {tid}", '_').Should().Be("project_task");
	}

	[TestMethod]
	[Description("Constrained dynamic segments are removed.")]
	public void When_ConstrainedDynamicSegment_Then_Removed()
	{
		McpToolNameFlattener.Flatten("contact {id:guid} show", '_').Should().Be("contact_show");
	}

	[TestMethod]
	[Description("Slash separator works.")]
	public void When_SlashSeparator_Then_UsesSlash()
	{
		McpToolNameFlattener.Flatten("contact add", '/').Should().Be("contact/add");
	}

	[TestMethod]
	[Description("Dot separator works.")]
	public void When_DotSeparator_Then_UsesDot()
	{
		McpToolNameFlattener.Flatten("contact add", '.').Should().Be("contact.add");
	}

	[TestMethod]
	[Description("Separator resolution from enum.")]
	public void When_ResolveSeparator_Then_ReturnsCorrectChar()
	{
		McpToolNameFlattener.ResolveSeparator(ToolNamingSeparator.Underscore).Should().Be('_');
		McpToolNameFlattener.ResolveSeparator(ToolNamingSeparator.Slash).Should().Be('/');
		McpToolNameFlattener.ResolveSeparator(ToolNamingSeparator.Dot).Should().Be('.');
	}
}
