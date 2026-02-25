public interface IEntityCrudAdapter<TEntity>
{
	// Entity-specific projections keep module logic generic.
	TEntity Create(string id, string label);
	TEntity UpdateFromLabel(TEntity current, string label);
	object ToView(TEntity entity);
}

public sealed class ClientCrudAdapter : IEntityCrudAdapter<ClientRecord>
{
	public ClientRecord Create(string id, string label) => new(id, label);

	public ClientRecord UpdateFromLabel(ClientRecord current, string label) => current with { Name = label };

	public object ToView(ClientRecord entity) => new { id = entity.Id, name = entity.Name };
}

public sealed class ContactCrudAdapter : IEntityCrudAdapter<ContactRecord>
{
	public ContactRecord Create(string id, string label) => new(id, label);

	public ContactRecord UpdateFromLabel(ContactRecord current, string label) => current with { DisplayName = label };

	public object ToView(ContactRecord entity) => new { id = entity.Id, displayName = entity.DisplayName };
}

public sealed class InvoiceCrudAdapter : IEntityCrudAdapter<InvoiceRecord>
{
	public InvoiceRecord Create(string id, string label) => new(id, label);

	public InvoiceRecord UpdateFromLabel(InvoiceRecord current, string label) => current with { Title = label };

	public object ToView(InvoiceRecord entity) => new { id = entity.Id, title = entity.Title };
}
