using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_cursor_list and the sp_describe_cursor family: each answers through a
// cursor variable it allocates, a scrollable read-only snapshot of its rows.
partial class Simulation
{
    private static readonly SqlType CursorRowCountType = DecimalSqlType.Get(10, 0);

    private static readonly SqlType[] CursorListSchema =
    [
        SqlType.SystemName, SqlType.SystemName, SqlType.TinyInt, SqlType.Int32, SqlType.TinyInt, SqlType.TinyInt, SqlType.TinyInt,
        SqlType.TinyInt, CursorRowCountType, SqlType.SmallInt, SqlType.SmallInt, CursorRowCountType, SqlType.TinyInt, SqlType.Int32,
    ];

    private static readonly string[] CursorListColumnNames =
    [
        "reference_name", "cursor_name", "cursor_scope", "status", "model", "concurrency", "scrollable",
        "open_status", "cursor_rows", "fetch_status", "column_count", "row_count", "last_operation", "cursor_handle",
    ];

    private static readonly SqlType[] CursorColumnsSchema =
    [
        SqlType.SystemName, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.SmallInt, SqlType.TinyInt, SqlType.TinyInt,
        SqlType.Int32, NVarcharSqlType.Get(1, Collation.Baseline, Coercibility.Implicit), SqlType.SmallInt, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.SystemName,
    ];

    private static readonly string[] CursorColumnsColumnNames =
    [
        "column_name", "ordinal_position", "column_characteristics_flags", "column_size", "data_type_sql", "column_precision", "column_scale",
        "order_position", "order_direction", "hidden_column", "columnid", "objectid", "dbid", "dbname",
    ];

    private static readonly SqlType[] CursorTablesSchema =
    [
        SqlType.SystemName, SqlType.SystemName, SqlType.SmallInt, SqlType.SmallInt, SqlType.SystemName, SqlType.Int32, SqlType.Int32, SqlType.SystemName,
    ];

    private static readonly string[] CursorTablesColumnNames =
    [
        "table_owner", "table_name", "optimizer_hint", "lock_type", "server_name", "objectid", "dbid", "dbname",
    ];

    /// <summary>What a describe procedure reports on.</summary>
    private enum CursorDescription
    {
        Cursor,
        Columns,
        Tables,
    }

    /// <summary>
    /// <c>sp_describe_cursor</c> / <c>_columns</c> / <c>_tables @cursor_return
    /// OUTPUT, @cursor_source, @cursor_identity</c>: the named cursor's
    /// attributes, its result columns, or the tables and views its query
    /// names. The source is <c>local</c>, <c>global</c> or <c>variable</c> in
    /// any case; a NULL or unknown one and a NULL identity are Msg 16902, a
    /// missing cursor Msg 16916 state 4 and an undeclared variable Msg 137
    /// state 100, each raised from the procedure's own line (probed 2026-09-29
    /// against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpDescribeCursor(BatchContext batch, string procedure, CursorDescription description)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (returnVariable, values) = BindCursorProcedureArguments(batch, procedure, arguments, ["cursor_source", "cursor_identity"]);
        var line = description == CursorDescription.Columns ? 13 : 14;
        var source = values[0] is { IsNull: false } sourceValue ? sourceValue.CoerceTo(SqlType.NVarcharMax).AsString : null;
        var scope = source is null ? -1 : ResolveCursorSource(source);
        if (scope < 0)
            throw AtProcedureLine(SimulatedSqlException.CursorStatusInvalidParameter("cursor_source", source is null ? (byte)40 : (byte)42), procedure, line);
        if (values[1] is not { IsNull: false } identityValue)
            throw AtProcedureLine(SimulatedSqlException.CursorStatusInvalidParameter("cursor_identity", 43), procedure, line);
        var identity = identityValue.CoerceTo(SqlType.NVarcharMax).AsString;

        Cursor? cursor;
        switch (scope)
        {
            case 1:
                _ = batch.LocalCursors.TryGetValue(identity, out cursor);
                break;
            case 3:
                var variable = identity.StartsWith('@') ? identity[1..] : identity;
                if (!batch.CursorVariables.TryGetValue(variable, out cursor))
                    throw AtProcedureLine(SimulatedSqlException.CursorProcedureUndeclaredVariable(variable), procedure, line);
                break;
            default:
                _ = batch.Connection.Cursors.TryGetValue(identity, out cursor);
                break;
        }
        if (cursor is null)
            throw AtProcedureLine(SimulatedSqlException.CursorDoesNotExist(identity, state: 4), procedure, line);

