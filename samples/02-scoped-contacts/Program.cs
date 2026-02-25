using System.ComponentModel;
using Repl;
using Microsoft.Extensions.DependencyInjection;

// Sample goal:
// - explicit root scope: contact
// - dynamic child scope: {name}
// - NavigateUp on remove to return to parent scope
var app = ReplApp.Create(services =>
{
	services.AddSingleton<IContactStore, InMemoryContactStore>();
	// Seed history with example commands so the user can press Up right away.
	// In a real app, implement IHistoryProvider to persist entries to disk or a
	// database so history survives across sessions.
	services.AddSingleton<IHistoryProvider>(new InMemoryHistoryProvider([
		"contact add Alice alice@test.com",
		"contact add Bob bob@test.com",
		"contact list",
		"contact Alice",
	]));
})
	.WithDescription("Scoped contacts sample: dynamic contact scope with DI-backed storage.")
	.WithBanner("""
		  Try: contact list, contact add Alice alice@test.com
		  Then: contact Alice (enters scope), show, remove
		""")
	.UseDefaultInteractive()
	.UseCliProfile();

app.Context(
	"contact",
	[Description("Manage contacts")]
	(IReplMap contact) =>
	{
		contact.WithBanner((TextWriter w, IContactStore store) =>
		{
			w.WriteLine($"  {store.All().Count} contact(s) in store. Try: list, add, remove");
		});

		// Root-level commands inside contact scope.
		contact.Map(
			"list",
			[Description("List all contacts")]
			(IContactStore store) => store.All());

		contact.Map(
			"add {name} {email:email}",
			[Description("Add a contact")]
			(string name, string email, IContactStore store) =>
			{
				store.Add(new Contact(name, email));
				return $"Contact '{name}' added.";
			});

		contact.Context(
			"{name}",
			[Description("Manage a specific contact")]
			(IReplMap scope) =>
			{
				// Scoped commands resolved for one selected contact name.
				scope.Map(
					"show",
					[Description("Show contact details")]
					(string name, IContactStore store) => store.Get(name));

				scope.Map(
					"remove",
					[Description("Remove this contact")]
					(string name, IContactStore store) =>
					{
						store.Remove(name);
						return Results.NavigateUp($"Contact '{name}' removed.");
					});
			},
			validation: (string name, IContactStore store) => store.Get(name) is not null);
	});

return app.Run(args);
