using Repl.Documentation;

namespace Repl.Tests;

[TestClass]
public sealed class Given_CommandBuilderEnrichment
{
	// ── WithDetails ────────────────────────────────────────────────────

	[TestMethod]
	[Description("Verifies WithDetails stores markdown content.")]
	public void When_WithDetailsIsCalled_Then_DetailsIsStored()
	{
		var sut = CoreReplApp.Create();
		var command = sut.Map("deploy", () => "ok");

		command.WithDetails("Deploys the application.");

		command.Details.Should().Be("Deploys the application.");
	}

	[TestMethod]
	[Description("Verifies WithDetails rejects empty content.")]
	public void When_WithDetailsIsCalledWithEmpty_Then_Throws()
	{
		var sut = CoreReplApp.Create();
		var command = sut.Map("deploy", () => "ok");

		var act = () => command.WithDetails("   ");

		act.Should().Throw<ArgumentException>();
	}

	// ── Annotation shortcuts ───────────────────────────────────────────

	[TestMethod]
	[Description("Verifies ReadOnly shortcut creates and sets annotation.")]
	public void When_ReadOnlyIsCalled_Then_AnnotationIsSet()
	{
		var sut = CoreReplApp.Create();
		var command = sut.Map("list", () => "ok");

		var chained = command.ReadOnly();

		chained.Should().BeSameAs(command);
		command.Annotations.Should().NotBeNull();
		command.Annotations!.ReadOnly.Should().BeTrue();
	}

	[TestMethod]
	[Description("Verifies chaining multiple annotation shortcuts preserves all flags.")]
	public void When_MultipleShortsAreChained_Then_AllFlagsArePreserved()
	{
		var sut = CoreReplApp.Create();
		var command = sut.Map("deploy", () => "ok");

		command.Destructive().LongRunning().OpenWorld();

		command.Annotations!.Destructive.Should().BeTrue();
		command.Annotations!.LongRunning.Should().BeTrue();
		command.Annotations!.OpenWorld.Should().BeTrue();
		command.Annotations!.ReadOnly.Should().BeFalse();
	}

	[TestMethod]
	[Description("Verifies WithAnnotations builder escape hatch works and overwrites shortcuts.")]
	public void When_WithAnnotationsIsCalled_Then_PreviousShortcutsAreOverwritten()
	{
		var sut = CoreReplApp.Create();
		var command = sut.Map("deploy", () => "ok");

		command.ReadOnly();
		command.WithAnnotations(a => a.Destructive().OpenWorld());

		command.Annotations!.ReadOnly.Should().BeFalse();
		command.Annotations!.Destructive.Should().BeTrue();
		command.Annotations!.OpenWorld.Should().BeTrue();
	}

	[TestMethod]
	[Description("Verifies WithAnnotations rejects null configure.")]
	public void When_WithAnnotationsIsCalledWithNull_Then_Throws()
	{
		var sut = CoreReplApp.Create();
		var command = sut.Map("deploy", () => "ok");

		var act = () => command.WithAnnotations(null!);

		act.Should().Throw<ArgumentNullException>();
	}

	// ── AsResource / AsPrompt ──────────────────────────────────────────

	[TestMethod]
	[Description("Verifies AsResource marks the command as a resource.")]
	public void When_AsResourceIsCalled_Then_IsResourceIsTrue()
	{
		var sut = CoreReplApp.Create();
		var command = sut.Map("contacts", () => "ok");

		command.AsResource();

		command.IsResource.Should().BeTrue();
	}

	[TestMethod]
	[Description("Verifies AsPrompt marks the command as a prompt source.")]
	public void When_AsPromptIsCalled_Then_IsPromptIsTrue()
	{
		var sut = CoreReplApp.Create();
		var command = sut.Map("explain", () => "ok");

		command.AsPrompt();

		command.IsPrompt.Should().BeTrue();
	}

	// ── WithMetadata ───────────────────────────────────────────────────

