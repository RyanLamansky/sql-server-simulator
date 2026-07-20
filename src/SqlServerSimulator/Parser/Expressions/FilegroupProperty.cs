using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>FILEGROUPPROPERTY(filegroup_name, 'property')</c>: per-filegroup
/// metadata for a filegroup of the current database, read from
/// <see cref="Database.Filegroups"/>. Returns <c>int</c>; NULL on any NULL arg,
/// an unknown filegroup name, or an unknown property. Property names are
/// case-insensitive and — matching SQL Server's internal <c>=</c> comparison —
/// trailing-space insensitive.
/// </summary>
/// <remarks>
/// <para>
/// Shipped properties (probe-confirmed against SQL Server 2025 — PRIMARY on
/// every database, a user filegroup created via <c>ALTER DATABASE … ADD
/// FILEGROUP</c> in a scratch database):
/// <list type="bullet">
/// <item><description><c>IsReadOnly</c> — always 0 (no read-only filegroups
/// modeled).</description></item>
/// <item><description><c>IsUserDefinedFG</c> — 0 for PRIMARY
/// (<c>data_space_id</c> 1), 1 for any registered user filegroup.</description></item>
/// <item><description><c>IsDefault</c> — 1 for PRIMARY, 0 for a user filegroup.
/// The simulator has no <c>MODIFY FILEGROUP … DEFAULT</c>, so PRIMARY is always
/// the default filegroup.</description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class FilegroupProperty : Expression
{
    private readonly Expression nameArg;
    private readonly Expression propertyArg;

    public FilegroupProperty(ParserContext context)
    {
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var nameValue = this.nameArg.Run(runtime);
        var propValue = this.propertyArg.Run(runtime);
        if (nameValue.IsNull || propValue.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var name = nameValue.CoerceTo(SqlType.NVarchar).AsString.TrimEnd(' ');
        var prop = propValue.CoerceTo(SqlType.NVarchar).AsString;
        return runtime.Batch.CurrentDatabase.Filegroups.TryGetValue(name, out var filegroupId)
            && EvaluateFilegroupProperty(filegroupId, prop.TrimEnd(' ')) is int result
            ? SqlValue.FromInt32(result)
            : SqlValue.Null(SqlType.Int32);
    }

    private static int? EvaluateFilegroupProperty(int filegroupId, string property)
    {
        var isPrimary = filegroupId == Database.PrimaryFilegroupId;
        Span<char> upper = stackalloc char[property.Length];
        return property.AsSpan().ToUpperInvariant(upper) switch
        {
            9 => upper switch
            {
                "ISDEFAULT" => isPrimary ? 1 : 0,
                _ => null,
            },
            10 => upper switch
            {
                "ISREADONLY" => 0,
                _ => null,
            },
            15 => upper switch
            {
                "ISUSERDEFINEDFG" => isPrimary ? 0 : 1,
                _ => null,
            },
            _ => null,
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"FILEGROUPPROPERTY({this.nameArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
