using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Organizes relevant information for parsing of SQL commands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lookahead contract.</b> Every <c>Parse</c>-style helper in this
/// namespace (e.g. <see cref="Expression.Parse(ParserContext)"/>,
/// <see cref="Selection.Parse"/>,
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
internal sealed class ParserContext(SimulatedDbCommand command, BatchContext batch)
{
#pragma warning disable CA2213 // Disposable fields should be disposed
    public readonly SimulatedDbCommand Command = command;
#pragma warning restore CA2213 // Suppressed because ParserContext doesn't own the command object.

    /// <summary>
    /// The owning batch's runtime state (variable slots, undo log). Parsers
    /// route runtime concerns through this backreference; the parser context
    /// itself holds only parse-time scratch (tokenizer cursor, collectors,
    /// outer-type resolver).
    /// </summary>
    public readonly BatchContext Batch = batch;

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

    /// <summary>
    /// True while an <c>Expression.Parse</c> call is running for a
    /// <c>CREATE TABLE</c> column's <c>DEFAULT</c> clause. Set by the
    /// CREATE-TABLE parser around the call to
    /// <see cref="Expression.Parse(ParserContext)"/> and cleared in
    /// <c>finally</c>. Built-in functions whose grammar restricts them to
    /// DEFAULT clauses (currently <c>NEWSEQUENTIALID</c>) inspect this flag
    /// and raise Msg 302 when it isn't set.
    /// </summary>
    public bool InDefaultClause;

    /// <summary>
    /// When non-null, every <see cref="Expressions.AggregateExpression"/>
    /// constructor registers itself here, letting the surrounding
    /// <see cref="Selection"/> parser learn which aggregates appear in the
    /// projection / HAVING clauses without re-walking the expression trees.
    /// Scoped by Selection.Parse: the outer caller sets the list before
    /// parsing projection / HAVING, then snapshots the collected aggregates
    /// and clears it. Nested SELECT scopes each get their own list.
    /// </summary>
    public List<Expressions.AggregateExpression>? AggregateCollector;

    /// <summary>
    /// When non-null, every <see cref="Expressions.WindowExpression"/>
    /// constructor registers itself here. Scoped by Selection.Parse around
    /// projection parsing — the executor needs the list to detect the
    /// windowed-projection branch (buffer + partition + sort + bind) and
    /// to know which expressions to bind row-number values into per row.
    /// </summary>
    public List<Expressions.WindowExpression>? WindowCollector;

    /// <summary>
    /// When false, registering a <see cref="Expressions.WindowExpression"/>
    /// raises Msg 4108 (`"Windowed functions can only appear in the SELECT
    /// or ORDER BY clauses."`). Default true; the Selection parser flips it
    /// false around the WHERE / GROUP BY / HAVING / ON / JOIN-predicate
    /// parses where SQL Server rejects windowed functions.
    /// </summary>
    public bool AllowsWindowExpressions = true;

    /// <summary>
    /// True when expression parsing is inside a clause where SQL Server
    /// rejects <c>NEXT VALUE FOR</c> (probe-confirmed: WHERE / GROUP BY /
    /// HAVING / ORDER BY / TOP / OVER / OUTPUT / ON all raise Msg 11720).
    /// Set by the Selection parser around the affected clauses and consumed
    /// by the <c>NEXT VALUE FOR</c> expression constructor; outside those
    /// scopes (projection / DEFAULT / INSERT VALUES / SET / etc.) the flag
    /// stays false and <c>NEXT VALUE FOR</c> is legal.
    /// </summary>
    public bool RejectNextValueFor;

    /// <summary>
    /// Parse-time chain of outer-scope column-type resolvers, used to plan
    /// the output schema of a correlated subquery whose projection references
    /// an enclosing SELECT's columns. Set by <see cref="Selection"/>'s
    /// FROM-source dispatch around the WHERE / GROUP BY / HAVING parse so
    /// any nested EXISTS / IN(SELECT) parser sees the chained resolver and
    /// passes it down. Each level captures the prior value so the chain
    /// recurses naturally; null means the top-level scope.
    /// </summary>
    public Func<MultiPartName, SqlType>? OuterTypeResolver;

    /// <summary>
    /// Common-table-expression bindings registered by a <c>WITH</c> prefix
    /// that scope to the immediately-following statement. Populated by
    /// <c>Simulation.ParseCteBindings</c> before the SELECT / INSERT /
    /// UPDATE / DELETE / MERGE dispatch and cleared at the top of the next
    /// statement iteration. Consulted by <c>Selection.ParseSingleFromSource</c>
    /// before falling through to <see cref="Database.Schemas"/>; matching
    /// names build a deferred-plan <see cref="FromSource"/> (re-runs per
    /// reference, parallel to derived tables in FROM). Null when no WITH
    /// prefix is in scope.
    /// </summary>
    public Dictionary<string, CteBinding>? CteBindings;

    public Simulation Simulation => Command.simulation;

    /// <summary>
    /// The connection backing <see cref="Command"/>. Always a
    /// <see cref="SimulatedDbConnection"/>: <see cref="SimulatedDbCommand"/>'s
    /// constructor takes one and the setter rejects re-assignment, so once
    /// the command exists this cast is never wrong and never null. Used by
    /// transaction-related parsers and <see cref="Expressions.TranCountExpression"/>
    /// to reach the connection's <see cref="SimulatedDbConnection.CurrentTransaction"/>.
    /// </summary>
    public SimulatedDbConnection Connection => (SimulatedDbConnection)Command.Connection!;

    /// <summary>
    /// The database this batch is executing against. Threads through
    /// <see cref="SimulatedDbConnection.CurrentDatabase"/>; once
    /// <c>USE &lt;db&gt;</c> support lands, switching mid-batch flips this
    /// for subsequent statements without parsers having to thread a separate
    /// pointer.
    /// </summary>
    public Database CurrentDatabase => Connection.CurrentDatabase;

    /// <summary>
    /// Snapshots the current tokenizer position and current token so a
    /// caller can probe the upcoming token via <see cref="MoveNext"/> and
    /// then restore to this point if the lookahead doesn't match. The
    /// tokenizer is index-driven (re-running <see cref="MoveNext"/> from
    /// the saved index produces the same token sequence), so a checkpoint
    /// + restore round-trip is byte-stable.
    /// </summary>
    public (int Index, Token? Token) SaveCheckpoint() => (this.index, this.Token);

    /// <summary>
    /// Restores a checkpoint captured by <see cref="SaveCheckpoint"/>.
    /// </summary>
    public void RestoreCheckpoint((int Index, Token? Token) checkpoint)
    {
        this.index = checkpoint.Index;
        this.Token = checkpoint.Token;
    }

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
    /// Advances <see cref="Token"/> to the next token, throwing an exception
    /// if the end was reached or if the new token isn't of type
    /// <typeparamref name="T"/>. Use when the caller needs the type assertion
    /// but not the token value — pairs with <see cref="GetNextRequired{T}"/>
    /// the same way <see cref="MoveNextRequired"/> pairs with
    /// <see cref="GetNextRequired"/>.
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
