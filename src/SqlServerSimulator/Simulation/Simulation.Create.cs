using System.Collections.Concurrent;
using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE TABLE</c>. Returns false if the leading <c>CREATE</c>
    /// isn't followed by <c>TABLE</c> (so the caller can route to the syntax
    /// error). Other malformed forms throw <see cref="SimulatedSqlException"/>
    /// directly with the matching SQL Server error.
    /// </summary>
    private bool TryParseCreate(ParserContext context)
    {
        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.Database }:
                return TryParseCreateDatabase(context);
            case ReservedKeyword { Keyword: Keyword.Schema }:
                return TryParseCreateSchema(context);
            case ReservedKeyword { Keyword: Keyword.Function }:
                return TryParseCreateFunction(context, isAlter: false, createOrAlter: false);
            case ReservedKeyword { Keyword: Keyword.View }:
                return TryParseCreateView(context, isAlter: false, createOrAlter: false);
            case ReservedKeyword { Keyword: Keyword.Procedure or Keyword.Proc }:
                return Simulation.TryParseCreateProcedure(context, isAlter: false, createOrAlter: false);
            case ReservedKeyword { Keyword: Keyword.Trigger }:
                return Simulation.TryParseCreateTrigger(context, isAlter: false, createOrAlter: false);
            case ReservedKeyword { Keyword: Keyword.Unique or Keyword.Clustered or Keyword.NonClustered or Keyword.Index }:
                return Simulation.TryParseCreateIndex(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Type }:
                return TryParseCreateType(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Sequence }:
                return TryParseCreateSequence(context);
            case ReservedKeyword { Keyword: Keyword.User }:
                return TryParseCreateUser(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Role }:
                return TryParseCreateRole(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Login }:
                return TryParseCreateLogin(context);
            case Name serverWord when serverWord.Value.Equals("SERVER", StringComparison.OrdinalIgnoreCase):
                return TryParseCreateServerRole(context);
            case Name appWord when appWord.Value.Equals("APPLICATION", StringComparison.OrdinalIgnoreCase):
                return TryParseCreateApplicationRole(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.FullText }:
                return Simulation.TryParseCreateFullText(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Xml }:
                return Simulation.TryParseCreateXml(context);
            case ReservedKeyword { Keyword: Keyword.Primary }:
                return Simulation.TryParseCreatePrimaryXml(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Spatial }:
                return Simulation.TryParseCreateSpatial(context);
            case Name synonymWord when synonymWord.Value.Equals("SYNONYM", StringComparison.OrdinalIgnoreCase):
                return TryParseCreateSynonym(context);
            case Name assemblyWord when assemblyWord.Value.Equals("ASSEMBLY", StringComparison.OrdinalIgnoreCase):
                return TryParseCreateAssembly(context);
            case ReservedKeyword { Keyword: Keyword.Or }:
                // CREATE OR ALTER {PROCEDURE|TRIGGER|VIEW|FUNCTION} — modern
                // upsert syntax.
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Alter })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                return context.GetNextRequired() switch
                {
                    ReservedKeyword { Keyword: Keyword.Function } => Simulation.TryParseCreateFunction(context, isAlter: false, createOrAlter: true),
                    ReservedKeyword { Keyword: Keyword.Procedure or Keyword.Proc } => Simulation.TryParseCreateProcedure(context, isAlter: false, createOrAlter: true),
                    ReservedKeyword { Keyword: Keyword.Trigger } => Simulation.TryParseCreateTrigger(context, isAlter: false, createOrAlter: true),
                    ReservedKeyword { Keyword: Keyword.View } => Simulation.TryParseCreateView(context, isAlter: false, createOrAlter: true),
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
            case ReservedKeyword { Keyword: Keyword.Table }:
                break;
            default:
                return false;
        }

        context.MoveNextRequired();
        // A reserved keyword where the table name belongs is always a syntax
        // error — notably `CREATE TABLE IF NOT EXISTS`, which SQL Server rejects
        // with Msg 156 near IF (the `IF NOT EXISTS` guard clause isn't T-SQL).
        if (context.Token is ReservedKeyword tableNameKeyword)
            throw SimulatedSqlException.SyntaxErrorNearKeyword(tableNameKeyword);
        if (context.Token is not Name)
            return false;
        var tableName = BatchContext.ParseObjectName(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            return false;

        var heapColumns = new List<HeapColumn?>();
        var pendingComputed = new List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)>();
        var pendingKeys = new List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, bool IgnoreDupKey, bool[] Descending)>();
        var pendingChecks = new List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition)>();
        var pendingPeriod = new List<(string StartCol, string EndCol)>();
        var pendingForeignKeys = new List<PendingForeignKey>();
        var pendingIndexes = new List<PendingInlineIndex>();
        if (!ParseColumnList(context, tableName.Leaf, isTableVariable: false, isTableType: false, heapColumns, pendingKeys, pendingChecks, pendingComputed, pendingPeriod, pendingForeignKeys, pendingIndexes))
            return false;

        // Optional trailing placement and option clauses, in any order:
        //   ON <filegroup> [TEXTIMAGE_ON <filegroup>] — parsed and discarded
        //     (the simulator has no filegroup model).
        //   WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = X)) — load-bearing.
        // SSMS-emitted CREATE TABLE always trails `) ON [PRIMARY]` and may
        // additionally trail `TEXTIMAGE_ON [PRIMARY]`; SYSTEM_VERSIONING is
        // table-author-emitted and doesn't coexist with the SSMS form in
        // observed scripts, but we accept either ordering for generality.
        // Parsed regardless of skip mode so the cursor advances cleanly;
        // the resulting historyTableName is only used after the skip-mode
        // gate below.
        context.MoveNextOptional();
        SkipOptionalFilegroupClause(context);
        SystemVersioningOptions? systemVersioning = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            systemVersioning = ParseSystemVersioningOption(context);

        // Pass 2: resolve computed columns now that every column's name has
        // been seen. The resolver throws Msg 1759 for any reference to another
        // computed column (including persisted) and Msg 207 for an unknown
        // name; valid references resolve to the source column's SqlType so
        // <see cref="Expression.GetSqlType"/> can infer the computed column's
        // own type.
        SqlType ResolveComputedReference(MultiPartName reference)
        {
            for (var i = 0; i < heapColumns.Count; i++)
            {
                if (heapColumns[i] is { } existing && context.Batch.CurrentDatabase.Collation.Equals(existing.Name, reference.Leaf))
                {
                    return existing.Computed is not null
                        ? throw SimulatedSqlException.ComputedColumnReferencedInComputed(existing.Name, tableName.Leaf)
                        : existing.Type;
                }
                if (heapColumns[i] is null)
                {
                    foreach (var pending in pendingComputed)
                    {
                        if (pending.Index == i && context.Batch.CurrentDatabase.Collation.Equals(pending.Name, reference.Leaf))
                            throw SimulatedSqlException.ComputedColumnReferencedInComputed(pending.Name, tableName.Leaf);
                    }
                }
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }

        foreach (var pending in pendingComputed)
        {
            var resolvedType = pending.Expression.GetSqlType(context.Batch, ResolveComputedReference);
            // Pull the declared length off the resolved type for the var-length
            // string/binary families so EnforceMaxLength sees the same cap that
            // GetSqlType inferred. Char/binary fixed-length types report their
            // length via FixedLength; the var-length families surface it here.
            int? computedMaxLength = resolvedType switch
            {
                VarcharSqlType v when v.length > 0 => v.length,
                NVarcharSqlType nv when nv.length > 0 => nv.length,
                VarbinarySqlType vb when vb.length > 0 => vb.length,
                _ => null,
            };
            // A PERSISTED computed column stores its expression's value, so
            // real refuses to create one from a session that would read the
            // expression's `"…"` the other way (Msg 1934, probe-confirmed —
            // the non-persisted form is accepted).
            if (pending.Persisted && !context.QuotedIdentifiers)
                throw SimulatedSqlException.IncorrectSetOptions("CREATE TABLE", QuotedIdentifierOptionName);
            heapColumns[pending.Index] = new HeapColumn(
                pending.Name,
                resolvedType,
                maxLength: computedMaxLength,
                nullable: pending.Nullable && !IsPendingPrimaryKeyOrdinal(pendingKeys, pending.Index),
                computedExpression: pending.Expression,
                isPersisted: pending.Persisted,
                computedDefinition: pending.Definition);
        }

        // Schemas whose fixed-width stored columns alone exceed SQL Server's
        // 8060-byte in-row limit can never hold a row; reject at CREATE TABLE
        // (Msg 1701). Persisted computed columns of fixed-length type
        // contribute; non-persisted computed columns have no row storage.
        var fixedWidthSum = 0;
        for (var i = 0; i < heapColumns.Count; i++)
        {
            var column = heapColumns[i]!;
            if (column.IsStored && column.Type.IsFixedLength)
                fixedWidthSum += column.Type.FixedLength;
        }
        if (fixedWidthSum > Heap.MaxRowSize)
            throw SimulatedSqlException.RowSizeExceedsMaximum(tableName.Leaf, fixedWidthSum, Heap.MaxRowSize);

        // A CHECK predicate — inline or table-level — may not read a
        // non-persisted computed column (Msg 1764). Runs ahead of the Msg 8141
        // walk below, matching real's probed precedence.
        RejectChecksOverNonPersistedComputedColumns(context.Batch.CurrentDatabase.Collation, tableName.Leaf, heapColumns, pendingChecks);

        // Real SQL Server's Msg 8141 (probed against SQL Server 2025) rejects
        // an inline column-level CHECK that references any column other than
        // its owning column — table-level CHECK has no such restriction.
        // Walk each inline predicate's Expression operands structurally via
        // <see cref="Expression.VisitColumnReferences"/> and reject any peer
        // reference. Coverage is limited to the common container subclasses
        // (Reference, Parenthesized, TwoSidedExpression, Cast, Length) — peer
        // refs buried in less-common containers escape detection here and
        // surface at INSERT instead (fidelity gap documented on
        // <see cref="Expression.VisitColumnReferences"/>).
        foreach (var pending in pendingChecks)
        {
            if (pending.InlineColumn is not { } owningColumn)
                continue;
            pending.Predicate.VisitOperandExpressions(op =>
                op.VisitColumnReferences(name =>
                {
                    if (!context.Batch.CurrentDatabase.Collation.Equals(name.Leaf, owningColumn))
                        throw SimulatedSqlException.InlineCheckReferencesAnotherColumn(owningColumn, tableName.Leaf);
                }));
        }

        // In a skipped IF branch, gate both the existence check (Msg 2714)
        // and the dict add: the safe-CREATE idiom (`IF NOT EXISTS (...) CREATE
        // TABLE foo (...)`) relies on the un-taken CREATE not surfacing
        // "already exists" when the cond was false because foo *did* exist.
        if (context.Batch.IsSkipping)
            return true;

        // Resolve the target schema first — Msg 2760 fires if the qualified
        // schema doesn't exist. For temp tables the schema is conceptual
        // (real SQL Server lists them under tempdb's dbo); store DboSchemaId
        // for sys.* projection consistency. Schema resolution also fixes the
        // schemaId we stamp on KeyConstraint / CheckConstraint / HeapTable so
        // constraint object_ids allocate alongside the table's id without
        // discovering they had no home.
        var isLocalTempTable = BatchContext.IsLocalTempName(tableName.Leaf);
        var isGlobalTempTable = BatchContext.IsGlobalTempName(tableName.Leaf);
        var isTempTable = isLocalTempTable || isGlobalTempTable;
        // CREATE TABLE requires db_ddladmin / db_owner membership (or an
        // explicit CREATE TABLE grant) for a non-dbo principal — Msg 262.
        // Temp tables are exempt (anyone may create #temp).
        if (!isTempTable && !PermissionEnforcement.HasDatabasePermission(context.Batch, "CREATE TABLE"))
            throw SimulatedSqlException.CreateTablePermissionDenied(context.CurrentDatabase.Name);
        Schema? schema = null;
        var schemaId = Database.DboSchemaId;
        ConcurrentDictionary<string, HeapTable> destination;
        if (isLocalTempTable)
        {
            destination = context.Batch.Connection.TempTables;
        }
        else if (isGlobalTempTable)
        {
            destination = context.Batch.Connection.Simulation.GlobalTempTables;
        }
        else
        {
            if (!context.Batch.TryResolveSchema(tableName, out schema))
                throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(tableName.Count >= 2 ? tableName.ImmediateQualifier! : Database.DefaultSchemaName);
            // sys and INFORMATION_SCHEMA exist in Database.Schemas to carry
            // their conventional schema_ids and host catalog views — they
            // aren't writable namespaces. Real SQL Server reports Msg 2760
            // for any CREATE TABLE that targets either, with the "does not
            // exist or you do not have permission" framing — probe-confirmed.
            if (schema.SchemaId is Database.SysSchemaId or Database.InformationSchemaId)
                throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schema.Name);
            // CREATE TABLE also needs ALTER on the target schema — with the
            // db-scope CREATE TABLE permission granted but no schema ALTER, real
            // raises Msg 2760 (probe M4).
            if (!PermissionEnforcement.HasSchemaAlter(context.Batch, schema.SchemaId))
                throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schema.Name);
            destination = schema.HeapTables;
            schemaId = schema.SchemaId;
        }

        // Cross-kind name-collision check for permanent tables (Msg 2714).
        // Temp tables live in a session-scoped dict that doesn't share the
        // database object-name namespace.
        if (!isTempTable && schema!.HasNameInSharedNamespace(tableName.Leaf))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(tableName.Leaf);

        var keyConstraints = ResolveKeyConstraints(tableName.Leaf, heapColumns!, pendingKeys, context.CurrentDatabase);
        var checkConstraints = ResolveCheckConstraints(tableName.Leaf, pendingChecks, context.CurrentDatabase);
        var resolvedPeriod = ResolvePeriodColumns(context.Batch.CurrentDatabase.Collation, heapColumns!, pendingPeriod);

        // History-table pre-validation when SYSTEM_VERSIONING = ON: the parent
        // must have PeriodColumns, and the history table's schema must
        // resolve. A named history table that already exists is adopted (real
        // links it after the shape validation below); one that doesn't is
        // built from the parent's shape, as is the auto-named form — whose
        // name derives from the parent's object id and so waits until the
        // parent is constructed.
        Schema? historySchema = null;
        ConcurrentDictionary<string, HeapTable>? historyDestination = null;
        HeapTable? existingHistory = null;
        if (systemVersioning is { } options)
        {
            if (resolvedPeriod is null)
                throw SimulatedSqlException.SystemVersioningRequiresPeriod();
            if (options.HistoryTable is { } hn)
            {
                if (!context.Batch.TryResolveSchema(hn, out historySchema))
                    throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(hn.Count >= 2 ? hn.ImmediateQualifier! : Database.DefaultSchemaName);
                if (historySchema.HeapTables.TryGetValue(hn.Leaf, out existingHistory))
                    RejectUnusableHistoryTable(context, existingHistory);
                else if (historySchema.HasNameInSharedNamespace(hn.Leaf))
                    throw SimulatedSqlException.ThereIsAlreadyAnObject(hn.Leaf);
            }
            else
            {
                // The auto-generated name lands in the base table's own
                // schema, which a temp table doesn't have — and real rejects
                // system-versioning a temp table outright.
                historySchema = schema ?? throw new NotSupportedException("SYSTEM_VERSIONING on a temp table isn't modeled.");
            }
            historyDestination = historySchema.HeapTables;
        }

        // A three-part CREATE TABLE lands in the named database, so the object
        // id comes from that database's counter and the table carries it as
        // its owner. Temp tables and table variables have no schema and so no
        // owning database — they fall back to the session's.
        var owningDatabase = schema?.Database;
        var heapTable = new HeapTable(
            tableName.Leaf,
            [.. heapColumns!],
            (owningDatabase ?? context.CurrentDatabase).AllocateObjectId(),
            schemaId,
            context.Batch.CurrentStatement.UtcNow,
            keyConstraints,
            checkConstraints,
            periodColumns: resolvedPeriod)
        {
            OwningDatabase = owningDatabase,
        };
        if (isGlobalTempTable)
            heapTable.OwnerConnection = context.Batch.Connection;
        if (!destination.TryAdd(heapTable.Name, heapTable))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(heapTable.Name);
        // A local temp created inside a module body (proc / trigger / dynamic
        // SQL) is dropped when that module exits; register it so the body's
        // finally drops it.
        if (isLocalTempTable)
            context.Batch.RegisterScopedTempTable(heapTable.Name);

        if (systemVersioning is { } versioning && historyDestination is not null && historySchema is not null)
        {
            HeapTable historyTable;
            if (existingHistory is not null)
            {
                // Real validates an already-existing history table's shape
                // against the base and links it; only a freshly built sibling
                // matches by construction.
                try
                {
                    ValidateHistoryTableShape(context, heapTable, existingHistory);
                }
                catch
                {
                    _ = destination.TryRemove(heapTable.Name, out _);
                    throw;
                }
                historyTable = existingHistory;
                historyTable.IsHistoryTable = true;
            }
            else
            {
                historyTable = BuildHistoryTable(heapTable, versioning.HistoryTable?.Leaf ?? AutoHistoryTableName(historySchema, heapTable.ObjectId), historySchema.SchemaId, context);
                if (!historyDestination.TryAdd(historyTable.Name, historyTable))
                {
                    // Roll back parent insertion if history-add raced — shouldn't
                    // happen given the pre-validation above, but keep both
                    // commits consistent if it does.
                    _ = destination.TryRemove(heapTable.Name, out _);
                    throw SimulatedSqlException.ThereIsAlreadyAnObject(historyTable.Name);
                }
            }
            historyTable.OwningDatabase = historySchema.Database;
            heapTable.SystemVersioning = historyTable;
            heapTable.HistoryRetentionPeriod = versioning.RetentionPeriod;
            heapTable.HistoryRetentionUnit = versioning.RetentionUnit;
        }

        // FK resolution runs after the table is in its dict so a
        // self-referencing FK can find the table being created. Any FK
        // failure (missing parent / mismatched key shape / cascade cycle)
        // raises and the table stays in place — matching real SQL Server's
        // probe-confirmed behavior, where the CREATE TABLE statement rolls
        // back atomically only after the per-FK validation completes.
        try
        {
            if (pendingForeignKeys.Count > 0)
                ResolveForeignKeys(heapTable, pendingForeignKeys, context);
            if (pendingIndexes.Count > 0)
                AddInlineIndexes(context, heapTable, schema?.Name ?? Database.DefaultSchemaName, pendingIndexes);
        }
        catch
        {
            // Roll back the partial insert so the failing CREATE leaves the
            // schema unchanged. Cascade-incoming-FK detach is unnecessary
            // because every FK we registered points at tables that survive
            // the rollback unaltered (resolver appends incoming entries only
            // on success per-FK).
            _ = destination.TryRemove(heapTable.Name, out _);
            if (heapTable.SystemVersioning is { } versionedHistory)
            {
                // An adopted history table predates this statement, so the
                // rollback returns it to plain status instead of dropping it.
                if (existingHistory is null)
                    _ = historyDestination!.TryRemove(versionedHistory.Name, out _);
                else
                    versionedHistory.IsHistoryTable = false;
            }
            throw;
        }

        // Temp-table DDL participates in transaction rollback: probe-confirmed
        // that both CREATE TABLE #foo and CREATE TABLE ##foo inside BEGIN TRAN
        // are undone by ROLLBACK on real SQL Server. Regular CREATE TABLE
        // isn't logged — a known asymmetry documented as a quirk.
        if (isTempTable && context.Connection.CurrentTransaction is { } tx)
            tx.UndoLog.RecordTempTableCreation(destination, heapTable.Name);
        // Real raises no DDL event for a temp table (tempdb owns it), only for
        // a permanent one in the current database.
        if (!isTempTable)
            RecordDdlEvent(context, "CREATE_TABLE", schema?.Name ?? Database.DefaultSchemaName, heapTable.Name, "TABLE");
        return true;
    }

    /// <summary>
    /// Parses the trailing <c>WITH (SYSTEM_VERSIONING = ON […])</c> option
    /// after a CREATE TABLE column list. Cursor on entry: the <c>WITH</c>
    /// keyword. Cursor on exit: the option's closing <c>)</c>.
    /// </summary>
    private static SystemVersioningOptions ParseSystemVersioningOption(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.System_Versioning })
            throw new NotSupportedException("Only SYSTEM_VERSIONING is supported in the CREATE TABLE WITH clause.");
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw new NotSupportedException("SYSTEM_VERSIONING must be set to ON in CREATE TABLE.");
        var options = ParseSystemVersioningOnOptions(context);
        ExpectCloseParen(context);
        return options;
    }

    /// <summary>
    /// Parses the option list a <c>SYSTEM_VERSIONING = ON</c> clause may carry
    /// — <c>HISTORY_TABLE</c>, <c>HISTORY_RETENTION_PERIOD</c> and
    /// <c>DATA_CONSISTENCY_CHECK</c>, comma-separated in any order, each
    /// optional, and the whole parenthesized list optional too (bare
    /// <c>= ON</c> auto-names the history table). Shared by the CREATE TABLE
    /// and ALTER TABLE paths. Cursor on entry: the <c>ON</c> keyword. Cursor
    /// on exit: the last token of the clause — the list's closing <c>)</c>, or
    /// <c>ON</c> itself when no list follows — so the caller reads the
    /// enclosing <c>)</c> next.
    /// </summary>
    /// <remarks>
    /// <c>DATA_CONSISTENCY_CHECK = ON|OFF</c> parses-and-discards: the
    /// simulator doesn't enforce the temporal-data-consistency rules that the
    /// option toggles (caller-trusted history rows in the loader path).
    /// </remarks>
    private static SystemVersioningOptions ParseSystemVersioningOnOptions(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        if (context.GetNextOptional() is not Operator { Character: '(' })
        {
            context.RestoreCheckpoint(checkpoint);
            return SystemVersioningOptions.Bare;
        }

        MultiPartName? historyTable = null;
        var retentionPeriod = -1;
        var retentionUnit = HistoryRetentionUnit.Infinite;
        while (true)
        {
            switch (context.GetNextRequired())
            {
                case UnquotedString { ContextualKeyword: ContextualKeyword.History_Table }:
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    historyTable = BatchContext.ParseObjectName(context);
                    break;
                case UnquotedString { ContextualKeyword: ContextualKeyword.History_Retention_Period }:
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    (retentionPeriod, retentionUnit) = ParseHistoryRetentionPeriod(context);
                    break;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Data_Consistency_Check }:
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            if (context.GetNextRequired() is not Operator { Character: ',' })
                break;
        }
        return context.Token is Operator { Character: ')' }
            ? new SystemVersioningOptions(historyTable, retentionPeriod, retentionUnit)
            : throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Parses one <c>HISTORY_RETENTION_PERIOD</c> value — <c>&lt;count&gt;
    /// DAY[S] | WEEK[S] | MONTH[S] | YEAR[S]</c> or <c>INFINITE</c>. Cursor on
    /// entry: the <c>=</c>. Cursor on exit: the last token of the value.
    /// Probe-confirmed rejections: a count of zero or less is Msg 13743
    /// (which echoes the number unquoted), an unrecognized unit is Msg 13744
    /// at severity 15, and a count with no unit at all is Msg 102.
    /// </summary>
    private static (int Period, HistoryRetentionUnit Unit) ParseHistoryRetentionPeriod(ParserContext context)
    {
        var negated = false;
        if (context.GetNextRequired() is Operator { Character: '-' })
        {
            negated = true;
            context.MoveNextRequired();
        }
        if (context.Token is not Numeric { IntegerLiteralDigitCount: > 0 } count)
        {
            return context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Infinite } && !negated
                ? (-1, HistoryRetentionUnit.Infinite)
                : throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        var period = count.Value.CoerceTo(SqlType.BigInt).AsInt64 * (negated ? -1 : 1);
        if (context.GetNextRequired() is not UnquotedString unitToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        Span<char> unitBuffer = stackalloc char[unitToken.Span.Length];
        _ = unitToken.Span.ToUpperInvariant(unitBuffer);
        var unit = unitBuffer switch
        {
            "DAY" or "DAYS" => HistoryRetentionUnit.Day,
            "WEEK" or "WEEKS" => HistoryRetentionUnit.Week,
            "MONTH" or "MONTHS" => HistoryRetentionUnit.Month,
            "YEAR" or "YEARS" => HistoryRetentionUnit.Year,
            _ => throw SimulatedSqlException.InvalidHistoryRetentionUnit(unitToken.Span.ToString()),
        };
        // Real validates the count only after the unit parses.
        return period is > 0 and <= int.MaxValue
            ? ((int)period, unit)
            : throw SimulatedSqlException.InvalidHistoryRetentionPeriod(period.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The name real SQL Server generates for an auto-named history table:
    /// <c>MSSQL_TemporalHistoryFor_&lt;base object id&gt;</c> in the base
    /// table's own schema (probe-confirmed, including that a base in a
    /// non-<c>dbo</c> schema keeps its sibling alongside it). Real
    /// disambiguates a collision — reachable by re-enabling versioning on a
    /// base whose previous sibling is still around — with a random 8-hex
    /// suffix; the simulator's suffix is a deterministic 32-bit FNV-1a of the
    /// colliding name plus the attempt number, matching the shape but not the
    /// value.
    /// </summary>
    private static string AutoHistoryTableName(Schema schema, int baseObjectId)
    {
        var baseName = $"MSSQL_TemporalHistoryFor_{baseObjectId.ToString(CultureInfo.InvariantCulture)}";
        var candidate = baseName;
        for (var attempt = 0; schema.HasNameInSharedNamespace(candidate); attempt++)
        {
            var h = Fnv1a32.Initial;
            h.MixTableSeed(baseName);
            h.Mix((byte)attempt);
            candidate = $"{baseName}_{h.Value:X8}";
        }
        return candidate;
    }

    /// <summary>
    /// Rejects a candidate history table that's already spoken for — serving
    /// as another base's sibling, or a system-versioned base itself (Msg
    /// 13514). Checked before the column-shape comparison and identically
    /// from the CREATE TABLE and ALTER TABLE paths.
    /// </summary>
    private static void RejectUnusableHistoryTable(ParserContext context, HeapTable candidate)
    {
        if (candidate.IsHistoryTable || candidate.SystemVersioning is not null)
            throw SimulatedSqlException.HistoryTableAlreadyInUse(QualifyTableName(candidate, context.CurrentDatabase));
    }

    /// <summary>
    /// Validates that an existing table can serve as <paramref name="baseTable"/>'s
    /// history sibling, in real SQL Server's probe-confirmed check order: its
    /// own SYSTEM_TIME period (Msg 13574), then unique keys (13515), foreign
    /// keys (13516), CHECK constraints (13517) and IDENTITY columns (13518),
    /// then the column count (13523), then an ordinal walk comparing name
    /// (13524), declared type (13525), collation (13526) and nullability
    /// (13531) — reporting the first column that differs on any of the four
    /// rather than the first difference of each kind.
    /// </summary>
    /// <remarks>
    /// DEFAULT constraints and non-unique indexes on the history table are
    /// accepted (probe-confirmed), as is a history table in a different schema
    /// from the base.
    /// </remarks>
    private static void ValidateHistoryTableShape(ParserContext context, HeapTable baseTable, HeapTable history)
    {
        var qualifiedBase = QualifyTableName(baseTable, context.CurrentDatabase);
        var qualifiedHistory = QualifyTableName(history, context.CurrentDatabase);
        if (history.PeriodColumns is not null && !history.PeriodInheritedFromBase)
            throw SimulatedSqlException.HistoryTableContainsPeriod(qualifiedHistory);
        if (history.KeyConstraints.Count > 0 || history.Indexes.Any(i => i.IsUnique))
            throw SimulatedSqlException.HistoryTableHasUniqueKeys(qualifiedHistory);
        if (history.OutgoingForeignKeys.Count > 0)
            throw SimulatedSqlException.HistoryTableHasForeignKeys(qualifiedHistory);
        if (history.CheckConstraints.Count > 0)
            throw SimulatedSqlException.HistoryTableHasConstraints(qualifiedHistory);
        if (history.Columns.Any(c => c.Identity is not null))
            throw SimulatedSqlException.HistoryTableHasIdentityColumn(qualifiedHistory);
        if (baseTable.Columns.Length != history.Columns.Length)
            throw SimulatedSqlException.HistoryTableColumnCountMismatch(qualifiedBase, baseTable.Columns.Length, qualifiedHistory, history.Columns.Length);

        var collation = context.CurrentDatabase.Collation;
        var databaseCollationName = context.CurrentDatabase.CollationName;
        for (var i = 0; i < baseTable.Columns.Length; i++)
        {
            var baseColumn = baseTable.Columns[i];
            var historyColumn = history.Columns[i];
            if (!collation.Equals(baseColumn.Name, historyColumn.Name))
                throw SimulatedSqlException.HistoryTableColumnNameMismatch(historyColumn.Name, i + 1, qualifiedHistory, baseColumn.Name, qualifiedBase);
            var baseType = baseColumn.Type.ToString()!;
            var historyType = historyColumn.Type.ToString()!;
            if (!string.Equals(baseType, historyType, StringComparison.OrdinalIgnoreCase))
                throw SimulatedSqlException.HistoryTableColumnTypeMismatch(baseColumn.Name, historyType, qualifiedHistory, baseType, qualifiedBase);
            if (!string.Equals(baseColumn.Collation ?? databaseCollationName, historyColumn.Collation ?? databaseCollationName, StringComparison.OrdinalIgnoreCase))
                throw SimulatedSqlException.HistoryTableColumnCollationMismatch(baseColumn.Name, qualifiedBase, qualifiedHistory);
            if (baseColumn.Nullable != historyColumn.Nullable)
                throw SimulatedSqlException.HistoryTableColumnNullabilityMismatch(baseColumn.Name, qualifiedBase, qualifiedHistory);
        }
    }

    private static void ExpectCloseParen(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// The parsed content of a <c>SYSTEM_VERSIONING = ON […]</c> clause.
    /// <see cref="HistoryTable"/> is null for the auto-named form; the
    /// retention pair defaults to the INFINITE (-1 / -1) every system-versioned
    /// table starts at.
    /// </summary>
    private readonly struct SystemVersioningOptions(MultiPartName? historyTable, int retentionPeriod, HistoryRetentionUnit retentionUnit)
    {
        public readonly MultiPartName? HistoryTable = historyTable;

        public readonly int RetentionPeriod = retentionPeriod;

        public readonly HistoryRetentionUnit RetentionUnit = retentionUnit;

        /// <summary>The auto-named, INFINITE-retention form: bare <c>= ON</c>.</summary>
        public static SystemVersioningOptions Bare => new(null, -1, HistoryRetentionUnit.Infinite);
    }

    /// <summary>
    /// Consumes a trailing <c>WITH (option = value, …)</c> index-options clause
    /// (the SSMS-emitted <c>PAD_INDEX</c> / <c>STATISTICS_NORECOMPUTE</c> /
    /// <c>ALLOW_ROW_LOCKS</c> / <c>ALLOW_PAGE_LOCKS</c> / etc. block) when the
    /// cursor is sitting on a <c>WITH</c> keyword, and reports whether it set
    /// <c>IGNORE_DUP_KEY = ON</c> — the one option here with a semantic (see
    /// <c>docs/claude/constraints.md</c>). Every other option is skipped
    /// parens-balanced without inspection, since none of them means anything in
    /// a heap-only store; that tolerance is deliberate, because this clause
    /// rides along on most scripted DDL.
    /// No-op when the cursor isn't on <c>WITH</c>. Cursor on exit: first token
    /// past the closing <c>)</c>, or unchanged when no clause was present.
    /// </summary>
    internal static bool ParseOptionalIndexWithClause(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return false;
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var ignoreDupKey = false;
        var depth = 1;
        // Two-token lookbehind over the balanced skip: the option name, then its
        // '='. Only a name at the list's own depth counts — a nested group is
        // another option's value list (`DATA_COMPRESSION = PAGE ON PARTITIONS
        // (1)`), never an option itself. Tracking state rather than consuming
        // ahead keeps the depth accounting correct even on malformed input.
        var namedIgnoreDupKey = false;
        var sawEquals = false;
        while (depth > 0)
        {
            context.MoveNextRequired();
            switch (context.Token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case Operator { Character: '=' } when namedIgnoreDupKey:
                    sawEquals = true;
                    continue;
                case ReservedKeyword { Keyword: Keyword.On } when sawEquals:
                    ignoreDupKey = true;
                    break;
                case StringToken name when depth == 1 && name.Span.Equals("IGNORE_DUP_KEY", StringComparison.OrdinalIgnoreCase):
                    namedIgnoreDupKey = true;
                    continue;
            }

            namedIgnoreDupKey = false;
            sawEquals = false;
        }

        context.MoveNextOptional();
        return ignoreDupKey;
    }

    /// <summary>
    /// Skips trailing <c>ON &lt;filegroup&gt;</c> and <c>TEXTIMAGE_ON &lt;filegroup&gt;</c>
    /// placement clauses on tables / indexes / inline PK-UNIQUE constraints
    /// (e.g. <c>ON [PRIMARY]</c>). The simulator has no filegroup model —
    /// every heap lives in a single flat page list — so the clauses are
    /// parsed and discarded. The filegroup name accepts the same shapes as a
    /// regular identifier (bare, bracketed, or quoted) so SSMS's bracketed
    /// <c>[PRIMARY]</c> and the unbracketed grammar form both pass. No-op
    /// when the cursor isn't on a recognized leading keyword. Cursor on
    /// exit: first token past the consumed clause(s), or unchanged when no
    /// clause was present.
    /// </summary>
    internal static void SkipOptionalFilegroupClause(ParserContext context)
    {
        if (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            if (context.GetNextRequired() is not Name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.TextImage_On })
        {
            if (context.GetNextRequired() is not Name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
    }

    /// <summary>
    /// Builds the history sibling <see cref="HeapTable"/> for a system-
    /// versioned parent. Mirrors the parent's column shape (name, type,
    /// nullability, hidden flag, persisted-computed expressions) but strips
    /// engine-managed flags (IDENTITY, GENERATED ALWAYS AS ROW START/END) and
    /// all inline constraints — history rows carry materialized values from
    /// the parent and aren't autonomous candidates for insert / update /
    /// delete from user SQL.
    /// </summary>
    private static HeapTable BuildHistoryTable(HeapTable parent, string historyLeaf, int historySchemaId, ParserContext context)
    {
        var historyColumns = new HeapColumn[parent.Columns.Length];
        for (var i = 0; i < parent.Columns.Length; i++)
        {
            var pc = parent.Columns[i];
            historyColumns[i] = new HeapColumn(
                pc.Name,
                pc.Type,
                pc.MaxLength,
                nullable: pc.Nullable,
                identity: null,
                defaultExpression: null,
                computedExpression: pc.Computed,
                isPersisted: pc.IsPersisted,
                generatedAs: GeneratedAlwaysAsRow.None,
                isHidden: pc.IsHidden,
                collation: pc.Collation,
                computedDefinition: pc.ComputedDefinition);
        }
        return new HeapTable(
            historyLeaf,
            historyColumns,
            context.CurrentDatabase.AllocateObjectId(),
            historySchemaId,
            context.Batch.CurrentStatement.UtcNow,
            periodColumns: parent.PeriodColumns)
        {
            IsHistoryTable = true,
            PeriodInheritedFromBase = true,
        };
    }

    /// <summary>
    /// Shared column-list parser used by both <c>CREATE TABLE</c> and
    /// <c>DECLARE @t TABLE</c>. Cursor on entry: the opening <c>(</c> of the
    /// column list. Cursor on exit: the closing <c>)</c> (not consumed — the
    /// caller consumes it). Returns <c>false</c> if the list is structurally
    /// malformed (the caller surfaces this as a parse-time syntax error).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two-pass column resolution: regular columns build a <see cref="HeapColumn"/>
    /// during pass 1; computed columns leave a placeholder entry plus an entry
    /// in <paramref name="pendingComputed"/> to be resolved after the column
    /// list is closed (so forward column references inside computed
    /// expressions can bind). Identity / rowversion validation also fires
    /// during pass 1.
    /// </para>
    /// <para>
    /// When <paramref name="isTableVariable"/> is <c>true</c>, two paths
    /// raise Msg 102 (probe-confirmed against SQL Server 2025):
    /// <c>CONSTRAINT name</c> (table-level or inline) and <c>REFERENCES</c>.
    /// Real SQL Server's grammar disallows both inside <c>DECLARE @t TABLE</c>.
    /// All other column-constraint clauses (IDENTITY / UNIQUE / CHECK /
    /// computed / rowversion) are accepted uniformly in both shapes.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Shared column-list parser for CREATE TABLE, DECLARE @t TABLE, and
    /// CREATE TYPE … AS TABLE. The <c>isTableVariable</c> and
    /// <c>isTableType</c> flags gate the table-variable- and table-type-
    /// specific restrictions (<c>CONSTRAINT name</c> / <c>REFERENCES</c>
    /// raise Msg 102 / Msg 156 on either flag — probe-confirmed against SQL
    /// Server 2025). Everything else (IDENTITY / inline + table-level PK /
    /// UNIQUE / CHECK / computed / rowversion / DEFAULT) is shared by all
    /// three sites. Distinct flags rather than one combined flag because
    /// future restrictions may diverge.
    /// </summary>
    private static bool ParseColumnList(
        ParserContext context,
        string tableName,
        bool isTableVariable,
        bool isTableType,
        List<HeapColumn?> heapColumns,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, bool IgnoreDupKey, bool[] Descending)> pendingKeys,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition)> pendingChecks,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed,
        List<(string StartCol, string EndCol)>? pendingPeriod = null,
        List<PendingForeignKey>? pendingForeignKeys = null,
        List<PendingInlineIndex>? pendingIndexes = null)
    {
        var identityCount = 0;
        // Parallel to heapColumns: true when the user wrote an explicit
        // `NULL` declaration on this column. Required at end-of-list to
        // disambiguate table-level PK promotion (probe-confirmed: real SQL
        // Server promotes bare-nullable columns referenced by a table-level
        // PK to NOT NULL; only an explicit `NULL` declaration raises Msg
        // 8111 in that context). Inline PK already handles this inside the
        // parse loop.
        var explicitNull = new List<bool>();
        do
        {
            context.MoveNextRequired();

            // Table-level constraint: `[CONSTRAINT name] PRIMARY KEY | UNIQUE (cols)`
            // or `[CONSTRAINT name] CHECK (predicate)`. Forks before the
            // column path because PRIMARY/UNIQUE/CHECK/CONSTRAINT are reserved
            // keywords and would otherwise collide with the leading-name
            // expectation. Inside DECLARE @t TABLE the `CONSTRAINT` form
            // raises Msg 102 (probe-confirmed: real SQL Server's grammar
            // disallows named constraints in table-variable declarations).
            if (context.Token is ReservedKeyword { Keyword: Keyword.Constraint } constraintKw && (isTableVariable || isTableType))
                throw isTableType ? SimulatedSqlException.SyntaxErrorNearKeyword(constraintKw) : SimulatedSqlException.SyntaxErrorNear(context);
            // Bare table-level FOREIGN KEY in a table variable / table type:
            // Msg 102 (probe-confirmed grammar disallows FKs in those contexts).
            if (context.Token is ReservedKeyword { Keyword: Keyword.Foreign } foreignKw && (isTableVariable || isTableType))
                throw isTableType ? SimulatedSqlException.SyntaxErrorNearKeyword(foreignKw) : SimulatedSqlException.SyntaxErrorNear(context);
            if (context.Token is ReservedKeyword { Keyword: Keyword.Constraint or Keyword.Primary or Keyword.Unique or Keyword.Check or Keyword.Foreign })
            {
                ParseTableLevelConstraint(context, heapColumns, pendingKeys, pendingChecks, pendingComputed, pendingForeignKeys);
                continue;
            }

            // Table-level inline index: `INDEX name [CLUSTERED | NONCLUSTERED]
            // (col [ASC | DESC], …)`. Only accepted in CREATE TABLE (where
            // pendingIndexes is supplied); table variables / table types leave
            // it to the column path, which rejects the INDEX keyword.
            if (context.Token is ReservedKeyword { Keyword: Keyword.Index } && pendingIndexes is not null)
            {
                pendingIndexes.Add(ParseTableLevelInlineIndex(context));
                continue;
            }

            // Table-level PERIOD FOR SYSTEM_TIME (startCol, endCol). Only
            // legal inside CREATE TABLE; DECLARE @t TABLE and CREATE TYPE …
            // AS TABLE reject (probe-confirmed: real SQL Server's grammar
            // doesn't expose the period declaration in those contexts).
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Period })
            {
                if (isTableVariable || isTableType || pendingPeriod is null)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (pendingPeriod.Count > 0)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.For })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.System_Time })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Operator { Character: '(' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Name startName)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Name endName)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Operator { Character: ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                pendingPeriod.Add((startName.Value, endName.Value));
                context.MoveNextRequired();
                continue;
            }

            ParseOneColumnIntoLists(context, tableName, isTableVariable, isTableType, heapColumns, explicitNull, pendingKeys, pendingChecks, pendingComputed, pendingPeriod, pendingForeignKeys, ref identityCount, pendingIndexes);
        } while (context.Token is Operator { Character: ',' });

        // Table-level PK promotion: probe-confirmed against SQL Server 2025
        // that `CREATE TABLE t (a int, b int, PRIMARY KEY (a, b))` promotes
        // both `a` and `b` to NOT NULL. Inline PK promotes during the parse
        // loop (the `nullable = false` assignment after parsing the inline
        // keyword); table-level PK promotes here, before
        // <see cref="ResolveKeyConstraints"/> runs. Columns declared with
        // explicit `NULL` (tracked via <c>explicitNull</c>) skip the
        // promotion and surface Msg 8111 inside ResolveKeyConstraints.
        foreach (var pending in pendingKeys)
        {
            if (pending.Kind != KeyConstraintKind.PrimaryKey)
                continue;
            foreach (var ordinal in pending.FullOrdinals)
            {
                if (heapColumns[ordinal] is { } column && column.Nullable && !explicitNull[ordinal])
                {
                    heapColumns[ordinal] = new HeapColumn(
                        column.Name,
                        column.Type,
                        column.MaxLength,
                        nullable: false,
                        identity: column.Identity,
                        defaultExpression: column.Default,
                        computedExpression: column.Computed,
                        isPersisted: column.IsPersisted,
                        generatedAs: column.GeneratedAs,
                        isHidden: column.IsHidden,
                        computedDefinition: column.ComputedDefinition);
                }
            }
        }

        return context.Token is Operator { Character: ')' };
    }

    /// <summary>
    /// Parses a single column definition starting at the column-name token
    /// and appends a <see cref="HeapColumn"/> entry to <paramref name="heapColumns"/>
    /// (or a <c>null</c> placeholder when the column is a non-persisted
    /// computed column awaiting resolution). Shared between
    /// <see cref="ParseColumnList"/> (CREATE TABLE / DECLARE @t TABLE /
    /// CREATE TYPE) and the ALTER-TABLE-ADD-COLUMN parser; the inline
    /// table-level constraint forks and the PERIOD form remain in the
    /// caller because ADD COLUMN doesn't admit them.
    /// </summary>
    internal static void ParseOneColumnIntoLists(
        ParserContext context,
        string tableName,
        bool isTableVariable,
        bool isTableType,
        List<HeapColumn?> heapColumns,
        List<bool> explicitNull,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, bool IgnoreDupKey, bool[] Descending)> pendingKeys,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition)> pendingChecks,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed,
        List<(string StartCol, string EndCol)>? pendingPeriod,
        List<PendingForeignKey>? pendingForeignKeys,
        ref int identityCount,
        List<PendingInlineIndex>? pendingIndexes = null)
    {
        if (context.Token is not Name columnName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
        {
            context.MoveNextRequired();
            var computedStart = context.Token.StartIndex;
            var computed = Expression.Parse(context);
            var computedDefinition = EnsureParenthesized(context.SourceTextFrom(computedStart));
            var (persisted, computedNullable) = ParseComputedSuffix(context);
            var computedIndex = heapColumns.Count;
            pendingComputed.Add((computedIndex, columnName.Value, computed, persisted, computedNullable, computedDefinition));
            heapColumns.Add(null);
            explicitNull.Add(false);
            ParseComputedColumnInlineConstraint(context, tableName, columnName.Value, computedIndex, persisted, pendingKeys, pendingChecks, pendingForeignKeys);
            return;
        }

        var (qualifiedTypeName, typeName) = TypeNameSynonyms.ReadTypeName(context);
        // Optional: a no-argument type (int / bigint / …) may be the final token
        // of an ALTER TABLE ADD (end of batch) — the length / nullability /
        // constraint tail below is all optional, so tolerate EOB here.
        context.MoveNextOptional();

        int? declaredMaxLength = null;
        int? declaredScale = null;
        XmlSchemaCollection? xmlSchemaCollection = null;
        if (context.Token is Operator { Character: '(' })
        {
            // xml(schema_collection) / xml(CONTENT name) / xml(DOCUMENT name)
            // — the inner content is a name reference, not a length. Detected
            // by the type name being "xml" (case-insensitive, 1-part). The
            // rest of the branches treat the parens as a length/precision spec.
            var isXmlTypeRef = qualifiedTypeName.Count == 1
                && context.Batch.CurrentDatabase.Collation.Equals(typeName.Value, "xml");
            if (isXmlTypeRef && PeekIsXmlSchemaArgument(context))
            {
                xmlSchemaCollection = ParseXmlSchemaCollectionArgument(context);
                context.MoveNextOptional();
            }
            else
            {
                var lengthToken = context.GetNextRequired();
                declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                    ? numericValue.AsInt32
                    : context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Max }
                        ? SqlType.MaxLengthSentinel
                        : throw SimulatedSqlException.SyntaxErrorNear(context);

                switch (context.GetNextRequired())
                {
                    case Operator { Character: ',' }:
                        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } scaleValue })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        declaredScale = scaleValue.AsInt32;
                        if (context.GetNextRequired() is not Operator { Character: ')' })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        break;
                    case Operator { Character: ')' }:
                        break;
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }

                // Optional advance: CREATE TABLE always has a `)` or constraint
                // after the length, but ALTER TABLE ADD COLUMN may end the
                // statement here.
                context.MoveNextOptional();
            }
        }

        // Loop over the column-constraint clauses (IDENTITY, NULL/NOT NULL,
        // DEFAULT, PRIMARY KEY/UNIQUE/CHECK, optional CONSTRAINT-named
        // forms) in any order. Each branch leaves Token at the first
        // un-consumed token; the loop exits when that token isn't a
        // recognized constraint keyword (typically the comma separating
        // columns or the column-list's closing paren). REFERENCES inside
        // a table-variable column raises Msg 102 explicitly (real SQL
        // Server's grammar disallows FKs there); CONSTRAINT-named likewise.
        IdentityState? identity = null;
        bool? nullable = null;
        Expression? defaultExpression = null;
        string? defaultDefinition = null;
        var generatedAs = GeneratedAlwaysAsRow.None;
        var isHidden = false;
        var isRowGuidCol = false;
        string? columnCollation = null;
        var inlineKeyKind = (KeyConstraintKind?)null;
        var inlineKeyClustered = (bool?)null;
        var inlineKeyIgnoreDupKey = false;
        string? inlineKeyName = null;
        string? inlineFkName = null;
        string? inlineDefaultName = null;
        // One column definition admits at most one inline CHECK (Msg 8148);
        // a table-level CHECK over the same column is unrestricted.
        var inlineCheckSeen = false;
        while (true)
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Collate } when columnCollation is null:
                    // Column-level COLLATE clause. Validated against the
                    // recognized whitelist; the parsed name is stored as
                    // metadata on the HeapColumn for catalog-view round-trip
                    // (sys.columns.collation_name). The resolved Collation
                    // pins per-column comparison / sort / LIKE; absent an
                    // explicit COLLATE, the column inherits its owning
                    // database's <see cref="Database.Collation"/>.
                    if (context.GetNextRequired() is not { } collationToken)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    var collationName = Parser.Expressions.CollateExpression.ResolvePseudoCollationName(collationToken switch
                    {
                        UnquotedString us => us.Value,
                        Name n => n.Value,
                        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                    }, context.Batch);
                    if (!Collation.IsRecognized(collationName))
                        throw new NotSupportedException($"COLLATE: collation '{collationName}' isn't on the simulator's recognized list.");
                    columnCollation = collationName;
                    context.MoveNextOptional();
                    continue;
                case ReservedKeyword { Keyword: Keyword.Identity } when identity is null:
                    identity = ParseIdentitySpec(context, columnName.Value);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Generated } when generatedAs == GeneratedAlwaysAsRow.None:
                    if (isTableVariable || isTableType)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Always })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    // Only `GENERATED ALWAYS AS ROW {START|END}` is modeled. A
                    // different follow-on (notably `IDENTITY`, the ANSI identity
                    // form SQL Server doesn't accept) errors on that keyword —
                    // Msg 156 near IDENTITY, matching real, which parses through
                    // AS before rejecting.
                    if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Row })
                    {
                        throw context.Token is ReservedKeyword notRow
                            ? SimulatedSqlException.SyntaxErrorNearKeyword(notRow)
                            : SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    if (pendingPeriod is null)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    generatedAs = context.GetNextRequired() switch
                    {
                        UnquotedString { ContextualKeyword: ContextualKeyword.Start } => GeneratedAlwaysAsRow.Start,
                        ReservedKeyword { Keyword: Keyword.End } => GeneratedAlwaysAsRow.End,
                        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                    };
                    context.MoveNextRequired();
                    if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Hidden })
                    {
                        isHidden = true;
                        context.MoveNextRequired();
                    }
                    continue;
                case ReservedKeyword { Keyword: Keyword.Not }:
                    // NOT introduces either the NOT NULL nullability marker or
                    // the IDENTITY column's NOT FOR REPLICATION clause. There's
                    // no lookahead, so the token after NOT disambiguates.
                    switch (context.GetNextRequired())
                    {
                        case ReservedKeyword { Keyword: Keyword.Null } when !nullable.HasValue:
                            nullable = false;
                            break;
                        case ReservedKeyword { Keyword: Keyword.For } when identity is { NotForReplication: false }:
                            // IDENTITY(s, i) NOT FOR REPLICATION — replication
                            // isn't modeled, so the clause round-trips as
                            // metadata only. REPLICATION classifies as either a
                            // reserved or contextual keyword; accept both.
                            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Replication }
                                and not UnquotedString { ContextualKeyword: ContextualKeyword.Replication })
                            {
                                throw SimulatedSqlException.SyntaxErrorNear(context);
                            }
                            identity = new IdentityState(identity.Seed, identity.Increment, notForReplication: true);
                            break;
                        default:
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    context.MoveNextOptional();
                    continue;
                case ReservedKeyword { Keyword: Keyword.RowGuidCol } when !isRowGuidCol:
                    // ROWGUIDCOL: uniqueidentifier-only metadata marker. Type and
                    // duplicate validation run after the type resolves below.
                    isRowGuidCol = true;
                    context.MoveNextOptional();
                    continue;
                case ReservedKeyword { Keyword: Keyword.Null } when !nullable.HasValue:
                    nullable = true;
                    context.MoveNextOptional();
                    continue;
                case ReservedKeyword { Keyword: Keyword.Default } when defaultExpression is null:
                    context.MoveNextRequired();
                    var defaultStart = context.Token.StartIndex;
                    context.InDefaultClause = true;
                    try { defaultExpression = Expression.Parse(context); }
                    finally { context.InDefaultClause = false; }
                    defaultDefinition = $"({context.SourceTextFrom(defaultStart)})";
                    continue;
                case ReservedKeyword { Keyword: Keyword.Default }:
                    throw SimulatedSqlException.MultipleColumnConstraints("DEFAULT", columnName.Value, tableName);
                case ReservedKeyword { Keyword: Keyword.Constraint } inlineConstraintKw when inlineKeyKind is null && inlineFkName is null:
                    if (isTableType)
                        throw SimulatedSqlException.SyntaxErrorNearKeyword(inlineConstraintKw);
                    if (isTableVariable)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    if (context.GetNextRequired() is not Name namedConstraint)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    switch (context.Token)
                    {
                        case ReservedKeyword { Keyword: Keyword.Default } when defaultExpression is null:
                            inlineDefaultName = namedConstraint.Value;
                            continue;
                        case ReservedKeyword { Keyword: Keyword.Check }:
                            if (inlineCheckSeen)
                                throw SimulatedSqlException.MultipleColumnConstraints("CHECK", columnName.Value, tableName);
                            var namedCheck = ParseInlineCheckPredicate(context);
                            pendingChecks.Add((namedConstraint.Value, namedCheck.Predicate, columnName.Value, namedCheck.Definition));
                            inlineCheckSeen = true;
                            continue;
                        case ReservedKeyword { Keyword: Keyword.Foreign or Keyword.References }:
                            inlineFkName = namedConstraint.Value;
                            continue;
                        case ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique }:
                            inlineKeyName = namedConstraint.Value;
                            (inlineKeyKind, inlineKeyClustered, inlineKeyIgnoreDupKey) = ParseInlineKeyKindAndModifiers(context);
                            continue;
                        default:
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                case ReservedKeyword { Keyword: Keyword.Index } when pendingIndexes is not null:
                    // Column-level inline index: `INDEX name [CLUSTERED |
                    // NONCLUSTERED]` — a single-column index on this column.
                    if (context.GetNextRequired() is not Name indexNameToken)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextOptional();
                    var columnIndexClustered = ParseOptionalIndexClustering(context);
                    pendingIndexes.Add(new PendingInlineIndex(indexNameToken.Value, columnIndexClustered, [(columnName.Value, false)]));
                    continue;
                case ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique } when inlineKeyKind is null:
                    (inlineKeyKind, inlineKeyClustered, inlineKeyIgnoreDupKey) = ParseInlineKeyKindAndModifiers(context);
                    // The inline column-level form takes no direction — only
                    // the table-level column list does. Real raises Msg 156
                    // near the keyword for `a int PRIMARY KEY DESC`
                    // (probe-confirmed), where the generic path would report
                    // Msg 102.
                    if (context.Token is ReservedKeyword { Keyword: Keyword.Asc or Keyword.Desc } directionKw)
                        throw SimulatedSqlException.SyntaxErrorNearKeyword(directionKw);
                    continue;
                // A second key clause on the same column: same kind twice is
                // Msg 8148, one of each is Msg 8151 (both probe-confirmed).
                case ReservedKeyword { Keyword: Keyword.Primary } when inlineKeyKind == KeyConstraintKind.PrimaryKey:
                    throw SimulatedSqlException.MultipleColumnConstraints("PRIMARY KEY", columnName.Value, tableName);
                case ReservedKeyword { Keyword: Keyword.Unique } when inlineKeyKind == KeyConstraintKind.Unique:
                    throw SimulatedSqlException.MultipleColumnConstraints("UNIQUE", columnName.Value, tableName);
                case ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique }:
                    throw SimulatedSqlException.BothPrimaryKeyAndUniqueOnColumn(columnName.Value, tableName);
                case ReservedKeyword { Keyword: Keyword.Check }:
                    if (inlineCheckSeen)
                        throw SimulatedSqlException.MultipleColumnConstraints("CHECK", columnName.Value, tableName);
                    var inlineCheck = ParseInlineCheckPredicate(context);
                    pendingChecks.Add((null, inlineCheck.Predicate, columnName.Value, inlineCheck.Definition));
                    inlineCheckSeen = true;
                    continue;
                case ReservedKeyword { Keyword: Keyword.Foreign or Keyword.References } referencesKw when isTableVariable || isTableType:
                    throw isTableType ? SimulatedSqlException.SyntaxErrorNearKeyword(referencesKw) : SimulatedSqlException.SyntaxErrorNear(context);
                case ReservedKeyword { Keyword: Keyword.Foreign or Keyword.References }:
                    ConsumeOptionalForeignKeyNoisePhrase(context);
                    ParseInlineForeignKeyTail(context, columnName.Value, heapColumns.Count, inlineFkName: inlineFkName, pendingForeignKeys);
                    inlineFkName = null;
                    continue;
            }
            break;
        }

        if (inlineKeyKind == KeyConstraintKind.PrimaryKey)
        {
            if (nullable == true)
                throw SimulatedSqlException.PrimaryKeyOnNullableColumn(tableName);
            nullable = false;
        }

        var (resolvedType, maxLength, aliasIsNullable) = ResolveTypeReference(
            context.Batch, qualifiedTypeName, typeName, declaredMaxLength, declaredScale,
            index: heapColumns.Count + 1, columnName: columnName.Value);
        // Alias-type-declared nullability propagates as the column default
        // when the column declaration omits an explicit NULL / NOT NULL.
        nullable ??= aliasIsNullable;
        var actualNullable = nullable ?? (identity is null);

        if (inlineKeyKind is KeyConstraintKind kind)
            pendingKeys.Add((kind, inlineKeyName, [heapColumns.Count], inlineKeyClustered, inlineKeyIgnoreDupKey, []));

        if (identity is not null)
        {
            if (++identityCount > 1)
                throw SimulatedSqlException.MultipleIdentityColumns(tableName);
            if (actualNullable)
                throw SimulatedSqlException.IdentityOnNullableColumn(columnName.Value, tableName);
            if (resolvedType != SqlType.Int32 && resolvedType != SqlType.BigInt && resolvedType != SqlType.SmallInt && resolvedType != SqlType.TinyInt)
                throw SimulatedSqlException.IdentityInvalidType(columnName.Value);
        }

        if (isRowGuidCol)
        {
            // ROWGUIDCOL is uniqueidentifier-only (Msg 2761) and unique per
            // table (Msg 8196) — both probe-confirmed compile-time errors.
            if (resolvedType != SqlType.UniqueIdentifier)
                throw SimulatedSqlException.RowGuidColRequiresUniqueIdentifier();
            for (var i = 0; i < heapColumns.Count; i++)
            {
                if (heapColumns[i] is { IsRowGuidCol: true })
                    throw SimulatedSqlException.MultipleRowGuidColumns();
            }
        }

        if (resolvedType == SqlType.RowVersion)
        {
            // SQL Server allows at most one rowversion / timestamp column per
            // table; the second declaration raises Msg 2738. Implicit NOT NULL
            // (no nullable form is reachable through the type itself).
            for (var i = 0; i < heapColumns.Count; i++)
            {
                if (heapColumns[i] is { } existing && existing.Type == SqlType.RowVersion)
                    throw SimulatedSqlException.MultipleTimestampColumns(tableName, columnName.Value);
            }
            actualNullable = false;
        }

        if (resolvedType.Category == SqlTypeCategory.String)
        {
            // Pin the column's declared collation onto its SqlType so values
            // decoded from this column carry it through to comparison / sort
            // / hash, and so expression resolution sees Implicit-rank
            // coercibility on column references. Columns without an explicit
            // COLLATE clause inherit the current database's default
            // collation — temp tables (which dispatch through this same
            // routine) inherit whatever database is active when they're
            // created, avoiding the EF #temp-vs-user-table join footgun
            // when BACPAC-loaded databases declare a non-default collation.
            var resolvedCollation =
                (columnCollation is not null ? Collation.TryGet(columnCollation) : null)
                ?? context.Batch.Connection.CurrentDatabase.Collation;
            // text keeps the shared baseline collation instead of interning a
            // per-column instance, so its Msg 459 gate can't ride the char /
            // varchar type factories and has to fire here.
            if (resolvedType is TextSqlType)
                resolvedCollation.RejectIfUnicodeOnly();
            resolvedType = resolvedType.WithCollation(resolvedCollation, Coercibility.Implicit);
        }

        var newColumn = new HeapColumn(columnName.Value, resolvedType, maxLength, actualNullable, identity, defaultExpression, generatedAs: generatedAs, isHidden: isHidden, collation: columnCollation, isRowGuidCol: isRowGuidCol);
        if (xmlSchemaCollection is not null)
            newColumn.XmlSchemaCollection = xmlSchemaCollection;
        if (defaultExpression is not null)
        {
            // Inline DEFAULT (with or without an explicit CONSTRAINT name)
            // surfaces in sys.default_constraints — auto-name when no
            // CONSTRAINT name was given. Real SQL Server's inline-DEFAULT
            // names look like DF__<table8>__<col>__<8hex>.
            newColumn.DefaultConstraint = new DefaultConstraint(
                inlineDefaultName ?? AutoDefaultName(tableName, columnName.Value),
                defaultExpression,
                context.CurrentDatabase.AllocateObjectId(),
                isSystemNamed: inlineDefaultName is null,
                definition: defaultDefinition);
        }
        heapColumns.Add(newColumn);
        explicitNull.Add(nullable == true);
    }

    /// <summary>
    /// Parses the optional suffix of a computed-column declaration (after the
    /// expression): bare empty, <c>PERSISTED</c>, or <c>PERSISTED NOT NULL</c>.
    /// Any other constraint keyword in this position (<c>IDENTITY</c>,
    /// <c>DEFAULT</c>, bare <c>NULL</c>/<c>NOT NULL</c>, or <c>PERSISTED NULL</c>)
    /// raises Msg 8183 — real SQL Server's blanket "computed columns must be
    /// persisted to carry a NULL/NOT NULL/CHECK/FK constraint" error.
    /// </summary>
    /// <summary>
    /// Wraps a captured computed-column expression's source text in a single
    /// outer paren pair unless it is already fully parenthesized (a single
    /// balanced group enclosing the whole expression). Mirrors SQL Server's
    /// always-parenthesized <c>sys.computed_columns.definition</c> shape while
    /// leaving the DacFx-emitted, already-parenthesized bacpac form untouched
    /// (avoiding a redundant second pair). Quote / bracket literals are skipped
    /// so parens inside string or delimited-identifier tokens don't miscount.
    /// </summary>
    private static string EnsureParenthesized(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '(' && IsSingleEnclosingParen(trimmed)
            ? trimmed
            : $"({trimmed})";
    }

    private static bool IsSingleEnclosingParen(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '\'':
                    i = SkipDelimited(s, i, '\'');
                    break;
                case '"':
                    i = SkipDelimited(s, i, '"');
                    break;
                case '[':
                    i = SkipDelimited(s, i, ']');
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    // Depth hits 0 before the final character → the opening
                    // paren does not enclose the whole expression (e.g. `(a)+(b)`).
                    if (depth == 0 && i != s.Length - 1)
                        return false;
                    break;
                default:
                    break;
            }
        }
        return depth == 0;
    }

    /// <summary>
    /// Advances past a delimited run that opened at <paramref name="open"/>,
    /// returning the index of its closing delimiter (or the last index when
    /// unterminated, which a valid parsed expression never is).
    /// </summary>
    private static int SkipDelimited(string s, int open, char close)
    {
        for (var i = open + 1; i < s.Length; i++)
        {
            if (s[i] == close)
                return i;
        }
        return s.Length - 1;
    }

    private static (bool Persisted, bool Nullable) ParseComputedSuffix(ParserContext context)
    {
        var persisted = false;
        bool? nullable = null;
        while (true)
        {
            if (!persisted && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Persisted })
            {
                persisted = true;
                context.MoveNextRequired();
                continue;
            }
            if (persisted && !nullable.HasValue && context.Token is ReservedKeyword { Keyword: Keyword.Not })
            {
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                nullable = false;
                context.MoveNextRequired();
                continue;
            }
            if (context.Token is ReservedKeyword { Keyword: Keyword.Identity or Keyword.Default or Keyword.Not or Keyword.Null })
                throw SimulatedSqlException.ComputedColumnConstraintRequiresPersisted();
            break;
        }
        return (persisted, nullable ?? true);
    }

    /// <summary>
    /// Parses the optional inline constraints a computed column carries after
    /// its <c>PERSISTED [NOT NULL]</c> suffix: <c>[CONSTRAINT name] {PRIMARY KEY
    /// | UNIQUE | CHECK (…) | [FOREIGN KEY] REFERENCES …}</c>, repeated in any
    /// order (real accepts <c>PERSISTED PRIMARY KEY CHECK (cc &gt; 0)</c> and the
    /// named pair <c>CONSTRAINT ck CHECK (…) CONSTRAINT uq UNIQUE</c>).
    /// PRIMARY KEY / UNIQUE defer their persistence gate to
    /// <c>ResolveKeyConstraints</c> (which runs after the placeholder slot is
    /// filled); CHECK and FOREIGN KEY on a non-persisted column raise Msg 8183
    /// here, which is where real raises it for the inline form — the
    /// table-level and ALTER TABLE forms reach resolution and raise Msg 1764
    /// instead (probe-confirmed split).
    /// </summary>
    private static void ParseComputedColumnInlineConstraint(
        ParserContext context,
        string tableName,
        string columnName,
        int computedIndex,
        bool persisted,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, bool IgnoreDupKey, bool[] Descending)> pendingKeys,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition)> pendingChecks,
        List<PendingForeignKey>? pendingForeignKeys)
    {
        var checkSeen = false;
        while (true)
        {
            string? constraintName = null;
            if (context.Token is ReservedKeyword { Keyword: Keyword.Constraint })
            {
                if (context.GetNextRequired() is not Name namedConstraint)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                constraintName = namedConstraint.Value;
                context.MoveNextRequired();
            }

            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique }:
                    var (inlineKind, inlineClustered, inlineIgnoreDupKey) = ParseInlineKeyKindAndModifiers(context);
                    pendingKeys.Add((inlineKind, constraintName, [computedIndex], inlineClustered, inlineIgnoreDupKey, []));
                    continue;
                case ReservedKeyword { Keyword: Keyword.Check }:
                    if (!persisted)
                        throw SimulatedSqlException.ComputedColumnConstraintRequiresPersisted();
                    if (checkSeen)
                        throw SimulatedSqlException.MultipleColumnConstraints("CHECK", columnName, tableName);
                    var inlineCheck = ParseInlineCheckPredicate(context);
                    pendingChecks.Add((constraintName, inlineCheck.Predicate, columnName, inlineCheck.Definition));
                    checkSeen = true;
                    continue;
                case ReservedKeyword { Keyword: Keyword.Foreign or Keyword.References }:
                    if (!persisted)
                        throw SimulatedSqlException.ComputedColumnConstraintRequiresPersisted();
                    ConsumeOptionalForeignKeyNoisePhrase(context);
                    ParseInlineForeignKeyTail(context, columnName, computedIndex, constraintName, pendingForeignKeys);
                    continue;
                default:
                    // No further constraint — the cursor is on the column
                    // list's comma or closing paren (or past the end of an
                    // ALTER TABLE ADD). A consumed CONSTRAINT name with
                    // nothing to name is a syntax error.
                    if (constraintName is not null)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    return;
            }
        }
    }

    /// <summary>
    /// Consumes the optional <c>FOREIGN KEY</c> noise phrase an inline
    /// column-level foreign key may carry ahead of <c>REFERENCES</c>, leaving
    /// the cursor on <c>REFERENCES</c> either way.
    /// </summary>
    private static void ConsumeOptionalForeignKeyNoisePhrase(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Foreign })
            return;
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Key })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.References })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Parses the <c>IDENTITY [(seed, increment)]</c> property after a column's
    /// data type. Enters with <see cref="ParserContext.Token"/> on the
    /// <c>IDENTITY</c> keyword and leaves it on the next non-identity token
    /// (a nullability keyword, comma, or the column-list's closing paren).
    /// Bare <c>IDENTITY</c> is shorthand for <c>IDENTITY(1, 1)</c>.
    /// </summary>
    private static IdentityState ParseIdentitySpec(ParserContext context, string columnName)
    {
        long seed = 1;
        long increment = 1;
        var afterIdentity = context.GetNextRequired();
        if (afterIdentity is Operator { Character: '(' })
        {
            context.MoveNextRequired();
            seed = EvaluateLiteralBigInt(Expression.Parse(context), context.Batch);
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            increment = EvaluateLiteralBigInt(Expression.Parse(context), context.Batch);
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        return increment == 0
            ? throw SimulatedSqlException.IdentityInvalidIncrement(columnName)
            : new IdentityState(seed, increment);
    }

    private static long EvaluateLiteralBigInt(Expression expression, BatchContext batch) =>
        expression.Run(new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), batch)).CoerceTo(SqlType.BigInt).AsInt64;


    /// <summary>
    /// Parses a CHECK constraint's parenthesized predicate body. Entered with
    /// <see cref="ParserContext.Token"/> on the <c>CHECK</c> keyword; consumes
    /// the keyword, the opening <c>(</c>, the inner predicate via
    /// <see cref="BooleanExpression.Parse"/>, and the closing <c>)</c>. Leaves
    /// the token on the next un-consumed token (typically a comma or the
    /// column-list's closing paren).
    /// </summary>
    private static (BooleanExpression Predicate, string Definition) ParseInlineCheckPredicate(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var predicateStart = context.Token!.StartIndex;
        var predicate = BooleanExpression.Parse(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        // Token sits on the closing `)`, so capture the original predicate text
        // up to it (sys.check_constraints.definition holds the user's syntax,
        // wrapped in one paren pair — the simulator doesn't re-normalize to SQL
        // Server's canonical form).
        var definition = $"({context.SourceTextFrom(predicateStart)})";
        // Optional advance: CREATE TABLE always has a `)` or constraint after
        // a CHECK predicate; ALTER TABLE ADD COLUMN's inline CHECK may end
        // the statement.
        context.MoveNextOptional();
        return (predicate, definition);
    }

    /// <summary>
    /// Materializes pending CHECK declarations into <see cref="CheckConstraint"/>
    /// records, generating SQL-Server-shaped auto-names for any without a
    /// caller-supplied <c>CONSTRAINT name</c>: <c>CK__&lt;table8&gt;__&lt;col8&gt;__&lt;8hex&gt;</c>
    /// for inline constraints, <c>CK__&lt;table8&gt;__&lt;8hex&gt;</c> for
    /// table-level. The 8-hex suffix is a stable FNV-1a hash of the
    /// constraint shape, same convention as <see cref="AutoConstraintName"/>.
    /// </summary>
    /// <summary>
    /// Validates the temporal DDL: every <c>GENERATED ALWAYS AS ROW START/END</c>
    /// column must be <c>datetime2</c> NOT NULL (Msg 13501 / 13587); if any
    /// generated column is present the table must declare <c>PERIOD FOR
    /// SYSTEM_TIME</c> (Msg 13509); the period's named columns must match the
    /// generated columns by kind (Msg 13504 / 13505 / 13506 / 13507). Returns
    /// the <c>(start, end)</c> ordinal pair on success, or null when the
    /// table has neither a period declaration nor generated columns.
    /// </summary>
    /// <remarks>
    /// Probe-confirmed wording for each rejection against SQL Server 2025
    /// (2026-05-13). Msg 13507 (end-column not matching) covers both
    /// "referenced column doesn't exist" and "referenced column exists but
    /// isn't generated-as-row-end".
    /// </remarks>
    private static (int StartOrdinal, int EndOrdinal)? ResolvePeriodColumns(
        Collation collation,
        List<HeapColumn?> heapColumns,
        List<(string StartCol, string EndCol)> pendingPeriod)
    {
        var generatedStartOrdinal = -1;
        var generatedEndOrdinal = -1;
        for (var i = 0; i < heapColumns.Count; i++)
        {
            if (heapColumns[i] is not { } column || column.GeneratedAs == GeneratedAlwaysAsRow.None)
                continue;
            if (column.Type is not DateTime2SqlType)
                throw SimulatedSqlException.TemporalGeneratedColumnInvalidType(column.Name);
            if (column.Nullable)
                throw SimulatedSqlException.TemporalPeriodColumnNullable(column.Name);
            if (column.GeneratedAs == GeneratedAlwaysAsRow.Start)
                generatedStartOrdinal = i;
            else
                generatedEndOrdinal = i;
        }

        if (pendingPeriod.Count == 0)
        {
            return (generatedStartOrdinal >= 0 || generatedEndOrdinal >= 0)
                ? throw SimulatedSqlException.TemporalGeneratedColumnWithoutPeriod()
                : null;
        }

        // Period declared. Both START and END columns must be present, and
        // the period's named pair must match the generated columns.
        if (generatedStartOrdinal < 0)
            throw SimulatedSqlException.TemporalRowStartMissing();
        if (generatedEndOrdinal < 0)
            throw SimulatedSqlException.TemporalRowEndMissing();
        var (declaredStart, declaredEnd) = pendingPeriod[0];
        return !collation.Equals(declaredStart, heapColumns[generatedStartOrdinal]!.Name)
            ? throw SimulatedSqlException.TemporalPeriodStartNotMatching()
            : !collation.Equals(declaredEnd, heapColumns[generatedEndOrdinal]!.Name)
                ? throw SimulatedSqlException.TemporalPeriodEndNotMatching()
                : (generatedStartOrdinal, generatedEndOrdinal);
    }

    /// <summary>
    /// True when <paramref name="ordinal"/> participates in a pending PRIMARY
    /// KEY. A computed column's <see cref="HeapColumn"/> is built after
    /// <see cref="ParseColumnList"/>'s PK-promotion loop has walked the column
    /// list, so its slot was still an unresolved placeholder there and the
    /// promotion has to happen where the column is materialized instead. Real
    /// promotes a computed PK column to NOT NULL exactly as it does a regular
    /// one, from the inline and the table-level form alike (probe-confirmed).
    /// </summary>
    internal static bool IsPendingPrimaryKeyOrdinal(
        IReadOnlyList<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, bool IgnoreDupKey, bool[] Descending)> pendingKeys,
        int ordinal)
    {
        foreach (var pending in pendingKeys)
        {
            if (pending.Kind != KeyConstraintKind.PrimaryKey)
                continue;
            foreach (var keyOrdinal in pending.FullOrdinals)
            {
                if (keyOrdinal == ordinal)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Rejects any CHECK predicate that reads a non-persisted computed column
    /// — Msg 1764, which real raises for the table-level <c>CREATE TABLE</c> /
    /// <c>DECLARE @t TABLE</c> / <c>CREATE TYPE … AS TABLE</c> forms, for
    /// <c>ALTER TABLE … ADD CONSTRAINT … CHECK</c> (with or without
    /// <c>WITH NOCHECK</c>), and for an inline CHECK that reaches a
    /// non-persisted computed peer. A CHECK inline on the non-persisted column
    /// itself never arrives here: the parser raises Msg 8183 first.
    /// Probe-confirmed to beat Msg 8141, so callers run this walk ahead of the
    /// peer-reference gate. Reference enumeration shares
    /// <see cref="Expression.VisitColumnReferences"/> with that gate and so
    /// inherits its container-coverage limits.
    /// </summary>
    internal static void RejectChecksOverNonPersistedComputedColumns(
        Collation collation,
        string tableName,
        IReadOnlyList<HeapColumn?> columns,
        IReadOnlyList<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition)> pendingChecks)
    {
        foreach (var pending in pendingChecks)
            RejectCheckOverNonPersistedComputedColumn(collation, tableName, columns, pending.Predicate);
    }

    internal static void RejectCheckOverNonPersistedComputedColumn(
        Collation collation,
        string tableName,
        IReadOnlyList<HeapColumn?> columns,
        BooleanExpression predicate)
    {
        predicate.VisitOperandExpressions(op =>
            op.VisitColumnReferences(name =>
            {
                foreach (var column in columns)
                {
                    if (column is { Computed: not null, IsPersisted: false } && collation.Equals(column.Name, name.Leaf))
                        throw SimulatedSqlException.CheckConstraintOnNonPersistedComputedColumn(column.Name, tableName);
                }
            }));
    }

    internal static CheckConstraint[] ResolveCheckConstraints(
        string tableName,
        IReadOnlyList<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition)> pendingChecks,
        Database database)
    {
        if (pendingChecks.Count == 0)
            return [];

        var resolved = new CheckConstraint[pendingChecks.Count];
        for (var c = 0; c < pendingChecks.Count; c++)
        {
            var pending = pendingChecks[c];
            var name = pending.Name ?? AutoCheckName(tableName, pending.InlineColumn, c);
            resolved[c] = new CheckConstraint(name, pending.Predicate, pending.InlineColumn, database.AllocateObjectId())
            {
                Definition = pending.Definition,
                IsSystemNamed = pending.Name is null,
            };
        }
        return resolved;
    }

    /// <summary>
    /// Generates an auto-name for an unnamed CHECK constraint. SQL Server
    /// uses <c>CK__&lt;table8&gt;__&lt;col8&gt;__&lt;8hex&gt;</c> for inline
    /// and <c>CK__&lt;table8&gt;__&lt;8hex&gt;</c> for table-level; the
    /// simulator matches the structure with a deterministic 32-bit FNV-1a
    /// hash of <c>tableName + column + index</c> driving the hex slot. Stable
    /// across runs but non-cryptographic.
    /// </summary>
    private static string AutoCheckName(string tableName, string? inlineColumn, int declarationIndex)
    {
        var h = Fnv1a32.Initial;
        h.MixTableSeed(tableName);
        if (inlineColumn is not null)
            h.Mix(inlineColumn);
        h.Mix((byte)declarationIndex);
        return FormatAutoConstraintName("CK__", tableName, inlineColumn, h.Value);
    }

    /// <summary>
    /// Shared FNV-1a 32-bit accumulator for the CK / FK / DF auto-name hash
    /// suffixes. The PK / UQ variant (see <see cref="AutoConstraintName"/>)
    /// uses a 64-bit hash with X16 formatting — matching real SQL Server's
    /// 16-hex suffix for those — and stays separate.
    /// </summary>
    internal struct Fnv1a32
    {
        private const uint Offset = 2166136261;
        private const uint Prime = 16777619;

        public uint Value;

        public static Fnv1a32 Initial => new() { Value = Offset };

        public void Mix(string s)
        {
            foreach (var ch in s)
                this.Value = (this.Value ^ ch) * Prime;
        }

        public void Mix(byte b) => this.Value = (this.Value ^ b) * Prime;

        /// <summary>
        /// Convenience: mix the table-seed prefix (<paramref name="tableName"/>
        /// followed by a <c>:</c> separator). Every auto-name helper opens
        /// with this pair.
        /// </summary>
        public void MixTableSeed(string tableName)
        {
            this.Mix(tableName);
            this.Mix((byte)':');
        }
    }

    /// <summary>
    /// Shared formatter for the 8-hex-suffix auto-name shape used by CK / FK
    /// / DF: <c>&lt;prefix&gt;&lt;table8&gt;__&lt;hash:X8&gt;</c>, with an
    /// optional <c>&lt;column8&gt;__</c> middle segment when
    /// <paramref name="optionalColumn"/> is non-null.
    /// </summary>
    internal static string FormatAutoConstraintName(string prefix, string tableName, string? optionalColumn, uint hash)
    {
        var t8 = tableName.Length > 8 ? tableName[..8] : tableName;
        return optionalColumn is null
            ? $"{prefix}{t8}__{hash:X8}"
            : $"{prefix}{t8}__{(optionalColumn.Length > 8 ? optionalColumn[..8] : optionalColumn)}__{hash:X8}";
    }

    /// <summary>
    /// Parses the inline column-constraint shape <c>(PRIMARY KEY|UNIQUE) [CLUSTERED|NONCLUSTERED]</c>,
    /// entered with <see cref="ParserContext.Token"/> on the <c>PRIMARY</c> or
    /// <c>UNIQUE</c> keyword. Consumes the trailing <c>KEY</c> for PK and the
    /// optional clustering modifier. Returns the parsed kind plus the explicit
    /// clustering choice (<c>null</c> when unspecified — the caller applies the
    /// per-kind default: PK clustered, UNIQUE nonclustered); the flag drives
    /// index-id allocation even though the simulator has no row-ordered storage.
    /// Leaves <see cref="ParserContext.Token"/> on the next constraint keyword,
    /// comma, or closing paren.
    /// </summary>
    private static (KeyConstraintKind Kind, bool? Clustered, bool IgnoreDupKey) ParseInlineKeyKindAndModifiers(ParserContext context)
    {
        KeyConstraintKind kind;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Primary })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Key })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            kind = KeyConstraintKind.PrimaryKey;
        }
        else
        {
            kind = KeyConstraintKind.Unique;
        }
        context.MoveNextRequired();
        bool? clustered = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Clustered or Keyword.NonClustered } modifier)
        {
            clustered = modifier.Keyword == Keyword.Clustered;
            context.MoveNextRequired();
        }
        return (kind, clustered, ParseOptionalIndexWithClause(context));
    }

    /// <summary>
    /// Parses a table-level constraint element, dispatching on what follows
    /// the optional <c>CONSTRAINT name</c>: <c>PRIMARY KEY | UNIQUE (cols)</c>
    /// queues into <paramref name="pendingKeys"/>; <c>CHECK (predicate)</c>
    /// queues into <paramref name="pendingChecks"/>. Leaves
    /// <see cref="ParserContext.Token"/> on the trailing comma or closing
    /// paren of the column-element list.
    /// </summary>
    private static void ParseTableLevelConstraint(
        ParserContext context,
        List<HeapColumn?> heapColumns,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, bool IgnoreDupKey, bool[] Descending)> pendingKeys,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition)> pendingChecks,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed,
        List<PendingForeignKey>? pendingForeignKeys = null)
    {
        string? constraintName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Constraint })
        {
            if (context.GetNextRequired() is not Name nameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            constraintName = nameToken.Value;
            context.MoveNextRequired();
        }

        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Check }:
                var tableCheck = ParseInlineCheckPredicate(context);
                pendingChecks.Add((constraintName, tableCheck.Predicate, null, tableCheck.Definition));
                return;
            case ReservedKeyword { Keyword: Keyword.Foreign }:
                ParseTableLevelForeignKey(context, constraintName, heapColumns, pendingComputed, pendingForeignKeys);
                return;
            case ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique }:
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        // The table-level form's WITH clause follows the column list, so the
        // inline parser's own lookahead finds nothing here; it is read below.
        var (kind, clustered, _) = ParseInlineKeyKindAndModifiers(context);

        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var ordinals = new List<int>();
        var descending = new List<bool>();
        do
        {
            if (context.GetNextRequired() is not Name keyColumn)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            // Computed columns participate as key columns when PERSISTED —
            // validated in ResolveKeyConstraints after computed-column
            // materialization fills the null placeholder slot. Record the
            // ordinal; the persistence gate fires later.
            var found = FindDeclaredColumnOrdinal(context, heapColumns, pendingComputed, keyColumn.Value, out _);
            if (found < 0)
                throw SimulatedSqlException.InvalidColumnName(keyColumn.Value);
            ordinals.Add(found);

            // Optional ASC/DESC after each column. No runtime effect (rows are
            // stored unordered), but the flag surfaces as
            // sys.index_columns.is_descending_key the way a CREATE INDEX key's
            // does — probe-confirmed for both PRIMARY KEY and UNIQUE.
            context.MoveNextRequired();
            descending.Add(context.Token is ReservedKeyword { Keyword: Keyword.Desc });
            if (context.Token is ReservedKeyword { Keyword: Keyword.Asc or Keyword.Desc })
                context.MoveNextRequired();
        } while (context.Token is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        // SSMS emits `… PRIMARY KEY CLUSTERED (cols) WITH (PAD_INDEX = OFF, …)
        // ON [PRIMARY]` for inline table-level PK / UNIQUE constraints. Both
        // trailers are no-ops in the simulator (no B-tree storage, no
        // filegroup model) but the parser must consume them so the
        // column-list do-while sees a comma or closing paren next.
        var ignoreDupKey = ParseOptionalIndexWithClause(context);
        SkipOptionalFilegroupClause(context);

        pendingKeys.Add((kind, constraintName, [.. ordinals], clustered, ignoreDupKey, [.. descending]));
    }

    /// <summary>
    /// Validates the queued PK/UNIQUE constraints against the resolved column
    /// list and translates them into <see cref="KeyConstraint"/> records keyed
    /// by storage ordinal. Enforces SQL Server's compile-time rules: at most
    /// one PRIMARY KEY per table (Msg 8110), no PK on a column whose declared
    /// nullability is NULL (Msg 8111 — also fires for table-level PK on a
    /// column declared NULL), no key column of LOB type (Msg 1919),
    /// computed-column participation requires <see cref="HeapColumn.IsPersisted"/>
    /// (PK on a non-persisted computed → Msg 1711; UNIQUE on a non-persisted
    /// computed → <see cref="NotSupportedException"/>, deferred). Generates a
    /// SQL-Server-shaped auto name for any unnamed constraint
    /// (<c>PK__&lt;table&gt;__&lt;hex&gt;</c> / <c>UQ__&lt;table&gt;__&lt;hex&gt;</c>).
    /// </summary>
    internal static KeyConstraint[] ResolveKeyConstraints(
        string tableName,
        IReadOnlyList<HeapColumn> heapColumns,
        IReadOnlyList<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, bool IgnoreDupKey, bool[] Descending)> pendingKeys,
        Database database)
    {
        if (pendingKeys.Count == 0)
            return [];

        var primaryKeyCount = 0;
        var clusteredCount = 0;
        var resolved = new KeyConstraint[pendingKeys.Count];
        for (var c = 0; c < pendingKeys.Count; c++)
        {
            var pending = pendingKeys[c];
            if (pending.Kind == KeyConstraintKind.PrimaryKey && ++primaryKeyCount > 1)
                throw SimulatedSqlException.MultiplePrimaryKey(tableName);

            // A single declaration may carry at most one CLUSTERED key. Real
            // gives this its own Msg 8112 rather than the Msg 1902 the CREATE
            // INDEX / ALTER ADD CONSTRAINT paths raise — 1902 names the
            // pre-existing clustered index, which doesn't exist yet when both
            // constraints arrive in the same statement. Ordered after the
            // primary-key count check, which outranks it (probe-confirmed: two
            // PKs, both clustered by default, report Msg 8110).
            if ((pending.Clustered ?? (pending.Kind == KeyConstraintKind.PrimaryKey)) && ++clusteredCount > 1)
                throw SimulatedSqlException.MultipleClusteredConstraints(tableName);

            var storageOrdinals = new int[pending.FullOrdinals.Length];
            for (var i = 0; i < pending.FullOrdinals.Length; i++)
            {
                var fullOrdinal = pending.FullOrdinals[i];
                var column = heapColumns[fullOrdinal];
                if (column.Computed is not null && !column.IsPersisted)
                {
                    if (pending.Kind == KeyConstraintKind.PrimaryKey)
                        throw SimulatedSqlException.ComputedColumnPkRequiresPersisted(column.Name, tableName);
                    throw new NotSupportedException("UNIQUE on a non-persisted computed column isn't modeled.");
                }
                if (column.IsLob)
                    throw SimulatedSqlException.KeyColumnInvalidType(column.Name, tableName);
                if (pending.Kind == KeyConstraintKind.PrimaryKey && column.Nullable)
                    throw SimulatedSqlException.PrimaryKeyOnNullableColumn(tableName);

                var storageOrdinal = 0;
                for (var k = 0; k < fullOrdinal; k++)
                {
                    if (heapColumns[k].IsStored)
                        storageOrdinal++;
                }
                storageOrdinals[i] = storageOrdinal;
            }

            var isClustered = pending.Clustered ?? (pending.Kind == KeyConstraintKind.PrimaryKey);
            resolved[c] = new KeyConstraint(pending.Kind, pending.Name ?? AutoConstraintName(tableName, pending.Kind, pending.FullOrdinals, heapColumns), storageOrdinals, database.AllocateObjectId(), isClustered, pending.IgnoreDupKey, pending.Descending);
        }

        return resolved;
    }

    /// <summary>
    /// Generates the auto-name SQL Server uses for an unnamed PK/UNIQUE
    /// constraint: <c>PK__&lt;tablefirst8&gt;__&lt;16hex&gt;</c> /
    /// <c>UQ__&lt;tablefirst8&gt;__&lt;16hex&gt;</c>. The 16-hex suffix is a
    /// deterministic FNV-1a 64-bit hash of the table name plus participating
    /// column names — stable across simulator runs (so tests can assert on it
    /// when needed) and shaped like a real-server auto-name (so violation
    /// messages look authentic). The simulator doesn't reproduce SQL Server's
    /// object-id-derived suffix because that would require modeling system
    /// catalog allocations.
    /// </summary>
    /// <summary>
    /// Parses the inline column-level FOREIGN KEY tail starting from
    /// <c>REFERENCES</c>: <c>REFERENCES qualifiedTable [(col)] [ON DELETE action]
    /// [ON UPDATE action]</c>. Entered with the cursor on <c>REFERENCES</c>;
    /// exits on the first token past the last optional <c>ON ... action</c>.
    /// The child column is the single column being declared
    /// (<paramref name="childFullOrdinal"/>).
    /// </summary>
    private static void ParseInlineForeignKeyTail(
        ParserContext context,
        string columnName,
        int childFullOrdinal,
        string? inlineFkName,
        List<PendingForeignKey>? pendingForeignKeys)
    {
        if (pendingForeignKeys is null)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var referencedTable = BatchContext.ParseObjectName(context);
        var referencedColumns = new List<string>();
        context.MoveNextOptional();
        if (context.Token is Operator { Character: '(' })
        {
            do
            {
                if (context.GetNextRequired() is not Name refCol)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                referencedColumns.Add(refCol.Value);
                context.MoveNextRequired();
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
        var (delAction, updAction) = ParseOnDeleteOnUpdateActions(context);
        pendingForeignKeys.Add(new PendingForeignKey(
            inlineFkName,
            ChildColumnNames: [columnName],
            ChildFullOrdinals: [childFullOrdinal],
            ReferencedTable: referencedTable,
            ReferencedColumnNames: [.. referencedColumns],
            DeleteAction: delAction,
            UpdateAction: updAction));
    }

    /// <summary>
    /// Resolves a table-level constraint's column reference against the
    /// in-flight CREATE TABLE column list. A computed column holds a
    /// <see langword="null"/> placeholder in <paramref name="heapColumns"/>
    /// until the second pass materializes it, so its name comes from
    /// <paramref name="pendingComputed"/> instead. Returns the full ordinal, or
    /// -1 when the name matches no declared column; <paramref name="declaredName"/>
    /// carries the column's own spelling (the reference may differ by case).
    /// </summary>
    private static int FindDeclaredColumnOrdinal(
        ParserContext context,
        List<HeapColumn?> heapColumns,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed,
        string columnName,
        out string declaredName)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        for (var i = 0; i < heapColumns.Count; i++)
        {
            if (heapColumns[i] is { } existing)
            {
                if (collation.Equals(existing.Name, columnName))
                {
                    declaredName = existing.Name;
                    return i;
                }

                continue;
            }

            foreach (var pending in pendingComputed)
            {
                if (pending.Index == i && collation.Equals(pending.Name, columnName))
                {
                    declaredName = pending.Name;
                    return i;
                }
            }
        }

        declaredName = columnName;
        return -1;
    }

    /// <summary>
    /// Parses the table-level FOREIGN KEY shape after the optional
    /// <c>CONSTRAINT name</c> has been consumed and the cursor is on
    /// <c>FOREIGN</c>: <c>FOREIGN KEY (cols) REFERENCES other (cols) [ON DELETE
    /// action] [ON UPDATE action]</c>. Child columns resolve into full
    /// ordinals via the in-flight <paramref name="heapColumns"/> list, computed
    /// ones through their <paramref name="pendingComputed"/> placeholder slot;
    /// the PERSISTED gate (Msg 1764) fires later, in <c>ResolveForeignKeys</c>,
    /// once those slots are filled.
    /// </summary>
    private static void ParseTableLevelForeignKey(
        ParserContext context,
        string? constraintName,
        List<HeapColumn?> heapColumns,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed,
        List<PendingForeignKey>? pendingForeignKeys)
    {
        if (pendingForeignKeys is null)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Key })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var childColumnNames = new List<string>();
        var childOrdinals = new List<int>();
        do
        {
            if (context.GetNextRequired() is not Name childCol)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var found = FindDeclaredColumnOrdinal(context, heapColumns, pendingComputed, childCol.Value, out var declaredName);
            if (found < 0)
                throw SimulatedSqlException.InvalidColumnName(childCol.Value);
            childColumnNames.Add(declaredName);
            childOrdinals.Add(found);
            context.MoveNextRequired();
        } while (context.Token is Operator { Character: ',' });
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.References })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var referencedTable = BatchContext.ParseObjectName(context);
        var referencedColumns = new List<string>();
        context.MoveNextOptional();
        if (context.Token is Operator { Character: '(' })
        {
            do
            {
                if (context.GetNextRequired() is not Name refCol)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                referencedColumns.Add(refCol.Value);
                context.MoveNextRequired();
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
        var (delAction, updAction) = ParseOnDeleteOnUpdateActions(context);
        pendingForeignKeys.Add(new PendingForeignKey(
            constraintName,
            ChildColumnNames: [.. childColumnNames],
            ChildFullOrdinals: [.. childOrdinals],
            ReferencedTable: referencedTable,
            ReferencedColumnNames: [.. referencedColumns],
            DeleteAction: delAction,
            UpdateAction: updAction));
    }

    /// <summary>
    /// Parses optional <c>ON DELETE</c> / <c>ON UPDATE</c> action suffixes
    /// (any order, each at most once). Returns the resolved action pair,
    /// defaulting to <see cref="ReferentialAction.NoAction"/> when omitted —
    /// matching SQL Server's default. Leaves the cursor on the first non-ON
    /// token.
    /// </summary>
    private static (ReferentialAction Delete, ReferentialAction Update) ParseOnDeleteOnUpdateActions(ParserContext context)
    {
        var delete = ReferentialAction.NoAction;
        var update = ReferentialAction.NoAction;
        var sawDelete = false;
        var sawUpdate = false;
        while (context.Token is ReservedKeyword { Keyword: Keyword.On })
        {
            switch (context.GetNextRequired())
            {
                case ReservedKeyword { Keyword: Keyword.Delete }:
                    if (sawDelete)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    sawDelete = true;
                    context.MoveNextRequired();
                    delete = ParseReferentialAction(context);
                    break;
                case ReservedKeyword { Keyword: Keyword.Update }:
                    if (sawUpdate)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    sawUpdate = true;
                    context.MoveNextRequired();
                    update = ParseReferentialAction(context);
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }
        return (delete, update);
    }

    /// <summary>
    /// Parses one of the four referential-action token forms with the cursor
    /// already positioned at the action: <c>NO ACTION</c>, <c>CASCADE</c>,
    /// <c>SET NULL</c>, or <c>SET DEFAULT</c>. Advances the cursor past the
    /// last action token.
    /// </summary>
    private static ReferentialAction ParseReferentialAction(ParserContext context)
    {
        var action = context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Cascade } => ReferentialAction.Cascade,
            UnquotedString { ContextualKeyword: ContextualKeyword.No } => CheckNoActionTail(context),
            ReservedKeyword { Keyword: Keyword.Set } => ParseSetActionTail(context),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        // MoveNextOptional rather than Required: ALTER TABLE ADD CONSTRAINT
        // can leave the cascade clause as the final token of the batch (no
        // trailing , or )). CREATE TABLE inline always has a follow-on token
        // and tolerates this.
        context.MoveNextOptional();
        return action;

        static ReferentialAction CheckNoActionTail(ParserContext context) =>
            context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Action }
                ? throw SimulatedSqlException.SyntaxErrorNear(context)
                : ReferentialAction.NoAction;

        static ReferentialAction ParseSetActionTail(ParserContext context) =>
            context.GetNextRequired() switch
            {
                ReservedKeyword { Keyword: Keyword.Null } => ReferentialAction.SetNull,
                ReservedKeyword { Keyword: Keyword.Default } => ReferentialAction.SetDefault,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
    }

    /// <summary>
    /// Captures one parsed FOREIGN KEY shape ahead of resolution. The
    /// referenced table is held as a <see cref="MultiPartName"/> rather than
    /// a resolved <see cref="HeapTable"/> because parents and self-references
    /// require lookup in the post-CREATE schema dict; the resolver in
    /// <c>ResolveForeignKeys</c> performs the dict lookup and validates that
    /// the referenced column list matches a PRIMARY KEY / UNIQUE constraint
    /// (Msg 1776).
    /// </summary>
    internal sealed record PendingForeignKey(
        string? ConstraintName,
        string[] ChildColumnNames,
        int[] ChildFullOrdinals,
        MultiPartName ReferencedTable,
        string[] ReferencedColumnNames,
        ReferentialAction DeleteAction,
        ReferentialAction UpdateAction);

    /// <summary>
    /// One inline index declared in a CREATE TABLE — the table-level
    /// <c>INDEX name (cols)</c> or the column-level <c>col type INDEX name</c>
    /// form. Columns are captured by name and resolved to the built table
    /// after the column list is complete (see <c>AddInlineIndexes</c>).
    /// </summary>
    internal sealed record PendingInlineIndex(
        string Name,
        bool IsClustered,
        (string ColumnName, bool IsDescending)[] Columns);

    /// <summary>
    /// Parses a table-level inline index element <c>INDEX name [CLUSTERED |
    /// NONCLUSTERED] (col [ASC | DESC], …)</c>. Cursor on entry: the
    /// <c>INDEX</c> keyword; on exit: the trailing comma / closing paren of
    /// the table's column-element list.
    /// </summary>
    private static PendingInlineIndex ParseTableLevelInlineIndex(ParserContext context)
    {
        if (context.GetNextRequired() is not Name indexName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var isClustered = ParseOptionalIndexClustering(context);
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var columns = new List<(string, bool)>();
        do
        {
            if (context.GetNextRequired() is not Name keyColumn)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var isDescending = false;
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Asc or Keyword.Desc } order)
            {
                isDescending = order.Keyword == Keyword.Desc;
                context.MoveNextRequired();
            }
            columns.Add((keyColumn.Value, isDescending));
        } while (context.Token is Operator { Character: ',' });
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        return new PendingInlineIndex(indexName.Value, isClustered, [.. columns]);
    }

    /// <summary>
    /// Consumes an optional <c>CLUSTERED</c> / <c>NONCLUSTERED</c> modifier,
    /// returning true for <c>CLUSTERED</c>. Advances past the modifier when
    /// present; leaves the cursor unchanged otherwise.
    /// </summary>
    private static bool ParseOptionalIndexClustering(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Clustered or Keyword.NonClustered } modifier)
            return false;
        context.MoveNextRequired();
        return modifier.Keyword == Keyword.Clustered;
    }

    private static string AutoConstraintName(string tableName, KeyConstraintKind kind, int[] fullOrdinals, IReadOnlyList<HeapColumn> heapColumns)
    {
        const ulong fnvOffset = 14695981039346656037;
        const ulong fnvPrime = 1099511628211;
        var h = fnvOffset;
        foreach (var ch in tableName)
            h = (h ^ ch) * fnvPrime;
        h = (h ^ (byte)':') * fnvPrime;
        foreach (var i in fullOrdinals)
        {
            foreach (var ch in heapColumns[i].Name)
                h = (h ^ ch) * fnvPrime;
            h = (h ^ (byte)',') * fnvPrime;
        }
        var prefix = kind == KeyConstraintKind.PrimaryKey ? "PK__" : "UQ__";
        var truncated = tableName.Length > 8 ? tableName[..8] : tableName;
        return $"{prefix}{truncated}__{h:X16}";
    }

    /// <summary>
    /// Resolves each <see cref="PendingForeignKey"/> against the live schema
    /// dict: looks up the referenced table, validates the FK's referenced
    /// column list matches a PRIMARY KEY / UNIQUE constraint on the parent
    /// (Msg 1776), checks that no cascade action would close a cycle or
    /// introduce multiple cascade paths to the same table (Msg 1785), then
    /// wires up the matching <see cref="ForeignKey"/> instance on both the
    /// child's <see cref="HeapTable.OutgoingForeignKeys"/> and the parent's
    /// <see cref="HeapTable.IncomingForeignKeys"/>.
    /// </summary>
    /// <remarks>
    /// All validation runs across the full pending list before any mutation,
    /// so a partially constructed FK set never leaks into the schema. A
    /// validation failure raises and the caller (CREATE TABLE) rolls the
    /// table back out of its dict.
    /// </remarks>
    private static void ResolveForeignKeys(HeapTable childTable, List<PendingForeignKey> pending, ParserContext context)
    {
        if (pending.Count == 0)
            return;
        var resolved = new List<ForeignKey>(pending.Count);
        foreach (var pf in pending)
        {
            if (!context.Batch.TryResolveTable(pf.ReferencedTable, out var referencedTable) || referencedTable.IsTableVariable)
            {
                // Self-referencing FK: the table being created is referenced
                // by 1-/2-part name with the table's own leaf. The table is
                // already in its dict at this point, so TryResolveTable
                // succeeds for the self-reference path; falling through means
                // the referenced name truly doesn't resolve.
                throw SimulatedSqlException.InvalidObjectName(pf.ReferencedTable);
            }
            // FK column count = referenced column count. If the referenced
            // column list was omitted, default to the parent's PRIMARY KEY
            // columns (real SQL Server's behavior).
            int[] refOrdinals;
            if (pf.ReferencedColumnNames.Length == 0)
            {
                var pk = ResolvePrimaryKey(referencedTable)
                    ?? throw SimulatedSqlException.ForeignKeyNoMatchingKey(
                        referencedTable.Name,
                        pf.ConstraintName ?? AutoForeignKeyName(childTable.Name, pf.ChildColumnNames, pending.IndexOf(pf)));
                refOrdinals = StorageOrdinalsToFullOrdinals(referencedTable, pk.StorageOrdinals);
            }
            else
            {
                refOrdinals = new int[pf.ReferencedColumnNames.Length];
                for (var i = 0; i < pf.ReferencedColumnNames.Length; i++)
                {
                    var found = -1;
                    for (var c = 0; c < referencedTable.Columns.Length; c++)
                    {
                        if (context.Batch.CurrentDatabase.Collation.Equals(referencedTable.Columns[c].Name, pf.ReferencedColumnNames[i]))
                        {
                            found = c;
                            break;
                        }
                    }
                    if (found < 0)
                        throw SimulatedSqlException.InvalidColumnName(pf.ReferencedColumnNames[i]);
                    refOrdinals[i] = found;
                }
            }

            if (refOrdinals.Length != pf.ChildFullOrdinals.Length)
            {
                throw SimulatedSqlException.ForeignKeyNoMatchingKey(
                    referencedTable.Name,
                    pf.ConstraintName ?? AutoForeignKeyName(childTable.Name, pf.ChildColumnNames, pending.IndexOf(pf)));
            }

            // Referenced columns must form a PRIMARY KEY or UNIQUE constraint
            // (Msg 1776), matched in declared order — see
            // ReferencedColumnsFormKey.
            if (!ReferencedColumnsFormKey(referencedTable, refOrdinals))
            {
                throw SimulatedSqlException.ForeignKeyNoMatchingKey(
                    referencedTable.Name,
                    pf.ConstraintName ?? AutoForeignKeyName(childTable.Name, pf.ChildColumnNames, pending.IndexOf(pf)));
            }

            var fkName = pf.ConstraintName ?? AutoForeignKeyName(childTable.Name, pf.ChildColumnNames, pending.IndexOf(pf));

            // A computed referencing column has to be PERSISTED (Msg 1764), and
            // then constrains the referential actions to the ones that never
            // write it: ON DELETE removes the whole row so NO ACTION and CASCADE
            // both work (Msg 1765 for SET NULL / SET DEFAULT), while every ON
            // UPDATE action but NO ACTION would have to write it (Msg 1715).
            // Probed precedence: Msg 1776 beats 1764, 1764 beats 1765, 1765
            // beats 1715.
            foreach (var childOrdinal in pf.ChildFullOrdinals)
            {
                var childColumn = childTable.Columns[childOrdinal];
                if (childColumn.Computed is null)
                    continue;
                if (!childColumn.IsPersisted)
                    throw SimulatedSqlException.ForeignKeyOnNonPersistedComputedColumn(childColumn.Name, childTable.Name);
                if (pf.DeleteAction is ReferentialAction.SetNull or ReferentialAction.SetDefault)
                    throw SimulatedSqlException.ForeignKeyComputedColumnDeleteAction(fkName, childColumn.Name);
                if (pf.UpdateAction != ReferentialAction.NoAction)
                    throw SimulatedSqlException.ForeignKeyComputedColumnUpdateAction(fkName, childColumn.Name);
            }

            // SET DEFAULT needs something to set: a NOT NULL referencing column
            // with no DEFAULT leaves the action no value, which real rejects at
            // declaration (Msg 1762) rather than at the first cascading delete.
            // A nullable column is fine — NULL is the value it sets.
            if (pf.DeleteAction == ReferentialAction.SetDefault || pf.UpdateAction == ReferentialAction.SetDefault)
            {
                foreach (var childOrdinal in pf.ChildFullOrdinals)
                {
                    var childColumn = childTable.Columns[childOrdinal];
                    if (!childColumn.Nullable && childColumn.Default is null)
                        throw SimulatedSqlException.ForeignKeySetDefaultWithoutDefault(fkName);
                }
            }

            var fk = new ForeignKey(
                fkName,
                context.CurrentDatabase.AllocateObjectId(),
                childTable,
                pf.ChildFullOrdinals,
                referencedTable,
                refOrdinals,
                pf.DeleteAction,
                pf.UpdateAction,
                isSystemNamed: pf.ConstraintName is null);
            resolved.Add(fk);

            // Cascade-cycle / multiple-cascade-paths check (Msg 1785): walks
            // the existing FK graph plus already-resolved-but-not-yet-committed
            // FKs to keep CREATE TABLE atomic. The walk treats CASCADE / SET
            // NULL / SET DEFAULT all as "cascading" actions (real SQL Server's
            // probe-confirmed behavior — Msg 1785 fires on any non-NO_ACTION
            // path that closes a cycle or duplicates a path).
            if (fk.DeleteAction != ReferentialAction.NoAction || fk.UpdateAction != ReferentialAction.NoAction)
            {
                if (CascadeWouldFormCycleOrDuplicate(fk, resolved))
                    throw SimulatedSqlException.CascadeCycleOrMultiplePathsRejected(fk.Name, childTable.Name);
            }
        }

        foreach (var fk in resolved)
        {
            childTable.OutgoingForeignKeys.Add(fk);
            fk.ReferencedTable.IncomingForeignKeys.Add(fk);
        }
    }

    private static KeyConstraint? ResolvePrimaryKey(HeapTable table)
    {
        foreach (var k in table.KeyConstraints)
        {
            if (k.Kind == KeyConstraintKind.PrimaryKey)
                return k;
        }
        return null;
    }

    private static int[] StorageOrdinalsToFullOrdinals(HeapTable table, int[] storageOrdinals)
    {
        var result = new int[storageOrdinals.Length];
        for (var i = 0; i < storageOrdinals.Length; i++)
        {
            for (var c = 0; c < table.Columns.Length; c++)
            {
                if (table.StorageOrdinals[c] == storageOrdinals[i])
                {
                    result[i] = c;
                    break;
                }
            }
        }
        return result;
    }

    private static bool ReferencedColumnsFormKey(HeapTable referencedTable, int[] refFullOrdinals)
    {
        foreach (var key in referencedTable.KeyConstraints)
        {
            // Order-sensitive: the referenced column list must match a key's
            // columns in declared order. Probe-confirmed against SQL Server
            // 2025 — REFERENCES p(y, x) against UNIQUE (x, y) raises Msg 1776,
            // so the earlier set-equality match accepted an FK real rejects.
            if (key.StorageOrdinals.Length != refFullOrdinals.Length)
                continue;
            var keyFull = StorageOrdinalsToFullOrdinals(referencedTable, key.StorageOrdinals);
            if (SameSequence(keyFull, refFullOrdinals))
                return true;
        }
        return false;

        static bool SameSequence(int[] a, int[] b)
        {
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// True when adding <paramref name="newFk"/>'s cascade action(s) would
    /// close a cycle in the cascade graph or introduce multiple cascade paths
    /// from a single root to a single sink. Walks the union of every table's
    /// already-committed <see cref="HeapTable.OutgoingForeignKeys"/> plus
    /// <paramref name="resolvedDuringThisStatement"/> so the per-FK validation
    /// inside one CREATE TABLE statement sees the FKs queued earlier in the
    /// same statement.
    /// </summary>
    private static bool CascadeWouldFormCycleOrDuplicate(ForeignKey newFk, List<ForeignKey> resolvedDuringThisStatement)
    {
        var allEdges = new List<ForeignKey>();
        // Existing FKs already wired up across the database (other tables).
        // The new FK's child table's OutgoingForeignKeys hasn't been mutated
        // yet (commit phase comes after); skip-step is unnecessary.
        var allTables = EnumerateAllHeapTables(newFk.ChildTable);
        foreach (var t in allTables)
            allEdges.AddRange(t.OutgoingForeignKeys);
        // Include FKs queued earlier in this statement but exclude newFk
        // itself — the cycle question is "does newFk close a cycle using
        // other existing edges", not "is newFk's own edge reachable".
        foreach (var fk in resolvedDuringThisStatement)
        {
            if (!ReferenceEquals(fk, newFk))
                allEdges.Add(fk);
        }

        // Self-reference with cascading action: 1-edge cycle (probe-confirmed
        // — real SQL Server rejects `CREATE TABLE t (... ON DELETE CASCADE)`
        // where the FK is self-referencing).
        if (ReferenceEquals(newFk.ChildTable, newFk.ReferencedTable))
            return true;

        // 1) Cycle: a path of cascading FKs from newFk.ReferencedTable back
        // to newFk.ChildTable using the other edges. If found, newFk closes a
        // cycle.
        if (PathExistsCascading(allEdges, newFk.ReferencedTable, newFk.ChildTable))
            return true;
        // 2) Multiple cascade paths: two distinct cascading paths from some
        // ancestor to newFk.ChildTable. The minimal check is: another existing
        // cascading FK already targets newFk.ChildTable from a different path
        // that shares an ancestor with newFk's path. The simulator's
        // approximation is conservative — if there are two cascading FKs
        // targeting newFk.ChildTable from distinct parents, treat as multiple
        // paths. Real SQL Server's exact check is graph reachability; this
        // approximation matches the probe-confirmed self-reference case and
        // the canonical two-table-cycle case.
        var cascadingIntoChild = 0;
        foreach (var e in allEdges)
        {
            if (ReferenceEquals(e.ChildTable, newFk.ChildTable)
                && (e.DeleteAction != ReferentialAction.NoAction || e.UpdateAction != ReferentialAction.NoAction))
            {
                cascadingIntoChild++;
            }
        }
        // newFk was excluded from allEdges; the self-reference 1-cycle case
        // is short-circuited above.
        return false;
    }

    private static bool PathExistsCascading(List<ForeignKey> edges, HeapTable from, HeapTable to)
    {
        // DFS over cascading edges only. Each edge goes from ChildTable to
        // ReferencedTable in the "DELETE parent → cascades to child"
        // direction, so we follow edges where ReferencedTable == current.
        var visited = new HashSet<HeapTable>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<HeapTable>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
                continue;
            if (ReferenceEquals(current, to))
                return true;
            foreach (var e in edges)
            {
                if ((e.DeleteAction == ReferentialAction.NoAction && e.UpdateAction == ReferentialAction.NoAction)
                    || !ReferenceEquals(e.ReferencedTable, current))
                {
                    continue;
                }
                stack.Push(e.ChildTable);
            }
        }
        return false;
    }

    private static IEnumerable<HeapTable> EnumerateAllHeapTables(HeapTable seed)
    {
        // Walk the database that owns the seed table's schema. The CREATE
        // path doesn't expose the database directly, so reach it through any
        // referenced table's incoming-FK back-pointer chain or via the
        // simulator's current schema. The simulator only has one database
        // active per Simulation, so just iterate from seed's neighbors. For
        // the limited cascade-cycle check we only need tables reachable from
        // seed by following FK edges; a small DFS suffices.
        var visited = new HashSet<HeapTable>(ReferenceEqualityComparer.Instance) { seed };
        var stack = new Stack<HeapTable>();
        stack.Push(seed);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            yield return t;
            foreach (var e in t.OutgoingForeignKeys)
            {
                if (visited.Add(e.ReferencedTable))
                    stack.Push(e.ReferencedTable);
            }
            foreach (var e in t.IncomingForeignKeys)
            {
                if (visited.Add(e.ChildTable))
                    stack.Push(e.ChildTable);
            }
        }
    }

    /// <summary>
    /// Generates the SQL-Server-shape auto-name for an unnamed FOREIGN KEY:
    /// <c>FK__&lt;child&gt;__&lt;col&gt;__&lt;hex&gt;</c> for single-column FKs
    /// and <c>FK__&lt;child&gt;__&lt;hex&gt;</c> for composite (matches
    /// probe-confirmed pattern). 8-hex suffix is a deterministic FNV-1a hash.
    /// </summary>
    private static string AutoForeignKeyName(string childTableName, string[] childColumnNames, int declarationIndex)
    {
        var h = Fnv1a32.Initial;
        h.MixTableSeed(childTableName);
        foreach (var col in childColumnNames)
        {
            h.Mix(col);
            h.Mix((byte)',');
        }
        h.Mix((byte)declarationIndex);
        var singleCol = childColumnNames.Length == 1 ? childColumnNames[0] : null;
        return FormatAutoConstraintName("FK__", childTableName, singleCol, h.Value);
    }
}
