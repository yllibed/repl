using System.ComponentModel.DataAnnotations;

internal sealed record Contact(
	[property: Display(Order = 0)] string Name,
	[property: Display(Order = 1)] string Email,
	[property: Display(Order = 2)] string Phone,
	[property: Display(Order = 3)] string Department);

internal interface IContactStore
{
	IReadOnlyList<Contact> All();
	Contact? Get(string name);
	void Add(Contact contact);
}

internal sealed class InMemoryContactStore : IContactStore
{
	private readonly List<Contact> _contacts =
	[
		new("Carl de Billy", "carl@gmail.com", "555-0101", "Engineering"),
		new("Bob Smith", "bob@company.com", "555-0102", "Sales"),
		new("Alice Martin", "alice@example.com", "555-0103", "Engineering"),
		new("Charlie Brown", "charlie@gmail.com", "555-0104", "Marketing"),
		new("Diana Prince", "diana@company.com", "555-0105", "Engineering"),
		new("Eve Torres", "eve@example.com", "555-0106", "Sales"),
		new("Frank Castle", "frank@gmail.com", "555-0107", "Marketing"),
		new("Grace Hopper", "grace@company.com", "555-0108", "Engineering"),
	];

	public IReadOnlyList<Contact> All() => _contacts;

	public Contact? Get(string name) =>
		_contacts.FirstOrDefault(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

	public void Add(Contact contact)
	{
		ArgumentNullException.ThrowIfNull(contact);
		_contacts.Add(contact);
	}
}
