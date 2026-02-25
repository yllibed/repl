using Repl;
using Microsoft.Extensions.DependencyInjection;

// Sample goal:
// - one generic CRUD module
// - one generic singleton store per entity type
// - one adapter per entity model
var app = ReplApp.Create(services =>
	{
		// Open generic registration: each TEntity gets its own singleton store instance.
		services.AddSingleton(typeof(IEntityStore<>), typeof(InMemoryEntityStore<>));

		// Adapters keep entity-specific shape while CRUD logic stays generic.
		services.AddSingleton<IEntityCrudAdapter<ClientRecord>, ClientCrudAdapter>();
		services.AddSingleton<IEntityCrudAdapter<ContactRecord>, ContactCrudAdapter>();
		services.AddSingleton<IEntityCrudAdapter<InvoiceRecord>, InvoiceCrudAdapter>();

		// Independent module to show generic CRUD can coexist with other modules.
		services.AddSingleton<OpsModule>();

		// Seed history with example commands so the user can press Up right away.
		// In a real app, implement IHistoryProvider to persist entries to disk or a
		// database so history survives across sessions.
		services.AddSingleton<IHistoryProvider>(new InMemoryHistoryProvider([
			"ops status",
			"ops ping",
			"client add Acme acme@corp.com",
			"client list",
			"contact add Alice alice@test.com",
			"invoice list",
		]));
	})
	.WithDescription("Modular ops sample: one generic CRUD module mounted across multiple entity contexts.")
	.WithBanner("""
		  Same generic CRUD module, 3 entity types:
		  Try: client list, contact add, invoice list
		  Also: ops status, ops ping
		""")
	.UseDefaultInteractive()
	.UseCliProfile()
	.MapModule<OpsModule>();

// Same module code, mounted three times with different TEntity.
app.Context("client", client => client.MapModule(new EntityCrudModule<ClientRecord>()));
app.Context("contact", contact => contact.MapModule(new EntityCrudModule<ContactRecord>()));
app.Context("invoice", invoice => invoice.MapModule(new EntityCrudModule<InvoiceRecord>()));

return app.Run(args);
