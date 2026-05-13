using System.Collections.Concurrent;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
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
            case ReservedKeyword { Keyword: Keyword.Schema }:
                return TryParseCreateSchema(context);
            case ReservedKeyword { Keyword: Keyword.Function }:
                return TryParseCreateFunction(context);
            case ReservedKeyword { Keyword: Keyword.View }:
                return TryParseCreateView(context);
            case ReservedKeyword { Keyword: Keyword.Procedure or Keyword.Proc }:
                return Simulation.TryParseCreateProcedure(context, isAlter: false, createOrAlter: false);
            case ReservedKeyword { Keyword: Keyword.Trigger }:
                return Simulation.TryParseCreateTrigger(context, isAlter: false, createOrAlter: false);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Type }:
                return TryParseCreateType(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Sequence }:
                return TryParseCreateSequence(context);
            case ReservedKeyword { Keyword: Keyword.Or }:
                // CREATE OR ALTER {PROCEDURE|TRIGGER} — modern upsert syntax.
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Alter })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                return context.GetNextRequired() switch
                {
                    ReservedKeyword { Keyword: Keyword.Procedure or Keyword.Proc } => Simulation.TryParseCreateProcedure(context, isAlter: false, createOrAlter: true),
                    ReservedKeyword { Keyword: Keyword.Trigger } => Simulation.TryParseCreateTrigger(context, isAlter: false, createOrAlter: true),
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
            case ReservedKeyword { Keyword: Keyword.Table }:
                break;
            default:
                return false;
        }

        context.MoveNextRequired();
        if (context.Token is not Name)
            return false;
        var tableName = BatchContext.ParseObjectName(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            return false;

        var heapColumns = new List<HeapColumn?>();
        var pendingComputed = new List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable)>();
        var pendingKeys = new List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals)>();
        var pendingChecks = new List<(string? Name, BooleanExpression Predicate, string? InlineColumn)>();
        var pendingPeriod = new List<(string StartCol, string EndCol)>();
        if (!ParseColumnList(context, tableName.Leaf, isTableVariable: false, isTableType: false, heapColumns, pendingKeys, pendingChecks, pendingComputed, pendingPeriod))
            return false;

        // Optional trailing WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = X))
        // clause. Parsed regardless of skip mode so the cursor advances past
        // it cleanly; the resulting historyTableName is only used after the
        // skip-mode gate below.
        context.MoveNextOptional();
        MultiPartName? historyTableName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            historyTableName = ParseSystemVersioningOption(context);

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
                if (heapColumns[i] is { } existing && Collation.Default.Equals(existing.Name, reference.Leaf))
                {
                    return existing.Computed is not null
                        ? throw SimulatedSqlException.ComputedColumnReferencedInComputed(existing.Name, tableName.Leaf)
                        : existing.Type;
                }
                if (heapColumns[i] is null)
                {
                    foreach (var pending in pendingComputed)
                    {
                        if (pending.Index == i && Collation.Default.Equals(pending.Name, reference.Leaf))
                            throw SimulatedSqlException.ComputedColumnReferencedInComputed(pending.Name, tableName.Leaf);
                    }
                }
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }

        foreach (var pending in pendingComputed)
        {
            var resolvedType = pending.Expression.GetSqlType(ResolveComputedReference);
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
            heapColumns[pending.Index] = new HeapColumn(
                pending.Name,
                resolvedType,
                maxLength: computedMaxLength,
                nullable: pending.Nullable,
                computedExpression: pending.Expression,
                isPersisted: pending.Persisted);
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
                    if (!Collation.Default.Equals(name.Leaf, owningColumn))
                        throw SimulatedSqlException.InlineCheckReferencesAnotherColumn(owningColumn, tableName.Leaf);
                }));
        }

        // #foo lives in the per-connection TempTables dict so it's isolated
        // from regular user tables and auto-drops at connection close.
        // ##foo (global temps) aren't modeled yet — surface explicitly rather
        // than letting it silently land as a regular table.
        if (tableName.Leaf.Length >= 2 && tableName.Leaf[0] == '#' && tableName.Leaf[1] == '#')
            throw new NotSupportedException($"Global temp tables (##{tableName.Leaf[2..]}) aren't modeled. Use a local temp table (#{tableName.Leaf[2..]}) or a permanent table.");
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
        var isTempTable = BatchContext.IsLocalTempName(tableName.Leaf);
        Schema? schema = null;
        var schemaId = Database.DboSchemaId;
        ConcurrentDictionary<string, HeapTable> destination;
        if (isTempTable)
        {
            destination = context.Batch.Connection.TempTables;
        }
        else
        {
            if (!context.Batch.TryResolveSchema(tableName, out schema))
                throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(tableName.Count >= 2 ? tableName.ImmediateQualifier! : Database.DefaultSchemaName);
            // sys and INFORMATION_SCHEMA exist in Database.Schemas to carry
            // their conventional schema_ids and host catalog views — they
            // aren't writable namespaces. Real SQL Server rejects user
            // CREATE TABLE in either via a permission error; the simulator
            // surfaces NotSupportedException with the schema name so the
            // diagnostic is clear.
            if (schema.SchemaId is Database.SysSchemaId or Database.InformationSchemaId)
                throw new NotSupportedException($"Cannot CREATE TABLE in the built-in '{schema.Name}' schema. Use 'dbo' or a user-created schema.");
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
        var resolvedPeriod = ResolvePeriodColumns(heapColumns!, pendingPeriod);

        // History-table pre-validation when SYSTEM_VERSIONING = ON: must have
        // PeriodColumns on the parent, and the history table name's
        // destination dict must accept it (schema lookup + collision check
        // upfront so a history failure doesn't leave an orphan parent).
        Schema? historySchema = null;
        ConcurrentDictionary<string, HeapTable>? historyDestination = null;
        if (historyTableName is { } hn)
        {
            if (resolvedPeriod is null)
                throw new NotSupportedException("SYSTEM_VERSIONING = ON requires a PERIOD FOR SYSTEM_TIME declaration with matching GENERATED ALWAYS AS ROW START / END columns.");
            if (!context.Batch.TryResolveSchema(hn, out historySchema))
                throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(hn.Count >= 2 ? hn.ImmediateQualifier! : Database.DefaultSchemaName);
            if (historySchema.HasNameInSharedNamespace(hn.Leaf))
                throw SimulatedSqlException.ThereIsAlreadyAnObject(hn.Leaf);
            historyDestination = historySchema.HeapTables;
        }

        var heapTable = new HeapTable(
            tableName.Leaf,
            [.. heapColumns!],
            context.CurrentDatabase.AllocateObjectId(),
            schemaId,
            context.Batch.CurrentStatement.UtcNow,
            keyConstraints,
            checkConstraints,
            periodColumns: resolvedPeriod);
        if (!destination.TryAdd(heapTable.Name, heapTable))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(heapTable.Name);

        if (historyTableName is { } historyName && historyDestination is not null && historySchema is not null)
        {
            var historyTable = BuildHistoryTable(heapTable, historyName.Leaf, historySchema.SchemaId, context);
            if (!historyDestination.TryAdd(historyTable.Name, historyTable))
            {
                // Roll back parent insertion if history-add raced — shouldn't
                // happen given the pre-validation above, but keep both
                // commits consistent if it does.
                _ = destination.TryRemove(heapTable.Name, out _);
                throw SimulatedSqlException.ThereIsAlreadyAnObject(historyTable.Name);
            }
            heapTable.SystemVersioning = historyTable;
        }

        // Temp-table DDL participates in transaction rollback: probe-confirmed
        // that a CREATE TABLE #foo inside BEGIN TRAN is undone by ROLLBACK on
        // real SQL Server. Regular CREATE TABLE isn't logged — a known
        // asymmetry documented as a quirk.
        if (isTempTable && context.Connection.CurrentTransaction is { } tx)
            tx.UndoLog.RecordTempTableCreation(context.Batch.Connection.TempTables, heapTable.Name);
        return true;
    }

    /// <summary>
    /// Parses the trailing <c>WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = X))</c>
    /// option after a CREATE TABLE column list. Cursor on entry: the <c>WITH</c>
    /// keyword. Cursor on exit: the option's closing <c>)</c>. The history
    /// table name is required (auto-generated history naming isn't modeled).
    /// </summary>
    private static MultiPartName ParseSystemVersioningOption(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.System_Versioning })
            throw new NotSupportedException("Only SYSTEM_VERSIONING is supported in the CREATE TABLE WITH clause.");
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw new NotSupportedException("SYSTEM_VERSIONING must be set to ON in CREATE TABLE.");
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw new NotSupportedException("SYSTEM_VERSIONING = ON without an explicit (HISTORY_TABLE = …) clause isn't modeled — auto-generated history-table naming is deferred.");
        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.History_Table })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var historyName = BatchContext.ParseObjectName(context);
        ExpectCloseParen(context);
        ExpectCloseParen(context);
        return historyName;
    }

    private static void ExpectCloseParen(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
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
                isHidden: pc.IsHidden);
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
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals)> pendingKeys,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn)> pendingChecks,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable)> pendingComputed,
        List<(string StartCol, string EndCol)>? pendingPeriod = null)
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
            if (context.Token is ReservedKeyword { Keyword: Keyword.Constraint or Keyword.Primary or Keyword.Unique or Keyword.Check })
            {
                ParseTableLevelConstraint(context, heapColumns, pendingKeys, pendingChecks, pendingComputed);
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

            if (context.Token is not Name columnName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();

            if (context.Token is ReservedKeyword { Keyword: Keyword.As })
            {
                context.MoveNextRequired();
                var computed = Expression.Parse(context);
                var (persisted, computedNullable) = ParseComputedSuffix(context);
                pendingComputed.Add((heapColumns.Count, columnName.Value, computed, persisted, computedNullable));
                heapColumns.Add(null);
                explicitNull.Add(false);
                continue;
            }

            if (context.Token is not Name typeName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();

            int? declaredMaxLength = null;
            int? declaredScale = null;
            if (context.Token is Operator { Character: '(' })
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

                context.MoveNextRequired();
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
            var generatedAs = GeneratedAlwaysAsRow.None;
            var isHidden = false;
            var inlineKeyKind = (KeyConstraintKind?)null;
            string? inlineKeyName = null;
            while (true)
            {
                switch (context.Token)
                {
                    case ReservedKeyword { Keyword: Keyword.Identity } when identity is null:
                        identity = ParseIdentitySpec(context, columnName.Value);
                        continue;
                    case UnquotedString { ContextualKeyword: ContextualKeyword.Generated } when generatedAs == GeneratedAlwaysAsRow.None:
                        if (isTableVariable || isTableType || pendingPeriod is null)
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Always })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Row })
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
                    case ReservedKeyword { Keyword: Keyword.Not } when !nullable.HasValue:
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        nullable = false;
                        context.MoveNextRequired();
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Null } when !nullable.HasValue:
                        nullable = true;
                        context.MoveNextRequired();
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Default } when defaultExpression is null:
                        context.MoveNextRequired();
                        context.InDefaultClause = true;
                        try { defaultExpression = Expression.Parse(context); }
                        finally { context.InDefaultClause = false; }
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Constraint } inlineConstraintKw when inlineKeyKind is null:
                        if (isTableType)
                            throw SimulatedSqlException.SyntaxErrorNearKeyword(inlineConstraintKw);
                        if (isTableVariable)
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        if (context.GetNextRequired() is not Name namedConstraint)
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        context.MoveNextRequired();
                        if (context.Token is ReservedKeyword { Keyword: Keyword.Check })
                        {
                            pendingChecks.Add((namedConstraint.Value, ParseInlineCheckPredicate(context), columnName.Value));
                            continue;
                        }
                        inlineKeyName = namedConstraint.Value;
                        if (context.Token is not ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        inlineKeyKind = ParseInlineKeyKindAndModifiers(context);
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique } when inlineKeyKind is null:
                        inlineKeyKind = ParseInlineKeyKindAndModifiers(context);
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Check }:
                        pendingChecks.Add((null, ParseInlineCheckPredicate(context), columnName.Value));
                        continue;
                    case ReservedKeyword { Keyword: Keyword.References } referencesKw when isTableVariable || isTableType:
                        throw isTableType ? SimulatedSqlException.SyntaxErrorNearKeyword(referencesKw) : SimulatedSqlException.SyntaxErrorNear(context);
                }
                break;
            }

            if (inlineKeyKind == KeyConstraintKind.PrimaryKey)
            {
                if (nullable == true)
                    throw SimulatedSqlException.PrimaryKeyOnNullableColumn(tableName);
                nullable = false;
            }

            var actualNullable = nullable ?? (identity is null);
            var (resolvedType, maxLength) = SqlType.GetByName(typeName, declaredMaxLength, declaredScale, heapColumns.Count + 1, columnName.Value);

            if (inlineKeyKind is KeyConstraintKind kind)
                pendingKeys.Add((kind, inlineKeyName, [heapColumns.Count]));

            if (identity is not null)
            {
                if (++identityCount > 1)
                    throw SimulatedSqlException.MultipleIdentityColumns(tableName);
                if (actualNullable)
                    throw SimulatedSqlException.IdentityOnNullableColumn(columnName.Value, tableName);
                if (resolvedType != SqlType.Int32 && resolvedType != SqlType.BigInt && resolvedType != SqlType.SmallInt && resolvedType != SqlType.TinyInt)
                    throw SimulatedSqlException.IdentityInvalidType(columnName.Value);
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

            heapColumns.Add(new HeapColumn(columnName.Value, resolvedType, maxLength, actualNullable, identity, defaultExpression, generatedAs: generatedAs, isHidden: isHidden));
            explicitNull.Add(nullable == true);
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
                        isHidden: column.IsHidden);
                }
            }
        }

        return context.Token is Operator { Character: ')' };
    }

    /// <summary>
    /// Parses the optional suffix of a computed-column declaration (after the
    /// expression): bare empty, <c>PERSISTED</c>, or <c>PERSISTED NOT NULL</c>.
    /// Any other constraint keyword in this position (<c>IDENTITY</c>,
    /// <c>DEFAULT</c>, bare <c>NULL</c>/<c>NOT NULL</c>, or <c>PERSISTED NULL</c>)
    /// raises Msg 8183 — real SQL Server's blanket "computed columns must be
    /// persisted to carry a NULL/NOT NULL/CHECK/FK constraint" error.
    /// </summary>
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
            context.MoveNextRequired();
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
    private static BooleanExpression ParseInlineCheckPredicate(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var predicate = BooleanExpression.Parse(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        return predicate;
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
        return !Collation.Default.Equals(declaredStart, heapColumns[generatedStartOrdinal]!.Name)
            ? throw SimulatedSqlException.TemporalPeriodStartNotMatching()
            : !Collation.Default.Equals(declaredEnd, heapColumns[generatedEndOrdinal]!.Name)
                ? throw SimulatedSqlException.TemporalPeriodEndNotMatching()
                : (generatedStartOrdinal, generatedEndOrdinal);
    }

    internal static CheckConstraint[] ResolveCheckConstraints(
        string tableName,
        IReadOnlyList<(string? Name, BooleanExpression Predicate, string? InlineColumn)> pendingChecks,
        Database database)
    {
        if (pendingChecks.Count == 0)
            return [];

        var resolved = new CheckConstraint[pendingChecks.Count];
        for (var c = 0; c < pendingChecks.Count; c++)
        {
            var pending = pendingChecks[c];
            var name = pending.Name ?? AutoCheckName(tableName, pending.InlineColumn, c);
            resolved[c] = new CheckConstraint(name, pending.Predicate, pending.InlineColumn, database.AllocateObjectId());
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
        const uint fnvOffset32 = 2166136261;
        const uint fnvPrime32 = 16777619;
        var h = fnvOffset32;
        foreach (var ch in tableName)
            h = (h ^ ch) * fnvPrime32;
        h = (h ^ (byte)':') * fnvPrime32;
        if (inlineColumn is not null)
        {
            foreach (var ch in inlineColumn)
                h = (h ^ ch) * fnvPrime32;
        }
        h = (h ^ (byte)declarationIndex) * fnvPrime32;
        var truncatedTable = tableName.Length > 8 ? tableName[..8] : tableName;
        return inlineColumn is null
            ? $"CK__{truncatedTable}__{h:X8}"
            : $"CK__{truncatedTable}__{(inlineColumn.Length > 8 ? inlineColumn[..8] : inlineColumn)}__{h:X8}";
    }

    /// <summary>
    /// Parses the inline column-constraint shape <c>(PRIMARY KEY|UNIQUE) [CLUSTERED|NONCLUSTERED]</c>,
    /// entered with <see cref="ParserContext.Token"/> on the <c>PRIMARY</c> or
    /// <c>UNIQUE</c> keyword. Consumes the trailing <c>KEY</c> for PK and the
    /// optional clustering modifier (which the simulator accepts and ignores
    /// because it has no index storage to attach the modifier to). Returns
    /// the parsed kind; leaves <see cref="ParserContext.Token"/> on the next
    /// constraint keyword, comma, or closing paren.
    /// </summary>
    private static KeyConstraintKind ParseInlineKeyKindAndModifiers(ParserContext context)
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
        if (context.Token is ReservedKeyword { Keyword: Keyword.Clustered or Keyword.NonClustered })
            context.MoveNextRequired();
        return kind;
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
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals)> pendingKeys,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn)> pendingChecks,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable)> pendingComputed)
    {
        string? constraintName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Constraint })
        {
            if (context.GetNextRequired() is not Name nameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            constraintName = nameToken.Value;
            context.MoveNextRequired();
        }

        if (context.Token is ReservedKeyword { Keyword: Keyword.Check })
        {
            pendingChecks.Add((constraintName, ParseInlineCheckPredicate(context), null));
            return;
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var kind = ParseInlineKeyKindAndModifiers(context);

        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var ordinals = new List<int>();
        do
        {
            if (context.GetNextRequired() is not Name keyColumn)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var found = -1;
            for (var i = 0; i < heapColumns.Count; i++)
            {
                if (heapColumns[i] is { } existing && Collation.Default.Equals(existing.Name, keyColumn.Value))
                {
                    found = i;
                    break;
                }
                if (heapColumns[i] is null)
                {
                    foreach (var pending in pendingComputed)
                    {
                        if (pending.Index == i && Collation.Default.Equals(pending.Name, keyColumn.Value))
                            throw new NotSupportedException("PRIMARY KEY/UNIQUE on a computed column.");
                    }
                }
            }
            if (found < 0)
                throw SimulatedSqlException.InvalidColumnName(keyColumn.Value);
            ordinals.Add(found);

            // Optional ASC/DESC after each column — accept and ignore.
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Asc or Keyword.Desc })
                context.MoveNextRequired();
        } while (context.Token is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        pendingKeys.Add((kind, constraintName, [.. ordinals]));
    }

    /// <summary>
    /// Validates the queued PK/UNIQUE constraints against the resolved column
    /// list and translates them into <see cref="KeyConstraint"/> records keyed
    /// by storage ordinal. Enforces SQL Server's compile-time rules: at most
    /// one PRIMARY KEY per table (Msg 8110), no PK on a column whose declared
    /// nullability is NULL (Msg 8111 — also fires for table-level PK on a
    /// column declared NULL), no key column of LOB type (Msg 1919). Generates
    /// a SQL-Server-shaped auto name for any unnamed constraint
    /// (<c>PK__&lt;table&gt;__&lt;hex&gt;</c> / <c>UQ__&lt;table&gt;__&lt;hex&gt;</c>).
    /// Computed columns are not yet supported as key participants — those
    /// raise <see cref="NotSupportedException"/>.
    /// </summary>
    internal static KeyConstraint[] ResolveKeyConstraints(
        string tableName,
        IReadOnlyList<HeapColumn> heapColumns,
        IReadOnlyList<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals)> pendingKeys,
        Database database)
    {
        if (pendingKeys.Count == 0)
            return [];

        var primaryKeyCount = 0;
        var resolved = new KeyConstraint[pendingKeys.Count];
        for (var c = 0; c < pendingKeys.Count; c++)
        {
            var pending = pendingKeys[c];
            if (pending.Kind == KeyConstraintKind.PrimaryKey && ++primaryKeyCount > 1)
                throw SimulatedSqlException.MultiplePrimaryKey(tableName);

            var storageOrdinals = new int[pending.FullOrdinals.Length];
            for (var i = 0; i < pending.FullOrdinals.Length; i++)
            {
                var fullOrdinal = pending.FullOrdinals[i];
                var column = heapColumns[fullOrdinal];
                if (column.Computed is not null)
                    throw new NotSupportedException("PRIMARY KEY/UNIQUE on a computed column.");
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

            resolved[c] = new KeyConstraint(pending.Kind, pending.Name ?? AutoConstraintName(tableName, pending.Kind, pending.FullOrdinals, heapColumns), storageOrdinals, database.AllocateObjectId());
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
}
