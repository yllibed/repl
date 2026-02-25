public interface IEntityStore<TEntity>
{
	// Simple store contract used by the generic CRUD module.
	IReadOnlyList<TEntity> List();
	TEntity? Get(string id);
	void Save(string id, TEntity entity);
	bool Remove(string id);
	string NextId();
}

public sealed class InMemoryEntityStore<TEntity> : IEntityStore<TEntity>
{
	// Per-process in-memory state, good enough for sample/demo usage.
	private readonly Dictionary<string, TEntity> _items = new(StringComparer.OrdinalIgnoreCase);
	private int _nextId = 1;

	public IReadOnlyList<TEntity> List() => _items.Values.ToArray();

	public TEntity? Get(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		return _items.TryGetValue(id, out var entity) ? entity : default;
	}

	public void Save(string id, TEntity entity)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		ArgumentNullException.ThrowIfNull(entity);
		_items[id] = entity;
	}

	public bool Remove(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		return _items.Remove(id);
	}

	public string NextId() => (_nextId++).ToString();
}
