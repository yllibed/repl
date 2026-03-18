using System.ComponentModel;
using Repl;

// ── A Repl app exposed as an MCP server for AI agents ──────────────
//
// Run interactively:  dotnet run
// Run as MCP server:  dotnet run -- mcp serve
//
// Configure in Claude Desktop or VS Code:
//   { "command": "dotnet", "args": ["run", "--project", "path/to/08-mcp-server", "--", "mcp", "serve"] }

var app = ReplApp.Create().UseDefaultInteractive();

// ── Resources (data to consult) ────────────────────────────────────

app.Map("contacts", () => new[]
	{
		new { Name = "Alice", Email = "alice@example.com" },
		new { Name = "Bob", Email = "bob@example.com" },
	})
	.WithDescription("List all contacts")
	.ReadOnly()
	.AsResource();

// ── Tools (operations to perform) ──────────────────────────────────

app.Map("contact {id:int}", ([Description("Contact numeric id")] int id) =>
		new { Id = id, Name = id == 1 ? "Alice" : "Bob", Email = $"user{id}@example.com" })
	.WithDescription("Get contact by ID")
	.ReadOnly();

app.Map("contact add", (string name, string email) =>
		new { Name = name, Email = email, Status = "created" })
	.WithDescription("Add a new contact")
	.WithDetails("""
		Creates a new contact record.
		The email must be unique across all contacts.
		""")
	.OpenWorld()
	.Idempotent();

app.Map("contact delete {id:int}",
	async ([Description("Contact numeric id")] int id, Repl.Interaction.IReplInteractionChannel interaction, CancellationToken ct) =>
	{
		if (!await interaction.AskConfirmationAsync("confirm", $"Delete contact {id}?", options: new(ct)))
		{
			return Results.Cancelled("Delete cancelled by user.");
		}

		return Results.Success($"Contact {id} deleted.");
	})
	.WithDescription("Delete a contact")
	.Destructive();

// ── Prompts (conversation starters) ────────────────────────────────

app.Map("explain-error {code}", (string code) =>
		$"Explain error code '{code}' in the context of the ContactManager application.")
	.WithDescription("Explain a ContactManager error code")
	.AsPrompt();

// ── Interactive-only commands ──────────────────────────────────────

app.Map("clear", async (Repl.Interaction.IReplInteractionChannel interaction, CancellationToken ct) =>
	{
		await interaction.ClearScreenAsync(ct);
		return Results.Ok("Screen cleared.");
	})
	.WithDescription("Clear the screen")
	.AutomationHidden();

// ── MCP server integration ─────────────────────────────────────────

app.UseMcpServer(o =>
{
	o.ServerName = "ContactManager";
});

return app.Run(args);
