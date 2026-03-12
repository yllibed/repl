namespace Repl.IntegrationTests;

[Flags]
internal enum SamplePermissions
{
	[System.ComponentModel.Description("View items")]
	Read = 1,
	[System.ComponentModel.Description("Edit items")]
	Write = 2,
	[System.ComponentModel.Description("Remove items")]
	Delete = 4,
}
