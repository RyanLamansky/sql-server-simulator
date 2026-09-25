using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// One operand of a type-pair legality check, carrying what real's diagnostic
/// says about it beyond its type: the expression, whose
/// <see cref="Expression.ResultReportsNumeric"/> says whether a <c>decimal</c>
/// operand spells itself <c>numeric</c>, and — for an operand that is a column
/// reference — the object and column Msg 260 names in place of Msg 257's bare
/// types.
/// </summary>
/// <remarks>
/// The expression is kept rather than its flag because the flag walks the
/// operand's whole tree: read for every node of a flat 50,000-term chain, it
/// made the bind quadratic and as deep as the chain. Only an error message
/// reads it.
/// </remarks>
internal readonly struct TypePairOperand(SqlType type, Expression? source = null, string? sourceTable = null, string? sourceColumn = null, bool reportsNumeric = false)
{
    public readonly SqlType Type = type;

    public readonly Expression? Source = source;

    /// <summary>
    /// The FROM clause's own spelling of the column's object (the alias is
    /// ignored; a derived table or CTE answers with its alias), or null when
    /// the operand isn't a column reference.
    /// </summary>
    public readonly string? SourceTable = sourceTable;

    public readonly string? SourceColumn = sourceColumn;

    /// <summary>
    /// A set operation's column arrives with no one expression to ask, so its
    /// precomputed <see cref="Expression.ResultReportsNumeric"/> rides here.
    /// </summary>
    public readonly bool ReportsNumeric = reportsNumeric;
}
