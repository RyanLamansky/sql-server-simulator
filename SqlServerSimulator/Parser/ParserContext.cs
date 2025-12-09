using SqlServerSimulator.Parser.Tokens;
using System.Collections.Frozen;
using System.Data.Common;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Organizes relevant information for parsing of SQL commands.
/// </summary>
internal sealed class ParserContext(SimulatedDbCommand command)
{
#pragma warning disable CA2213 // Disposable fields should be disposed
    public readonly SimulatedDbCommand Command = command;
#pragma warning restore CA2213 // Suppressed because ParserContext doesn't own the command object.

    private readonly string commandText = string.IsNullOrEmpty(command.CommandText) ?
        throw new InvalidOperationException("ExecuteReader: CommandText property has not been initialized") :
        command.CommandText;

    /// <summary>
    /// The tokenizer position within <see cref="commandText"/>.
    /// </summary>
    private int index = -1;

    /// <summary>
    /// The most recently identified token in the command string.
    /// </summary>
    public Token? Token { get; private set; }

    private readonly FrozenDictionary<string, (string Name, (DataType type, object? Value) TypeValue)> variables = command
        .Parameters
        .Cast<DbParameter>()
        .Select(parameter =>
        {
            var name = parameter.ParameterName;
            var type = DataType.GetByDbType(parameter.DbType);
            return (Name: name.StartsWith('@') ? name[1..] : name, TypeValue: (DataType: type, Value: parameter.Value is null ? null : type.ConvertFrom(parameter.Value)));
        })
        .ToFrozenDictionary(tuple => tuple.Name, StringComparer.InvariantCultureIgnoreCase);

    public Simulation Simulation => Command.simulation;

    /// <summary>
    /// Gets the value of the variable with the provided <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The name of the variable.</param>
    /// <returns>The variable's value.</returns>
    /// <exception cref="SimulatedSqlException">Must declare the scalar variable \"@{value of <paramref name="name"/>}\".</exception>
    public object? GetVariableValue(string name) =>
        variables.TryGetValue(name, out var value)
        ? value.TypeValue.Value
        : throw new SimulatedSqlException($"Must declare the scalar variable \"@{name}\".");

    /// <summary>
    /// Advances <see cref="Token"/> to the next token, if one exists.
    /// </summary>
    public void MoveNextOptional()
    {
        _ = MoveNext();
    }

    /// <summary>
    /// Returns the next token in the enumeration, or null.
    /// Also updates <see cref="Token"/> to the new value.
    /// </summary>
    /// <returns>The next token if the enumerator was advanced, otherwise null.</returns>
    public Token? GetNextOptional()
    {
        return MoveNext() ? this.Token : null;
    }

    /// <summary>
    /// Returns the next token in the enumeration, throwing an exception if the end was reached instead.
    /// Also updates <see cref="Token"/> to the new value.
    /// </summary>
    /// <returns>The next token.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public Token GetNextRequired()
    {
        var previous = this.Token;
        return MoveNext() ? this.Token! : throw SimulatedSqlException.SyntaxErrorNear(previous);
    }

    /// <summary>
    /// Returns the next token in the enumeration, throwing an exception if the end was reached instead or the token is the wrong type.
    /// Also updates <see cref="Token"/> to the new value.
    /// </summary>
    /// <typeparam name="T">The expected type of the new token.</typeparam>
    /// <returns>The next token.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public T GetNextRequired<T>()
        where T : Token
    {
        var previous = this.Token;

        return MoveNext() && this.Token is T current ? current : throw SimulatedSqlException.SyntaxErrorNear(previous);
    }

    /// <summary>
    /// Advances <see cref="Token"/> to the next token in the enumeration, throwing an exception if the end was reached instead or the token is the wrong type.
    /// </summary>
    /// <typeparam name="T">The expected type of the new token.</typeparam>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public void MoveNextRequired<T>()
        where T : Token
    {
        var previous = this.Token;

        if (!MoveNext() || this.Token is not T)
            throw SimulatedSqlException.SyntaxErrorNear(previous);
    }

    /// <summary>
    /// Advances <see cref="Token"/> to the next token in the enumeration, throwing an exception if the end was reached instead.
    /// </summary>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public void MoveNextRequired()
    {
        var previous = this.Token;
        if (!MoveNext())
            throw SimulatedSqlException.SyntaxErrorNear(previous);
    }

    private bool MoveNext()
    {
        while (Tokenizer.NextToken(commandText, ref index) is Token token)
        {
            if (token is Whitespace)
                continue;

            this.Token = token;
            return true;
        }

        this.Token = null;
        return false;
    }

#if DEBUG
    /// <summary>
    /// Returns a string representation of the tokenized command.
    /// The <see cref="Token"/> token is wrapped by '»' and '«'.
    /// </summary>
    /// <returns>The string representation.</returns>
    public override string ToString()
    {
        var command = this.commandText;
        Span<char> result = stackalloc char[command.Length + 2];
        if (index < 0)
        {
            result[0] = '»';
            result[1] = '«';
            command.CopyTo(result[2..]);
        }
        else if (index >= command.Length)
        {
            command.CopyTo(result);
            result[^2] = '»';
            result[^1] = '«';
        }
        else
        {
            System.Diagnostics.Debug.Assert(this.Token is not null);
            this.Token.Highlight(result);
        }

        return new string(result);
    }
#endif
}
