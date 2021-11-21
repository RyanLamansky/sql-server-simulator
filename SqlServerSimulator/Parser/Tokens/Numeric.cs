namespace SqlServerSimulator.Parser.Tokens;

internal sealed class Numeric : Token
{
    public readonly decimal Value;

    public Numeric(decimal value) => this.Value = value;

#if DEBUG
    public override string ToString() => this.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
#endif
}
