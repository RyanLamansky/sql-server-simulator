using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>REPLACE(input, oldValue, newValue)</c>: replaces every occurrence
/// of <c>oldValue</c> in <c>input</c> with <c>newValue</c>. Matching runs under
/// the collation the three arguments resolve to; the matched segment is removed
/// and the new value substituted, however its case, accent or width differed
/// from the pattern.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/replace-transact-sql</remarks>
internal sealed class Replace : Expression
{
    private readonly Expression input;
    private readonly Expression oldValue;
    private readonly Expression newValue;

    public Replace(ParserContext context)
    {
        this.input = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.oldValue = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.newValue = Parse(context.MoveNextRequiredReturnSelf());
    }

    internal override bool ParallelSafe => this.input.ParallelSafe && this.oldValue.ParallelSafe && this.newValue.ParallelSafe;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var rawInput = input.Run(runtime);
        var rawOld = oldValue.Run(runtime);
        var rawNew = newValue.Run(runtime);
        StringScalars.RejectLegacyLob(rawInput, "replace", argumentIndex: 1);
        StringScalars.RejectLegacyLob(rawOld, "replace", argumentIndex: 2);
        StringScalars.RejectLegacyLob(rawNew, "replace", argumentIndex: 3);
        if (rawInput.IsNull || rawOld.IsNull || rawNew.IsNull)
            return SqlValue.Null(ResultType(rawInput.Type, rawOld.Type, rawNew.Type, runtime.Batch));
        var i = StringScalars.CoerceToVarchar(rawInput, runtime.Batch, "replace", argumentIndex: 1);
        var o = StringScalars.CoerceToVarchar(rawOld, runtime.Batch, "replace", argumentIndex: 2);
        var n = StringScalars.CoerceToVarchar(rawNew, runtime.Batch, "replace", argumentIndex: 3);
        var oldString = o.AsString;
        // SQL Server returns the input unchanged for an empty search string.
        var replaced = oldString.Length == 0
            ? i.AsString
            : ReplaceUnderCollation(
                i.AsString,
                oldString,
                n.AsString,
                StringScalars.CollationFor(runtime.Batch, rawInput.Type, rawOld.Type, rawNew.Type));
        // REPLACE can grow the input (a longer replacement per match), so the
        // result type is the family container (varchar(8000) / nvarchar(4000))
        // regardless of the input's declared width — probe-confirmed against
        // SQL Server 2025 (REPLACE(varchar(3), 'a', 'XY') → varchar(8000)).
        return SqlValue.FromString(ResultType(i.Type, rawOld.Type, rawNew.Type, runtime.Batch), replaced);
    }

    /// <summary>
    /// The input's container type, made <c>nvarchar</c> when the pattern or the
    /// replacement is Unicode though the input isn't (probed 2026-09-25 against
    /// SQL Server 2025: <c>REPLACE(&lt;varchar(10)&gt;, 'a', N'b')</c> is
    /// <c>nvarchar(4000)</c>).
    /// </summary>
    private static SqlType ResultType(SqlType inputType, SqlType oldType, SqlType newType, BatchContext batch)
    {
        var container = StringScalars.ContainerResultType(inputType, batch);
        return container is VarcharSqlType varchar && (SqlType.IsNationalStringCategory(oldType) || SqlType.IsNationalStringCategory(newType))
            ? NVarcharSqlType.Get(varchar.length == SqlType.MaxLengthSentinel ? SqlType.MaxLengthSentinel : 4000, varchar.Collation, varchar.Coercibility)
            : container;
    }

    /// <summary>
    /// Rewrites every occurrence of <paramref name="pattern"/> under
    /// <paramref name="collation"/>. Each hit resumes past <em>what the subject
    /// gave up</em>, not past the pattern's own length — an accent-insensitive
    /// <c>e</c> matching a decomposed <c>e</c> + U+0301 consumes both units, so
    /// <c>REPLACE</c> over a decomposed <c>café</c> comes back as a
    /// four-character <c>cafX</c> (probe-confirmed against SQL Server 2025).
    /// </summary>
    private static string ReplaceUnderCollation(string input, string pattern, string replacement, Collation collation)
    {
        var first = collation.IndexOf(input, pattern, 0, out var matched);
        if (first < 0)
            return input;

        var builder = new System.Text.StringBuilder(input.Length);
        var position = 0;
        var found = first;
        while (found >= 0)
        {
            _ = builder.Append(input, position, found - position).Append(replacement);
            position = found + matched;
            found = collation.IndexOf(input, pattern, position, out matched);
        }

        return builder.Append(input, position, input.Length - position).ToString();
    }

    /// <summary>
    /// Compile-time mirror of the three <c>RejectLegacyLob</c> calls in
    /// <see cref="Run"/>, keeping the same argument numbering.
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var inputType = StringScalars.BindArgument(input, batch, resolveColumnType, "replace");
        var oldType = StringScalars.BindArgument(oldValue, batch, resolveColumnType, "replace", argumentIndex: 2);
        var newType = StringScalars.BindArgument(newValue, batch, resolveColumnType, "replace", argumentIndex: 3);
        return ResultType(inputType, oldType, newType, batch);
    }

    internal override string DebugDisplay() => $"REPLACE({input.DebugDisplay()}, {oldValue.DebugDisplay()}, {newValue.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.input).Child(this.oldValue).Child(this.newValue);
}
