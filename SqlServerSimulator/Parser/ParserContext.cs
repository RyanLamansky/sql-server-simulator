using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Collections.Frozen;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Organizes relevant information for parsing of SQL commands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lookahead contract.</b> Every <c>Parse</c>-style helper in this
/// namespace (e.g. <see cref="Expression.Parse(ParserContext)"/>,
/// <see cref="Selection.Parse(ParserContext, uint)"/>,
/// <see cref="BooleanExpression.Parse"/>) leaves <see cref="Token"/> at the
/// first token it did <i>not</i> consume — its caller's lookahead position.
/// A helper that reads up to and including a closing delimiter (e.g. a
/// function call's <c>)</c>) leaves <see cref="Token"/> on that delimiter;
/// the surrounding loop's next <see cref="GetNextOptional"/> /
/// <see cref="MoveNext"/> advances past it. Callers must not "step back" or
/// "step forward" to re-align after a Parse returns.
/// </para>
/// <para>
/// This contract is what makes recursive descent compose. Violations show up
/// as silently dropped tokens. When in doubt, read a token at the call site,
/// decide whether to consume it, and never assume a previous Parse left the
/// cursor "before" or "after" something the contract didn't promise.
/// </para>
/// </remarks>
internal sealed class ParserContext(SimulatedDbCommand command)
{
#pragma warning disable CA2213 // Disposable fields should be disposed
    public readonly SimulatedDbCommand Command = command;
#pragma warning restore CA2213 // Suppressed because ParserContext doesn't own the command object.

    private readonly string commandText = string.IsNullOrEmpty(command.CommandText) ?
        throw new InvalidOperationException("ExecuteReader: CommandText property has not been initialized") :
        command.CommandText;

    /// <summary>
    /// The tokenizer position within <see cref="commandText"/>: the next
    /// un-read character. <see cref="MoveNext"/> advances this past the
    /// returned token (see <see cref="Tokenizer"/>'s index contract).
    /// </summary>
    private int index;

    /// <summary>
    /// The most recently identified token in the command string.
    /// </summary>
    public Token? Token;

    private readonly FrozenDictionary<string, SqlValue> variables = command
        .Parameters
        .Cast<DbParameter>()
        .ToFrozenDictionary(parameter =>
        {
            var name = parameter.ParameterName;
            return name.StartsWith('@') ? name[1..] : name;
        }, parameter => ConvertParameter(parameter.Value, SqlType.GetByDbType(parameter.DbType)),
        StringComparer.InvariantCultureIgnoreCase);

    private static SqlValue ConvertParameter(object? raw, SqlType type) =>
        raw is null or DBNull ? SqlValue.Null(type) : type.ConvertParameter(raw);

    public Simulation Simulation => Command.simulation;

    /// <summary>
    /// Gets the value of the variable with the provided <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The name of the variable.</param>
    /// <returns>The variable's value.</returns>
    /// <exception cref="SimulatedSqlException">Must declare the scalar variable \"@{value of <paramref name="name"/>}\".</exception>
    public SqlValue GetVariableValue(string name) =>
        variables.TryGetValue(name, out var value)
        ? value
        : throw SimulatedSqlException.MustDeclareScalarVariable(name);

    /// <summary>
    /// Advances <see cref="Token"/> to the next token, if one exists.
    /// </summary>
    public void MoveNextOptional()
    {
        _ = MoveNext();
    }

    /// <summary>
    /// Returns the next token in the enumeration, or null.
    /// </summary>
    /// <returns>The next token if the enumerator was advanced, otherwise null.</returns>
    public Token? GetNextOptional()
    {
        return MoveNext() ? this.Token : null;
    }

    /// <summary>
    /// Returns the next token in the enumeration, throwing an exception if the end was reached instead.
    /// </summary>
    /// <returns>The next token.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public Token GetNextRequired()
    {
        var previous = this.Token;
        return MoveNext() ? this.Token : throw SimulatedSqlException.SyntaxErrorNear(previous);
    }

    /// <summary>
    /// Returns the next token in the enumeration, throwing an exception if the end was reached instead or the token is the wrong type.
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
    /// Advances <see cref="Token"/> to the next token in the enumeration, throwing an exception if the end was reached instead.
    /// The <see cref="ParserContext"/> used for this call is returned.
    /// </summary>
    /// <returns>This instance.</returns>
    /// <exception cref="SimulatedSqlException">Incorrect syntax near '{token}'.</exception>
    public ParserContext MoveNextRequiredReturnSelf()
    {
        this.MoveNextRequired();
        return this;
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

    /// <summary>
    /// Updates <see cref="Token"/> with the next usable token in <see cref="commandText"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Whitespace"/> and <see cref="Comment"/> tokens are skipped.
    /// <see cref="index"/> is updated to the position of the next token.
    /// </remarks>
    /// <returns>True if another token was found, otherwise false.</returns>
    [MemberNotNullWhen(true, nameof(Token))]
    public bool MoveNext()
    {
        while (Tokenizer.NextToken(commandText, ref index) is Token token)
        {
            if (token is Whitespace or Comment)
                continue;

#if DEBUG
            tokens.Add(token);
#endif
            this.Token = token;
            return true;
        }

        this.Token = null;
        return false;
    }

#if DEBUG
    /// <summary>
    /// Contains all the non-whitespace tokens that have been read so far.
    /// </summary>
    private readonly List<Token> tokens = [];

    /// <summary>
    /// Returns a string representation of the tokenized command.
    /// The <see cref="Token"/> token is wrapped by '»' and '«'.
    /// </summary>
    /// <returns>The string representation.</returns>
    public override string ToString()
    {
        var command = this.commandText;
        Span<char> result = stackalloc char[command.Length + 2];
        if (this.Token is { } token)
        {
            token.Highlight(result);
        }
        else if (index >= command.Length)
        {
            command.CopyTo(result);
            result[^2] = '»';
            result[^1] = '«';
        }
        else
        {
            // Pre-MoveNext state: cursor at the start.
            result[0] = '»';
            result[1] = '«';
            command.CopyTo(result[2..]);
        }

        return new string(result);
    }
#endif
}
