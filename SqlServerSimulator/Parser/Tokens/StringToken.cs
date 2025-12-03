using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

abstract class StringToken : Token
{
    public readonly string Value;

    /// <summary>
    /// Creates a <see cref="StringToken"/> from a plain string.
    /// </summary>
    /// <param name="value">Transfers to <see cref="Value"/>.</param>
    private protected StringToken(string value) => this.Value = value;

    /// <summary>
    /// Creates a <see cref="StringToken"/> from the provided <paramref name="buffer"/> and then <see cref="StringBuilder.Clear"/>s it.
    /// </summary>
    /// <param name="buffer">The source of the string.</param>
    private protected StringToken(StringBuilder buffer)
        : this(buffer.ToString())
    {
        _ = buffer.Clear();
    }
}
