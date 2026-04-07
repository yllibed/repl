namespace Repl;

internal readonly record struct TextTableStyle(Func<int, int, string, string>? CellFormatter)
{
	public static TextTableStyle None { get; } = new(CellFormatter: null);

	public static TextTableStyle ForHeader(string headerStyle) =>
		new((row, _, value) =>
			row == 0 ? AnsiText.Apply(value, headerStyle) : value);
}
