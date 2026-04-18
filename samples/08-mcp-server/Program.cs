using System.ComponentModel;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Repl;
using Repl.Interaction;
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

app.Map("contacts dashboard", (ContactStore contacts) =>
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
	.WithDescription("Open the contacts dashboard")
	.AsMcpAppResource()
	.WithMcpAppBorder()
	.WithMcpAppDisplayMode(McpAppDisplayModes.Fullscreen);

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

// ── Import with agent capabilities ────────────────────────────────
// A realistic workflow: import a CSV, use sampling to detect duplicates,
// use elicitation to let the user resolve conflicts, then commit.
// Sampling and elicitation are optional steps — the import works without them.

app.Map("import {file}",
	async (string file, ContactStore contacts,
		IMcpSampling sampling, IMcpElicitation elicitation,
		Repl.Interaction.IReplInteractionChannel interaction, CancellationToken ct) =>
	{
		// ── Phase 1: Read the file ─────────────────────────────────
		await interaction.WriteNoticeAsync($"Starting import for '{file}'.", ct);
		await interaction.WriteProgressAsync("Reading CSV...", 5, ct);
		var (headers, rawRows) = ContactCsvParser.ReadRaw(file);
		// ... validate file structure, reject malformed rows ...

		// ── Phase 2: Column mapping (sampling) ─────────────────────
		// The CSV may have arbitrary column names ("Full Name", "E-Mail",
		// "Nombre", etc.). The app knows it needs "name" and "email" but
		// can't guess the mapping — ask the LLM to figure it out.
		int nameCol = 0, emailCol = 1; // defaults for standard headers

		if (sampling.IsSupported)
		{
			await interaction.WriteIndeterminateProgressAsync(
				"Identifying columns...",
				"Waiting for the connected MCP client to complete sampling.",
				ct);

			var sampleRows = string.Join("\n", rawRows.Take(3).Select(
				row => string.Join(", ", row.Select((cell, i) => $"[{i}] \"{cell}\""))));

			var mapping = await sampling.SampleAsync(
				"A CSV file has these column headers:\n" +
				string.Join(", ", headers.Select((h, i) => $"[{i}] \"{h}\"")) + "\n\n" +
				"Here are the first rows of data:\n" + sampleRows + "\n\n" +
				"Which column index contains the person's name and which contains " +
				"their email address? Reply as two numbers, e.g.: name=0 email=2",
				maxTokens: 50,
				cancellationToken: ct);

			(nameCol, emailCol) = ContactCsvParser.ParseColumnMapping(mapping, nameCol, emailCol);
			await interaction.WriteProgressAsync("Columns identified", 25, ct);
		}
		else
		{
			await interaction.WriteWarningProgressAsync(
				"Sampling unavailable",
				25,
				"Falling back to the default name/email column positions.",
				ct);
		}

		var rows = ContactCsvParser.MapRows(rawRows, nameCol, emailCol);

		// ── Phase 3: Duplicate detection ───────────────────────────
		await interaction.WriteProgressAsync("Checking duplicates...", 45, ct);
		var duplicates = ContactMatcher.FindCandidates(rows, contacts.All);

		// ── Phase 4: Conflict resolution (elicitation) ─────────────
		// Some incoming contacts may match existing ones.
		// Ask the user how to handle them through a structured form.
		var remaining = duplicates;

		if (remaining.Count > 0)
		{
			await interaction.WriteWarningProgressAsync(
				"Possible duplicates detected",
				60,
				$"{remaining.Count} potential contact matches need review.",
				ct);

			if (elicitation.IsSupported)
			{
				string[] strategies = ["skip", "overwrite", "keep-both"];

				var choice = await elicitation.ElicitChoiceAsync(
					$"{remaining.Count} contact(s) may already exist. How should they be handled?",
					strategies,
					ct);

				if (choice is null)
				{
					await interaction.WriteErrorProgressAsync(
						"Import cancelled",
						60,
						"The user cancelled during conflict resolution.",
						ct);
					await interaction.WriteProblemAsync(
						"Import cancelled during conflict resolution.",
						"No contacts were imported because the duplicate-handling step was cancelled.",
						"import_cancelled",
						ct);
					return Results.Cancelled("Import cancelled during conflict resolution.");
				}

				rows = ContactMatcher.ApplyStrategy(rows, remaining, strategies[choice.Value]);
				await interaction.WriteProgressAsync("Conflicts resolved", 72, ct);
			}
			else
			{
				await interaction.WriteWarningAsync(
					"Elicitation is unavailable, so duplicate handling falls back to the current rows.",
					ct);
			}
		}

		// ── Phase 5: Commit ────────────────────────────────────────
		await interaction.WriteProgressAsync("Importing...", 85, ct);
		var imported = contacts.Import(rows);
		await interaction.WriteProgressAsync("Done", 100, ct);
		await interaction.WriteNoticeAsync($"Imported {imported} of {rows.Count} contacts.", ct);

		return Results.Success($"Imported {imported} of {rows.Count} contacts.");
	})
	.WithDescription("Import contacts from CSV with smart column mapping and conflict resolution")
	.WithDetails("""
		Imports contacts from a CSV file. When the connected agent supports it:
		- **Sampling** identifies which CSV columns map to name and email (handles arbitrary headers)
		- **Elicitation** asks the user how to handle duplicate contacts
		Both steps are optional — the import falls back to positional columns and skips conflict resolution.
		""")
	.LongRunning()
	.OpenWorld();

app.Context("feedback", feedback =>
{
	feedback.Map("demo",
		async (Repl.Interaction.IReplInteractionChannel interaction, CancellationToken ct) =>
		{
			await interaction.WriteNoticeAsync("Starting the MCP feedback demo.", ct);
			await interaction.WriteProgressAsync("Preparing import workspace", 10, ct);
			await FeedbackDemo.DelayAsync(ct);
			await interaction.WriteIndeterminateProgressAsync(
				"Waiting for agent review",
				"Sampling or prompting may still be in progress.",
				ct);
			await FeedbackDemo.DelayAsync(ct);
			await interaction.WriteWarningProgressAsync(
				"Potential conflict detected",
				55,
				"A duplicate contact may need user review.",
				ct);
			await FeedbackDemo.DelayAsync(ct);
			await interaction.WriteProgressAsync("Finalizing import plan", 90, ct);
			await FeedbackDemo.DelayAsync(ct);
			await interaction.WriteNoticeAsync("Feedback demo completed.", ct);
			return Results.Success("Feedback demo completed.");
		})
		.WithDescription("Run a deterministic feedback sequence for MCP Inspector demos")
		.LongRunning();

	feedback.Map("fail",
		async (Repl.Interaction.IReplInteractionChannel interaction, CancellationToken ct) =>
		{
			await interaction.WriteNoticeAsync("Starting the failing MCP feedback demo.", ct);
			await interaction.WriteProgressAsync("Preparing import workspace", 15, ct);
			await FeedbackDemo.DelayAsync(ct);
			await interaction.WriteWarningProgressAsync(
				"Retrying remote validation",
				50,
				"The validation worker timed out.",
				ct);
			await FeedbackDemo.DelayAsync(ct);
			await interaction.WriteErrorProgressAsync(
				"Validation failed",
				80,
				"The worker stayed unavailable after the retry window.",
				ct);
			await FeedbackDemo.DelayAsync(ct);
			await interaction.WriteProblemAsync(
				"Feedback demo failed.",
				"The sample emitted an error-state progress update before returning the tool error.",
				"feedback_demo_failed",
				ct);
			return Results.Error("feedback_demo_failed", "Feedback demo failed.");
		})
		.WithDescription("Run a failing feedback sequence that emits warning and error notifications")
		.LongRunning();
});

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
	private readonly List<Contact> _contacts =
	[
		new("Alice", "alice@example.com"),
		new("Bob", "bob@example.com"),
	];

	public IReadOnlyList<Contact> All => _contacts;

	public Contact? Get(int id) => id >= 1 && id <= _contacts.Count
		? _contacts[id - 1]
		: null;

	public int Import(IReadOnlyList<Contact> rows)
	{
		_contacts.AddRange(rows);
		return rows.Count;
	}
}

