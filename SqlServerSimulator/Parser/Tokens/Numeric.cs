namespace SqlServerSimulator.Parser.Tokens;

internal sealed class Numeric : Token
{
    public readonly DataValue Value;

    public Numeric(string command, int index, int length) : base(command, index, length)
    {
        var number = base.Source;

        if (int.TryParse(number, out var int32))
        {
            this.Value = new(int32, DataType.BuiltInDbInt32);
            return;
        }

        throw new NotSupportedException($"Simulated command tokenizer couldn't parse {number} as a number.");
    }
}
