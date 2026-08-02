using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Parse-time context for <see cref="Expression.ResultIsNullable"/>: the
/// per-source column facts a projection's nullability inference needs, plus
/// the <see cref="BatchContext"/> two of SQL Server's rules can't be answered
/// without.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ColumnType"/> is what separates the two meanings of <c>+</c> —
/// real projects a string / binary concatenation NOT NULL when both operands
/// are (<c>col_a + col_b</c> over two NOT NULL <c>varchar</c>s), while
/// arithmetic <c>+</c> claims nullable even over two non-null <c>int</c>s
/// (even <c>1 + 1</c>). The dispatch is by operand type, so the inference has
/// to resolve one.
/// </para>
/// <para>
/// <see cref="Batch"/> drives the constant folds real applies before it
/// computes nullability: a <c>CASE</c> / <c>IIF</c> arm whose condition folds
/// to a constant, and the <c>NULLIF</c> / <c>COALESCE</c> shapes that desugar
/// into one. See <see cref="Expression.ResultIsNullable"/> for the rule set.
/// </para>
/// </remarks>
internal readonly struct NullabilityContext(
    BatchContext batch,
    Func<MultiPartName, bool> columnIsNullable,
    Func<MultiPartName, SqlType> columnType)
{
    /// <summary>The parsing batch, for the constant folds and for type resolution.</summary>
    internal readonly BatchContext Batch = batch;

    /// <summary>Whether a named source column is declared nullable. Unresolvable names answer nullable.</summary>
    internal readonly Func<MultiPartName, bool> ColumnIsNullable = columnIsNullable;

    /// <summary>The declared type of a named source column.</summary>
    internal readonly Func<MultiPartName, SqlType> ColumnType = columnType;

    /// <summary>The static result type of <paramref name="expression"/> under this context's resolvers.</summary>
    internal SqlType TypeOf(Expression expression) =>
        expression.GetSqlType(this.Batch, this.ColumnType);

    /// <summary>
    /// Evaluates <paramref name="expression"/> when real would have folded it
    /// to a constant before inferring nullability. Returns <see langword="false"/>
    /// for anything reaching a column, a variable, a subquery or server state,
    /// and for a fold that raises (<c>1 / 0</c>) — real leaves those standing,
    /// and a metadata inference must never be the thing that surfaces an error
    /// the statement's own evaluation would raise.
    /// </summary>
    internal bool TryFold(Expression expression, out SqlValue value)
    {
        if (expression.IsWrittenConstant)
        {
            try
            {
                value = expression.Run(UnreachableRow());
                return true;
            }
            catch (Exception e) when (e is SimulatedSqlException or NotSupportedException)
            {
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Evaluates a <c>CASE</c> / <c>IIF</c> condition real folds at compile
    /// time, reporting whether the branch it guards is taken. UNKNOWN reports
    /// not-taken, matching the runtime rule that only TRUE selects a branch.
    /// </summary>
    internal bool TryFoldCondition(BooleanExpression condition, out bool branchTaken)
    {
        if (condition.IsWrittenConstant)
        {
            try
            {
                branchTaken = condition.Run(UnreachableRow()) == true;
                return true;
            }
            catch (Exception e) when (e is SimulatedSqlException or NotSupportedException)
            {
            }
        }

        branchTaken = false;
        return false;
    }

    /// <summary>
    /// A runtime context for folding a written constant: it reaches no column,
    /// so the resolver is unreachable rather than merely unused.
    /// </summary>
    private RuntimeContext UnreachableRow() =>
        new(static _ => throw new NotSupportedException(), this.Batch);
}
