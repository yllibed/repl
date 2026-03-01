namespace Repl.Internal.Options;

internal enum OptionSchemaTokenKind
{
	NamedOption = 0,
	BoolFlag = 1,
	ReverseFlag = 2,
	ValueAlias = 3,
	EnumAlias = 4,
}
