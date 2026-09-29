using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Applies compiled <see cref="DataMask"/>s at a statement's output — the
/// rows a SELECT returns, the values <c>SELECT … INTO</c> and
/// <c>INSERT … SELECT</c> write, a <c>SELECT @v = …</c> assignment — for the
/// executing principal. Every entry point answers from the plan's mask array
/// first, which is null for any query reading no masked column, so an
/// unmasked query and a <c>dbo</c> session pay one null test.
/// </summary>
/// <remarks>
/// <c>UNMASK</c> is checked against each masked table column the output reads,
/// at the column, the table, the schema or the database (probed 2026-09-27
/// against SQL Server 2025): a column-level DENY masks that column under a
/// database-wide GRANT, <c>CONTROL</c> and <c>db_owner</c> unmask, and an
/// output column reading two masked columns is unmasked only when both are.
/// Ownership chaining doesn't unmask — a <c>dbo</c> procedure or view read by a
/// principal without <c>UNMASK</c> returns masked values — so the check runs
/// against the effective principal even inside a module body.
/// </remarks>
internal static class DataMasking
{
    /// <summary>Whether <paramref name="mask"/> applies for the batch's effective principal.</summary>
    public static bool Applies(BatchContext batch, DataMask mask)
    {
        var connection = batch.Connection;
        foreach (var source in mask.Sources)
        {
            var table = source.Table;
            var database = table.OwningDatabase ?? connection.CurrentDatabase;
            if (PermissionEnforcement.Bypasses(connection, database))
                continue;
            int principalId;
            if (ReferenceEquals(database, connection.CurrentDatabase))
                principalId = connection.Security.Effective.DatabasePrincipalId;
            else if (PermissionEnforcement.TryResolveCrossDatabasePrincipal(connection, database, out var principal))
                principalId = principal.PrincipalId;
            else
                return true;
            var unmasked = table.IsTableVariable
                ? PermissionChecker.IsGranted(database, principalId, Permission.Unmask, PermissionChecker.ClassDatabase, 0, 0, ServerLoginRights.For(connection))
                : PermissionChecker.IsColumnGranted(database, principalId, Permission.Unmask, table.ObjectId, table.SchemaId, source.ColumnOrdinal, ServerLoginRights.For(connection));
            if (!unmasked)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Per output column, the function the executing principal reads it
    /// through, or null when <paramref name="masks"/> leaves every column
    /// unmasked for them.
    /// </summary>
    public static MaskingFunction?[]? Applying(BatchContext batch, DataMask?[]? masks)
    {
        if (masks is null)
            return null;
        MaskingFunction?[]? functions = null;
        for (var i = 0; i < masks.Length; i++)
        {
            if (masks[i] is { } mask && Applies(batch, mask))
                (functions ??= new MaskingFunction?[masks.Length])[i] = mask.Function;
        }
        return functions;
    }

    /// <summary>
    /// The value a <c>SET</c> or <c>DECLARE</c> assigns from
    /// <paramref name="expression"/>: <c>default()</c> of the variable's type
    /// when the expression reads a masked column through a subquery the
    /// executing principal can't unmask (probed 2026-09-27 against SQL Server
    /// 2025: <c>SET @v = (SELECT TOP 1 masked_int FROM t)</c> into a
    /// <c>varchar</c> assigns <c>xxxx</c>). A <c>DECLARE</c> initializer passes
    /// false for <paramref name="functionResults"/>: it assigns a scalar UDF's
    /// result unmasked where <c>SET</c> masks it (probed 2026-09-27).
    /// </summary>
    public static SqlValue ForAssignment(BatchContext batch, Expression expression, SqlValue source, SqlValue value, SqlType type, bool functionResults = true) =>
        !batch.Connection.Simulation.DeclaresDataMasks || DataMask.Of(expression, static _ => null, typeOf: null, functionResults) is not { } mask
            ? value
            : ForAssignment(batch, mask, source, value, type);

    /// <summary>
    /// <paramref name="value"/>, assigned to a variable of
    /// <paramref name="type"/> from <paramref name="source"/>, as a principal
    /// without <c>UNMASK</c> assigns it: through the mask's own function when
    /// the variable is declared as the value's own type, else through
    /// <c>default()</c> of the variable's type (probed 2026-09-27 against SQL
    /// Server 2025: an email-masked <c>varchar(40)</c> assigns
    /// <c>jXXX@XXXX.com</c> to a <c>varchar(40)</c> variable and <c>xxxx</c> to
    /// a <c>varchar(20)</c> one). Inside a scalar UDF body nothing is masked
    /// on assignment — a comparison there reads the stored value — and the
    /// function's result masks instead (<see cref="Schemas.ScalarFunction.ReturnMask"/>).
    /// </summary>
    public static SqlValue ForAssignment(BatchContext batch, DataMask mask, SqlValue source, SqlValue value, SqlType type) =>
        value.IsNull || batch.UdfFrame is not null || !Applies(batch, mask)
            ? value
            : ForStorage((DataMask.SameDeclaredType(source.Type, type) ? mask.Function : MaskingFunction.Default).Apply(value, type));

    /// <summary>
    /// <paramref name="error"/> as real reports it over a value the executing
    /// principal reads through <paramref name="mask"/>, or null when it stands:
    /// Msg 220 / 232 / 245 / 248 replace the value and every type name with
    /// <c>******</c>, while Msg 8114 / 8115 / 235 / 241, which quote no value,
    /// keep their text (probed 2026-09-27 against SQL Server 2025). Only an
    /// error raised computing a masked output column's value qualifies — a
    /// filter, an ordering or a <c>CASE WHEN</c> condition reads the stored
    /// value and reports it.
    /// </summary>
    public static SimulatedSqlException? Redacted(SimulatedSqlException error, BatchContext batch, DataMask mask) =>
        error.RedactedForMask() is { } redacted && Applies(batch, mask) ? redacted : null;

    /// <summary>A result set whose rows the executing principal reads through <paramref name="masks"/>.</summary>
    public static SimulatedSqlResultSet ForClient(SimulatedSqlResultSet resultSet, DataMask?[]? masks, BatchContext batch) =>
        Applying(batch, masks) is { } functions ? resultSet.WithMaskedColumns(functions) : resultSet;

    /// <summary>A copy of <paramref name="row"/> with each masked column's value replaced.</summary>
    public static SqlValue[] MaskRow(SqlValue[] row, MaskingFunction?[] functions, SqlType[] schema)
    {
        var masked = (SqlValue[])row.Clone();
        for (var i = 0; i < functions.Length && i < masked.Length; i++)
        {
            if (functions[i] is { } function)
                masked[i] = function.Apply(masked[i], schema[i]);
        }
        return masked;
    }

    /// <summary>
    /// <paramref name="row"/> masked and normalized for storage: a fixed-length
    /// value the mask left short is padded to its type again, as the value it
    /// stands for would be on its way into a row.
    /// </summary>
    public static SqlValue[] MaskRowForStorage(SqlValue[] row, MaskingFunction?[] functions, SqlType[] schema)
    {
        var masked = MaskRow(row, functions, schema);
        for (var i = 0; i < functions.Length && i < masked.Length; i++)
        {
            if (functions[i] is not null)
                masked[i] = ForStorage(masked[i]);
        }
        return masked;
    }

    /// <summary>A masked value padded back to its fixed-length type.</summary>
    public static SqlValue ForStorage(SqlValue value) => value switch
    {
        { IsNull: true } => value,
        { Type: CharSqlType } => SqlValue.FromChar(value.Type, value.AsString),
        { Type: NCharSqlType } => SqlValue.FromNChar(value.Type, value.AsString),
        { Type: BinarySqlType } => SqlValue.FromBinary(value.Type, value.AsBytes),
        _ => value,
    };
}
