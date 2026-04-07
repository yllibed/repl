using System.ComponentModel;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Repl;
using Repl.Mcp;

// ── A Repl app exposed as an MCP server for AI agents ──────────────
//
// Run interactively:  dotnet run
// Run as MCP server:  dotnet run -- mcp serve
//
// Configure in Claude Desktop or VS Code:
//   { "command": "dotnet", "args": ["run", "--project", "path/to/08-mcp-server", "--", "mcp", "serve"] }

var app = ReplApp.Create(services =>
{
	services.AddSingleton<ContactStore>();
}).UseDefaultInteractive();

app.UseMcpServer(o =>
{
	o.ServerName = "ContactManager";
});

// ── Resources (data to consult) ────────────────────────────────────

app.Map("contacts", (ContactStore contacts) => contacts.All)
	.WithDescription("List all contacts")
	.ReadOnly()
	.AsResource();

app.Map("contacts dashboard", () => "Opening the contacts dashboard.")
	.WithDescription("Open the contacts dashboard")
	.ReadOnly()
	.WithMcpApp("ui://contacts/dashboard");

app.Map("contacts dashboard app",
		(ContactStore contacts) =>
	{
		var items = string.Join(
			"",
			contacts.All.Select(static contact =>
				$"<li><strong>{Html(contact.Name)}</strong> <span>{Html(contact.Email)}</span></li>"));

		return $$"""
			<html>
			<head>
				<meta charset="utf-8">
				<meta name="viewport" content="width=device-width, initial-scale=1">
				<style>
					body { font-family: system-ui, sans-serif; margin: 24px; background: #232; color: cyan; }
					li { margin: 8px 0; }
				</style>
			</head>
			<body>
				<h1>Contacts from Repl</h1>
				<p>This HTML was rendered from a <code>ui://</code> MCP resource.</p>
				<ul>{{items}}</ul>
			</body>
			</html>
			""";
	})
	.WithDescription("Render the contacts dashboard app")
	.AsMcpAppResource("ui://contacts/dashboard", resource =>
	{
		resource.Name = "Contacts Dashboard";
		resource.Description = "Minimal contacts dashboard.";
		resource.PrefersBorder = true;
	}, visibility: McpAppVisibility.App, preferredDisplayMode: McpAppDisplayModes.Fullscreen);

// ── Contact operations (grouped context) ───────────────────────────

app.Context("contact", contact =>
{
	contact.Map("{id:int}", ([Description("Contact numeric id")] int id, ContactStore contacts) =>
			new { Id = id, Contact = contacts.Get(id) })
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
			if (!await interaction.AskConfirmationAsync("confirm", $"Delete contact {id}?", options: new(CancellationToken: ct)))
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

static string Html(string value) => WebUtility.HtmlEncode(value);

internal sealed record Contact(string Name, string Email);

internal sealed class ContactStore
{
	private readonly Contact[] _contacts =
	[
		new("Alice", "alice@example.com"),
		new("Bob", "bob@example.com"),
	];

	public IReadOnlyList<Contact> All => _contacts;

	public Contact Get(int id) => id == 1 ? _contacts[0] : _contacts[1];
}
