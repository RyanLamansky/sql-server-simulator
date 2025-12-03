using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

abstract class Name : StringToken
{
    /// <summary>
    /// Creates a <see cref="Name"/> from a plain string.
    /// </summary>
    /// <param name="value">Transfers to <see cref="StringToken.Value"/>.</param>
    private protected Name(string value)
        : base(value)
    {
    }

    /// <summary>
    /// Creates a <see cref="Name"/> from the provided <paramref name="buffer"/> and then <see cref="StringBuilder.Clear"/>s it.
    /// </summary>
    /// <param name="buffer">The source of the string.</param>
    private protected Name(StringBuilder buffer)
        : base(buffer)
    {
    }
}
