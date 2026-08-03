namespace SqlServerSimulator.Parser.Tokens;

abstract class Name : StringToken
{
    /// <param name="command">The command text being tokenized.</param>
    /// <param name="index">Start of the token within <paramref name="command"/>.</param>
    /// <param name="length">Length of the token's source text, delimiters included.</param>
    /// <param name="value">
    /// The identifier's own characters — the undelimited body for a delimited
    /// identifier, with <c>]]</c> / <c>""</c> escapes already collapsed. Real
    /// measures the 128-character limit against these rather than the source
    /// text, so <c>[</c> + 128 characters + <c>]</c> is legal and an escaped
    /// bracket counts once (probe-confirmed), and Msg 103 quotes the first 128
    /// characters of the body.
    /// </param>
    private protected Name(string command, int index, int length, ReadOnlySpan<char> value)
        : base(command, index, length)
    {
        if (value.Length > 128)
            throw SimulatedSqlException.IdentifierTooLong(value[..128]);
    }
}
