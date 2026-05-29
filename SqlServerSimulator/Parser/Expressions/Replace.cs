using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>REPLACE(input, oldValue, newValue)</c>: replaces every occurrence
/// of <c>oldValue</c> in <c>input</c> with <c>newValue</c>. Matching uses the
/// default collation (case-insensitive); the replaced segment is removed and
/// the new value substituted, even when its case differs from the match.
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

    public override SqlValue Run(RuntimeContext runtime)
    {
        var rawInput = input.Run(runtime);
        var rawOld = oldValue.Run(runtime);
        var rawNew = newValue.Run(runtime);
        if (rawInput.IsNull || rawOld.IsNull || rawNew.IsNull)
            return SqlValue.Null(StringScalars.ResolveResultType(rawInput.Type, runtime.Batch));
        var i = StringScalars.CoerceToVarchar(rawInput, runtime.Batch, "replace", argumentIndex: 1);
        var o = StringScalars.CoerceToVarchar(rawOld, runtime.Batch, "replace", argumentIndex: 2);
        var n = StringScalars.CoerceToVarchar(rawNew, runtime.Batch, "replace", argumentIndex: 3);
        var oldString = o.AsString;
        // SQL Server returns the input unchanged for an empty search string;
        // .NET's String.Replace rejects it with ArgumentException.
        var replaced = oldString.Length == 0
            ? i.AsString
            : i.AsString.Replace(oldString, n.AsString, StringComparison.InvariantCultureIgnoreCase);
        return SqlValue.FromString(i.Type, replaced);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        StringScalars.ResolveResultType(input.GetSqlType(batch, resolveColumnType), batch);

    internal override string DebugDisplay() => $"REPLACE({input.DebugDisplay()}, {oldValue.DebugDisplay()}, {newValue.DebugDisplay()})";
}
