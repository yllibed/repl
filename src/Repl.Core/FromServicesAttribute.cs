namespace Repl;

/// <summary>
/// Forces a parameter to resolve from dependency injection services.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromServicesAttribute : Attribute
{
	/// <summary>
	/// Gets the optional keyed service identifier.
	/// </summary>
	public string? Key { get; }

	/// <summary>
	/// Creates an attribute that resolves from the default service registration.
	/// </summary>
	public FromServicesAttribute()
	{
	}

	/// <summary>
	/// Creates an attribute that resolves from a keyed service registration.
	/// </summary>
	/// <param name="key">Service key.</param>
	public FromServicesAttribute(string key)
	{
		Key = string.IsNullOrWhiteSpace(key)
			? throw new ArgumentException("Service key cannot be empty.", nameof(key))
			: key;
	}
}
