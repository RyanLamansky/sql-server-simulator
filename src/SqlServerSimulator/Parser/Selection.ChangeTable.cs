using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The <c>CHANGETABLE</c> rowset: <c>CHANGES t, last_sync_version</c> folds a
/// tracked table's committed history into one net change per row, and
/// <c>VERSION t, (key columns), (values)</c> looks rows up in the table and
/// reports each one's latest change. Column shapes and every refusal are
/// probed against SQL Server 2025 (2026-09-27); see
/// <c>docs/claude/change-tracking.md</c>.
/// </summary>
internal sealed partial class Selection
{
    private static readonly NCharSqlType ChangeOperationType = NCharSqlType.Get(1, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit);
    private static readonly VarbinarySqlType ChangeColumnsType = VarbinarySqlType.Get(4100);
    private static readonly VarbinarySqlType ChangeContextType = VarbinarySqlType.Get(128);
    private static readonly SqlValue InsertOperation = SqlValue.FromNChar(ChangeOperationType, "I");
    private static readonly SqlValue UpdateOperation = SqlValue.FromNChar(ChangeOperationType, "U");
    private static readonly SqlValue DeleteOperation = SqlValue.FromNChar(ChangeOperationType, "D");

    private static bool IsChangeTableName(string name) => BuiltInToken.Equals(name, "CHANGETABLE");

