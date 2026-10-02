using SqlServerSimulator.Parser.FullText;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>CONTAINS(column_spec, 'contains_search_condition' [, LANGUAGE n])</c> and
/// <c>FREETEXT(column_spec, 'freetext_string' [, LANGUAGE n])</c> — the two
/// full-text predicates. Both are boolean-only: real reserves the names, so
/// <c>SELECT CONTAINS(…)</c> is a syntax error and the construct is legal only
/// where a predicate is expected.
/// </summary>
/// <remarks>
/// <para>
/// The column specification binds at parse time, which is where real reports
/// its Msg 7601 — a <c>CREATE PROCEDURE</c> whose body names a table with no
/// full-text index fails to create, exactly as one naming a missing column
/// fails with Msg 207.
/// </para>
/// <para>
/// A <b>literal</b> condition is parsed at parse time too, so a syntax error
/// (Msg 7630) surfaces at statement compile — including inside a never-taken
/// <c>IF</c> branch, which real also rejects. The one place real defers is a
/// module body: <c>CREATE PROCEDURE</c> stores a bad condition happily and
/// raises at <c>EXEC</c>, so the parse is skipped while
/// <see cref="BatchContext.CreateTimeBinding"/> is set. A condition arriving
/// through a variable or parameter parses per execution, matching real.
/// </para>
/// <para>
/// A condition holding only system stopwords matches nothing and raises real's
/// severity-10 <b>Msg 9927</b> through the <c>InfoMessage</c> surface.
/// </para>
/// </remarks>
internal sealed class FullTextPredicate : BooleanExpression
{
    private readonly FullTextBinding binding;
    private readonly Expression condition;
    private readonly Expression? language;
    private readonly bool freeText;
    private readonly FullTextSearchCondition? parsedCondition;

    private FullTextPredicate(FullTextBinding binding, Expression condition, Expression? language, bool freeText, FullTextSearchCondition? parsedCondition)
    {
        this.binding = binding;
        this.condition = condition;
        this.language = language;
        this.freeText = freeText;
        this.parsedCondition = parsedCondition;
    }

    /// <summary>
    /// Parses either predicate with the cursor on the <c>CONTAINS</c> /
    /// <c>FREETEXT</c> keyword; on return the cursor sits one past the closing
    /// <c>)</c>.
    /// </summary>
    public static new BooleanExpression Parse(ParserContext context)
    {
        var freeText = context.Token is ReservedKeyword { Keyword: Keyword.FreeText };
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var spec = FullTextColumnSpec.Parse(context);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var condition = ParseLiteralOrVariable(context.MoveNextRequiredReturnSelf(), numberAllowed: false);

        // `, LANGUAGE n` selects the stoplist and morphology the condition
        // is read with; without it, the first searched column's language.
        Expression? language = null;
        if (context.Token is Operator { Character: ',' })
        {
            if (context.GetNextRequired() is not Name languageToken
                || !context.Batch.CurrentDatabase.Collation.Equals(languageToken.Value, "LANGUAGE"))
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            language = ParseLiteralOrVariable(context.MoveNextRequiredReturnSelf(), numberAllowed: true);
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var binding = FullTextScope.Bind(context, spec);
        var parsed = TryParseLiteralCondition(context, condition, language, binding, freeText);
        return new FullTextPredicate(binding, condition, language, freeText, parsed);
    }

    /// <summary>
    /// Reads a full-text condition or <c>LANGUAGE</c> argument, which real's
    /// grammar takes only as a string literal or a variable — and the language
    /// as a number or a binary (an LCID) too: a column, a function call, a parenthesized or
    /// concatenated value, a binary literal and <c>NULL</c> are each a syntax
    /// error at the offending token (probed 2026-10-02 against SQL Server
    /// 2025). Shared with the rowset forms.
    /// </summary>
    internal static Expression ParseLiteralOrVariable(ParserContext context, bool numberAllowed)
    {
        var accepted = context.Token switch
        {
            Literal { Value.Type.Category: SqlTypeCategory.String } or AtPrefixedString => true,
            Numeric or Literal { Value.Type: VarbinarySqlType } => numberAllowed,
            _ => false,
        };
        if (!accepted)
            throw SyntaxErrorAt(context);
        var checkpoint = context.SaveCheckpoint();
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: ',' or ')' })
            throw SyntaxErrorAt(context);
        context.RestoreCheckpoint(checkpoint);
        return Expression.Parse(context);
    }

    private static SimulatedSqlException SyntaxErrorAt(ParserContext context) =>
        context.Token is ReservedKeyword keyword
            ? SimulatedSqlException.SyntaxErrorNearKeyword(keyword)
            : SimulatedSqlException.SyntaxErrorNear(context);

    /// <summary>
    /// Parses the condition ahead of execution when it is a compile-time
    /// constant, so its syntax errors land where real puts them. Returns null
    /// when the condition can only be read per row.
    /// </summary>
    private static FullTextSearchCondition? TryParseLiteralCondition(ParserContext context, Expression condition, Expression? language, FullTextBinding binding, bool freeText)
    {
        if (context.Batch.CreateTimeBinding || condition.ContainsVariableReference || language is { ContainsVariableReference: true })
            return null;
        SqlValue value;
        SqlValue? languageValue = null;
        try
        {
            var probe = new RuntimeContext(
                _ => throw new InvalidOperationException("Not parse-time constant."),
                context.Batch);
            value = condition.Run(probe);
            if (language is not null)
                languageValue = language.Run(probe);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        return Compile(value, languageValue, binding, freeText);
    }

    /// <summary>
    /// Turns a condition value into a match tree, applying real's NULL / empty
    /// rejection first (Msg 7645).
    /// </summary>
    private static FullTextSearchCondition Compile(SqlValue value, SqlValue? languageValue, FullTextBinding binding, bool freeText)
    {
        if (value.IsNull || string.IsNullOrWhiteSpace(value.AsString))
            throw SimulatedSqlException.FullTextNullOrEmptyPredicate();
        var text = value.AsString;
        var language = languageValue is { } written ? FullTextLanguage.Resolve(written) : binding.DefaultQueryLanguage;
        return freeText
            ? FullTextSearchCondition.ParseFreeText(text, binding.AccentSensitive, language, binding.UsesStoplist)
            : FullTextSearchCondition.ParseContains(text, binding.AccentSensitive, language, binding.UsesStoplist);
    }

    public override bool? Run(RuntimeContext runtime)
    {
        var compiled = this.parsedCondition ?? Compile(this.condition.Run(runtime), this.language?.Run(runtime), this.binding, this.freeText);
        if (compiled.SawStopword)
            runtime.Batch.AppendFullTextNoiseWordMessage();
        return compiled.Matches(this.binding.BuildDocument(runtime.ResolveColumn));
    }

    internal override void VisitOperandExpressions(Action<Expression> visitor)
    {
        visitor(this.condition);
        if (this.language is not null)
            visitor(this.language);
    }

    internal override string DebugDisplay() =>
        $"{(this.freeText ? "FREETEXT" : "CONTAINS")}({string.Join(", ", this.binding.ColumnNames)}, {this.condition.DebugDisplay()})";

    internal override void Describe(NodeShape shape)
    {
        _ = shape.Local(this.freeText).Local(this.binding.Table).Local(this.binding.ColumnNames.Length);
        foreach (var name in this.binding.ColumnNames)
            _ = shape.Local(name.ToString());
        _ = shape.Child(this.condition);
        if (this.language is not null)
            _ = shape.Child(this.language);
    }
}
