using System.ComponentModel;
using Repl;

// Sample goal:
// - minimal CoreReplApp (no DI package)
// - simple contact commands + metadata attributes
var store = new ContactStore();
var commands = new ContactCommands(store);

var app = CoreReplApp.Create()
	.WithDescription("Core basics sample: minimal contacts REPL without DI dependencies.")
	.WithBanner("""
		  Try: list, add Alice alice@test.com, show 1, count
		  Also: error (exception handling), debug reset
		""");

app.Map("list", commands.List);
app.Map("add {name} {email:email}", commands.Add);
app.Map("show {id:int}", commands.Show);
app.Map("count", commands.Count);
app.Map("error", ErrorCommand);
app.Map("debug reset", commands.Reset);

return app.Run(args);

static object ErrorCommand() =>
	throw new ApplicationException("this is an error.");

file sealed class ContactCommands(ContactStore store)
{
	[Description("List all contacts.")]
	public List<Contact> List() => [.. store.List()];

	[Description("Add a new contact.")]
	public Contact Add(
		[Description("Contact full name")] string name,
		[Description("Email address")] string email)
		=> store.Add(name, email);

	[Description("Show one contact by id.")]
	public object Show([Description("Contact numeric id")] int id)
		=> store.Find(id) is { } contact
			? contact
			: Results.NotFound($"Contact '{id}' was not found.");

	[Description("Return the number of contacts.")]
	public object Count() => Results.Success("Contact count.", store.Count());

	[Description("Reset in-memory sample data.")]
	[Browsable(false)]
	public object Reset()
	{
		store.Reset();
		return Results.Success("Data reset complete.");
	}
}
