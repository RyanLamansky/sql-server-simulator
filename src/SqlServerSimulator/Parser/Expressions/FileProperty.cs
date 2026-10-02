using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>FILEPROPERTY(file_name, 'property')</c>: per-file metadata for a
/// file of the current database. Returns <c>int</c>; NULL on any NULL arg,
/// an unknown file name (within the current database), or an unknown
/// property. Property names are case-insensitive and — matching real SQL
/// Server's internal <c>=</c> comparison — trailing-space insensitive.
/// </summary>
/// <remarks>
/// Shipped properties (probe-confirmed against SQL Server 2025):
/// <list type="bullet">
/// <item><description><c>SpaceUsed</c> — for the primary data file, the live
/// page total across every modeled allocation unit (see
/// <see cref="BuiltInResources.SumDataFilePages"/>), the same value
/// <c>sys.allocation_units</c> / <c>sys.database_files.size</c> derive from,
/// so SSMS's SpaceAvailable = size − SpaceUsed stays non-negative; every row
/// lands in that file, so another data file reports the 8 pages an empty one
/// does, and a log file a small synthetic constant.</description></item>
/// <item><description><c>IsReadOnly</c> — 1 for a file of a
/// <c>READ_ONLY</c> filegroup.</description></item>
/// <item><description><c>IsPrimaryFile</c> — 1 for the primary data file
/// (file_id 1) alone.</description></item>
/// <item><description><c>IsLogFile</c> — 1 for a log file.</description></item>
/// </list>
/// </remarks>
internal sealed class FileProperty : Expression
{
    private readonly Expression nameArg;
    private readonly Expression propertyArg;

    public FileProperty(ParserContext context)
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
        var name = nameValue.CoerceTo(SqlType.NVarchar).AsString;
        var prop = propValue.CoerceTo(SqlType.NVarchar).AsString;
        var database = runtime.Batch.CurrentDatabase;

        return database.FindFile(name.TrimEnd(' ')) is { IsContainer: false } file
            && EvaluateFileProperty(database, file, prop.TrimEnd(' ')) is int result
            ? SqlValue.FromInt32(result)
            : SqlValue.Null(SqlType.Int32);
    }

    private static int? EvaluateFileProperty(Database database, DatabaseFile file, string property)
    {
        var isLog = file.IsLog;
        Span<char> upper = stackalloc char[property.Length];
        return property.AsSpan().ToUpperInvariant(upper) switch
        {
            9 => upper switch
            {
                "ISLOGFILE" => isLog ? 1 : 0,
                "SPACEUSED" => file.FileId switch
                {
                    1 => (int)BuiltInResources.SumDataFilePages(database),
                    2 => BuiltInResources.LogFileUsedPages,
                    _ => isLog ? BuiltInResources.SecondaryLogFileUsedPages : BuiltInResources.EmptyDataFileUsedPages,
                },
                _ => null,
            },
            10 => upper switch
            {
                "ISREADONLY" => !isLog && database.IsFilegroupReadOnly(file.DataSpaceId) ? 1 : 0,
                _ => null,
            },
            13 => upper switch
            {
                "ISPRIMARYFILE" => file.FileId == 1 ? 1 : 0,
                _ => null,
            },
            _ => null,
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"FILEPROPERTY({this.nameArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.nameArg).Child(this.propertyArg);
}
