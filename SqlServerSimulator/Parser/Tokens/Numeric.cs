namespace SqlServerSimulator.Parser.Tokens;

internal sealed class Numeric : Token
{
    public readonly ISpanFormattable Value;

    public Numeric(string command, int index, int length) : base(command, index, length)
    {
        var number = base.Source;

        if (int.TryParse(number, out var int32))
        {
            this.Value = int32;
            return;
        }

        if (long.TryParse(number, out var int64))
        {
            this.Value = int64;
            return;
        }

        if (double.TryParse(number, out var float64))
        {
            this.Value = float64;
            return;
        }

        throw new NotSupportedException($"Simulated command tokenizer couldn't parse {number} as a number.");
    }
}
