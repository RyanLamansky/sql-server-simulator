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
/// <para>
/// The simulator models exactly two files per database (mirroring
/// <c>sys.database_files</c>): the primary data file <c>&lt;db&gt;_Data</c>
/// (file_id 1, ROWS) and the log file <c>&lt;db&gt;_Log</c> (file_id 2, LOG).
/// </para>
/// <para>
/// Shipped properties (probe-confirmed against SQL Server 2025):
/// <list type="bullet">
/// <item><description><c>SpaceUsed</c> — for the data file, the live page
/// total across every modeled allocation unit (see
/// <see cref="BuiltInResources.SumDataFilePages"/>), the same value
/// <c>sys.allocation_units</c> / <c>sys.database_files.size</c> derive from,
/// so SSMS's SpaceAvailable = size − SpaceUsed stays non-negative; for the
/// log file, a small synthetic constant.</description></item>
/// <item><description><c>IsReadOnly</c> — always 0 (no read-only files
/// modeled).</description></item>
/// <item><description><c>IsPrimaryFile</c> — 1 for the data file (file_id 1),
/// 0 for the log file.</description></item>
/// <item><description><c>IsLogFile</c> — 1 for the log file, 0 for the data
/// file.</description></item>
/// </list>
/// </para>
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

        // file_id 1 = <db>_Data (ROWS, primary); file_id 2 = <db>_Log (LOG).
        // Baseline.Equals applies SQL Server's trailing-space-insensitive
        // comparison to the file name.
        bool isLog;
        if (Collation.Baseline.Equals(name, database.Name + "_Data"))
            isLog = false;
        else if (Collation.Baseline.Equals(name, database.Name + "_Log"))
            isLog = true;
        else
            return SqlValue.Null(SqlType.Int32);

        return EvaluateFileProperty(database, isLog, prop.TrimEnd(' ')) is int result
            ? SqlValue.FromInt32(result)
            : SqlValue.Null(SqlType.Int32);
    }

    private static int? EvaluateFileProperty(Database database, bool isLog, string property)
    {
        Span<char> upper = stackalloc char[property.Length];
        return property.AsSpan().ToUpperInvariant(upper) switch
        {
            9 => upper switch
            {
                "ISLOGFILE" => isLog ? 1 : 0,
                "SPACEUSED" => isLog ? BuiltInResources.LogFileUsedPages : (int)BuiltInResources.SumDataFilePages(database),
                _ => null,
            },
            10 => upper switch
            {
                "ISREADONLY" => 0,
                _ => null,
            },
            13 => upper switch
            {
                "ISPRIMARYFILE" => isLog ? 0 : 1,
                _ => null,
            },
            _ => null,
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"FILEPROPERTY({this.nameArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
