namespace SqlServerSimulator.Parser.Tokens;

abstract class Name : StringToken
{
    private protected Name(string command, int index, int length)
        : base(command, index, length)
    {
        if (length > 128)
            throw SimulatedSqlException.IdentifierTooLong(command.AsSpan(index, 128));
    }
}
