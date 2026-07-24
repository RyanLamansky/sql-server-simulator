using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Sentinel for the <c>DEFAULT</c> keyword appearing as an element of an
/// <c>INSERT … VALUES (…)</c> tuple (SQL Server allows <c>DEFAULT</c> only
/// there — the FROM-clause table-value constructor rejects it with Msg 156).
/// The sentinel carries no value of its own: the INSERT row encoder detects
/// it per cell and resolves it to the target column's <c>DEFAULT</c>
/// constraint value, or NULL when the column has no default — the same path
/// an omitted column takes. <see cref="Run"/> / <see cref="GetSqlType"/> are
/// therefore never reached on the INSERT path; they throw defensively so any
/// escape (e.g. a future caller that forgets to intercept) fails loudly
/// rather than silently producing a bogus value.
/// </summary>
internal sealed class DefaultValueExpression : Expression
{
    private DefaultValueExpression()
    {
    }

    internal static readonly DefaultValueExpression Instance = new();

    public override SqlValue Run(RuntimeContext runtime) =>
        throw new NotSupportedException("DEFAULT is only valid in INSERT ... VALUES and must be resolved by the INSERT encoder.");

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        throw new NotSupportedException("DEFAULT is only valid in INSERT ... VALUES and must be resolved by the INSERT encoder.");

    internal override string DebugDisplay() => "DEFAULT";
}
