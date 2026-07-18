using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// A decoded table-valued-parameter value carried on a
/// <see cref="SimulatedDbParameter.Value"/> when a TVP arrives over the TDS
/// wire (RPC parameter type <c>0xF3</c>). It joins <c>DataTable</c> and
/// <c>IDataReader</c> as a third recognized structured-parameter source shape:
/// the engine's structured-parameter binding
/// (<c>BatchContext.SeedTableVariablesFromStructuredParameters</c>) reads the
/// parameter's <see cref="SimulatedDbParameter.TypeName"/>, resolves the table
/// type, and materializes these already-decoded rows through the same clone +
/// positional-insert path the in-process ADO.NET path uses. Each cell is a
/// <see cref="SqlValue"/> typed at its wire type and coerced to the destination
/// column on insert (a string-into-int mismatch raises Msg 245 just as a
/// reordered <c>DataTable</c> source does on real SQL Server).
/// </summary>
internal sealed class TableValuedParameterData(string typeName, int columnCount, List<SqlValue[]> rows)
{
    /// <summary>The schema-qualified table-type name from the wire TVP_TYPENAME (empty db segment dropped).</summary>
    public readonly string TypeName = typeName;

    /// <summary>
    /// The column count the client declared in TVP_COLMETADATA, used for the
    /// Msg 500 arity check even when zero rows were sent. <c>-1</c> marks a
    /// TVP_NULL value (whole parameter NULL), which binds an empty table
    /// variable and skips the arity check.
    /// </summary>
    public readonly int ColumnCount = columnCount;

    /// <summary>The decoded rows, each a positional value array in wire-column order.</summary>
    public readonly List<SqlValue[]> Rows = rows;
}
