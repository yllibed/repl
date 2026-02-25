namespace Repl;

/// <summary>
/// Root options for REPL behavior.
/// </summary>
public sealed class ReplOptions
{
	/// <summary>
	/// Initializes a new instance of the <see cref="ReplOptions"/> class.
	/// </summary>
	public ReplOptions()
	{
		Parsing = new ParsingOptions();
		Interactive = new InteractiveOptions();
		Output = new OutputOptions();
		Binding = new BindingOptions();
		Capabilities = new CapabilityOptions();
		AmbientCommands = new AmbientCommandOptions();
		Interaction = new InteractionOptions();
	}

	/// <summary>
	/// Gets parsing options.
	/// </summary>
	public ParsingOptions Parsing { get; }

	/// <summary>
	/// Gets interactive options.
	/// </summary>
	public InteractiveOptions Interactive { get; }

	/// <summary>
	/// Gets output options.
	/// </summary>
	public OutputOptions Output { get; }

	/// <summary>
	/// Gets binding options.
	/// </summary>
	public BindingOptions Binding { get; }

	/// <summary>
	/// Gets host capability options.
	/// </summary>
	public CapabilityOptions Capabilities { get; }

	/// <summary>
	/// Gets ambient command options.
	/// </summary>
	public AmbientCommandOptions AmbientCommands { get; }

	/// <summary>
	/// Gets prompt interaction options.
	/// </summary>
	public InteractionOptions Interaction { get; }
}
