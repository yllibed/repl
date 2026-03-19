using System.Text.Json;
using Repl.Documentation;
using Repl.Mcp;

namespace Repl.McpTests;

[TestClass]
public sealed class Given_McpSchemaGenerator
{
	// ── Type mapping ───────────────────────────────────────────────────

	[TestMethod]
	[Description("String argument produces { type: string }.")]
	public void When_StringArgument_Then_SchemaTypeIsString()
	{
		var cmd = CreateCommand(arguments: [new("name", "string", Required: true, Description: null)]);

		var schema = McpSchemaGenerator.BuildInputSchema(cmd);

		GetPropertyType(schema, "name").Should().Be("string");
	}

	[TestMethod]
	[Description("Int argument produces { type: integer }.")]
	public void When_IntArgument_Then_SchemaTypeIsInteger()
	{
		var cmd = CreateCommand(arguments: [new("id", "int", Required: true, Description: null)]);

		var schema = McpSchemaGenerator.BuildInputSchema(cmd);

		GetPropertyType(schema, "id").Should().Be("integer");
	}

	[TestMethod]
	[Description("Bool option produces { type: boolean }.")]
	public void When_BoolOption_Then_SchemaTypeIsBoolean()
	{
		var cmd = CreateCommand(
			options: [CreateOption("verbose", "bool")]);

		var schema = McpSchemaGenerator.BuildInputSchema(cmd);

		GetPropertyType(schema, "verbose").Should().Be("boolean");
	}

	[TestMethod]
	[Description("Email constraint produces { type: string, format: email }.")]
	public void When_EmailConstraint_Then_FormatIsEmail()
	{
		var cmd = CreateCommand(arguments: [new("email", "email", Required: true, Description: null)]);

		var schema = McpSchemaGenerator.BuildInputSchema(cmd);

		GetPropertyFormat(schema, "email").Should().Be("email");
	}

	[TestMethod]
	[Description("Guid constraint produces { type: string, format: uuid }.")]
	public void When_GuidConstraint_Then_FormatIsUuid()
	{
		var cmd = CreateCommand(arguments: [new("id", "guid", Required: true, Description: null)]);

		var schema = McpSchemaGenerator.BuildInputSchema(cmd);

		GetPropertyFormat(schema, "id").Should().Be("uuid");
	}

	[TestMethod]
	[Description("Timespan produces { type: string, format: duration }.")]
	public void When_TimespanType_Then_FormatIsDuration()
	{
		var cmd = CreateCommand(
			options: [CreateOption("timeout", "timespan")]);

		var schema = McpSchemaGenerator.BuildInputSchema(cmd);

		GetPropertyFormat(schema, "timeout").Should().Be("duration");
	}

	// ── Required / Optional ────────────────────────────────────────────

	[TestMethod]
	[Description("Required arguments appear in the required array.")]
	public void When_ArgumentIsRequired_Then_IncludedInRequired()
	{
		var cmd = CreateCommand(arguments: [new("name", "string", Required: true, Description: null)]);

		var schema = McpSchemaGenerator.BuildInputSchema(cmd);

		var required = schema.GetProperty("required");
		required.GetArrayLength().Should().Be(1);
		required[0].GetString().Should().Be("name");
	}

	[TestMethod]
	[Description("Optional arguments are not in the required array.")]
	public void When_ArgumentIsOptional_Then_NotInRequired()
	{
		var cmd = CreateCommand(arguments: [new("name", "string", Required: false, Description: null)]);

		var schema = McpSchemaGenerator.BuildInputSchema(cmd);

		schema.TryGetProperty("required", out _).Should().BeFalse();
	}

	// ── Enum values ────────────────────────────────────────────────────

	[TestMethod]
	[Description("Enum options include enum values in schema.")]
	public void When_OptionHasEnumValues_Then_SchemaContainsEnum()
	{
		var cmd = CreateCommand(
			options: [CreateOption("format", "string", enumValues: ["json", "xml", "yaml"])]);

		var schema = McpSchemaGenerator.BuildInputSchema(cmd);

		var prop = schema.GetProperty("properties").GetProperty("format");
		prop.TryGetProperty("enum", out var enumProp).Should().BeTrue();
		enumProp.GetArrayLength().Should().Be(3);
	}

