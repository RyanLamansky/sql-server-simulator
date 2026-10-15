namespace SqlServerSimulator.Parser.Tokens;

/// <summary>
/// The quoted string, delimited identifier or block comment a batch ends
/// inside, which the parser reads in place of the tokens the text never got
/// to. Real's lexer reports it (Msg 105 / 113) and hands its parser nothing
/// more, so the parse ends there: a statement complete before it stands and
/// the lexer's error is the whole report, while one that needed more adds the
/// syntax error its parser raises there (probed 2026-10-15 against SQL Server
/// 2025: <c>select 1 'x</c> is Msg 105 alone, <c>select 1 as 'x</c> Msg 105
/// then Msg 102 near <c>'x'</c>, <c>select 1 + /*x</c> Msg 113 then Msg 102
/// near <c>'+'</c>).
/// </summary>
/// <remarks>
/// The token is a statement boundary (<c>Simulation.IsStatementBoundary</c>)
/// and nothing else, so a construct that can end before it does, and the
/// dispatch loop meeting it as a statement's start raises <see cref="Alone"/>;
/// a site that can't end raises its syntax error on it, which
/// <see cref="SimulatedSqlException.SyntaxErrorNear(Token?, byte)"/> turns
/// into <see cref="WithSyntaxError"/>.
/// </remarks>
/// <param name="command">The batch text.</param>
/// <param name="index">Where the unterminated string or comment opens.</param>
/// <param name="previous">The token the parser stood on before this one, which a comment's Msg 102 names.</param>
internal sealed class UnterminatedToken(string command, int index, Token? previous) : Token(command, index, command.Length - index)
{

    private bool IsComment => this.command[this.StartIndex] == '/';

    /// <summary>
    /// The unclosed string's body as the tokenizer scanned it: everything past
    /// the opening delimiter, each doubled closing delimiter read as one.
    /// </summary>
    private string Body
    {
        get
        {
            var open = this.command[this.StartIndex] is 'N' or 'n' ? this.StartIndex + 1 : this.StartIndex;
            var close = this.command[open] == '[' ? ']' : this.command[open];
            return this.command[(open + 1)..].Replace(new string(close, 2), close.ToString(), StringComparison.Ordinal);
        }
    }

    public override string ErrorText => this.IsComment ? previous?.ErrorText ?? "" : Clip(this.Body);

    /// <summary>The lexer's error alone, which a batch whose statements parsed whole before it reports.</summary>
    public SimulatedSqlException Alone => this.IsComment
        ? SimulatedSqlException.MissingEndCommentMark(LineAt(this.command, this.command.Length))
        : SimulatedSqlException.UnclosedQuotationMark(this.Body, this.LineNumber);

    /// <summary>The lexer's error and the syntax error a statement that needed more raises on it.</summary>
    public SimulatedSqlException WithSyntaxError => this.IsComment
        ? SimulatedSqlException.MissingEndCommentMark(LineAt(this.command, this.command.Length), previous)
        : SimulatedSqlException.UnclosedStringLiteral(this.Body, this.LineNumber);
}