        var rows = description switch
        {
            CursorDescription.Cursor => [DescribeCursorRow(identity, cursor, scope == 2 ? (byte)2 : (byte)1, listed: false)],
            CursorDescription.Columns => DescribeCursorColumns(batch, cursor),
            _ => DescribeCursorTables(batch, cursor),
        };
        var (schema, names) = description switch
        {
            CursorDescription.Cursor => (CursorListSchema, CursorListColumnNames),
            CursorDescription.Columns => (CursorColumnsSchema, CursorColumnsColumnNames),
            _ => (CursorTablesSchema, CursorTablesColumnNames),
        };
        HandOutDescribedCursor(batch, returnVariable, schema, names, rows);
    }

    /// <summary>
    /// <c>sp_cursor_list @cursor_return OUTPUT, @cursor_scope</c>: one row per
    /// cursor the session can name at <paramref name="batch"/>'s scope — 1 its
    /// local cursors and cursor variables, 2 its global cursors, 3 both — in
    /// declaration order. Any other scope is an informational Msg 16902 that
    /// leaves the variable unallocated (probed 2026-09-29 against SQL Server
    /// 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpCursorList(BatchContext batch, string procedure)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (returnVariable, values) = BindCursorProcedureArguments(batch, procedure, arguments, ["cursor_scope"]);
        var scope = values[0] is { IsNull: false } scopeValue ? ScalarArguments.CoerceProcedureParameter(scopeValue, SqlType.Int32) : 0;
        if (scope is < 1 or > 3)
        {
            yield return new SimulatedInfoOutcome(new SimulatedError(@class: 1, lineNumber: 13, "sys.sp_cursor_list: The value of the parameter @cursor_scope is invalid.", 16902, procedure: procedure, server: batch.Connection.DataSource, source: "SqlServerSimulator", state: 1));
            yield break;
        }

        var listed = new List<(int Handle, SqlValue[] Row)>();
        if ((scope & 1) != 0)
        {
            foreach (var (name, cursor) in batch.LocalCursors)
                listed.Add((cursor.Handle, DescribeCursorRow(name, cursor, 1, listed: true)));
            foreach (var (name, cursor) in batch.CursorVariables)
            {
                if (cursor is not null)
                    listed.Add((cursor.Handle, DescribeCursorRow("@" + name, cursor, 1, listed: true)));
            }
        }
        if ((scope & 2) != 0)
        {
            foreach (var (name, cursor) in batch.Connection.Cursors)
            {
                if (!cursor.IsUnnamed)
                    listed.Add((cursor.Handle, DescribeCursorRow(name, cursor, 2, listed: true)));
            }
        }
        listed.Sort(static (a, b) => a.Handle.CompareTo(b.Handle));
        HandOutDescribedCursor(batch, returnVariable, CursorListSchema, CursorListColumnNames, listed.ConvertAll(static entry => entry.Row));
    }

    /// <summary>
    /// Binds a cursor-reporting procedure's arguments by name or position:
    /// <c>@cursor_return</c> first, then <paramref name="parameters"/>. A
    /// missing one is Msg 201, and a scalar variable passed OUTPUT for the
    /// cursor Msg 206 — both at line 0, as a procedure's binding errors are.
    /// Returns the caller's cursor variable (null when the call wrote no
    /// OUTPUT) and each further parameter's value.
    /// </summary>
    private static (string? ReturnVariable, SqlValue?[] Values) BindCursorProcedureArguments(BatchContext batch, string procedure, List<ProcArgument> arguments, string[] parameters)
    {
        var collation = batch.CurrentDatabase.Collation;
        var bound = new ProcArgument?[parameters.Length + 1];
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var index = i;
            if (argument.Name is { } name)
            {
                index = collation.Equals(name, "cursor_return") ? 0 : Array.FindIndex(parameters, parameter => collation.Equals(parameter, name)) is var found and >= 0 ? found + 1 : -1;
                if (index < 0)
                    throw SimulatedSqlException.NotAParameterForProcedure(name, procedure).PinLine(0);
            }
            if (index >= bound.Length)
                throw SimulatedSqlException.TooManyArgumentsToFunction(procedure).PinLine(0);
            bound[index] = argument;
        }

        for (var i = 0; i < bound.Length; i++)
        {
            if (bound[i] is not { IsDefault: false })
                throw SimulatedSqlException.ProcedureExpectsParameter(procedure, i == 0 ? "cursor_return" : parameters[i - 1]).PinLine(0);
        }

        var returnArgument = bound[0]!.Value;
        if (returnArgument.OutputSlot is { } scalar)
            throw SimulatedSqlException.OperandTypeClash(SimulatedSqlException.FamilyRootName(scalar.DeclaredType), "cursor").PinLine(0);
        if (returnArgument.CursorVariableName is { } variable && batch.CursorVariables.TryGetValue(variable, out var held) && held is not null)
            throw SimulatedSqlException.CursorOutputArgumentAllocated(variable);

        var values = new SqlValue?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            values[i] = bound[i + 1]!.Value.Value;
        return (returnArgument.CursorVariableName, values);
    }

    /// <summary>1 local, 2 global, 3 variable, or -1 for anything else.</summary>
    private static int ResolveCursorSource(string source)
    {
        Span<char> upper = stackalloc char[8];
        if (source.Length > upper.Length)
            return -1;
        upper = upper[..source.Length];
        _ = source.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "GLOBAL" => 2,
            "LOCAL" => 1,
            "VARIABLE" => 3,
            _ => -1,
        };
    }

    /// <summary>Attributes an error to the describe procedure's own source line.</summary>
    private static SimulatedSqlException AtProcedureLine(SimulatedSqlException error, string procedure, int line)
    {
        error.PreserveDiagnostics(line, procedure);
        error.RaisedBySystemProcedure = true;
        return error;
    }

    /// <summary>
    /// One <c>sp_cursor_list</c> / <c>sp_describe_cursor</c> row. A cursor a
    /// variable's SET built is named by that variable whichever variable
    /// reaches it — as its reference too in a listing, where a description
    /// keeps the name it was asked by (probed 2026-09-29 against SQL Server
    /// 2025).
    /// </summary>
    private static SqlValue[] DescribeCursorRow(string referenceName, Cursor cursor, byte scope, bool listed)
    {
        if (listed && cursor.OriginVariable is { } origin)
            referenceName = origin;
        var cursorName = cursor.OriginVariable ?? (cursor.Name.Length == 0 ? referenceName : cursor.Name);
        var model = cursor.FastForward ? 4 : cursor.Sensitivity switch
        {
            CursorSensitivity.Static => 1,
            CursorSensitivity.Keyset => 2,
            _ => 3,
        };
        var concurrency = cursor.ReadOnly ? 1 : cursor.Concurrency == CursorConcurrency.ScrollLocks ? 2 : 3;
        return
        [
            SqlValue.FromSystemName(referenceName),
            SqlValue.FromSystemName(cursorName),
            SqlValue.FromByte(scope),
            SqlValue.FromInt32(cursor.StatusValue),
            SqlValue.FromByte((byte)model),
            SqlValue.FromByte((byte)concurrency),
            SqlValue.FromByte(cursor.Scrollable ? (byte)1 : (byte)0),
            SqlValue.FromByte(cursor.IsOpen ? (byte)1 : (byte)0),
            SqlValue.FromDecimal(CursorRowCountType, cursor.DescribedRowCount),
            SqlValue.FromInt16((short)cursor.FetchStatus),
            SqlValue.FromInt16((short)cursor.Selection.Schema.Length),
            SqlValue.FromDecimal(CursorRowCountType, cursor.LastOperationRows),
            SqlValue.FromByte(cursor.LastOperation),
            SqlValue.FromInt32(cursor.Handle),
        ];
    }

    /// <summary>
    /// One <c>sp_describe_cursor_columns</c> row per result column. The flags
    /// are 0x2 for a fixed-length type, 0x4 for a column the projection
    /// reports nullable, and 0x10 for a column a positioned UPDATE may
    /// assign: a base column other than a rowversion, of a cursor that isn't
    /// read-only, listed in its <c>FOR UPDATE OF</c> list when it has one. The
    /// size is the column's storage length, a <c>max</c> or LOB type
    /// reporting 2147483647, and a computed column names no table.
    /// </summary>
    private static List<SqlValue[]> DescribeCursorColumns(BatchContext batch, Cursor cursor)
    {
        var selection = cursor.Selection;
        var rows = new List<SqlValue[]>(selection.Schema.Length);
        for (var i = 0; i < selection.Schema.Length; i++)
        {
            var type = selection.Schema[i];
            var origin = selection.ProjectionBaseColumn(i);
            var column = origin is var (table, ordinal) ? table.Columns[ordinal] : null;
            var (maxLength, precision, scale) = BuiltInResources.GetSysColumnMetadata(column ?? new HeapColumn(string.Empty, type, maxLength: null, nullable: true));
            var flags = (IsFixedLengthType(type) ? 0x2 : 0)
                | (selection.ColumnNullability?[i] ?? true ? 0x4 : 0)
                | (column is not null && !cursor.ReadOnly && type != SqlType.RowVersion && cursor.IsColumnUpdatable(selection.ColumnNames[i], batch) ? 0x10 : 0);
            var database = origin?.Table.OwningDatabase ?? batch.CurrentDatabase;
            rows.Add(
            [
                SqlValue.FromSystemName(selection.ColumnNames[i]),
                SqlValue.FromInt32(i),
                SqlValue.FromInt32(flags),
                SqlValue.FromInt32(maxLength < 0 ? int.MaxValue : maxLength),
                SqlValue.FromInt16(type.SystemTypeId),
                SqlValue.FromByte(precision),
                SqlValue.FromByte(scale),
                SqlValue.FromInt32(0),
                SqlValue.Null(CursorColumnsSchema[8]),
                SqlValue.FromInt16(0),
                SqlValue.FromInt32(origin is var (_, columnOrdinal) ? columnOrdinal + 1 : -1),
                SqlValue.FromInt32(origin?.Table.ObjectId ?? -1),
                SqlValue.FromInt32(origin is null ? -1 : database.Id),
                origin is null ? SqlValue.Null(SqlType.SystemName) : SqlValue.FromSystemName(database.Name),
            ]);
        }
        return rows;
    }

    /// <summary>Whether a type stores a fixed number of bytes.</summary>
    private static bool IsFixedLengthType(SqlType type) =>
        type is not (VarcharSqlType or NVarcharSqlType or VarbinarySqlType or XmlSqlType or TextSqlType or NTextSqlType or ImageSqlType or SqlVariantSqlType);

    /// <summary>
    /// One <c>sp_describe_cursor_tables</c> row per table or view the cursor's
    /// query names — a view as itself rather than what it reads — whatever
    /// the cursor resolved to, with no hint or lock type reported.
    /// </summary>
    private static List<SqlValue[]> DescribeCursorTables(BatchContext batch, Cursor cursor)
    {
        var rows = new List<SqlValue[]>();
        foreach (var source in cursor.Selection.BranchFromSources ?? [])
        {
            if (source.IsPlaceholder)
                continue;
            Schemas.SchemaObject named;
            var database = batch.CurrentDatabase;
            if (source.BackingView is { } view)
            {
                named = view;
            }
            else if (source.BackingTable is { IsTableVariable: false } table)
            {
                named = table;
                // A #temp table reports as tempdb's.
                database = table.Name.StartsWith('#')
                    ? batch.Connection.Simulation.Databases[TempdbDatabaseName]
                    : table.OwningDatabase ?? database;
            }
            else
            {
                continue;
            }
            var schemaName = Database.DefaultSchemaName;
            foreach (var schema in database.Schemas.Values)
            {
                if (schema.SchemaId == named.SchemaId)
                    schemaName = schema.Name;
            }
            rows.Add(
            [
                SqlValue.FromSystemName(schemaName),
                SqlValue.FromSystemName(named.Name),
                SqlValue.FromInt16(0),
                SqlValue.FromInt16(0),
                SqlValue.FromSystemName("SIMULATED"),
                SqlValue.FromInt32(named.ObjectId),
                SqlValue.FromInt32(database.Id),
                SqlValue.FromSystemName(database.Name),
            ]);
        }
        return rows;
    }

    /// <summary>
    /// Opens a scrollable read-only snapshot of <paramref name="rows"/> and
    /// binds it into the caller's cursor variable; a call that wrote no OUTPUT
    /// hands it to no one.
    /// </summary>
    private static void HandOutDescribedCursor(BatchContext batch, string? variable, SqlType[] schema, string[] names, List<SqlValue[]> rows)
    {
        if (variable is null)
            return;
        var encoded = rows.ConvertAll(row => RowEncoder.EncodeRow(schema, row));
        var selection = Selection.FromRows(schema, names, encoded);
        var cursor = new Cursor(string.Empty, selection, CursorSensitivity.Static, scrollable: true, readOnly: true, plan: null)
        {
            IsUnnamed = true,
            Handle = batch.Connection.LastCursorHandle += 2,
        };
        cursor.Open(batch);
        RebindCursorVariable(batch, variable, cursor);
    }
}