	// ── Descriptions ───────────────────────────────────────────────────

	[TestMethod]
	[Description("Argument descriptions propagate to schema.")]
	public void When_ArgumentHasDescription_Then_SchemaContainsDescription()
	{
		var cmd = CreateCommand(
			arguments: [new("name", "string", Required: true, Description: "Contact name")]);

		var schema = McpSchemaGenerator.BuildInputSchema(cmd);

		var prop = schema.GetProperty("properties").GetProperty("name");
		prop.GetProperty("description").GetString().Should().Be("Contact name");
	}

	// ── Annotation mapping ─────────────────────────────────────────────

	[TestMethod]
	[Description("Null annotations produce null ToolAnnotations.")]
	public void When_AnnotationsAreNull_Then_ResultIsNull()
	{
		McpSchemaGenerator.MapAnnotations(annotations: null).Should().BeNull();
	}

	[TestMethod]
	[Description("Destructive annotation maps to destructiveHint: true.")]
	public void When_DestructiveIsTrue_Then_DestructiveHintIsTrue()
	{
		var annotations = new CommandAnnotations { Destructive = true };

		var result = McpSchemaGenerator.MapAnnotations(annotations);

		result.Should().NotBeNull();
		result!.DestructiveHint.Should().BeTrue();
	}

	[TestMethod]
	[Description("ReadOnly annotation maps to readOnlyHint: true and destructiveHint: false.")]
	public void When_ReadOnlyIsTrue_Then_ReadOnlyHintIsTrue()
	{
		var annotations = new CommandAnnotations { ReadOnly = true };

		var result = McpSchemaGenerator.MapAnnotations(annotations);

		result.Should().NotBeNull();
		result!.ReadOnlyHint.Should().BeTrue();
		result!.DestructiveHint.Should().BeFalse();
	}

	[TestMethod]
	[Description("Unset flags produce null hints, not false.")]
	public void When_FlagsAreFalse_Then_HintsAreNull()
	{
		var annotations = new CommandAnnotations();

		var result = McpSchemaGenerator.MapAnnotations(annotations);

		result.Should().NotBeNull();
		result!.DestructiveHint.Should().BeNull();
		result!.ReadOnlyHint.Should().BeNull();
	}

	// ── BuildDescription ───────────────────────────────────────────────

	[TestMethod]
	[Description("Description without details returns description only.")]
	public void When_NoDetails_Then_DescriptionOnly()
	{
		var cmd = CreateCommand(description: "List contacts");

		McpSchemaGenerator.BuildDescription(cmd).Should().Be("List contacts");
	}

	[TestMethod]
	[Description("Description with details returns combined text.")]
	public void When_DetailsPresent_Then_CombinesDescriptionAndDetails()
	{
		var cmd = CreateCommand(description: "Deploy", details: "Deploys to env.");

		McpSchemaGenerator.BuildDescription(cmd).Should().Contain("Deploy").And.Contain("Deploys to env.");
	}

	// ── Helpers ─────────────────────────────────────────────────────────

	private static ReplDocCommand CreateCommand(
		string path = "test",
		string? description = null,
		string? details = null,
		ReplDocArgument[]? arguments = null,
		ReplDocOption[]? options = null) =>
		new(
			Path: path,
			Description: description,
			Aliases: [],
			IsHidden: false,
			Arguments: arguments ?? [],
			Options: options ?? [],
			Details: details);

	private static ReplDocOption CreateOption(
		string name,
		string type,
		bool required = false,
		string[]? enumValues = null) =>
		new(
			Name: name,
			Type: type,
			Required: required,
			Description: null,
			Aliases: [],
			ReverseAliases: [],
			ValueAliases: [],
			EnumValues: enumValues ?? [],
			DefaultValue: null);

	private static string GetPropertyType(JsonElement schema, string name) =>
		schema.GetProperty("properties").GetProperty(name).GetProperty("type").GetString()!;

	private static string GetPropertyFormat(JsonElement schema, string name) =>
		schema.GetProperty("properties").GetProperty(name).GetProperty("format").GetString()!;
}
