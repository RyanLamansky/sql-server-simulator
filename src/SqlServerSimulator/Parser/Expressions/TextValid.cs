using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>TEXTVALID('table.column', text_ptr)</c>: returns <c>1</c> when the
/// pointer is a valid in-row text pointer for the named column, else <c>0</c>.
/// A NULL pointer or NULL name, a pointer that isn't a simulator-fabricated
/// text pointer, and a name whose column segment doesn't match the pointer's
/// source column all return <c>0</c> — probe-confirmed against SQL Server 2025.
/// Reference:
/// https://learn.microsoft.com/en-us/sql/t-sql/functions/textvalid-transact-sql
/// </summary>
/// <remarks>
/// The name argument is matched by its final (column) segment against the
/// column identity the pointer carries (see <see cref="LegacyTextPointer"/>);
/// the table portion is required to be present (a bare single-part name returns
/// <c>0</c>, matching real) but is not resolved against the catalog. A
/// syntactically valid name whose column segment matches the pointer's source
/// column therefore returns <c>1</c> even if its table portion names a
/// different table — real cross-checks the exact column object. This divergence
/// is unobservable through the sanctioned <c>TEXTVALID('t.c', TEXTPTR(c))</c>
/// idiom, where the two column names always agree.
/// </remarks>
internal sealed class TextValid : Expression
{
    private readonly Expression nameArg;
    private readonly Expression pointerArg;

    public TextValid(ParserContext context)
    {
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.pointerArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var name = this.nameArg.Run(runtime);
        var pointer = this.pointerArg.Run(runtime);
        if (name.IsNull || pointer.IsNull || pointer.Type.ClrType != typeof(byte[]))
            return SqlValue.FromInt32(0);
        var column = ColumnSegment(StringScalars.CoerceToVarchar(name, runtime.Batch, "textvalid").AsString);
        return SqlValue.FromInt32(column is not null && LegacyTextPointer.Matches(pointer.AsBytes, column) ? 1 : 0);
    }

    /// <summary>
    /// Returns the final dotted segment of a <c>[db.][schema.]table.column</c>
    /// name (brackets stripped), or <c>null</c> when the name has fewer than two
    /// non-empty segments — real requires at least <c>table.column</c>.
    /// </summary>
    private static string? ColumnSegment(string name)
    {
        var segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length < 2 ? null : segments[^1].Trim('[', ']', '"');
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"TEXTVALID({this.nameArg.DebugDisplay()}, {this.pointerArg.DebugDisplay()})";

    internal override void VisitColumnReferences(Action<MultiPartName> visit)
    {
        this.nameArg.VisitColumnReferences(visit);
        this.pointerArg.VisitColumnReferences(visit);
    }
}
