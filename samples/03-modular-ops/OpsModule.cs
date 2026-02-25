using System.ComponentModel;
using Repl;

public sealed class OpsModule : IReplModule
{
	public void Map(IReplMap map)
	{
		// Tiny independent module used as a "liveness" command at root.
		map.Context("ops", ops =>
		{
			ops.Map(
				"ping",
				[Description("Health probe at root")]
				() => "pong");
		});
	}
}