    /// <summary>
    /// Parses a <c>CHANGETABLE(…) [AS] alias [(column, …)]</c> source from the
    /// <c>CHANGETABLE</c> name, leaving the cursor past it. The whole source
    /// parses before its table binds, so a syntax error wins over a missing
    /// table; then the alias is mandatory (Msg 22104). A table missing while
    /// the batch walks in skip mode becomes a placeholder, as an ordinary
    /// table reference does.
    /// </summary>
    private static FromSource ParseChangeTableSource(ParserContext context, QueryScope scope)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var versionForm = context.GetNextRequired() switch
        {
            Name word when BuiltInToken.Equals(word.Value, "VERSION") => true,
            Name word when BuiltInToken.Equals(word.Value, "CHANGES") => false,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        if (context.GetNextRequired() is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = BatchContext.ParseObjectName(context);
        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        Expression? lastSyncVariable = null;
        long? lastSyncLiteral = null;
        List<string>? versionColumns = null;
        List<Expression>? versionValues = null;
        if (versionForm)
            (versionColumns, versionValues) = ParseChangeTableVersionLists(context, scope);
        else
            (lastSyncVariable, lastSyncLiteral) = ParseChangeTableLastSync(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        var alias = ConsumeOptionalAliasAtCurrent(context);
        List<string>? columnAliases = null;
        if (alias is not null && context.Token is Operator { Character: '(' })
        {
            columnAliases = [];
            do
            {
                columnAliases.Add(context.GetNextRequired<Name>().Value);
            }
            while (context.GetNextRequired() is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }

        var table = ResolveChangeTableTarget(context, name);
        if (table is null)
        {
            context.Batch.CurrentStatement.BindsDeferredSource = true;
            return FromSource.DeferredPlaceholder(alias ?? throw SimulatedSqlException.ChangeTableRequiresAlias());
        }
        // Real binds the tracking state while compiling, so an untracked table
        // refuses even from a branch never taken or a module body at CREATE
        // (probed 2026-09-27 against SQL Server 2025).
        if (table.ChangeTracking is null)
            throw SimulatedSqlException.ChangeTrackingNotEnabledOnTable(table.Name);
        if (alias is null)
            throw SimulatedSqlException.ChangeTableRequiresAlias();

        var plan = versionForm
            ? BindChangeTableVersion(context, table, versionColumns!, versionValues!)
            : BindChangeTableChanges(table, lastSyncVariable, lastSyncLiteral);
        var columnNames = plan.ColumnNames;
        if (columnAliases is not null)
        {
            if (columnAliases.Count < columnNames.Length)
                throw SimulatedSqlException.HasMoreColumnsThanColumnList(alias);
            if (columnAliases.Count > columnNames.Length)
                throw SimulatedSqlException.HasFewerColumnsThanColumnList(alias);
            columnNames = [.. columnAliases];
        }
        var columns = new HeapColumn[plan.Schema.Length];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = new HeapColumn(columnNames[i], plan.Schema[i], maxLength: null, nullable: plan.ColumnNullability?[i] ?? true);
        return new FromSource(
            qualifier: alias,
            columnNames: columnNames,
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            lateralPlan: plan);
    }

    /// <summary>
    /// Resolves <c>CHANGETABLE</c>'s table. Anything that exists but isn't a
    /// user table — a view, a catalog view, a function, a <c>#temp</c> table —
    /// is Msg 22107 naming it as written; a missing name is Msg 208.
    /// </summary>
    private static HeapTable? ResolveChangeTableTarget(ParserContext context, MultiPartName name)
    {
        var batch = context.Batch;
        if (batch.TryResolveTable(name, out var table))
        {
            return table.IsTableVariable || BatchContext.IsLocalTempName(table.Name) || BatchContext.IsGlobalTempName(table.Name)
                ? throw SimulatedSqlException.ChangeTableObjectNotSupported(name.Written)
                : table;
        }
        if (batch.TryResolveView(name, out _) || batch.TryResolveCatalogView(name, out _, out _) || batch.TryResolveFunction(name, out _))
            throw SimulatedSqlException.ChangeTableObjectNotSupported(name.Written);
        if (batch.IsSkipping)
            return null;
        throw BatchContext.IsLocalTempName(name.Leaf) || BatchContext.IsGlobalTempName(name.Leaf)
            ? SimulatedSqlException.InvalidObjectName(name, state: 0)
            : batch.UnresolvableObjectName(name);
    }

    /// <summary>
    /// Parses <c>CHANGES</c>' last synchronized version from the token after
    /// the table's comma, leaving the cursor past it: <c>NULL</c>, an integer
    /// literal with an optional minus, or a variable converted to
    /// <c>bigint</c> — an expression is a syntax error, and a parenthesized
    /// one is reported at its first token.
    /// </summary>
    private static (Expression? Variable, long? Literal) ParseChangeTableLastSync(ParserContext context)
    {
        Expression? variable = null;
        long? literal = null;
        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.Null }:
                break;
            case Numeric number:
                literal = IntegerLiteralValue(context, number, negate: false);
                break;
            case Operator { Character: '-' }:
                literal = context.GetNextRequired() is Numeric negative
                    ? IntegerLiteralValue(context, negative, negate: true)
                    : throw SimulatedSqlException.SyntaxErrorNear(context);
                break;
            case AtPrefixedString atPrefixed:
                variable = new VariableReference(atPrefixed, context);
                break;
            case Operator { Character: '(' }:
                context.MoveNextRequired();
                throw SimulatedSqlException.SyntaxErrorNear(context);
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        context.MoveNextRequired();
        return (variable, literal);
    }

    /// <summary>
    /// An integer literal's value; a fraction, an exponent or a literal past
    /// <c>bigint</c> is the syntax error at it.
    /// </summary>
    private static long IntegerLiteralValue(ParserContext context, Numeric number, bool negate) =>
        long.TryParse(number.Source, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? negate ? -value : value
            : throw SimulatedSqlException.SyntaxErrorNear(context);

    private static Selection BindChangeTableChanges(HeapTable table, Expression? lastSyncVariable, long? lastSyncLiteral)
    {
        if (lastSyncVariable is VariableReference { DeclaredType: { Category: SqlTypeCategory.DateTime } declared })
            throw SimulatedSqlException.ImplicitConversionNotAllowed(declared.SqlServerName, "bigint");
        var keyOrdinals = TableChangeTracking.KeyOrdinals(table);
        var schema = new SqlType[5 + keyOrdinals.Length];
        var names = new string[schema.Length];
        var nullability = new bool[schema.Length];
        // Only the key columns trace to the table (probed 2026-09-27 against
        // SQL Server 2025, sp_describe_first_result_set's is_updateable).
        var wireFlags = new byte[schema.Length];
        (schema[0], names[0]) = (SqlType.BigInt, "SYS_CHANGE_VERSION");
        (schema[1], names[1]) = (SqlType.BigInt, "SYS_CHANGE_CREATION_VERSION");
        (schema[2], names[2]) = (ChangeOperationType, "SYS_CHANGE_OPERATION");
        (schema[3], names[3]) = (ChangeColumnsType, "SYS_CHANGE_COLUMNS");
        (schema[4], names[4]) = (ChangeContextType, "SYS_CHANGE_CONTEXT");
        for (var i = 0; i < 5; i++)
            nullability[i] = true;
        for (var i = 0; i < keyOrdinals.Length; i++)
        {
            schema[5 + i] = table.Columns[keyOrdinals[i]].Type;
            names[5 + i] = table.Columns[keyOrdinals[i]].Name;
            wireFlags[5 + i] = 0x08;
        }

        return new Selection(schema, names, hasOrderBy: false, hasTopOrOffsetOrFetch: false,
            valueRowSource: (batch, _) => EnumerateChanges(batch, table, schema, lastSyncVariable, lastSyncLiteral))
        {
            ColumnNullability = nullability,
            ColumnWireFlags = wireFlags,
        };
    }

    private static IEnumerable<SqlValue[]> EnumerateChanges(BatchContext batch, HeapTable table, SqlType[] schema, Expression? lastSyncVariable, long? lastSyncLiteral)
    {
        var tracking = table.ChangeTracking ?? throw SimulatedSqlException.ChangeTrackingNotEnabledOnTable(table.Name);
        var lastSync = lastSyncLiteral;
        if (lastSyncVariable is not null)
        {
            var value = lastSyncVariable.Run(new RuntimeContext(static name => throw SimulatedSqlException.InvalidColumnName(name), batch));
            if (!value.IsNull)
            {
                if (value.Type.Category == SqlTypeCategory.String)
                {
                    lastSync = long.TryParse(value.AsString.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : throw SimulatedSqlException.StringConversionToNumberFailed(value.Type, "bigint");
                }
                else
                {
                    lastSync = value.CoerceTo(SqlType.BigInt).AsInt64;
                }
            }
        }

        var changes = tracking.NetChanges(lastSync);
        changes.Sort(static (a, b) => CompareKeys(a.Key, b.Key));
        foreach (var change in changes)
        {
            var row = new SqlValue[schema.Length];
            row[0] = SqlValue.FromInt64(change.Version);
            row[1] = change.CreationVersion is { } creation ? SqlValue.FromInt64(creation) : SqlValue.Null(SqlType.BigInt);
            row[2] = change.Operation switch
            {
                ChangeTrackingOperation.Insert => InsertOperation,
                ChangeTrackingOperation.Update => UpdateOperation,
                _ => DeleteOperation,
            };
            row[3] = change.Columns is { } columns ? SqlValue.FromVarbinary(ChangeColumnsType, EncodeColumnsMask(columns)) : SqlValue.Null(ChangeColumnsType);
            row[4] = change.Context is { } changeContext ? SqlValue.FromVarbinary(ChangeContextType, changeContext) : SqlValue.Null(ChangeContextType);
            for (var i = 0; i < change.Key.Length; i++)
                row[5 + i] = change.Key[i].CoerceTo(schema[5 + i]);
            yield return row;
        }
    }

    /// <summary>
    /// <c>SYS_CHANGE_COLUMNS</c>' encoding as real writes it: four zero bytes,
    /// then each column id as a little-endian <c>int</c>.
    /// </summary>
    private static byte[] EncodeColumnsMask(List<int> columnIds)
    {
        var mask = new byte[4 + (4 * columnIds.Count)];
        for (var i = 0; i < columnIds.Count; i++)
            _ = BitConverter.TryWriteBytes(mask.AsSpan(4 + (4 * i)), columnIds[i]);
        return mask;
    }

    private static int CompareKeys(SqlValue[] a, SqlValue[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var compared = a[i].IsNull ? (b[i].IsNull ? 0 : -1) : b[i].IsNull ? 1 : a[i].CompareTo(b[i]);
            if (compared != 0)
                return compared;
        }
        return 0;
    }

    /// <summary>
    /// Parses <c>VERSION</c>'s <c>(key columns), (values)</c> pair from the
    /// token after the table's comma, leaving the cursor on the closing
    /// <c>)</c>. The values may be any expression, including one that
    /// correlates to an <c>APPLY</c>'s left side.
    /// </summary>
    private static (List<string> Columns, List<Expression> Values) ParseChangeTableVersionLists(ParserContext context, QueryScope scope)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var columns = new List<string>();
        while (true)
        {
            if (context.GetNextRequired() is not Name column)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            columns.Add(column.Value);
            var separator = context.GetNextRequired();
            if (separator is Operator { Character: ')' })
                break;
            if (separator is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var values = new List<Expression>();
        context.MoveNextRequired();
        while (true)
        {
            values.Add(Expression.Parse(context));
            if (context.Token is Operator { Character: ')' })
                break;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
        var typeResolver = scope.OuterTypeResolver ?? (name => throw SimulatedSqlException.InvalidColumnName(name));
        foreach (var value in values)
            _ = value.GetSqlType(context.Batch, typeResolver);
        context.MoveNextRequired();
        return (columns, values);
    }

    /// <summary>
    /// Binds <c>VERSION</c>'s lists to <paramref name="table"/>: every column
    /// must be a key column (Msg 22111), then the list must be as long as the
    /// key (Msg 22110), then the values as long as the columns (Msg 22103).
    /// </summary>
    private static Selection BindChangeTableVersion(ParserContext context, HeapTable table, List<string> columns, List<Expression> values)
    {
        var keyOrdinals = TableChangeTracking.KeyOrdinals(table);
        var collation = context.Batch.DatabaseFor(table).Collation;
        var lookupOrdinals = new int[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var ordinal = Array.FindIndex(table.Columns, candidate => collation.Equals(candidate.Name, column));
            if (ordinal < 0 || Array.IndexOf(keyOrdinals, ordinal) < 0)
                throw SimulatedSqlException.ChangeTableVersionColumnNotInKey(column, table.Name);
            lookupOrdinals[i] = ordinal;
        }
        if (lookupOrdinals.Length != keyOrdinals.Length)
            throw SimulatedSqlException.ChangeTableVersionColumnCount(table.Name);
        if (values.Count != lookupOrdinals.Length)
            throw SimulatedSqlException.ChangeTableVersionArgumentsInvalid();

        var schema = new SqlType[2 + keyOrdinals.Length];
        var names = new string[schema.Length];
        var nullability = new bool[schema.Length];
        // Real reports every column but the version as tracing to the table
        // (probed 2026-09-27 against SQL Server 2025).
        var wireFlags = new byte[schema.Length];
        (schema[0], names[0], nullability[0]) = (SqlType.BigInt, "SYS_CHANGE_VERSION", true);
        (schema[1], names[1], nullability[1], wireFlags[1]) = (ChangeContextType, "SYS_CHANGE_CONTEXT", true, 0x08);
        for (var i = 0; i < keyOrdinals.Length; i++)
        {
            schema[2 + i] = table.Columns[keyOrdinals[i]].Type;
            names[2 + i] = table.Columns[keyOrdinals[i]].Name;
            wireFlags[2 + i] = 0x08;
        }

        Expression[] lookupValues = [.. values];
        return new Selection(schema, names, hasOrderBy: false, hasTopOrOffsetOrFetch: false,
            valueRowSource: (batch, outerResolver) => EnumerateVersions(batch, outerResolver, table, keyOrdinals, lookupOrdinals, lookupValues, schema.Length))
        {
            ColumnNullability = nullability,
            ColumnWireFlags = wireFlags,
        };
    }

    private static IEnumerable<SqlValue[]> EnumerateVersions(BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver, HeapTable table, int[] keyOrdinals, int[] lookupOrdinals, Expression[] lookupValues, int width)
    {
        var tracking = table.ChangeTracking ?? throw SimulatedSqlException.ChangeTrackingNotEnabledOnTable(table.Name);
        var runtime = new RuntimeContext(outerResolver ?? (name => throw SimulatedSqlException.InvalidColumnName(name)), batch);
        var wanted = new SqlValue[lookupValues.Length];
        for (var i = 0; i < wanted.Length; i++)
            wanted[i] = lookupValues[i].Run(runtime);
        var pending = batch.Connection.CurrentTransaction?.UndoLog;

        var matches = new List<SqlValue[]>();
        foreach (var bytes in table.Rows)
        {
            var row = RowDecoder.DecodeRow(table.StoredColumns, bytes, table.Heap);
            var matched = true;
            for (var i = 0; i < lookupOrdinals.Length && matched; i++)
            {
                var stored = table.StorageOrdinals[lookupOrdinals[i]];
                matched = BooleanExpression.CompareValuesPromoted(row[stored], wanted[i], "equal to", static (l, r) => l.Equals(r)) == true;
            }
            if (!matched)
                continue;
            var key = new SqlValue[keyOrdinals.Length];
            for (var i = 0; i < key.Length; i++)
                key[i] = row[table.StorageOrdinals[keyOrdinals[i]]];
            matches.Add(key);
        }

        foreach (var key in matches)
        {
            var result = new SqlValue[width];
            var lookupKey = new SqlValueKey(key);
            var latest = pending is not null && pending.HasPendingChange(tracking, lookupKey) ? null : tracking.LatestChange(lookupKey);
            result[0] = latest is { } found ? SqlValue.FromInt64(found.Version) : SqlValue.Null(SqlType.BigInt);
            result[1] = latest is { Context: { } changeContext } ? SqlValue.FromVarbinary(ChangeContextType, changeContext) : SqlValue.Null(ChangeContextType);
            for (var i = 0; i < key.Length; i++)
                result[2 + i] = key[i];
            yield return result;
        }
    }
}
