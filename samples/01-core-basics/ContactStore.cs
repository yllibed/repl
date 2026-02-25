using System.ComponentModel.DataAnnotations;

sealed record Contact(
	[property: Display(Name = "#", Order = 0)] int Id,
	[property: Display(Name = "Full Name", Order = 1)] string Name,
	[property: Display(Name = "Email", Order = 2)] string Email,
	[property: Display(Name = "Phone", Order = 3), DisplayFormat(NullDisplayText = "-")] string? Phone = null);

internal sealed class ContactStore
{
	private readonly List<Contact> _contacts =
	[
		new Contact(1, "Alice Martin", "alice@example.com", "555-0101"),
		new Contact(2, "Bob Tremblay", "bob@example.com"),
	];

	public List<Contact> List() => _contacts;

	public Contact? Find(int id) => _contacts.FirstOrDefault(contact => contact.Id == id);

	public Contact Add(string name, string email)
	{
		var nextId = _contacts.Count == 0 ? 1 : _contacts.Max(contact => contact.Id) + 1;
		var contact = new Contact(nextId, name, email);
		_contacts.Add(contact);
		return contact;
	}

	public int Count() => _contacts.Count;

	public void Reset()
	{
		_contacts.Clear();
		_contacts.Add(new Contact(1, "Alice Martin", "alice@example.com", "555-0101"));
		_contacts.Add(new Contact(2, "Bob Tremblay", "bob@example.com"));
	}
}
