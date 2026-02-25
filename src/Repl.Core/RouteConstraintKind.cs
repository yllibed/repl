namespace Repl;

internal enum RouteConstraintKind
{
	String = 0,
	Alpha = 1,
	Bool = 2,
	Email = 3,
	Uri = 4,
	Url = 5,
	Urn = 6,
	Time = 7,
	Date = 8,
	DateTime = 9,
	DateTimeOffset = 10,
	TimeSpan = 11,
	Guid = 12,
	Long = 13,
	Int = 14,
	Custom = 15,
}
