using System.ComponentModel.DataAnnotations;

internal sealed record Contact(
	[property: Display(Order = 0)] string Name,
	[property: Display(Order = 1)] string Email);

internal sealed record ImportSummary(int Imported, int Overwritten, int Skipped);

internal interface IContactStore
{
	IReadOnlyList<Contact> All();
	void Add(Contact contact);
	void Clear();
	Task<IReadOnlyList<Contact>> ParseFileAsync(string file, CancellationToken cancellationToken);
	int CountDuplicates(IReadOnlyList<Contact> batch);
	bool IsDuplicate(Contact contact);
	Task<bool> ImportOneAsync(Contact contact, CancellationToken cancellationToken);
}

internal sealed class InMemoryContactStore : IContactStore
{
	private readonly List<Contact> _contacts =
	[
		new Contact("Carl de Billy", "carl@gmail.com"),
		new Contact("Bob Smith", "bob@company.com"),
	];

	public IReadOnlyList<Contact> All() => _contacts;

	public void Add(Contact contact)
	{
		ArgumentNullException.ThrowIfNull(contact);
		_contacts.Add(contact);
	}

	public void Clear() => _contacts.Clear();

	public Task<IReadOnlyList<Contact>> ParseFileAsync(string file, CancellationToken cancellationToken)
	{
		var baseName = Path.GetFileNameWithoutExtension(file);
		IReadOnlyList<Contact> batch =
		[
			new Contact("Alice Martin", "alice@example.com"),
			new Contact("Bob Smith", "bob@company.com"),          // duplicate
			new Contact("Charlie Brown", "charlie@example.com"),
			new Contact("Carl de Billy", "carl@gmail.com"),       // duplicate
			new Contact($"{baseName} User", $"{baseName.ToLowerInvariant()}@import.com"),
		];
		return Task.FromResult(batch);
	}

	public int CountDuplicates(IReadOnlyList<Contact> batch) =>
		batch.Count(b => IsDuplicate(b));

	public bool IsDuplicate(Contact contact) =>
		_contacts.Any(c => string.Equals(c.Name, contact.Name, StringComparison.OrdinalIgnoreCase));

	public async Task<bool> ImportOneAsync(Contact contact, CancellationToken cancellationToken)
	{
		await Task.Delay(50, cancellationToken);

		var existing = _contacts.FirstOrDefault(c =>
			string.Equals(c.Name, contact.Name, StringComparison.OrdinalIgnoreCase));
		if (existing is not null)
		{
			_contacts.Remove(existing);
			_contacts.Add(contact);
			return true;
		}

		_contacts.Add(contact);
		return false;
	}
}