// ── CSV parsing and duplicate detection stubs ─────────────────────
// In a real app these would be full implementations.

internal static class ContactCsvParser
{
	public static (string[] Headers, string[][] Rows) ReadRaw(string file) =>
		// ... read CSV headers and raw rows ...
		(["Full Name", "E-Mail Address", "Company"],
		[["Charlie", "charlie@example.com", "Acme"], ["Alice", "alice@corp.com", "Corp"]]);

	public static (int NameCol, int EmailCol) ParseColumnMapping(string? llmResponse, int defaultName, int defaultEmail)
	{
		// ... parse "name=0 email=1" from LLM response, fall back to defaults ...
		if (llmResponse is null)
		{
			return (defaultName, defaultEmail);
		}

		var nameCol = defaultName;
		var emailCol = defaultEmail;
		foreach (var kv in llmResponse
			.Split(' ', StringSplitOptions.RemoveEmptyEntries)
			.Select(part => part.Split('=')))
		{
			if (kv.Length == 2 && int.TryParse(kv[1], out var idx))
			{
				if (kv[0].Contains("name", StringComparison.OrdinalIgnoreCase))
				{
					nameCol = idx;
				}
				else if (kv[0].Contains("email", StringComparison.OrdinalIgnoreCase))
				{
					emailCol = idx;
				}
			}
		}

		return (nameCol, emailCol);
	}

	public static List<Contact> MapRows(string[][] rawRows, int nameCol, int emailCol) =>
		// ... map raw rows to Contact records using the resolved column indices ...
		rawRows.Select(row => new Contact(row[nameCol], row[emailCol])).ToList();
}

internal sealed record DuplicatePair(Contact Incoming, Contact Existing);

internal static class ContactMatcher
{
	public static IReadOnlyList<DuplicatePair> FindCandidates(
		IReadOnlyList<Contact> incoming, IReadOnlyList<Contact> existing) =>
		// ... fuzzy name/email matching to find potential duplicates ...
		incoming
			.SelectMany(i => existing.Where(e =>
				e.Name.Equals(i.Name, StringComparison.OrdinalIgnoreCase) && e.Email != i.Email)
				.Select(e => new DuplicatePair(i, e)))
			.ToList();

	public static List<Contact> ApplyStrategy(
		List<Contact> rows, IReadOnlyList<DuplicatePair> unresolved, string strategy) =>
		// ... apply user's chosen strategy (skip, overwrite, keep-both) ...
		rows;
}

internal static class FeedbackDemo
{
	public static Task DelayAsync(CancellationToken cancellationToken) =>
		Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
}
