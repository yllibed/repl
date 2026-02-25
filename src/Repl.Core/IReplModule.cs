namespace Repl;

/// <summary>
/// Defines a reusable command module.
/// </summary>
public interface IReplModule
{
	/// <summary>
	/// Registers module routes into the target map.
	/// </summary>
	/// <param name="map">Destination map.</param>
	void Map(IReplMap map);
}