	[TestMethod]
	[Description("Verifies WithMetadata stores key-value pairs.")]
	public void When_WithMetadataIsCalled_Then_EntryIsStored()
	{
		var sut = CoreReplApp.Create();
		var command = sut.Map("deploy", () => "ok");

		command.WithMetadata("category", "operations");

		command.Metadata.Should().ContainKey("category");
		command.Metadata["category"].Should().Be("operations");
	}

	[TestMethod]
	[Description("Verifies WithMetadata rejects empty key.")]
	public void When_WithMetadataIsCalledWithEmptyKey_Then_Throws()
	{
		var sut = CoreReplApp.Create();
		var command = sut.Map("deploy", () => "ok");

		var act = () => command.WithMetadata("", "value");

		act.Should().Throw<ArgumentException>();
	}

	// ── Documentation model enrichment ─────────────────────────────────

	[TestMethod]
	[Description("Verifies enriched fields propagate through the documentation model.")]
	public void When_DocumentationModelIsCreated_Then_EnrichedFieldsArePresent()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contacts", () => "ok")
			.WithDescription("List contacts")
			.WithDetails("Returns all contacts.")
			.ReadOnly()
			.AsResource()
			.WithMetadata("scope", "crm");

		var model = sut.CreateDocumentationModel();

		var cmd = model.Commands.Should().ContainSingle(c => c.Path == "contacts").Which;
		cmd.Details.Should().Be("Returns all contacts.");
		cmd.Annotations.Should().NotBeNull();
		cmd.Annotations!.ReadOnly.Should().BeTrue();
		cmd.IsResource.Should().BeTrue();
		cmd.Metadata.Should().NotBeNull();
		cmd.Metadata!["scope"].Should().Be("crm");
	}

	[TestMethod]
	[Description("Verifies resources collection is populated from AsResource commands.")]
	public void When_CommandIsMarkedAsResource_Then_ResourcesCollectionContainsIt()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contacts", () => "ok")
			.WithDescription("List contacts")
			.AsResource();

		var model = sut.CreateDocumentationModel();

		model.Resources.Should().ContainSingle(r => r.Path == "contacts");
	}

	[TestMethod]
	[Description("Verifies ReadOnly commands are auto-promoted to resources.")]
	public void When_CommandIsReadOnly_Then_ResourcesCollectionContainsIt()
	{
		var sut = CoreReplApp.Create();
		sut.Map("status", () => "ok")
			.WithDescription("Show status")
			.ReadOnly();

		var model = sut.CreateDocumentationModel();

		model.Resources.Should().ContainSingle(r => r.Path == "status");
	}

	[TestMethod]
	[Description("Verifies AsPrompt flag propagates through the documentation model.")]
	public void When_CommandIsMarkedAsPrompt_Then_IsPromptIsTrue()
	{
		var sut = CoreReplApp.Create();
		sut.Map("explain {code}", (string code) => $"Explain {code}")
			.AsPrompt();

		var model = sut.CreateDocumentationModel();

		model.Commands.Should().ContainSingle(c => c.Path == "explain {code}").Which
			.IsPrompt.Should().BeTrue();
	}

	[TestMethod]
	[Description("Verifies route argument descriptions are picked up from [Description] attributes.")]
	public void When_HandlerHasDescriptionAttribute_Then_ArgumentDescriptionIsPopulated()
	{
		var sut = CoreReplApp.Create();
		sut.Map("contact {id:int}", ([System.ComponentModel.Description("Contact numeric id")] int id) => id);

		var model = sut.CreateDocumentationModel();

		var arg = model.Commands.Should().ContainSingle(c => c.Path == "contact {id:int}").Which
			.Arguments.Should().ContainSingle().Which;
		arg.Description.Should().Be("Contact numeric id");
	}

	// ── ReplRuntimeChannel.Programmatic ────────────────────────────────

	[TestMethod]
	[Description("Verifies Programmatic channel value exists.")]
	public void When_ProgrammaticChannel_Then_ValueIs3()
	{
		((int)ReplRuntimeChannel.Programmatic).Should().Be(3);
	}
}
