using System.ComponentModel.DataAnnotations;

internal sealed record Contact(
	[property: Display(Order = 0)] string Name,
	[property: Display(Order = 1)] string Email);

internal interface IContactStore
{
	IReadOnlyList<Contact> All();
	Contact? Get(string name);
	void Add(Contact contact);
	bool Remove(string name);
}

internal sealed class InMemoryContactStore : IContactStore
{
	private readonly List<Contact> _contacts =
	[
		new Contact("Carl de Billy", "carl@gmail.com"),
		new Contact("Bob Smith", "bob@company.com"),
	];

	public IReadOnlyList<Contact> All() => _contacts;

	public Contact? Get(string name) =>
		_contacts.FirstOrDefault(
			contact => string.Equals(contact.Name, name, StringComparison.OrdinalIgnoreCase));

	public void Add(Contact contact)
	{
		ArgumentNullException.ThrowIfNull(contact);
		_contacts.Add(contact);
	}

	public bool Remove(string name)
	{
		var contact = Get(name);
		if (contact is null)
		{
			return false;
		}

		return _contacts.Remove(contact);
	}
}
