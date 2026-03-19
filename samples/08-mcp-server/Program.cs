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

app.UseMcpServer(o => o.ServerName = "ContactManager");

// ── Resources (data to consult) ────────────────────────────────────

app.Map("contacts", () => new[]
	{
		new { Name = "Alice", Email = "alice@example.com" },
		new { Name = "Bob", Email = "bob@example.com" },
	})
	.WithDescription("List all contacts")
	.ReadOnly()
	.AsResource();

// ── Contact operations (grouped context) ───────────────────────────

app.Context("contact", contact =>
{
	contact.Map("{id:int}", ([Description("Contact numeric id")] int id) =>
			new { Id = id, Name = id == 1 ? "Alice" : "Bob", Email = $"user{id}@example.com" })
		.WithDescription("Get contact by ID")
		.ReadOnly();

	contact.Map("add", (string name, string email) =>
			new { Name = name, Email = email, Status = "created" })
		.WithDescription("Add a new contact")
		.WithDetails("""
			Creates a new contact record.
			The email must be unique across all contacts.
			""")
		.OpenWorld()
		.Idempotent();

	contact.Map("delete {id:int}",
		async ([Description("Contact numeric id")] int id, Repl.Interaction.IReplInteractionChannel interaction, CancellationToken ct) =>
		{
			if (!await interaction.AskConfirmationAsync("confirm", $"Delete contact {id}?", options: new(ct)))
			{
				return Results.Cancelled("Delete cancelled by user.");
			}

			return Results.Success($"Contact {id} deleted.");
		})
		.WithDescription("Delete a contact")
		.Destructive()
		.WithAnswer("confirm", "bool", "Confirm deletion");
});

// ── Prompts (reusable agent instructions) ──────────────────────────
// The handler returns text that becomes the agent's starting instructions.

app.Map("troubleshoot {symptom}", (string symptom) =>
		$"The user reports: '{symptom}'. " +
		"Investigate using the available contact tools. " +
		"Check if any contacts are missing or malformed. " +
		"Summarize your findings and suggest a fix.")
	.WithDescription("Guide the agent through diagnosing a contact issue")
	.AsPrompt();

// ── Interactive-only commands ──────────────────────────────────────

app.Map("clear", async (Repl.Interaction.IReplInteractionChannel interaction, CancellationToken ct) =>
	{
		await interaction.ClearScreenAsync(ct);
		return Results.Ok("Screen cleared.");
	})
	.WithDescription("Clear the screen")
	.AutomationHidden();

return app.Run(args);
