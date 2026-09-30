using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>TEXTVALID('table.column', text_ptr)</c>: returns <c>1</c> when the
/// pointer is a valid text pointer for the named column, else <c>0</c>. A NULL
/// pointer or NULL name, a name that resolves to no table or no
/// <c>text</c> / <c>ntext</c> / <c>image</c> column, a pointer read from
/// another table or column, and a pointer to a deleted row all return <c>0</c>,
/// while a cell a write set NULL keeps its pointer valid — probe-confirmed
/// against SQL Server 2025. Reference:
/// https://learn.microsoft.com/en-us/sql/t-sql/functions/textvalid-transact-sql
/// </summary>
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
        var batch = runtime.Batch;
        return SqlValue.FromInt32(
            ResolveColumn(StringScalars.CoerceToVarchar(name, batch, "textvalid").AsString, batch) is var (table, column)
            && LegacyTextPointer.TryResolve(pointer.AsBytes, table, column, out _) ? 1 : 0);
    }

    /// <summary>
    /// The table and LOB column a <c>[db.][schema.]table.column</c> name
    /// (brackets stripped) names, or null when it names none — including a
    /// bare column, since real requires at least <c>table.column</c>.
    /// </summary>
    private static (HeapTable Table, int Column)? ResolveColumn(string name, BatchContext batch)
    {
        var segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length is < 2 or > 4)
            return null;
        var tableName = new MultiPartName(Unbracket(segments[0]));
        for (var i = 1; i < segments.Length - 1; i++)
            tableName = tableName.WithAddedPart(Unbracket(segments[i]));
        if (!batch.TryResolveTable(tableName, out var table))
            return null;
        var columnName = Unbracket(segments[^1]);
        var collation = batch.DatabaseFor(table).Collation;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (collation.Equals(table.Columns[i].Name, columnName))
                return table.Columns[i].Type is TextSqlType or NTextSqlType or ImageSqlType ? (table, i) : null;
        }
        return null;
    }

    private static string Unbracket(string segment) => segment.Trim('[', ']', '"');

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = AssignmentRules.ArgumentType(this.pointerArg, SqlType.Varbinary, batch, resolveColumnType);
        return SqlType.Int32;
    }

    internal override string DebugDisplay() => $"TEXTVALID({this.nameArg.DebugDisplay()}, {this.pointerArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.nameArg).Child(this.pointerArg);
}
