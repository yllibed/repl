public interface IEntity
{
	static abstract string EntityName { get; }
}

public sealed record ClientRecord(string Id, string Name) : IEntity
{
	public static string EntityName => "client";
}

public sealed record ContactRecord(string Id, string DisplayName) : IEntity
{
	public static string EntityName => "contact";
}

public sealed record InvoiceRecord(string Id, string Title) : IEntity
{
	public static string EntityName => "invoice";
}
