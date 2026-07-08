namespace Repl;

/// <summary>Classification of one committed interactive input line.</summary>
internal enum CommittedKind
{
	Ambient,
	Help,
	Ambiguous,
	Routed,
}
