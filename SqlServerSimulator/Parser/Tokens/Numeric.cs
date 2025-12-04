using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

internal sealed class Numeric : Token
{
    public readonly ISpanFormattable Value;

    public Numeric(StringBuilder buffer)
    {
        Span<char> number = stackalloc char[buffer.Length];
        buffer.CopyTo(0, number, buffer.Length);
        _ = buffer.Clear();

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

        throw new SimulatedSqlException($"Simulated command tokenizer couldn't parse {number} as a number.");
    }
    public override string ToString() => this.Value.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
}
