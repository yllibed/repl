using Repl;

public sealed class EntityCrudModule<TEntity> : IReplModule
	where TEntity : IEntity
{
	public void Map(IReplMap map)
	{
		var name = TEntity.EntityName;

		// Top-level collection commands.
		map.Map(
			"list",
			([FromServices] IEntityStore<TEntity> store, [FromServices] IEntityCrudAdapter<TEntity> adapter) =>
				store.List().Select(adapter.ToView).ToArray())
			.WithDescription($"List all {name}s");

		map.Map(
			"add {label}",
			(string label, [FromServices] IEntityStore<TEntity> store, [FromServices] IEntityCrudAdapter<TEntity> adapter) =>
			{
				var id = store.NextId();
				var entity = adapter.Create(id, label);
				store.Save(id, entity);
				return Results.Success($"{name} '{id}' added.", adapter.ToView(entity));
			})
			.WithDescription($"Add a {name}");

		// Scoped commands for one selected entity id.
		map.Context(
			"{id}",
			scope =>
			{
				scope.Map(
					"show",
					(string id, [FromServices] IEntityStore<TEntity> store, [FromServices] IEntityCrudAdapter<TEntity> adapter) =>
					{
						var entity = store.Get(id);
						return (object)(entity is null
							? Results.NotFound($"{name} '{id}' not found.")
							: adapter.ToView(entity));
					})
					.WithDescription($"Show one {name} by id");

				scope.Map(
					"update {label}",
					(string id, string label, [FromServices] IEntityStore<TEntity> store, [FromServices] IEntityCrudAdapter<TEntity> adapter) =>
					{
						var entity = store.Get(id);
						if (entity is null)
						{
							return Results.NotFound($"{name} '{id}' not found.");
						}

						var updated = adapter.UpdateFromLabel(entity, label);
						store.Save(id, updated);
						return Results.Success($"{name} '{id}' updated.", adapter.ToView(updated));
					})
					.WithDescription($"Update one {name} by id");

				scope.Map(
					"remove",
					(string id, [FromServices] IEntityStore<TEntity> store, [FromServices] IEntityCrudAdapter<TEntity> adapter) =>
					{
						if (!store.Remove(id))
						{
							return (object)Results.NotFound($"{name} '{id}' not found.");
						}

						return (object)Results.NavigateUp(Results.Success($"{name} '{id}' removed."));
					})
					.WithDescription($"Remove one {name} by id");
			},
			// Prevent entering the scoped context when id does not exist.
			validation: (string id, IEntityStore<TEntity> store) => store.Get(id) is not null);
	}
}
