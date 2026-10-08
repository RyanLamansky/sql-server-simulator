using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using StoredIndex = SqlServerSimulator.Storage.Index;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// The refusal of a <c>CREATE OR ALTER</c> of a kind that has none, the
    /// cursor on the kind's word: Msg 102 near it, followed for a security
    /// policy by the second Msg 102 real's parser recovers to.
    /// </summary>
    private static SimulatedSqlException RefusedCreateOrAlter(ParserContext context)
    {
        var refused = SimulatedSqlException.SyntaxErrorNear(context);
        return RecoveredOrAlterPolicyError(context) is { } recovered
            ? SimulatedSqlException.Aggregate([refused, recovered])
            : refused;
    }

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
            case UnquotedString { Span: var word } when word.Equals("COLUMNSTORE", StringComparison.OrdinalIgnoreCase):
                return Simulation.TryParseCreateIndex(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Type }:
                return TryParseCreateType(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Sequence }:
                return TryParseCreateSequence(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Partition }:
                return TryParseCreatePartition(context);
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
            case Name credentialWord when credentialWord.Value.Equals("CREDENTIAL", StringComparison.OrdinalIgnoreCase):
                return TryParseCreateCredential(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.FullText }:
                return Simulation.TryParseCreateFullText(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Xml }:
                return Simulation.TryParseCreateXml(context);
            case Name jsonWord when jsonWord.Value.Equals("JSON", StringComparison.OrdinalIgnoreCase):
                return Simulation.TryParseCreateJsonIndex(context);
            case Name vectorWord when vectorWord.Value.Equals("VECTOR", StringComparison.OrdinalIgnoreCase):
                return Simulation.TryParseCreateVectorIndex(context);
            case ReservedKeyword { Keyword: Keyword.Primary }:
                return Simulation.TryParseCreatePrimaryXml(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Spatial }:
                return Simulation.TryParseCreateSpatial(context);
            case Name synonymWord when synonymWord.Value.Equals("SYNONYM", StringComparison.OrdinalIgnoreCase):
                return TryParseCreateSynonym(context);
            case Name securityWord when securityWord.Value.Equals("SECURITY", StringComparison.OrdinalIgnoreCase):
                return TryParseCreateSecurityPolicy(context);
            case ReservedKeyword { Keyword: Keyword.Statistics }:
                return Simulation.TryParseCreateStatistics(context);
            case Name assemblyWord when assemblyWord.Value.Equals("ASSEMBLY", StringComparison.OrdinalIgnoreCase):
                return TryParseCreateAssembly(context);
            case Name aggregateWord when aggregateWord.Value.Equals("AGGREGATE", StringComparison.OrdinalIgnoreCase):
                return TryParseCreateAggregate(context);
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
                    _ => throw RefusedCreateOrAlter(context),
                };
            case ReservedKeyword { Keyword: Keyword.Default }:
                return TryParseCreateDefaultOrRule(context, isRule: false);
            case ReservedKeyword { Keyword: Keyword.Rule }:
                return TryParseCreateDefaultOrRule(context, isRule: true);
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
        // tempdb pads a local temp table's name to 116 characters plus a
        // 12-digit suffix, so a longer one is Msg 193 (probed 2026-10-01).
        if (BatchContext.IsLocalTempName(tableName.Leaf) && tableName.Leaf.Length > 116)
            throw SimulatedSqlException.TempTableNameTooLong(tableName.Leaf);

        // `AS NODE` / `AS EDGE` follows the column list, which an edge table
        // may leave out altogether.
        var hasColumnList = context.GetNextRequired() is Operator { Character: '(' };
        if (!hasColumnList && (context.Token is not ReservedKeyword { Keyword: Keyword.As } || PeekGraphKeyword(context) == GraphTableKind.None))
            return false;

        var heapColumns = new List<HeapColumn?>();
        var pendingComputed = new List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)>();
        var pendingKeys = new List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)>();
        var pendingChecks = new List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition, bool NotForReplication)>();
        var pendingPeriod = new List<(string StartCol, string EndCol)>();
        var pendingForeignKeys = new List<PendingForeignKey>();
        var pendingIndexes = new List<PendingInlineIndex>();
        var pendingEdgeConstraints = new List<PendingEdgeConstraint>();
        GraphTableKind graphKind;
        if (hasColumnList)
        {
            graphKind = PeekGraphTableKind(context);
            if (graphKind != GraphTableKind.None)
                heapColumns.AddRange(GraphColumns.Create(graphKind, tableName.Leaf, context.Batch.Connection.CurrentDatabase.Collation));
            using (ParserScope.Enter(ref context.VariablesRefusedIn, "CREATE TABLE"))
            using (ParserScope.Enter(ref context.ColumnIndexOptions, IndexOptionStatement.CreateTable))
            {
                if (!ParseColumnList(context, tableName.Leaf, isTableVariable: false, isTableType: false, heapColumns, pendingKeys, pendingChecks, pendingComputed, pendingPeriod, pendingForeignKeys, pendingIndexes, pendingEdgeConstraints))
                    return false;
            }
            // A table holds at most 1024 columns (probed 2026-10-01).
            if (heapColumns.Count > 1024)
                throw SimulatedSqlException.TooManyColumns(heapColumns[1024]?.Name ?? pendingComputed.Find(computed => computed.Index == 1024).Name, tableName.Leaf);
            if (graphKind != GraphTableKind.None)
            {
                context.MoveNextRequired();
                _ = ConsumeGraphTableClause(context, hasColumnList: true);
            }
        }
        else
        {
            graphKind = ConsumeGraphTableClause(context, hasColumnList: false);
            heapColumns.AddRange(GraphColumns.Create(graphKind, tableName.Leaf, context.Batch.Connection.CurrentDatabase.Collation));
        }
        var graphIndexPosition = -1;
        if (graphKind != GraphTableKind.None)
        {
            if (BatchContext.IsLocalTempName(tableName.Leaf) || BatchContext.IsGlobalTempName(tableName.Leaf))
                throw SimulatedSqlException.GraphTableCannotBeTemporary();
            graphIndexPosition = pendingIndexes.Count;
            pendingIndexes.Add(GraphUniqueIndex(heapColumns[0]!, pendingKeys.Count, partitioned: false));
        }

        // Optional trailing placement and option clauses, in any order:
        //   ON <filegroup> | ON <scheme>(<column>) [TEXTIMAGE_ON <filegroup>]
        //     — only a partition scheme placement is recorded.
        //   WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = X)) — load-bearing.
        // SSMS-emitted CREATE TABLE always trails `) ON [PRIMARY]` and may
        // additionally trail `TEXTIMAGE_ON [PRIMARY]`; SYSTEM_VERSIONING is
        // table-author-emitted and doesn't coexist with the SSMS form in
        // observed scripts, but we accept either ordering for generality.
        // Parsed regardless of skip mode so the cursor advances cleanly;
        // the resulting historyTableName is only used after the skip-mode
        // gate below. A graph table's clause already moved past its list.
        if (graphKind == GraphTableKind.None)
            context.MoveNextOptional();
        var tableDataSpace = ParseOptionalDataSpaceClause(context, out var textImageOn);
        // On a partitioned graph table the graph-id index stays unaligned, on
        // PRIMARY (probed 2026-10-05 against SQL Server 2025).
        if (graphIndexPosition >= 0 && tableDataSpace is { Columns: not null })
            pendingIndexes[graphIndexPosition] = GraphUniqueIndex(heapColumns[0]!, pendingIndexes[graphIndexPosition].KeysBefore, partitioned: true);
        // FILESTREAM_ON names where FILESTREAM data goes, which a table with no
        // FILESTREAM column has none of (Msg 1716); the column itself is refused
        // where it is written, so a clause here always lacks one.
        var fileStreamOn = false;
        if (context.Token is StringToken { Span: var fileStreamKeyword } && fileStreamKeyword.Equals("FILESTREAM_ON", StringComparison.OrdinalIgnoreCase))
        {
            if (context.GetNextRequired() is not Name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            fileStreamOn = true;
        }
        SystemVersioningOptions? systemVersioning = null;
        var memoryOptimization = default(MemoryOptimizationOptions);
        (byte? Data, bool? Xml, List<PartitionCompressionClause>? Partitions, List<PartitionCompressionClause>? XmlPartitions) tableCompression = default;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            systemVersioning = ParseTableOptions(context, tableDataSpace is not null, out memoryOptimization, out tableCompression);
        var memoryOptimized = memoryOptimization.MemoryOptimized;
        if (memoryOptimized && (BatchContext.IsLocalTempName(tableName.Leaf) || BatchContext.IsGlobalTempName(tableName.Leaf)))
            throw SimulatedSqlException.TemporaryMemoryOptimizedTable();
        // A node or edge table is neither memory-optimized nor temporal
        // (probed 2026-10-05 against SQL Server 2025).
        if (graphKind != GraphTableKind.None && memoryOptimized)
            throw SimulatedSqlException.GraphTableCannotBeMemoryOptimized();
        if (graphKind != GraphTableKind.None && systemVersioning is { On: true })
            throw SimulatedSqlException.GraphTableCannotBeTemporal();

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

        // A computed column's own nullability is what its expression's is —
        // real derives it with the same rules it derives a projection's
        // COLMETADATA flag with, so `CONCAT(a, b)` and `ISNULL(b, 0)` are NOT
        // NULL while `a + b` and `LEN(a)` are nullable (probe-confirmed cell
        // for cell). The declaration matters: a history table's matching column
        // has to agree, which is what SYSTEM_VERSIONING's shape check reads.
        bool ResolveComputedReferenceNullable(MultiPartName reference)
        {
            foreach (var existing in heapColumns)
            {
                if (existing is not null && context.Batch.CurrentDatabase.Collation.Equals(existing.Name, reference.Leaf))
                    return existing.Nullable;
            }
            return true;
        }

        foreach (var pending in pendingComputed)
        {
            if (Parser.Expressions.XmlMethodCall.AppearsIn(pending.Expression))
                throw SimulatedSqlException.XmlMethodInComputedColumn(pending.Name, tableName.Leaf, "CREATE TABLE", tableVariable: false);
            // A bare NULL gives the column no type to take: real reads it as a
            // type name it can't find (probed 2026-10-02 against SQL Server 2025).
            if (Expression.IsUntypedNullLiteral(pending.Expression))
                throw SimulatedSqlException.CannotFindDataType("NULL", pending.Index + 1);
            var resolvedType = pending.Expression.GetSqlType(context.Batch, ResolveComputedReference);
            // The column has to settle on one collation, as a projection does.
            if (UnresolvedCollation.On(resolvedType) is { } conflict)
                throw SimulatedSqlException.UnresolvedCollationInOutputColumn(conflict.RightName, conflict.LeftName, conflict.OperatorName, "CREATE TABLE", pending.Index + 1, state: 16);
            var inferredNullable = pending.Expression.ResultIsNullable(
                new NullabilityContext(context.Batch, ResolveComputedReferenceNullable, ResolveComputedReference));
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
            // real refuses to create one from a session whose SET options
            // would read the expression differently (Msg 1934,
            // probe-confirmed — the non-persisted form is accepted). ANSI_NULLS
            // is the table's own capture instead, which an index over the
            // column later refuses (Msg 1935, probed 2026-10-04).
            if (pending.Persisted && IncorrectSetOptionNames(context, exemptAnsiNulls: true) is { } setOptions)
                throw SimulatedSqlException.IncorrectSetOptions("CREATE TABLE", setOptions);
            // …and refuses one whose expression isn't deterministic at all
            // (Msg 4936), since the stored value could then disagree with a
            // fresh evaluation.
            if (pending.Persisted)
                RejectNondeterministicPersisted(context, heapColumns, pending.Name, tableName.Leaf, pending.Definition);
            // A numeric-spelled peer names the expression that reads it.
            Parser.Expressions.Reference.MarkNumericSpelled(pending.Expression, name => heapColumns.Exists(
                peer => peer is { SpelledNumeric: true } && context.Batch.CurrentDatabase.Collation.Equals(peer.Name, name.Leaf)));
            heapColumns[pending.Index] = new HeapColumn(
                pending.Name,
                resolvedType,
                maxLength: computedMaxLength,
                nullable: pending.Nullable && inferredNullable && !IsPendingPrimaryKeyOrdinal(pendingKeys, pending.Index),
                computedExpression: pending.Expression,
                isPersisted: pending.Persisted,
                computedDefinition: pending.Definition,
                spelledNumeric: resolvedType is DecimalSqlType && pending.Expression.ResultReportsNumeric);
        }

        // Created under SET ANSI_PADDING OFF, the columns trim what they store.
        if (!context.Batch.Connection.AnsiPadding)
        {
            var padded = UnderSessionAnsiPadding([.. heapColumns!], context.Batch.Connection);
            for (var i = 0; i < padded.Length; i++)
                heapColumns[i] = padded[i];
        }

        RejectOversizedMinimumRow([.. heapColumns!], tableName.Leaf);

        // A CHECK predicate — inline or table-level — may not read a
        // non-persisted computed column (Msg 1764). Runs ahead of the Msg 8141
        // walk below, matching real's probed precedence.
        BindCheckConstraints(context.Batch, heapColumns, pendingChecks);
        RejectChecksOverNonPersistedComputedColumns(context.Batch.CurrentDatabase.Collation, tableName.Leaf, heapColumns, pendingChecks);

        // Real SQL Server's Msg 8141 (probed against SQL Server 2025) rejects
        // an inline column-level CHECK that references any column other than
        // its owning column — table-level CHECK has no such restriction.
        // Walk each inline predicate's Expression operands structurally via
        // <see cref="Expression.VisitColumnReferences(Action{MultiPartName})"/> and reject any peer
        // reference. Coverage is limited to the common container subclasses
        // (Reference, Parenthesized, TwoSidedExpression, Cast, Length) — peer
        // refs buried in less-common containers escape detection here and
        // surface at INSERT instead (fidelity gap documented on
        // <see cref="Expression.VisitColumnReferences(Action{MultiPartName})"/>).
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

        context.Batch.NoteTempTableCreation(tableName.Leaf);

        // The period declaration is checked as the batch compiles: a refusal
        // stops the whole batch, an un-taken branch's included (probed
        // 2026-10-04 against SQL Server 2025).
        RejectRepeatedLedgerColumns(heapColumns!);
        var resolvedPeriod = ResolvePeriodColumns(context.Batch.CurrentDatabase.Collation, heapColumns!, pendingPeriod);
        if (systemVersioning is { On: false })
            systemVersioning = null;

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
        var createTargetDatabase = context.Batch.DatabaseForName(tableName);
        if (!isTempTable && !PermissionEnforcement.HasDatabasePermission(context.Batch, createTargetDatabase, "CREATE TABLE"))
            throw SimulatedSqlException.DatabasePermissionDenied("CREATE TABLE", createTargetDatabase.Name);
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
            if (!context.Batch.TryResolveCreateSchema(tableName, out schema))
                throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(tableName.ImmediateQualifier ?? Database.DefaultSchemaName);
            // This branch is the permanent-table one — a #temp / ##temp target
            // took the branch above and stays legal whatever the session's own
            // database is set to.
            schema.Database.RejectWriteWhenReadOnly();
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
            if (!PermissionEnforcement.HasSchemaAlterForCreate(context.Batch, schema))
                throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schema.Name);
            destination = schema.HeapTables;
            schemaId = schema.SchemaId;
            RequireColumnTypeReferences(context.Batch, heapColumns!);
        }

        // Cross-kind name-collision check for permanent tables (Msg 2714).
        // Temp tables live in a session-scoped dict that doesn't share the
        // database object-name namespace.
        if (!isTempTable && schema!.HasNameInSharedNamespace(tableName.Leaf))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(tableName.Leaf);
        RejectTakenConstraintNames(isTempTable ? null : schema, tableName.Leaf, heapColumns!, pendingKeys, pendingChecks, pendingForeignKeys, isTempTable ? context.Connection : null, pendingEdgeConstraints);
        ValidateTableDeclaration(createTargetDatabase, tableName.Leaf, memoryOptimization, heapColumns, pendingKeys, pendingIndexes);

        var (keyObjectIds, indexObjectIds) = AllocateDeclarationObjectIds(context.CurrentDatabase, pendingKeys, pendingIndexes);
        var keyConstraints = ResolveKeyConstraints(tableName.Leaf, heapColumns!, pendingKeys, context.CurrentDatabase, context.Batch.CurrentStatement.UtcNow, keyObjectIds, tableName.ToString());
        var checkConstraints = ResolveCheckConstraints(tableName.Leaf, pendingChecks, context.CurrentDatabase, context.Batch.CurrentStatement.UtcNow);

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
            // A temporary table can't be versioned, nor can a history table be
            // temporary (probed 2026-10-04 against SQL Server 2025).
            if (isTempTable)
                throw SimulatedSqlException.TemporalTableInTempdb(tableName.Leaf);
            if (resolvedPeriod is null)
                throw SimulatedSqlException.SystemVersioningRequiresPeriod();
            if (!pendingKeys.Exists(static key => key.Kind == KeyConstraintKind.PrimaryKey))
                throw SimulatedSqlException.TemporalTableRequiresPrimaryKey($"{schema!.Database.Name}.{schema.Name}.{tableName.Leaf}");
            if (options.HistoryTable is { } hn)
            {
                if (!context.Batch.TryResolveCreateSchema(hn, out historySchema))
                    throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(hn.ImmediateQualifier ?? Database.DefaultSchemaName);
                // Naming the table being created reads as a history table with
                // a period of its own.
                if (ReferenceEquals(historySchema, schema) && context.Batch.CurrentDatabase.Collation.Equals(hn.Leaf, tableName.Leaf))
                    throw SimulatedSqlException.HistoryTableContainsPeriod($"{schema!.Database.Name}.{schema.Name}.{tableName.Leaf}");
                if (historySchema.HeapTables.TryGetValue(hn.Leaf, out existingHistory))
                    RejectUnusableHistoryTable(context, existingHistory);
                else if (historySchema.HasNameInSharedNamespace(hn.Leaf))
                    throw SimulatedSqlException.ObjectCannotBeHistoryTable($"{historySchema.Name}.{hn.Leaf}");
            }
            else
            {
                historySchema = schema!;
            }
            historyDestination = historySchema.HeapTables;
        }

        // A three-part CREATE TABLE lands in the named database, so the object
        // id comes from that database's counter and the table carries it as
        // its owner. Temp tables and table variables have no schema and so no
        // owning database; their ids come from tempdb's counter, the database
        // whose catalog lists them.
        var owningDatabase = schema?.Database;
        var heapTable = new HeapTable(
            tableName.Leaf,
            [.. heapColumns!],
            (owningDatabase ?? context.Connection.Simulation.Databases[TempdbDatabaseName]).AllocateObjectId(),
            schemaId,
            context.Batch.CurrentStatement.UtcNow,
            keyConstraints,
            checkConstraints,
            periodColumns: resolvedPeriod)
        {
            OwningDatabase = owningDatabase,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
            GraphKind = graphKind,
            IsMemoryOptimized = memoryOptimized,
            Durability = memoryOptimization.Durability,
            HeapDataCompression = tableCompression.Data ?? 0,
            HeapXmlCompression = tableCompression.Xml ?? false,
        };
        // The table's storage options describe its rows, which a clustered key
        // declared with it holds (probed 2026-10-05 against SQL Server 2025).
        if (Array.Find(keyConstraints, static key => key.IsClustered) is { } clusteredKey)
        {
            clusteredKey.DataCompression = tableCompression.Data ?? clusteredKey.DataCompression;
            clusteredKey.XmlCompression = tableCompression.Xml ?? clusteredKey.XmlCompression;
        }
        AttachGraphColumns(heapTable);
        PlaceNewTable(context.Batch, heapTable, tableDataSpace, textImageOn, fileStreamOn);
        if (tableCompression.Partitions is { } compressionClauses)
            ApplyTablePartitionCompression(heapTable, Array.Find(keyConstraints, static key => key.IsClustered), tableCompression.Data ?? 0, compressionClauses, xml: false);
        if (tableCompression.XmlPartitions is { } xmlCompressionClauses)
            ApplyTablePartitionCompression(heapTable, Array.Find(keyConstraints, static key => key.IsClustered), tableCompression.Xml == true ? (byte)1 : (byte)0, xmlCompressionClauses, xml: true);
        if (isGlobalTempTable)
            heapTable.OwnerSession = context.Batch.Connection.Session;
        if (isLocalTempTable)
            heapTable.TempScopeId = context.Batch.TempTableScopeId();
        VersionStore.NoteDefinitionChange(context.Batch, heapTable);
        if (!(isLocalTempTable ? context.Batch.Connection.TryAddTempTable(heapTable) : destination.TryAdd(heapTable.Name, heapTable)))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(heapTable.Name);
        // A local temp created inside a module body (proc / trigger / dynamic
        // SQL) is dropped when that module exits; register it so the body's
        // finally drops it.
        if (isLocalTempTable)
            context.Batch.RegisterScopedTempTable(heapTable);

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
                    RequireHistoryCleanupIndex(context, heapTable, existingHistory, versioning);
                    CheckHistoryConsistency(context, heapTable, existingHistory, versioning);
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
        // A temporary table's FOREIGN KEY is skipped with a notice rather
        // than resolved (probed 2026-09-28 against SQL Server 2025).
        if (isTempTable)
        {
            foreach (var _ in pendingForeignKeys)
                context.Batch.AppendInfoError(@class: 0, state: 0, number: 1756, SimulatedSqlException.TemporaryTableForeignKeySkippedMessage(tableName.Leaf));
            pendingForeignKeys.Clear();
        }
        try
        {
            if (pendingForeignKeys.Count > 0)
                ResolveForeignKeys(heapTable, pendingForeignKeys, context);
            if (pendingIndexes.Count > 0)
                AddInlineIndexes(context.Batch, heapTable, tableName.ToString(), pendingIndexes, indexObjectIds);
            if (pendingEdgeConstraints.Count > 0)
                heapTable.EdgeConstraints.AddRange(ResolveEdgeConstraints(context, heapTable, pendingEdgeConstraints));
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

        // CREATE TABLE participates in transaction rollback, temp tables
        // included (probe-confirmed for #foo, ##foo and a permanent table).
        if (isTempTable && context.Connection.CurrentTransaction is { } tx)
        {
            if (isLocalTempTable)
                tx.UndoLog.RecordLocalTempTableCreation(context.Connection, heapTable);
            else
                tx.UndoLog.RecordTempTableCreation(destination, heapTable.Name);
        }
        else if (!isTempTable)
        {
            var createdHistory = existingHistory is null ? heapTable.SystemVersioning : null;
            RecordDdlUndo(context, () =>
            {
                _ = destination.TryRemove(heapTable.Name, out _);
                foreach (var fk in heapTable.OutgoingForeignKeys)
                    _ = fk.ReferencedTable.IncomingForeignKeys.Remove(fk);
                if (createdHistory is not null)
                    _ = historyDestination!.TryRemove(createdHistory.Name, out _);
                else if (existingHistory is { } adopted)
                    adopted.IsHistoryTable = false;
            });
        }
        WarnOfOversizedMaximumRow(context.Batch, heapTable.Columns, tableName.Leaf, state: 2);
        foreach (var key in keyConstraints)
        {
            key.KeyMayExceedLimit = WarnOfWideIndexKey(context.Batch, heapTable.Columns, key.FullOrdinals, key.Name, key.IsClustered);
            heapTable.KeysMayExceedLimit |= key.KeyMayExceedLimit;
        }
        // Real raises no DDL event for a temp table (tempdb owns it), only for
        // a permanent one in the current database.
        if (!isTempTable)
            RecordDdlEvent(context, "CREATE_TABLE", schema?.Name ?? Database.DefaultSchemaName, heapTable.Name, "TABLE");
        return true;
    }

    /// <summary>
    /// Sends Msg 1945 when an index's or key's widest key passes what its kind
    /// can hold — 900 bytes clustered, 1700 nonclustered — the sum of its
    /// columns' declared byte lengths; the index is built all the same
    /// (probed 2026-10-01 against SQL Server 2025). Returns whether a row's
    /// key can pass the limit, which each write then measures
    /// (<see cref="EnforceIndexKeyLength"/>). With <paramref name="rejectFixedOverflow"/>
    /// a key whose fixed-length columns alone pass it is Msg 1944 instead,
    /// since every row's would (probed 2026-10-05).
    /// </summary>
    internal static bool WarnOfWideIndexKey(BatchContext batch, HeapColumn[] columns, int[] keyFullOrdinals, string indexName, bool clustered, bool rejectFixedOverflow = false)
    {
        var length = 0;
        var fixedLength = 0;
        foreach (var ordinal in keyFullOrdinals)
        {
            var columnLength = BuiltInResources.GetSysColumnMetadata(columns[ordinal]).MaxLength;
            if (columnLength < 0)
                return false;
            length += columnLength;
            if (columns[ordinal].Type.IsFixedLength)
                fixedLength += columnLength;
        }
        var limit = clustered ? 900 : 1700;
        if (rejectFixedOverflow && fixedLength > limit)
            throw SimulatedSqlException.IndexKeyTooLarge(indexName, fixedLength, clustered, limit);
        if (length <= limit)
            return false;
        if (!batch.IsSkipping)
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.WideIndexKeyMessage(batch, clustered, limit, indexName, length));
        return true;
    }

    /// <summary>
    /// Raises Msg 1946 when <paramref name="rowValues"/>' key for an index or
    /// key whose declared width can pass its limit actually does: the sum of
    /// the key values' stored lengths, a <c>sql_variant</c> carrying 8 bytes
    /// of its own (probed 2026-10-05 against SQL Server 2025). The insert and
    /// update enforcement paths call it ahead of the uniqueness checks.
    /// </summary>
    internal static void EnforceIndexKeyLength(HeapTable table, SqlValue[] rowValues)
    {
        foreach (var key in table.KeyConstraints)
        {
            if (key.KeyMayExceedLimit && !key.IsDisabled)
                RejectOversizedEntry(key.Name, key.FullOrdinals, key.IsClustered, rowValues);
        }
        foreach (var index in table.Indexes)
        {
            if (index.KeyMayExceedLimit && !index.IsDisabled)
                RejectOversizedEntry(index.Name, index.KeyFullOrdinals, index.IsClustered, rowValues);
        }
    }

    private static void RejectOversizedEntry(string indexName, int[] fullOrdinals, bool clustered, SqlValue[] rowValues)
    {
        var length = 0;
        foreach (var ordinal in fullOrdinals)
        {
            var value = rowValues[ordinal];
            if (value.IsNull)
                continue;
            length += Parser.Expressions.DataLength.ByteCount(value) + (value.Type is SqlVariantSqlType ? 8 : 0);
        }
        var limit = clustered ? 900 : 1700;
        if (length > limit)
            throw SimulatedSqlException.IndexEntryTooLong(length, indexName, limit, clustered);
    }

    /// <summary>
    /// Parses the trailing <c>WITH (option, …)</c> list after a CREATE TABLE
    /// column list: <c>SYSTEM_VERSIONING = ON […]</c> and
    /// <c>MEMORY_OPTIMIZED</c> / <c>DURABILITY</c>
    /// (<paramref name="memoryOptimization"/>), which are load-bearing, and the
    /// storage options SSMS scripts carry — <c>DATA_COMPRESSION = {NONE | ROW
    /// | PAGE}</c> and <c>XML_COMPRESSION = {ON | OFF}</c> — which are parsed
    /// and discarded, the simulator storing no compressed pages.
    /// <paramref name="placed"/> says an <c>ON</c> clause came before the list.
    /// Cursor on entry: the <c>WITH</c> keyword. Cursor on exit: the list's
    /// closing <c>)</c>. Returns the system-versioning options, or null when
    /// none were given.
    /// </summary>
    private static SystemVersioningOptions? ParseTableOptions(ParserContext context, bool placed, out MemoryOptimizationOptions memoryOptimization, out (byte? Data, bool? Xml, List<PartitionCompressionClause>? Partitions, List<PartitionCompressionClause>? XmlPartitions) compression)
    {
        compression = default;
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        SystemVersioningOptions? systemVersioning = null;
        bool? memoryOptimized = null;
        byte? durability = null;
        var dataCompression = false;
        while (true)
        {
            var option = context.GetNextRequired();
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            switch (option)
            {
                case UnquotedString { ContextualKeyword: ContextualKeyword.System_Versioning }:
                    // OFF declares the table as it would be without the clause
                    // (probed 2026-10-04 against SQL Server 2025).
                    systemVersioning = context.GetNextRequired() switch
                    {
                        ReservedKeyword { Keyword: Keyword.On } => ParseSystemVersioningOnOptions(context),
                        ReservedKeyword { Keyword: Keyword.Off } => new SystemVersioningOptions(null, -1, HistoryRetentionUnit.Infinite, on: false),
                        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                    };
                    break;
                case StringToken name when name.Span.Equals("DATA_COMPRESSION", StringComparison.OrdinalIgnoreCase):
                    if (context.GetNextRequired() is not StringToken level
                        || !(level.Span.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                            || level.Span.Equals("ROW", StringComparison.OrdinalIgnoreCase)
                            || level.Span.Equals("PAGE", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    var tableLevel = level.Span.Equals("ROW", StringComparison.OrdinalIgnoreCase) ? (byte)1
                        : level.Span.Equals("PAGE", StringComparison.OrdinalIgnoreCase) ? (byte)2
                        : (byte)0;
                    if (FollowedByOnPartitions(context))
                        (compression.Partitions ??= []).Add(new PartitionCompressionClause(tableLevel, ReadOnPartitionsList(context)));
                    else
                        compression.Data = tableLevel;
                    dataCompression = true;
                    break;
                case StringToken name when name.Span.Equals("XML_COMPRESSION", StringComparison.OrdinalIgnoreCase):
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off } xmlToggle)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    var xmlLevel = xmlToggle.Keyword == Keyword.On ? (byte)1 : (byte)0;
                    var xmlOnPartitions = FollowedByOnPartitions(context);
                    // Named for the whole table and for partitions is Msg 7741
                    // state 1, then Msg 1750 state 0 (probed 2026-10-06 against
                    // SQL Server 2025).
                    if (compression.Xml is not null || (!xmlOnPartitions && compression.XmlPartitions is not null))
                        throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.XmlCompressionSpecifiedTwice(state: 1), state: 0);
                    if (xmlOnPartitions)
                        (compression.XmlPartitions ??= []).Add(new PartitionCompressionClause(xmlLevel, ReadOnPartitionsList(context)));
                    else
                        compression.Xml = xmlLevel == 1;
                    break;
                case StringToken name when name.Span.Equals("MEMORY_OPTIMIZED", StringComparison.OrdinalIgnoreCase):
                    memoryOptimized = context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.On or Keyword.Off } toggle
                        ? toggle.Keyword == Keyword.On
                        : throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case StringToken name when name.Span.Equals("DURABILITY", StringComparison.OrdinalIgnoreCase):
                    durability = context.GetNextRequired() is StringToken { Span: var value }
                        ? value.Equals("SCHEMA_ONLY", StringComparison.OrdinalIgnoreCase) ? (byte)1
                            : value.Equals("SCHEMA_AND_DATA", StringComparison.OrdinalIgnoreCase) ? (byte)0
                            : throw SimulatedSqlException.SyntaxErrorNear(context)
                        : throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case StringToken name:
                    throw new NotSupportedException($"The CREATE TABLE option {name.Span.ToString().ToUpperInvariant()} isn't modeled.");
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            if (context.GetNextRequired() is not Operator { Character: ',' })
                break;
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // DURABILITY belongs to a memory-optimized table, and such a table
        // takes no placement or compression (probed 2026-10-02 against SQL
        // Server 2025).
        if (durability is { } writtenDurability && memoryOptimized != true)
            throw SimulatedSqlException.DurabilityWithoutMemoryOptimized(writtenDurability);
        if (memoryOptimized == true && placed)
            throw SimulatedSqlException.NotSupportedWithMemoryOptimized("The feature 'ON'", 1);
        if (memoryOptimized == true && dataCompression)
            throw SimulatedSqlException.NotSupportedWithMemoryOptimized("The option 'DATA_COMPRESSION'", 2);
        memoryOptimization = new(memoryOptimized == true, durability ?? 0);
        return systemVersioning;
    }

    /// <summary>
    /// What a <c>CREATE TABLE</c> or <c>CREATE TYPE … AS TABLE</c> option list
    /// said about memory optimization: <c>MEMORY_OPTIMIZED = ON</c> and the
    /// <c>DURABILITY</c> (0 <c>SCHEMA_AND_DATA</c>, 1 <c>SCHEMA_ONLY</c>).
    /// </summary>
    internal readonly struct MemoryOptimizationOptions(bool memoryOptimized, byte durability)
    {
        public readonly bool MemoryOptimized = memoryOptimized;
        public readonly byte Durability = durability;
    }

    /// <summary>
    /// A <c>CREATE TABLE</c>'s <c>DATA_COMPRESSION … ON PARTITIONS (…)</c>
    /// clauses, applied to the rows' rowset — the heap, or the clustered key
    /// declared with the table: an unpartitioned table refuses them with Msg
    /// 7729 in the create table wording, or for a clustered key the create
    /// index wording followed by Msg 1750, and a number past the partitions is
    /// Msg 7722 (probed 2026-10-05 against SQL Server 2025). The
    /// <paramref name="xml"/> clauses of <c>XML_COMPRESSION</c> apply the same
    /// way (probed 2026-10-06).
    /// </summary>
    private static void ApplyTablePartitionCompression(HeapTable table, KeyConstraint? clusteredKey, byte wholeLevel, List<PartitionCompressionClause> clauses, bool xml)
    {
        if (table.Partitioning is not { } placement)
        {
            throw clusteredKey is null
                ? SimulatedSqlException.PartitionNumberOnUnpartitionedCreateTable(table.Name)
                : SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.PartitionNumberOnUnpartitionedCreate());
        }
        var levels = PartitionCompression.Apply(wholeLevel, null, placement.Fanout, clauses,
            number => SimulatedSqlException.InvalidPartitionNumber(number, table.Name, placement.Fanout), xml);
        if (clusteredKey is null && xml)
            table.HeapPartitionXmlCompression = levels;
        else if (clusteredKey is null)
            table.HeapPartitionDataCompression = levels;
        else if (xml)
            clusteredKey.PartitionXmlCompression = levels;
        else
            clusteredKey.PartitionDataCompression = levels;
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
    /// <c>DATA_CONSISTENCY_CHECK</c> decides whether an adopted history
    /// table's rows are checked (<see cref="CheckHistoryConsistency"/>). An
    /// option written twice is Msg 102 at severity 16 naming it upper-cased,
    /// and a history table named in other than two parts Msg 13539 (probed
    /// 2026-10-04 against SQL Server 2025).
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
        bool? dataConsistencyCheck = null;
        while (true)
        {
            switch (context.GetNextRequired())
            {
                case UnquotedString { ContextualKeyword: ContextualKeyword.History_Table }:
                    if (historyTable is not null)
                        throw SimulatedSqlException.SystemVersioningOptionRepeated("HISTORY_TABLE", 12);
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    historyTable = BatchContext.ParseObjectName(context);
                    // Only schema.table names a history table, and not a
                    // temporary one (probed 2026-10-04 against SQL Server 2025).
                    if (BatchContext.IsLocalTempName(historyTable.Value.Leaf) || BatchContext.IsGlobalTempName(historyTable.Value.Leaf))
                        throw SimulatedSqlException.HistoryTableInTempdb(historyTable.Value.Leaf);
                    if (historyTable.Value.Count != 2)
                        throw SimulatedSqlException.HistoryTableNotTwoPartName(historyTable.Value.ToString());
                    break;
                case UnquotedString { ContextualKeyword: ContextualKeyword.History_Retention_Period }:
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    (retentionPeriod, retentionUnit) = ParseHistoryRetentionPeriod(context);
                    break;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Data_Consistency_Check }:
                    if (dataConsistencyCheck is not null)
                        throw SimulatedSqlException.SystemVersioningOptionRepeated("DATA_CONSISTENCY_CHECK", 13);
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    dataConsistencyCheck = context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.On or Keyword.Off } toggle
                        ? toggle.Keyword == Keyword.On
                        : throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            if (context.GetNextRequired() is not Operator { Character: ',' })
                break;
        }
        return context.Token is Operator { Character: ')' }
            ? new SystemVersioningOptions(historyTable, retentionPeriod, retentionUnit, dataConsistencyCheck ?? true)
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
            // A number past int, or a fractional one, is out of range (probed
            // 2026-10-04 against SQL Server 2025).
            if (context.Token is Numeric other)
                throw SimulatedSqlException.IntegerValueOutOfRange(other.Source.ToString());
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
        // Real validates the count only after the unit parses, and keeps at
        // most 1,000 years — 365,242 days, 52,177 weeks, 12,000 months — which
        // it refuses while the batch compiles (probed 2026-10-04 against SQL
        // Server 2025).
        if (period is <= 0 or > int.MaxValue)
            throw SimulatedSqlException.InvalidHistoryRetentionPeriod(period.ToString(CultureInfo.InvariantCulture));
        var (limit, unitName) = unit switch
        {
            HistoryRetentionUnit.Day => (365242, "days"),
            HistoryRetentionUnit.Week => (52177, "weeks"),
            HistoryRetentionUnit.Month => (12000, "months"),
            _ => (1000, "years"),
        };
        return period <= limit
            ? ((int)period, unit)
            : throw SimulatedSqlException.HistoryRetentionTooBig((int)period, unitName);
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
        // A versioned base named as the history is "temporal table … in use"
        // (Msg 13566), another base's history Msg 13514 (probed 2026-10-04
        // against SQL Server 2025).
        if (candidate.SystemVersioning is not null)
            throw SimulatedSqlException.TemporalTableAlreadyInUse(QualifyTableName(candidate, context.CurrentDatabase));
        if (candidate.IsHistoryTable)
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
        // A computed column and a ROWGUIDCOL are refused ahead of the column
        // count (probed 2026-10-04 against SQL Server 2025).
        if (history.Columns.Any(c => c.Computed is not null))
            throw SimulatedSqlException.HistoryTableHasComputedColumn(qualifiedHistory);
        if (history.Columns.Any(c => c.IsRowGuidCol))
            throw SimulatedSqlException.HistoryTableHasRowGuidColumn(qualifiedHistory);
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
            // A period column's counterpart has its own refusal for being
            // nullable, and a sparse column must stay sparse (probed 2026-10-04
            // against SQL Server 2025).
            if (baseColumn.GeneratedAs.IsPeriod() && historyColumn.Nullable)
                throw SimulatedSqlException.HistoryPeriodColumnNullable(historyColumn.Name, qualifiedHistory, qualifiedBase);
            if (baseColumn.Nullable != historyColumn.Nullable)
                throw SimulatedSqlException.HistoryTableColumnNullabilityMismatch(baseColumn.Name, qualifiedBase, qualifiedHistory);
            if (baseColumn.IsSparse != historyColumn.IsSparse)
                throw SimulatedSqlException.HistoryColumnSparseMismatch(baseColumn.Name, qualifiedBase, qualifiedHistory);
        }
    }

    /// <summary>
    /// <c>DATA_CONSISTENCY_CHECK</c>, on unless the link said <c>OFF</c>: an
    /// adopted history table's rows may not end before they start (Msg 13541),
    /// end in the future — the maximum included (Msg 13543) — or overlap
    /// another row of the same key (Msg 13573, identical rows included),
    /// checked in that order across the whole table (probed 2026-10-04 against
    /// SQL Server 2025). A row ending as it starts passes.
    /// </summary>
    private static void CheckHistoryConsistency(ParserContext context, HeapTable baseTable, HeapTable history, SystemVersioningOptions options)
    {
        if (!options.DataConsistencyCheck || baseTable.PeriodColumns is not { } period)
            return;
        var rows = new List<SqlValue[]>();
        foreach (var bytes in history.Heap.EnumerateRows())
            rows.Add(DecodeFullRow(history, bytes));
        if (rows.Count == 0)
            return;
        var qualifiedHistory = QualifyTableName(history, context.CurrentDatabase);
        // The shape check has matched the two tables column for column.
        var (start, end) = period;
        foreach (var row in rows)
        {
            if (row[end].AsDateTime2 < row[start].AsDateTime2)
                throw SimulatedSqlException.HistoryEndBeforeStart(qualifiedHistory);
        }
        var now = context.Batch.CurrentStatement.UtcNow;
        foreach (var row in rows)
        {
            if (row[end].AsDateTime2 > now)
                throw SimulatedSqlException.HistoryEndInFuture(qualifiedHistory);
        }
        var collation = context.CurrentDatabase.Collation;
        var keyOrdinals = new List<int>();
        foreach (var ordinal in TableChangeTracking.KeyOrdinals(baseTable))
        {
            var match = Array.FindIndex(history.Columns, column => collation.Equals(column.Name, baseTable.Columns[ordinal].Name));
            if (match >= 0)
                keyOrdinals.Add(match);
        }
        var byKey = new Dictionary<SqlValueKey, List<(DateTime Start, DateTime End)>>();
        foreach (var row in rows)
        {
            var key = new SqlValueKey([.. keyOrdinals.Select(ordinal => row[ordinal])]);
            if (!byKey.TryGetValue(key, out var periods))
                byKey[key] = periods = [];
            periods.Add((row[start].AsDateTime2, row[end].AsDateTime2));
        }
        foreach (var (_, periods) in byKey)
        {
            periods.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            for (var i = 1; i < periods.Count; i++)
            {
                if (periods[i].Start < periods[i - 1].End)
                    throw SimulatedSqlException.HistoryOverlappingRecords(qualifiedHistory);
            }
        }
    }

    /// <summary>
    /// Enforces real's Msg 13765: a finite <c>HISTORY_RETENTION_PERIOD</c> is
    /// accepted only when the history table carries a clustered index whose
    /// <b>leading</b> key column is the period end column — the one real's
    /// aged-data cleanup task seeks through. An engine-built sibling always
    /// satisfies it (see <see cref="BuildHistoryTable"/>); an adopted table has
    /// to have been given one.
    /// </summary>
    /// <remarks>
    /// Probe-confirmed against SQL Server 2025: the state splits on whether the
    /// history table has a clustered index at all (state 1) or has one leading
    /// with another column (state 2); the leading column's ASC / DESC direction
    /// and every key column after the first are irrelevant, so a clustered
    /// index on <c>(PeriodEnd DESC)</c> alone passes while a nonclustered one on
    /// <c>(PeriodEnd, PeriodStart)</c> does not. <c>INFINITE</c> retention is
    /// accepted on a plain heap history table. All three entry points behave
    /// the same — CREATE TABLE adopting an existing sibling, ALTER TABLE
    /// turning versioning on, and a re-issue against an already-versioned base.
    /// </remarks>
    private static void RequireHistoryCleanupIndex(ParserContext context, HeapTable baseTable, HeapTable history, SystemVersioningOptions options)
    {
        if (options.RetentionUnit == HistoryRetentionUnit.Infinite)
            return;

        // A linked history table can only carry a clustered CREATE INDEX entry:
        // the shape validation refuses to adopt one holding a PRIMARY KEY or
        // UNIQUE constraint (Msg 13515), and an engine-built sibling declares
        // none, so HeapTable.KeyConstraints is empty on every table reaching
        // this gate.
        var clusteredLeadingOrdinal = -1;
        foreach (var index in history.Indexes)
        {
            // A clustered columnstore index serves as well (probed 2026-10-04
            // against SQL Server 2025), as the message itself suggests.
            if (index is { IsClustered: true, IsColumnstore: true })
                return;
            if (index.IsClustered && index.KeyColumns.Length > 0)
            {
                clusteredLeadingOrdinal = index.KeyColumns[0].ColumnOrdinal;
                break;
            }
        }
        if (clusteredLeadingOrdinal == baseTable.PeriodColumns!.Value.EndOrdinal)
            return;

        throw SimulatedSqlException.FiniteRetentionRequiresHistoryClusteredIndex(
            QualifyTableName(baseTable, context.CurrentDatabase),
            QualifyTableName(history, context.CurrentDatabase),
            state: clusteredLeadingOrdinal < 0 ? (byte)1 : (byte)2);
    }

    /// <summary>
    /// The parsed content of a <c>SYSTEM_VERSIONING = ON […]</c> clause.
    /// <see cref="HistoryTable"/> is null for the auto-named form; the
    /// retention pair defaults to the INFINITE (-1 / -1) every system-versioned
    /// table starts at.
    /// </summary>
    private readonly struct SystemVersioningOptions(MultiPartName? historyTable, int retentionPeriod, HistoryRetentionUnit retentionUnit, bool dataConsistencyCheck = true, bool on = true)
    {
        public readonly MultiPartName? HistoryTable = historyTable;

        public readonly int RetentionPeriod = retentionPeriod;

        public readonly HistoryRetentionUnit RetentionUnit = retentionUnit;

        /// <summary><c>DATA_CONSISTENCY_CHECK</c>, on unless written <c>OFF</c>: whether an adopted history table's rows are checked.</summary>
        public readonly bool DataConsistencyCheck = dataConsistencyCheck;

        /// <summary>False for a <c>CREATE TABLE</c>'s <c>SYSTEM_VERSIONING = OFF</c>, which links nothing.</summary>
        public readonly bool On = on;

        /// <summary>The auto-named, INFINITE-retention form: bare <c>= ON</c>.</summary>
        public static SystemVersioningOptions Bare => new(null, -1, HistoryRetentionUnit.Infinite);
    }

    /// <summary>
    /// Consumes a trailing <c>WITH (option = value, …)</c> index-options clause
    /// (the SSMS-emitted <c>PAD_INDEX</c> / <c>STATISTICS_NORECOMPUTE</c> /
    /// <c>ALLOW_ROW_LOCKS</c> / <c>ALLOW_PAGE_LOCKS</c> / etc. block), or the
    /// legacy unparenthesized <c>WITH FILLFACTOR = n</c>, when the cursor is
    /// sitting on a <c>WITH</c> keyword. It reports <c>IGNORE_DUP_KEY = ON</c>
    /// — the one option here with a semantic (see
    /// <c>docs/claude/constraints.md</c>) — and the <c>FILLFACTOR</c> /
    /// <c>PAD_INDEX</c> pair the catalog reports, a fill factor outside 1 to
    /// 100 being real's Msg 129 (probed 2026-09-26 against SQL Server 2025).
    /// Every other option is skipped parens-balanced without inspection, since
    /// none of them means anything in a heap-only store.
    /// No-op when the cursor isn't on <c>WITH</c>. Cursor on exit: first token
    /// past the closing <c>)</c>, or unchanged when no clause was present.
    /// </summary>
    internal static IndexOptions ParseOptionalIndexWithClause(ParserContext context, IndexOptionStatement statement = IndexOptionStatement.Unchecked, string? indexName = null, JsonRebuildRefusal? jsonRefusal = null, bool rangeIndex = false)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return default;
        // The legacy unparenthesized list: FILLFACTOR and, for CREATE INDEX,
        // the hypothetical index's STATISTICS_ONLY, in any order (probed
        // 2026-10-02 against SQL Server 2025).
        context.MoveNextRequired();
        if (statement == IndexOptionStatement.CreateIndex && context.Token is StringToken or ReservedKeyword { Keyword: Keyword.FillFactor })
            return ParseLegacyCreateIndexOptions(context);
        // A rebuild takes only the parenthesized list (probed 2026-10-05).
        if (statement is IndexOptionStatement.AlterIndexRebuild or IndexOptionStatement.RebuildColumnstoreIndex or IndexOptionStatement.RebuildJsonIndex
            && context.Token is not Operator { Character: '(' })
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        if (context.Token is ReservedKeyword { Keyword: Keyword.FillFactor } || IsLegacyStatisticsOnly(context, statement))
        {
            byte? legacyFillFactor = null;
            var legacyStatisticsOnly = false;
            while (true)
            {
                var isFillFactor = context.Token is ReservedKeyword { Keyword: Keyword.FillFactor };
                if (!isFillFactor && !IsLegacyStatisticsOnly(context, statement))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                if (isFillFactor)
                {
                    legacyFillFactor = ReadFillFactor(context);
                }
                else
                {
                    ReadStatisticsOnlyValue(context);
                    legacyStatisticsOnly = true;
                }
                if (context.GetNextOptional() is not Operator { Character: ',' })
                    break;
                context.MoveNextRequired();
            }
            return new IndexOptions(false, legacyFillFactor, null, statisticsOnly: legacyStatisticsOnly);
        }
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var ignoreDupKey = false;
        var dropExisting = false;
        byte? fillFactor = null;
        bool? padIndex = null;
        bool? allowRowLocks = null;
        bool? allowPageLocks = null;
        bool? optimizeForSequentialKey = null;
        bool? statisticsNoRecompute = null;
        var statisticsOnly = false;
        int? bucketCount = null;
        byte? dataCompression = null;
        bool? xmlCompression = null;
        var compressionOnPartitions = false;
        List<PartitionCompressionClause>? partitionCompressions = null;
        List<PartitionCompressionClause>? xmlPartitionCompressions = null;
        var statisticsIncremental = false;
        var ignoreDupKeyWritten = false;
        var depth = 1;
        // Two-token lookbehind over the balanced skip: the option name, then its
        // '='. Only a name at the list's own depth counts — a nested group is
        // another option's value list (`DATA_COMPRESSION = PAGE ON PARTITIONS
        // (1)`), never an option itself. Tracking state rather than consuming
        // ahead keeps the depth accounting correct even on malformed input.
        string? namedOption = null;
        var sawEquals = false;
        var expectName = true;
        var maxDuration = false;
        var resumable = false;
        var online = false;
        int? compressionDelay = null;
        bool? columnstoreArchive = null;
        var columnstore = statement is IndexOptionStatement.CreateColumnstoreIndex or IndexOptionStatement.RebuildColumnstoreIndex;
        while (depth > 0)
        {
            context.MoveNextRequired();
            if (expectName && statement != IndexOptionStatement.Unchecked && context.Token is StringToken or ReservedKeyword)
            {
                var name = context.Token.Source.ToString();
                // WAIT_AT_LOW_PRIORITY is legal only nested in ONLINE's own list (probed 2026-09-30).
                if (statement is IndexOptionStatement.AlterIndexRebuild or IndexOptionStatement.RebuildJsonIndex && name.Equals("WAIT_AT_LOW_PRIORITY", StringComparison.OrdinalIgnoreCase))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                var checkpoint = context.SaveCheckpoint();
                var valueToken = context.MoveNext() && context.Token is Operator { Character: '=' } && context.MoveNext() ? context.Token : null;
                context.RestoreCheckpoint(checkpoint);
                try
                {
                    CheckIndexOptionName(name, statement, indexName);
                }
                // Every statement but a rebuild follows an option no index
                // takes, given a number, with Msg 153 (probed 2026-09-26 and
                // 2026-10-07 against SQL Server 2025).
                catch (SimulatedSqlException unknown) when (unknown.Number == 155 && valueToken is Numeric
                    && !IndexOptionNames.Contains(name) && !name.Equals("STATISTICS_ONLY", StringComparison.OrdinalIgnoreCase)
                    && statement is not (IndexOptionStatement.AlterIndexRebuild or IndexOptionStatement.RebuildColumnstoreIndex or IndexOptionStatement.RebuildJsonIndex))
                {
                    throw SimulatedSqlException.Aggregate([unknown, SimulatedSqlException.InvalidUsageOfIndexOption(name)]);
                }
                if (statement is IndexOptionStatement.CreateIndex or IndexOptionStatement.AlterIndexRebuild or IndexOptionStatement.AlterTable)
                    ValidateIndexOptionValue(context, name, statement);
                if (jsonRefusal is { State: 0 } && valueToken is ReservedKeyword { Keyword: Keyword.On })
                    jsonRefusal.Note(name);
                maxDuration |= name.Equals("MAX_DURATION", StringComparison.OrdinalIgnoreCase);
                if (valueToken is ReservedKeyword { Keyword: Keyword.On })
                {
                    resumable |= name.Equals("RESUMABLE", StringComparison.OrdinalIgnoreCase);
                    online |= name.Equals("ONLINE", StringComparison.OrdinalIgnoreCase);
                }
            }
            expectName = depth == 1 && context.Token is Operator { Character: ',' };
            switch (context.Token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case Operator { Character: '=' } when namedOption is not null:
                    sawEquals = true;
                    continue;
                case ReservedKeyword { Keyword: Keyword.On or Keyword.Off } toggle when sawEquals:
                    var on = toggle.Keyword == Keyword.On;
                    switch (namedOption)
                    {
                        case "ALLOW_PAGE_LOCKS":
                            allowPageLocks = on;
                            break;
                        case "ALLOW_ROW_LOCKS":
                            allowRowLocks = on;
                            break;
                        case "DROP_EXISTING":
                            dropExisting = on;
                            break;
                        case "IGNORE_DUP_KEY":
                            ignoreDupKey = on;
                            ignoreDupKeyWritten = true;
                            break;
                        case "OPTIMIZE_FOR_SEQUENTIAL_KEY":
                            optimizeForSequentialKey = on;
                            break;
                        case "PAD_INDEX":
                            padIndex = on;
                            break;
                        case "STATISTICS_INCREMENTAL":
                            statisticsIncremental = on;
                            break;
                        case "STATISTICS_NORECOMPUTE":
                            statisticsNoRecompute = on;
                            break;
                        case "XML_COMPRESSION" when depth == 1:
                            // Named for the whole index and for partitions is
                            // Msg 7741 state 1 (probed 2026-10-06 against SQL
                            // Server 2025).
                            var xmlOnPartitions = FollowedByOnPartitions(context);
                            if (statement != IndexOptionStatement.Unchecked
                                && (xmlCompression is not null || (!xmlOnPartitions && xmlPartitionCompressions is not null)))
                            {
                                throw XmlCompressionTwice(statement);
                            }
                            if (xmlOnPartitions)
                                (xmlPartitionCompressions ??= []).Add(new PartitionCompressionClause(on ? (byte)1 : (byte)0, ReadOnPartitionsList(context)));
                            else
                                xmlCompression = on;
                            break;
                    }
                    break;
                case Numeric or Operator { Character: '-' } when sawEquals && namedOption == "FILLFACTOR":
                    fillFactor = ReadFillFactor(context);
                    break;
                case Numeric or Operator { Character: '-' } when sawEquals && namedOption == "BUCKET_COUNT":
                    bucketCount = ReadBucketCount(context);
                    break;
                case StringToken name when depth == 1 && name.Span.Equals("BUCKET_COUNT", StringComparison.OrdinalIgnoreCase):
                    // A key or index a table's definition declares without
                    // HASH refuses it as the batch compiles, memory-optimized
                    // table or not (probed 2026-10-07 against SQL Server 2025).
                    if (rangeIndex && statement is IndexOptionStatement.CreateTable or IndexOptionStatement.CreateType or IndexOptionStatement.AlterTable)
                        throw SimulatedSqlException.BucketCountOnRangeIndexWhileCompiling(name.Span.ToString());
                    namedOption = "BUCKET_COUNT";
                    continue;
                case Numeric or Operator { Character: '-' } when sawEquals && namedOption == "STATISTICS_ONLY":
                    ReadStatisticsOnlyValue(context);
                    statisticsOnly = true;
                    break;
                case StringToken name when depth == 1 && statement == IndexOptionStatement.CreateIndex && name.Span.Equals("STATISTICS_ONLY", StringComparison.OrdinalIgnoreCase):
                    namedOption = "STATISTICS_ONLY";
                    continue;
                case Operator { Character: '-' } when sawEquals && namedOption == "COMPRESSION_DELAY":
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                case Numeric delay when sawEquals && namedOption == "COMPRESSION_DELAY":
                    compressionDelay = delay.Value.AsInt32;
                    if (compressionDelay > 10080)
                        throw SimulatedSqlException.CompressionDelayOutOfRange(compressionDelay.Value);
                    // An optional MINUTE / MINUTES unit follows.
                    var afterDelay = context.SaveCheckpoint();
                    if (!(context.MoveNext() && context.Token is StringToken { Span: var unit }
                        && (unit.Equals("MINUTE", StringComparison.OrdinalIgnoreCase) || unit.Equals("MINUTES", StringComparison.OrdinalIgnoreCase))))
                    {
                        context.RestoreCheckpoint(afterDelay);
                    }
                    break;
                case StringToken level when sawEquals && namedOption == "DATA_COMPRESSION":
                    var archive = level.Span.Equals("COLUMNSTORE_ARCHIVE", StringComparison.OrdinalIgnoreCase);
                    var columnstoreLevel = archive || level.Span.Equals("COLUMNSTORE", StringComparison.OrdinalIgnoreCase);
                    if (columnstore)
                    {
                        columnstoreArchive = columnstoreLevel
                            ? archive
                            : throw SimulatedSqlException.ColumnstoreInvalidCompression(statement == IndexOptionStatement.CreateColumnstoreIndex ? (byte)15 : (byte)16);
                    }
                    // A rebuild's target is known only once the batch runs.
                    else if (columnstoreLevel && statement == IndexOptionStatement.AlterIndexRebuild && !context.Batch.IsSkipping)
                    {
                        throw SimulatedSqlException.RowstoreColumnstoreCompression();
                    }
                    else if (columnstoreLevel && statement == IndexOptionStatement.CreateIndex)
                    {
                        throw SimulatedSqlException.RowstoreColumnstoreCompression(15);
                    }
                    else if (!columnstoreLevel && depth == 1)
                    {
                        var compressionLevel = level.Span.Equals("ROW", StringComparison.OrdinalIgnoreCase) ? (byte)1
                            : level.Span.Equals("PAGE", StringComparison.OrdinalIgnoreCase) ? (byte)2
                            : level.Span.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? (byte)0
                            : statement == IndexOptionStatement.Unchecked ? (byte)0
                            : throw SimulatedSqlException.SyntaxErrorNear(context);
                        var onPartitions = FollowedByOnPartitions(context);
                        // Named twice for the whole object, or for the whole
                        // object and some partitions, is Msg 7711 (probed
                        // 2026-10-05 against SQL Server 2025).
                        if (statement != IndexOptionStatement.Unchecked
                            && (dataCompression is not null || (!onPartitions && partitionCompressions is not null)))
                        {
                            throw SimulatedSqlException.DataCompressionSpecifiedTwice();
                        }
                        if (onPartitions)
                            (partitionCompressions ??= []).Add(new PartitionCompressionClause(compressionLevel, ReadOnPartitionsList(context)));
                        else
                            dataCompression = compressionLevel;
                        compressionOnPartitions |= onPartitions;
                    }
                    break;
                case StringToken name when depth == 1 && name.Span.Equals("COMPRESSION_DELAY", StringComparison.OrdinalIgnoreCase):
                    namedOption = "COMPRESSION_DELAY";
                    continue;
                case StringToken name when depth == 1 && name.Span.Equals("DATA_COMPRESSION", StringComparison.OrdinalIgnoreCase):
                    namedOption = "DATA_COMPRESSION";
                    continue;
                case ReservedKeyword { Keyword: Keyword.FillFactor } when depth == 1:
                    namedOption = "FILLFACTOR";
                    continue;
                case StringToken name when depth == 1 && name.Span.Equals("IGNORE_DUP_KEY", StringComparison.OrdinalIgnoreCase):
                    namedOption = "IGNORE_DUP_KEY";
                    continue;
                case StringToken name when depth == 1 && name.Span.Equals("PAD_INDEX", StringComparison.OrdinalIgnoreCase):
                    namedOption = "PAD_INDEX";
                    continue;
                case StringToken name when depth == 1 && name.Span.Equals("DROP_EXISTING", StringComparison.OrdinalIgnoreCase):
                    namedOption = "DROP_EXISTING";
                    continue;
                case StringToken name when depth == 1 && name.Span.Equals("ALLOW_ROW_LOCKS", StringComparison.OrdinalIgnoreCase):
                    namedOption = "ALLOW_ROW_LOCKS";
                    continue;
                case StringToken name when depth == 1 && name.Span.Equals("ALLOW_PAGE_LOCKS", StringComparison.OrdinalIgnoreCase):
                    namedOption = "ALLOW_PAGE_LOCKS";
                    continue;
                case StringToken name when depth == 1 && name.Span.Equals("OPTIMIZE_FOR_SEQUENTIAL_KEY", StringComparison.OrdinalIgnoreCase):
                    namedOption = "OPTIMIZE_FOR_SEQUENTIAL_KEY";
                    continue;
                case StringToken name when depth == 1 && name.Span.Equals("STATISTICS_NORECOMPUTE", StringComparison.OrdinalIgnoreCase):
                    namedOption = "STATISTICS_NORECOMPUTE";
                    continue;
                case StringToken name when depth == 1 && name.Span.Equals("STATISTICS_INCREMENTAL", StringComparison.OrdinalIgnoreCase):
                    namedOption = "STATISTICS_INCREMENTAL";
                    continue;
                case StringToken name when depth == 1 && name.Span.Equals("XML_COMPRESSION", StringComparison.OrdinalIgnoreCase):
                    namedOption = "XML_COMPRESSION";
                    continue;
            }

            namedOption = null;
            sawEquals = false;
        }

        if (online)
            context.OnlineIndexBuildsParsed++;
        if (maxDuration && !resumable)
            throw SimulatedSqlException.MaxDurationRequiresResumable();
        // A resumable build has to be an online one, and a columnstore index
        // can't be resumable at all (probed 2026-09-26 against SQL Server 2025).
        if (resumable && statement is IndexOptionStatement.AlterIndexRebuild or IndexOptionStatement.RebuildJsonIndex or IndexOptionStatement.CreateTable or IndexOptionStatement.AlterTable && !online)
            throw SimulatedSqlException.ResumableRequiresOnline();
        if (resumable && statement is IndexOptionStatement.CreateIndex or IndexOptionStatement.CreateColumnstoreIndex)
        {
            if (!online)
                throw SimulatedSqlException.ResumableRequiresOnline();
            if (statement == IndexOptionStatement.CreateColumnstoreIndex)
                throw SimulatedSqlException.ColumnstoreResumable();
        }
        context.MoveNextOptional();
        return new IndexOptions(ignoreDupKey, fillFactor, padIndex, dropExisting, compressionDelay, columnstoreArchive, allowRowLocks, allowPageLocks, optimizeForSequentialKey,
            statisticsNoRecompute: statisticsNoRecompute, statisticsOnly: statisticsOnly, bucketCount: bucketCount,
            dataCompression: dataCompression, xmlCompression: xmlCompression, compressionOnPartitions: compressionOnPartitions, statisticsIncremental: statisticsIncremental,
            ignoreDupKeyWritten: ignoreDupKeyWritten, partitionCompressions: partitionCompressions, xmlPartitionCompressions: xmlPartitionCompressions);
    }

    /// <summary>
    /// Msg 7741 state 1 for <c>XML_COMPRESSION</c> named for the whole object
    /// and for partitions: alone from <c>CREATE INDEX</c>, followed by Msg 1750 state 0
    /// from a rebuild (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    private static SimulatedSqlException XmlCompressionTwice(IndexOptionStatement statement) =>
        statement == IndexOptionStatement.AlterIndexRebuild
            ? SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.XmlCompressionSpecifiedTwice(state: 1), state: 0)
            : SimulatedSqlException.XmlCompressionSpecifiedTwice(state: 1);

    /// <summary>
    /// Reads a hash index's <c>BUCKET_COUNT</c>, which must be a positive
    /// integer no larger than 2^30 (Msg 41303, raised compiling the batch —
    /// probed 2026-10-02 against SQL Server 2025), leaving the cursor on its
    /// last token.
    /// </summary>
    private static int ReadBucketCount(ParserContext context)
    {
        if (context.Token is Operator { Character: '-' })
        {
            context.MoveNextRequired();
            throw SimulatedSqlException.BucketCountOutOfRange();
        }
        return context.Token is Numeric { Value: var value } && value.Type.Category is SqlTypeCategory.Integer or SqlTypeCategory.Decimal
            && value.CoerceTo(SqlType.Float).AsDouble is >= 1 and <= 1073741824 and var count
            ? (int)count
            : throw SimulatedSqlException.BucketCountOutOfRange();
    }

    /// <summary>
    /// Consumes a <c>HASH</c> word after an index's or key's clustering, true
    /// when there was one.
    /// </summary>
    private static bool ConsumeHashKeyword(ParserContext context)
    {
        if (context.Token is not StringToken { Span: var word } || !word.Equals("HASH", StringComparison.OrdinalIgnoreCase))
            return false;
        context.MoveNextRequired();
        return true;
    }

    private static bool IsLegacyStatisticsOnly(ParserContext context, IndexOptionStatement statement) =>
        statement == IndexOptionStatement.CreateIndex && context.Token is StringToken { Span: var name } && name.Equals("STATISTICS_ONLY", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <c>STATISTICS_ONLY</c>'s integer value, a leading minus sign
    /// included, leaving the cursor on its last token.
    /// </summary>
    private static void ReadStatisticsOnlyValue(ParserContext context)
    {
        if (context.Token is Operator { Character: '-' })
            context.MoveNextRequired();
        if (context.Token is not Numeric)
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// The relational index options real's <c>WITH</c> clause knows. Each
    /// statement refuses a name outside them with Msg 155 naming itself, and
    /// some refuse ones inside them too (probed 2026-09-26 against SQL Server
    /// 2025).
    /// </summary>
    private static readonly FrozenSet<string> IndexOptionNames = new[]
    {
        "ALLOW_PAGE_LOCKS", "ALLOW_ROW_LOCKS", "COMPRESSION_DELAY", "DATA_COMPRESSION", "DROP_EXISTING", "FILLFACTOR",
        "IGNORE_DUP_KEY", "MAX_DURATION", "MAXDOP", "ONLINE", "OPTIMIZE_FOR_SEQUENTIAL_KEY", "PAD_INDEX", "RESUMABLE",
        "SORT_IN_TEMPDB", "STATISTICS_INCREMENTAL", "STATISTICS_NORECOMPUTE", "XML_COMPRESSION",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses an option name <paramref name="statement"/> doesn't take: an
    /// unknown one as that statement's option, a known one REBUILD or ALTER
    /// TABLE can't take as theirs, and <c>COMPRESSION_DELAY</c> — a columnstore
    /// option — with Msg 122 wherever it isn't refused by name.
    /// </summary>
    private static void CheckIndexOptionName(string name, IndexOptionStatement statement, string? indexName)
    {
        var known = IndexOptionNames.Contains(name);
        switch (statement)
        {
            case IndexOptionStatement.CreateIndex:
                if (!known && !name.Equals("STATISTICS_ONLY", StringComparison.OrdinalIgnoreCase))
                    throw SimulatedSqlException.UnrecognizedIndexOption(name, "CREATE INDEX");
                break;
            case IndexOptionStatement.AlterIndexRebuild or IndexOptionStatement.RebuildJsonIndex:
                // BUCKET_COUNT reaches the target, which refuses it as a
                // memory-optimized table's index (Msg 10794).
                if (!known && !name.Equals("BUCKET_COUNT", StringComparison.OrdinalIgnoreCase))
                    throw SimulatedSqlException.UnrecognizedIndexOption(name, "ALTER INDEX");
                if (name.Equals("DROP_EXISTING", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("OPTIMIZE_FOR_SEQUENTIAL_KEY", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("COMPRESSION_DELAY", StringComparison.OrdinalIgnoreCase))
                {
                    throw SimulatedSqlException.UnrecognizedIndexOption(name, "ALTER INDEX REBUILD");
                }
                break;
            case IndexOptionStatement.AlterTable:
                RefuseStatisticsOnly(name, "ALTER TABLE");
                if ((!known && !name.Equals("BUCKET_COUNT", StringComparison.OrdinalIgnoreCase)) || name.Equals("DROP_EXISTING", StringComparison.OrdinalIgnoreCase))
                    throw SimulatedSqlException.UnrecognizedIndexOption(name, "ALTER TABLE");
                break;
            case IndexOptionStatement.CreateTable:
                // A table's keys and inline indexes are built with the table,
                // so the options of a build on its own don't apply.
                RefuseStatisticsOnly(name, "CREATE TABLE");
                if ((!known && !name.Equals("BUCKET_COUNT", StringComparison.OrdinalIgnoreCase))
                    || name.Equals("DROP_EXISTING", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("MAXDOP", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("ONLINE", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("SORT_IN_TEMPDB", StringComparison.OrdinalIgnoreCase))
                {
                    throw SimulatedSqlException.UnrecognizedIndexOption(name, "CREATE TABLE");
                }
                break;
            case IndexOptionStatement.CreateType:
                // A table type's keys take IGNORE_DUP_KEY alone, real naming
                // three of the others in lower case whatever was written.
                RefuseStatisticsOnly(name, "CREATE TYPE");
                if (name.Equals("COMPRESSION_DELAY", StringComparison.OrdinalIgnoreCase))
                    throw SimulatedSqlException.Aggregate([SimulatedSqlException.CompressionDelayRequiresColumnstore(), SimulatedSqlException.UnrecognizedIndexOption(name, "CREATE TYPE")]);
                if (!name.Equals("IGNORE_DUP_KEY", StringComparison.OrdinalIgnoreCase) && !name.Equals("BUCKET_COUNT", StringComparison.OrdinalIgnoreCase))
                {
                    throw SimulatedSqlException.UnrecognizedIndexOption(
                        name.Equals("FILLFACTOR", StringComparison.OrdinalIgnoreCase) ? "fillfactor"
                            : name.Equals("DATA_COMPRESSION", StringComparison.OrdinalIgnoreCase) ? "data_compression"
                            : name.Equals("XML_COMPRESSION", StringComparison.OrdinalIgnoreCase) ? "xml_compression"
                            : name,
                        "CREATE TYPE");
                }
                return;
            case IndexOptionStatement.CreateColumnstoreIndex or IndexOptionStatement.RebuildColumnstoreIndex:
                var creating = statement == IndexOptionStatement.CreateColumnstoreIndex;
                if (!known)
                    throw SimulatedSqlException.UnrecognizedIndexOption(name, creating ? "CREATE INDEX" : "ALTER INDEX");
                var upper = name.ToUpperInvariant();
                if (ColumnstoreRefusedOptions.Contains(upper))
                    throw creating ? SimulatedSqlException.ColumnstoreIndexOption(upper) : SimulatedSqlException.ColumnstoreRebuildOption(upper);
                if (ColumnstoreRefusedLockOptions.Contains(upper))
                    throw creating ? SimulatedSqlException.ColumnstoreIndexLockOption(upper) : SimulatedSqlException.ColumnstoreRebuildLockOption(upper);
                if (upper == "XML_COMPRESSION")
                    throw SimulatedSqlException.ColumnstoreXmlCompression(indexName ?? "");
                if (!creating && upper is "DROP_EXISTING" or "COMPRESSION_DELAY")
                    throw SimulatedSqlException.UnrecognizedIndexOption(name, "ALTER INDEX REBUILD");
                return;
        }
        if (name.Equals("COMPRESSION_DELAY", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.CompressionDelayRequiresColumnstore();
    }

    /// <summary>
    /// <c>STATISTICS_ONLY</c>, a hypothetical <c>CREATE INDEX</c>'s option,
    /// anywhere else: a syntax error on the name followed by its Msg 155
    /// (probed 2026-10-07 against SQL Server 2025).
    /// </summary>
    private static void RefuseStatisticsOnly(string name, string statementName)
    {
        if (name.Equals("STATISTICS_ONLY", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.Aggregate([SimulatedSqlException.SyntaxErrorNear(name, state: 1), SimulatedSqlException.UnrecognizedIndexOption(name, statementName)]);
    }

    /// <summary>
    /// The first option a <c>REBUILD</c> of a JSON index refuses when it is turned on, noted while the
    /// clause parses and raised once the index has resolved (probed 2026-09-30 against SQL Server 2025:
    /// a partition number is refused ahead of it, and the same options off are accepted).
    /// </summary>
    internal sealed class JsonRebuildRefusal
    {
        public byte State;
        public string Name = "";

        public void Note(string written)
        {
            // Two names are reported in lower case and the rest in capitals, whatever was written.
            (State, Name) = written switch
            {
                _ when written.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) => ((byte)37, "ONLINE"),
                _ when written.Equals("IGNORE_DUP_KEY", StringComparison.OrdinalIgnoreCase) => ((byte)36, "ignore_dup_key"),
                _ when written.Equals("STATISTICS_INCREMENTAL", StringComparison.OrdinalIgnoreCase) => ((byte)40, "statistics_incremental"),
                _ when written.Equals("SORT_IN_TEMPDB", StringComparison.OrdinalIgnoreCase) => ((byte)42, "SORT_IN_TEMPDB"),
                _ when written.Equals("STATISTICS_NORECOMPUTE", StringComparison.OrdinalIgnoreCase) => ((byte)43, "STATISTICS_NORECOMPUTE"),
                _ when written.Equals("XML_COMPRESSION", StringComparison.OrdinalIgnoreCase) => ((byte)45, "XML_COMPRESSION"),
                _ => ((byte)0, ""),
            };
        }
    }

    /// <summary>Which statement an index <c>WITH</c> clause belongs to, for the option names it validates.</summary>
    internal enum IndexOptionStatement
    {
        /// <summary>A clause whose statement's option set isn't modeled: every name is accepted.</summary>
        Unchecked,
        CreateIndex,
        AlterIndexRebuild,
        AlterTable,

        /// <summary>A key or inline index in a <c>CREATE TABLE</c>, a table variable's or a function's return table included.</summary>
        CreateTable,

        /// <summary>A key or inline index in a <c>CREATE TYPE … AS TABLE</c>.</summary>
        CreateType,
        CreateColumnstoreIndex,
        RebuildColumnstoreIndex,
        RebuildJsonIndex,
    }

    /// <summary>
    /// The rowstore options a columnstore index refuses by name — Msg 35317
    /// creating it, Msg 35327 rebuilding it (probed 2026-09-26 against SQL
    /// Server 2025).
    /// </summary>
    private static readonly FrozenSet<string> ColumnstoreRefusedOptions = new[]
    {
        "FILLFACTOR", "IGNORE_DUP_KEY", "PAD_INDEX", "SORT_IN_TEMPDB", "STATISTICS_INCREMENTAL", "STATISTICS_NORECOMPUTE",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The locking options a columnstore index refuses — Msg 35318 creating it, Msg 35328 rebuilding or setting them.</summary>
    private static readonly FrozenSet<string> ColumnstoreRefusedLockOptions = new[]
    {
        "ALLOW_PAGE_LOCKS", "ALLOW_ROW_LOCKS", "OPTIMIZE_FOR_SEQUENTIAL_KEY",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Reads a <c>FILLFACTOR</c> value at the cursor (a number, or a minus
    /// sign and one), leaving the cursor on its last token; real refuses
    /// anything outside 1 to 100 with Msg 129.
    /// </summary>
    private static byte ReadFillFactor(ParserContext context)
    {
        var negative = context.Token is Operator { Character: '-' };
        if (negative)
            context.MoveNextRequired();
        var value = ReadIntegerOptionLiteral(context);
        if (negative)
            value = -value;
        return value is < 1 or > 100
            ? throw SimulatedSqlException.FillFactorOutOfRange(value)
            : (byte)value;
    }

    /// <summary>
    /// <c>CREATE INDEX</c>'s legacy unparenthesized option list, the cursor on
    /// its first name: each item a bare <c>PAD_INDEX</c>, <c>IGNORE_DUP_KEY</c>,
    /// <c>SORT_IN_TEMPDB</c>, <c>STATISTICS_NORECOMPUTE</c> or
    /// <c>DROP_EXISTING</c>, or a <c>FILLFACTOR</c> / <c>STATISTICS_ONLY</c>
    /// given a number. Real refuses any other bare name with Msg 153 naming the
    /// CREATE INDEX statement, any other name given a number with Msg 153 naming
    /// the INDEX statement, a non-number value as a syntax error and
    /// <c>ALLOW_DUP_ROW</c> with Msg 1070 (probed 2026-10-05 against SQL Server
    /// 2025).
    /// </summary>
    private static IndexOptions ParseLegacyCreateIndexOptions(ParserContext context)
    {
        byte? fillFactor = null;
        bool? padIndex = null;
        bool? statisticsNoRecompute = null;
        bool ignoreDupKey = false, dropExisting = false, statisticsOnly = false;
        // An identifier is at most 128 characters.
        Span<char> buffer = stackalloc char[128];
        while (true)
        {
            if (context.Token is not (StringToken or ReservedKeyword { Keyword: Keyword.FillFactor }))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var name = context.Token.Source.ToString();
            var upper = buffer[..Math.Min(name.Length, buffer.Length)];
            _ = name.AsSpan(0, upper.Length).ToUpperInvariant(upper);
            if (context.GetNextOptional() is Operator { Character: '=' })
            {
                context.MoveNextRequired();
                var negative = context.Token is Operator { Character: '-' };
                if (negative)
                    context.MoveNextRequired();
                if (context.Token is not Numeric)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                switch (upper)
                {
                    case "FILLFACTOR":
                        var value = ReadIntegerOptionLiteral(context);
                        fillFactor = (negative ? -value : value) is var written and >= 1 and <= 100
                            ? (byte)written
                            : throw SimulatedSqlException.FillFactorOutOfRange(negative ? -value : value);
                        break;
                    case "STATISTICS_ONLY":
                        statisticsOnly = true;
                        break;
                    default:
                        throw SimulatedSqlException.InvalidUsageOfIndexOption(name);
                }
                context.MoveNextOptional();
            }
            else
            {
                switch (upper)
                {
                    case "ALLOW_DUP_ROW":
                        throw SimulatedSqlException.IndexOptionNoLongerSupported(name);
                    case "DROP_EXISTING":
                        dropExisting = true;
                        break;
                    case "IGNORE_DUP_KEY":
                        ignoreDupKey = true;
                        break;
                    case "PAD_INDEX":
                        padIndex = true;
                        break;
                    case "SORT_IN_TEMPDB":
                        break;
                    case "STATISTICS_NORECOMPUTE":
                        statisticsNoRecompute = true;
                        break;
                    default:
                        throw SimulatedSqlException.InvalidUsageOfIndexOption(name, "CREATE INDEX");
                }
            }
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        return new IndexOptions(ignoreDupKey, fillFactor, padIndex, dropExisting, statisticsNoRecompute: statisticsNoRecompute, statisticsOnly: statisticsOnly);
    }

    /// <summary>
    /// Reads the integer literal an index option's value slot takes, the cursor
    /// on it: a decimal or one past <c>int</c> is Msg 1080 echoing it as
    /// written, and anything else — a float, a string, a binary — is Msg 102
    /// near it (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    private static int ReadIntegerOptionLiteral(ParserContext context) => context.Token switch
    {
        Numeric { Value: { IsNull: false } number } when number.Type == SqlType.Int32 => number.AsInt32,
        Numeric { Value: { IsNull: false } number } when number.Type.Category == SqlTypeCategory.Decimal =>
            throw SimulatedSqlException.IntegerValueOutOfRange(context.Token.Source.ToString()),
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    /// <summary>
    /// Reads the <c>ON PARTITIONS ( n [ TO m ] [, …] )</c> list after a
    /// compression level, the cursor on the level and left on the list's
    /// closing parenthesis.
    /// </summary>
    private static List<(long Low, long High)> ReadOnPartitionsList(ParserContext context)
    {
        context.MoveNextRequired();
        context.MoveNextRequired();
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        return ReadPartitionRanges(context);
    }

    /// <summary>
    /// Reads a parenthesized list of partition numbers and <c>n TO m</c>
    /// ranges, the cursor on its opening parenthesis and left on its closing one.
    /// </summary>
    internal static List<(long Low, long High)> ReadPartitionRanges(ParserContext context)
    {
        var ranges = new List<(long Low, long High)>();
        while (true)
        {
            var low = context.GetNextRequired() is Numeric { Value: { IsNull: false } lowValue } ? lowValue.CoerceTo(SqlType.BigInt).AsInt64 : throw SimulatedSqlException.SyntaxErrorNear(context);
            var high = low;
            if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.To })
            {
                high = context.GetNextRequired() is Numeric { Value: { IsNull: false } highValue } ? highValue.CoerceTo(SqlType.BigInt).AsInt64 : throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
            ranges.Add((low, high));
            if (context.Token is Operator { Character: ')' })
                return ranges;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    private static bool FollowedByOnPartitions(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var onPartitions = context.MoveNext() && context.Token is ReservedKeyword { Keyword: Keyword.On }
            && context.MoveNext() && context.Token is StringToken { Span: var word } && word.Equals("PARTITIONS", StringComparison.OrdinalIgnoreCase);
        context.RestoreCheckpoint(checkpoint);
        return onPartitions;
    }

    /// <summary>
    /// Validates a relational index option's value the way real's grammar does,
    /// the cursor on the option's name and restored on exit (probed 2026-10-05
    /// against SQL Server 2025): an <c>ON</c> / <c>OFF</c> option given a
    /// number is Msg 153 naming it — but for <c>ONLINE</c>, <c>RESUMABLE</c>,
    /// <c>XML_COMPRESSION</c> and <c>IGNORE_DUP_KEY</c>, whose grammar reads a
    /// keyword there, Msg 102 near the number; <c>FILLFACTOR</c> and
    /// <c>MAXDOP</c> take an integer literal (<see cref="ReadIntegerOptionLiteral"/>),
    /// <c>MAXDOP</c> within 0 to 32767 (Msg 304); <c>DATA_COMPRESSION</c> takes a
    /// level word; and <c>ONLINE = OFF</c> takes no low-priority list, which
    /// <c>CREATE INDEX</c> refuses as an unrecognized option.
    /// </summary>
    private static void ValidateIndexOptionValue(ParserContext context, string name, IndexOptionStatement statement)
    {
        var checkpoint = context.SaveCheckpoint();
        if (!(context.MoveNext() && context.Token is Operator { Character: '=' } && context.MoveNext()))
        {
            context.RestoreCheckpoint(checkpoint);
            return;
        }
        Span<char> upper = stackalloc char[name.Length];
        _ = name.AsSpan().ToUpperInvariant(upper);
        switch (upper)
        {
            case "ALLOW_PAGE_LOCKS" or "ALLOW_ROW_LOCKS" or "DROP_EXISTING" or "OPTIMIZE_FOR_SEQUENTIAL_KEY" or "PAD_INDEX"
                or "SORT_IN_TEMPDB" or "STATISTICS_INCREMENTAL" or "STATISTICS_NORECOMPUTE":
                if (context.Token is Numeric)
                    throw SimulatedSqlException.InvalidUsageOfIndexOption(name);
                if (context.Token is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                break;
            case "IGNORE_DUP_KEY" or "ONLINE" or "RESUMABLE" or "XML_COMPRESSION":
                if (context.Token is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off } toggle)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (toggle.Keyword == Keyword.Off && statement == IndexOptionStatement.CreateIndex && upper is "ONLINE"
                    && context.MoveNext() && context.Token is Operator { Character: '(' } && context.MoveNext() && context.Token is StringToken lowPriority)
                {
                    throw SimulatedSqlException.UnrecognizedIndexOption(lowPriority.Source.ToString().ToUpperInvariant(), "CREATE INDEX");
                }
                break;
            case "FILLFACTOR":
                if (context.Token is Operator { Character: '-' })
                    context.MoveNextRequired();
                _ = ReadIntegerOptionLiteral(context);
                break;
            case "MAXDOP":
                var negative = context.Token is Operator { Character: '-' };
                if (negative)
                    context.MoveNextRequired();
                var maxdop = ReadIntegerOptionLiteral(context);
                if (negative || maxdop > 32767)
                    throw SimulatedSqlException.IndexMaxDopOutOfRange((negative ? "-" : "") + context.Token.Source.ToString());
                break;
            case "DATA_COMPRESSION":
                if (context.Token is not StringToken { Span: var level }
                    || !(level.Equals("NONE", StringComparison.OrdinalIgnoreCase) || level.Equals("ROW", StringComparison.OrdinalIgnoreCase)
                        || level.Equals("PAGE", StringComparison.OrdinalIgnoreCase) || level.Equals("COLUMNSTORE", StringComparison.OrdinalIgnoreCase)
                        || level.Equals("COLUMNSTORE_ARCHIVE", StringComparison.OrdinalIgnoreCase)))
                {
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                break;
        }
        context.RestoreCheckpoint(checkpoint);
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
            // A computed column becomes a plain one holding the value the base
            // row computed, and a sparse one stays sparse (probed 2026-10-04
            // against SQL Server 2025).
            historyColumns[i] = new HeapColumn(
                pc.Name,
                pc.Type,
                pc.MaxLength,
                nullable: pc.Nullable,
                identity: null,
                defaultExpression: null,
                generatedAs: GeneratedAlwaysAsRow.None,
                // A history sibling's period columns are never hidden, whatever
                // the parent's (probed 2026-10-02 against SQL Server 2025).
                isHidden: false,
                collation: pc.Collation,
                spelledNumeric: pc.SpelledNumeric)
            {
                AliasType = pc.AliasType,
                IsSparse = pc.IsSparse,
            };
        }
        var history = new HeapTable(
            historyLeaf,
            historyColumns,
            context.CurrentDatabase.AllocateObjectId(),
            historySchemaId,
            context.Batch.CurrentStatement.UtcNow,
            periodColumns: parent.PeriodColumns)
        {
            IsHistoryTable = true,
            PeriodInheritedFromBase = true,
            UsesAnsiNulls = context.Batch.Connection.AnsiNulls,
            PageCompressed = !Array.Exists(parent.Columns, static column => column.IsSparse),
        };
        VersionStore.NoteDefinitionChange(context.Batch, history);
        // Real gives every engine-built history table a non-unique clustered
        // index named ix_<history leaf> on (period end, period start) — in that
        // order, so aged-version cleanup seeks on the end column — and that
        // index is what a finite HISTORY_RETENTION_PERIOD requires (Msg 13765).
        // Probe-confirmed on both the CREATE TABLE and the ALTER TABLE …
        // SET (SYSTEM_VERSIONING = ON) sibling-building paths, and for an
        // auto-named sibling alike (ix_MSSQL_TemporalHistoryFor_<id>).
        var (startOrdinal, endOrdinal) = parent.PeriodColumns!.Value;
        history.Indexes.Add(new StoredIndex(
            $"ix_{historyLeaf}",
            context.CurrentDatabase.AllocateObjectId(),
            isUnique: false,
            isClustered: true,
            [
                new IndexKeyColumn(history.StorageOrdinals[endOrdinal], endOrdinal, isDescending: false),
                new IndexKeyColumn(history.StorageOrdinals[startOrdinal], startOrdinal, isDescending: false),
            ],
            [],
            [],
            filter: null,
            filterDefinition: null,
            options: default));
        return history;
    }

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
    /// <remarks>
    /// <para>
    /// Cursor on entry: the opening <c>(</c> of the
    /// column list. Cursor on exit: the closing <c>)</c> (not consumed — the
    /// caller consumes it). Returns <c>false</c> if the list is structurally
    /// malformed (the caller surfaces this as a parse-time syntax error).
    /// </para>
    /// <para>
    /// Two-pass column resolution: regular columns build a <see cref="HeapColumn"/>
    /// during pass 1; computed columns leave a placeholder entry plus an entry
    /// in <paramref name="pendingComputed"/> to be resolved after the column
    /// list is closed (so forward column references inside computed
    /// expressions can bind). Identity / rowversion validation also fires
    /// during pass 1.
    /// </para>
    /// </remarks>
    private static bool ParseColumnList(
        ParserContext context,
        string tableName,
        bool isTableVariable,
        bool isTableType,
        List<HeapColumn?> heapColumns,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)> pendingKeys,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition, bool NotForReplication)> pendingChecks,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed,
        List<(string StartCol, string EndCol)>? pendingPeriod = null,
        List<PendingForeignKey>? pendingForeignKeys = null,
        List<PendingInlineIndex>? pendingIndexes = null,
        List<PendingEdgeConstraint>? pendingEdgeConstraints = null)
    {
        // A computed column or CHECK reading a CLR type column's member
        // (`x AS p.X`, `CHECK (p.Y > 0)`) or a spatial column's property
        // (`lat AS loc.Lat`) binds the member off the column's declared type:
        // a CLR type's as far as the list has parsed, a spatial one's anywhere
        // in it. A qualifier naming the table itself is the column reading.
        bool parsed;
        var collation = context.CurrentDatabase.Collation;
        var spatialColumns = ScanSpatialColumns(context);
        SqlType? DeclaredType(MultiPartName name)
        {
            if (name.Count != 1 || collation.Equals(name.Leaf, tableName))
                return null;
            if (heapColumns.Find(column => column is not null && collation.Equals(column.Name, name.Leaf)) is { } declared)
                return declared.Type;
            return spatialColumns?.Find(column => collation.Equals(column.Name, name.Leaf)).Type;
        }
        using (ParserScope.Enter(ref context.DeclaredColumnTypes, DeclaredType))
            parsed = ParseColumnListBody(context, tableName, isTableVariable, isTableType, heapColumns, pendingKeys, pendingChecks, pendingComputed, pendingPeriod, pendingForeignKeys, pendingIndexes, pendingEdgeConstraints);
        if (parsed && pendingIndexes is not null && pendingIndexes.Exists(static index => index.IsClustered))
            YieldClusteringToInlineIndex(tableName, pendingKeys);
        return parsed;
    }

    /// <summary>
    /// The spatial columns a table list declares anywhere in it, read ahead of
    /// the list: a computed column or CHECK may read the property of a column
    /// declared after it (<c>lat AS loc.Lat, loc geography</c>), and an inline
    /// CHECK its own column's (probed 2026-10-06 against SQL Server 2025).
    /// Entered and left on the list's <c>(</c>; null when there are none.
    /// </summary>
    private static List<(string Name, SqlType? Type)>? ScanSpatialColumns(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        List<(string Name, SqlType? Type)>? found = null;
        var depth = 0;
        var elementStart = true;
        while (context.GetNextOptional() is { } token)
        {
            switch (token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' } when depth == 0:
                    context.RestoreCheckpoint(checkpoint);
                    return found;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case Operator { Character: ',' } when depth == 0:
                    elementStart = true;
                    continue;
                case Name column when depth == 0 && elementStart:
                    var afterName = context.SaveCheckpoint();
                    if (context.GetNextOptional() is Name { Value: var typeName } && BuiltInToken.Equals(typeName, "GEOGRAPHY"))
                        (found ??= []).Add((column.Value, SqlType.Geography));
                    else if (context.Token is Name { Value: var otherName } && BuiltInToken.Equals(otherName, "GEOMETRY"))
                        (found ??= []).Add((column.Value, SqlType.Geometry));
                    else
                        context.RestoreCheckpoint(afterName);
                    break;
            }
            elementStart = false;
        }
        context.RestoreCheckpoint(checkpoint);
        return found;
    }

    /// <summary>
    /// An inline <c>CLUSTERED</c> index takes the clustering a PRIMARY KEY
    /// would by default, leaving the key nonclustered, while a key written
    /// <c>CLUSTERED</c> beside it is Msg 8112 state 0 (probed 2026-10-06
    /// against SQL Server 2025, for a table, a table variable and a
    /// multi-statement function's return table).
    /// </summary>
    private static void YieldClusteringToInlineIndex(
        string tableName,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)> pendingKeys)
    {
        for (var i = 0; i < pendingKeys.Count; i++)
        {
            switch (pendingKeys[i].Clustered)
            {
                case true:
                    throw SimulatedSqlException.MultipleClusteredConstraints(tableName, state: 0);
                case null when pendingKeys[i].Kind == KeyConstraintKind.PrimaryKey:
                    pendingKeys[i] = pendingKeys[i] with { Clustered = false };
                    break;
            }
        }
    }

    private static bool ParseColumnListBody(
        ParserContext context,
        string tableName,
        bool isTableVariable,
        bool isTableType,
        List<HeapColumn?> heapColumns,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)> pendingKeys,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition, bool NotForReplication)> pendingChecks,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed,
        List<(string StartCol, string EndCol)>? pendingPeriod,
        List<PendingForeignKey>? pendingForeignKeys,
        List<PendingInlineIndex>? pendingIndexes,
        List<PendingEdgeConstraint>? pendingEdgeConstraints)
    {
        var identityCount = 0;
        // A node or edge table's internal columns arrive already in the list.
        var leadingColumns = heapColumns.Count;
        // Parallel to heapColumns: true when the user wrote an explicit
        // `NULL` declaration on this column. Required at end-of-list to
        // disambiguate table-level PK promotion (probe-confirmed: real SQL
        // Server promotes bare-nullable columns referenced by a table-level
        // PK to NOT NULL; only an explicit `NULL` declaration raises Msg
        // 8111 in that context). Inline PK already handles this inside the
        // parse loop.
        var explicitNull = new List<bool>(Enumerable.Repeat(false, leadingColumns));
        do
        {
            context.MoveNextRequired();

            // Real tolerates one trailing comma before the closing paren of a
            // CREATE TABLE element list — after a column or a table-level
            // constraint alike — and nowhere else. Not `DECLARE @t TABLE`, not
            // `CREATE TYPE … AS TABLE` (both share this parser and both refuse
            // it), not an INSERT column list, a VALUES list, an index key list,
            // a constraint's own column list, or `ALTER TABLE … ADD`. Two
            // commas and a leading comma stay Msg 102 as well: only the single
            // trailing one is admitted, which is why this tests for the paren
            // rather than looping. All probe-confirmed against SQL Server 2025.
            if (!isTableVariable && !isTableType && context.Token is Operator { Character: ')' } && heapColumns.Count > leadingColumns)
                break;

            // Table-level constraint: `[CONSTRAINT name] PRIMARY KEY | UNIQUE (cols)`
            // or `[CONSTRAINT name] CHECK (predicate)`. Forks before the
            // column path because PRIMARY/UNIQUE/CHECK/CONSTRAINT are reserved
            // keywords and would otherwise collide with the leading-name
            // expectation. Inside DECLARE @t TABLE the `CONSTRAINT` form
            // raises Msg 102 (probe-confirmed: real SQL Server's grammar
            // disallows named constraints in table-variable declarations).
            if (context.Token is ReservedKeyword { Keyword: Keyword.Constraint } && (isTableVariable || isTableType))
                throw SimulatedSqlException.SyntaxErrorNearKeyword("CONSTRAINT", state: 2);
            // Bare table-level FOREIGN KEY in a table variable / table type:
            // Msg 102 (probe-confirmed grammar disallows FKs in those contexts).
            if (context.Token is ReservedKeyword { Keyword: Keyword.Foreign } && (isTableVariable || isTableType))
                throw SimulatedSqlException.SyntaxErrorNearKeyword("FOREIGN");
            if (context.Token is ReservedKeyword { Keyword: Keyword.Constraint or Keyword.Primary or Keyword.Unique or Keyword.Check or Keyword.Foreign })
            {
                ParseTableLevelConstraint(context, heapColumns, pendingKeys, pendingChecks, pendingComputed, pendingForeignKeys, pendingEdgeConstraints);
                continue;
            }
            if (pendingEdgeConstraints is not null && IsEdgeConstraintAhead(context))
            {
                pendingEdgeConstraints.Add(ParseEdgeConstraint(context, name: null));
                continue;
            }

            // Table-level inline index: `INDEX name [UNIQUE] [CLUSTERED | NONCLUSTERED]
            // (col [ASC | DESC], …)`, for the callers that supply
            // pendingIndexes; the others leave it to the column path, which
            // rejects the INDEX keyword.
            if (context.Token is ReservedKeyword { Keyword: Keyword.Index } && pendingIndexes is not null)
            {
                var tableLevelIndex = ParseTableLevelInlineIndex(context, tableName, isTableVariable || isTableType);
                tableLevelIndex.KeysBefore = pendingKeys.Count;
                pendingIndexes.Add(tableLevelIndex);
                continue;
            }

            // Table-level PERIOD FOR SYSTEM_TIME (startCol, endCol). Only
            // legal inside CREATE TABLE; DECLARE @t TABLE and CREATE TYPE …
            // AS TABLE reject (probe-confirmed: real SQL Server's grammar
            // doesn't expose the period declaration in those contexts).
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Period })
            {
                // A table variable's period is refused by name (probed
                // 2026-10-04 against SQL Server 2025); a second period is
                // recorded for ResolvePeriodColumns' Msg 13508.
                if (isTableVariable)
                    throw SimulatedSqlException.TableVariableWithPeriod();
                if (isTableType || pendingPeriod is null)
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
                    heapColumns[ordinal] = WithNotNull(column);
            }
        }

        RejectRepeatedColumnNames(context, tableName, heapColumns, pendingComputed);
        return context.Token is Operator { Character: ')' };
    }

    /// <summary>
    /// Raises Msg 2705 for a column name the list repeats, naming the later
    /// spelling — state 2 when a computed column is one of the pair, 3
    /// otherwise (probed 2026-09-24 against SQL Server 2025). A computed
    /// column's slot in <paramref name="heapColumns"/> is still a placeholder
    /// here, so its name comes from <paramref name="pendingComputed"/>.
    /// </summary>
    private static void RejectRepeatedColumnNames(
        ParserContext context,
        string tableName,
        List<HeapColumn?> heapColumns,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed)
    {
        var names = new string[heapColumns.Count];
        var computed = new bool[heapColumns.Count];
        for (var i = 0; i < heapColumns.Count; i++)
            names[i] = heapColumns[i]?.Name ?? "";
        foreach (var (index, name, _, _, _, _) in pendingComputed)
        {
            names[index] = name;
            computed[index] = true;
        }
        var collation = context.Batch.CurrentDatabase.Collation;
        for (var i = 1; i < names.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (collation.Equals(names[i], names[j]))
                    throw SimulatedSqlException.DuplicateColumnInTable(names[i], tableName, computed[i] || computed[j] ? (byte)2 : (byte)3);
            }
        }
    }

    /// <summary>
    /// What a <c>CREATE TABLE</c> column stating neither <c>NULL</c> nor
    /// <c>NOT NULL</c> gets: the session's <c>ANSI_NULL_DFLT_ON</c> /
    /// <c>ANSI_NULL_DFLT_OFF</c> when either is on, else the database's
    /// <c>ANSI_NULL_DEFAULT</c> — tempdb's, off, for a <c>#temp</c> table. An
    /// alias type's own nullability wins over it, and a table variable, a
    /// table type and <c>ALTER TABLE … ADD</c> keep nullable (probed 2026-09-28
    /// against SQL Server 2025).
    /// </summary>
    private static bool DefaultsColumnsToNull(ParserContext context, string tableName)
    {
        var connection = context.Connection;
        var database = context.Batch.CurrentDatabase;
        return connection.AnsiNullDefaultOn
            || (!connection.AnsiNullDefaultOff && !tableName.StartsWith('#')
                && (context.Batch.CompiledAnsiNullDefaults is { } compiled && compiled.TryGetValue(database, out var asCompiled)
                    ? asCompiled
                    : (database.Switches & DatabaseSwitches.AnsiNullDefault) != 0));
    }

    /// <summary>
    /// Parses a single column definition starting at the column-name token
    /// and appends a <see cref="HeapColumn"/> entry to <paramref name="heapColumns"/>
    /// (or a <c>null</c> placeholder when the column is a non-persisted
    /// computed column awaiting resolution). Shared between
    /// <see cref="ParseColumnList"/> (CREATE TABLE / DECLARE @t TABLE /
    /// CREATE TYPE) and the ALTER-TABLE-ADD-COLUMN parser; the inline
    /// table-level constraint forks and the PERIOD form remain in the
    /// caller because ADD COLUMN doesn't admit them. ADD COLUMN alone passes
    /// <paramref name="withValuesColumns"/>, which collects the index of each
    /// column whose DEFAULT carries <c>WITH VALUES</c>; elsewhere the clause is
    /// Msg 156 near <c>VALUES</c>, as it is on a column without a DEFAULT
    /// (probed 2026-09-24 against SQL Server 2025). ADD COLUMN also passes
    /// <paramref name="ordinalOffset"/>, the table's existing column count,
    /// since real numbers an added column by its place in the whole table.
    /// </summary>
    internal static void ParseOneColumnIntoLists(
        ParserContext context,
        string tableName,
        bool isTableVariable,
        bool isTableType,
        List<HeapColumn?> heapColumns,
        List<bool> explicitNull,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)> pendingKeys,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition, bool NotForReplication)> pendingChecks,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed,
        List<(string StartCol, string EndCol)>? pendingPeriod,
        List<PendingForeignKey>? pendingForeignKeys,
        ref int identityCount,
        List<PendingInlineIndex>? pendingIndexes = null,
        List<int>? withValuesColumns = null,
        int ordinalOffset = 0,
        HeapColumn[]? existingColumns = null)
    {
        if (context.Token is not Name columnName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var atColumnName = context.SaveCheckpoint();
        context.MoveNextRequired();

        // A column written as just `timestamp` is a timestamp column named
        // timestamp (probed 2026-09-30 against SQL Server 2025, in a table, a
        // table variable, a table type and a function's return table): the
        // word is read again as the type.
        if (BuiltInToken.Equals(columnName.Value, "TIMESTAMP")
            && context.Token is Operator { Character: ',' or ')' } or ReservedKeyword { Keyword: Keyword.Not or Keyword.Null or Keyword.Constraint or Keyword.Primary or Keyword.Unique or Keyword.Default or Keyword.Check })
        {
            context.RestoreCheckpoint(atColumnName);
        }

        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
        {
            context.MoveNextRequired();
            var computedStart = context.Token.StartIndex;
            // A computed column is another construct Msg 11719 names.
            Expression computed;
            var computedAggregates = new List<Parser.Expressions.AggregateExpression>();
            using (context.EnterNextValueForScope(NextValueForScope.Nested))
            using (ParserScope.Enter(ref context.InScalarDefinition, true))
            using (ParserScope.Enter(ref context.AggregateCollector, computedAggregates))
            {
                computed = Expression.Parse(context);
            }
            Selection.RefuseClauseAggregates(context.Batch, computedAggregates, SimulatedSqlException.AggregateInComputedOrCheck());

            var computedDefinition = context.CanonicalDefinitionFrom(computedStart, predicate: false) ?? EnsureParenthesized(context.SourceTextFrom(computedStart));
            var (persisted, computedNullable) = ParseComputedSuffix(context);
            var computedIndex = heapColumns.Count;
            pendingComputed.Add((computedIndex, columnName.Value, computed, persisted, computedNullable, computedDefinition));
            heapColumns.Add(null);
            explicitNull.Add(false);
            ParseComputedColumnInlineConstraint(context, tableName, columnName.Value, computedIndex, persisted, heapColumns, pendingComputed, existingColumns, pendingKeys, pendingChecks, pendingForeignKeys);
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
        var xmlDocument = false;
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
                (xmlSchemaCollection, xmlDocument) = ParseXmlSchemaCollectionArgument(context);
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
                        _ = context.GetNextRequired();
                        declaredScale = TypeNameSynonyms.ReadSecondTypeArgument(context, typeName);
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
        IdentitySpec? identitySpec = null;
        var identityNotForReplication = false;
        var withValues = false;
        bool? nullable = null;
        Expression? defaultExpression = null;
        string? defaultDefinition = null;
        var generatedAs = GeneratedAlwaysAsRow.None;
        var isHidden = false;
        var isRowGuidCol = false;
        var isSparse = false;
        var isColumnSet = false;
        string? columnCollation = null;
        var inlineKeyKind = (KeyConstraintKind?)null;
        var inlineKeyClustered = (bool?)null;
        var inlineKeyOptions = default(IndexOptions);
        string? inlineKeyName = null;
        var inlineKeyColumns = new List<(string Name, bool Descending)>();
        string? inlineFkName = null;
        string? inlineDefaultName = null;
        // One column definition admits at most one inline CHECK (Msg 8148);
        // a table-level CHECK over the same column is unrestricted.
        var inlineCheckSeen = false;
        // MASKED WITH follows the type, COLLATE and SPARSE and precedes every
        // other clause (probed 2026-09-27 against SQL Server 2025).
        string? maskingFunctionText = null;
        var foreignKeysBefore = pendingForeignKeys?.Count ?? 0;
        var indexesBefore = pendingIndexes?.Count ?? 0;
        bool NothingButCollateOrSparseYet() =>
            maskingFunctionText is null && identitySpec is null && !nullable.HasValue && defaultExpression is null
            && generatedAs == GeneratedAlwaysAsRow.None && !isRowGuidCol && inlineKeyKind is null && !inlineCheckSeen
            && inlineFkName is null && (pendingForeignKeys?.Count ?? 0) == foreignKeysBefore && (pendingIndexes?.Count ?? 0) == indexesBefore;
        while (true)
        {
            switch (context.Token)
            {
                case UnquotedString { ContextualKeyword: ContextualKeyword.Masked } when NothingButCollateOrSparseYet():
                    maskingFunctionText = ParseMaskedWithClause(context);
                    continue;
                case ReservedKeyword { Keyword: Keyword.Collate } when columnCollation is null && maskingFunctionText is null:
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
                        throw SimulatedSqlException.InvalidCollation(collationName, state: 2);
                    columnCollation = collationName;
                    context.MoveNextOptional();
                    continue;
                case ReservedKeyword { Keyword: Keyword.Identity } when identitySpec is null:
                    identitySpec = ParseIdentitySpec(context);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Generated } when generatedAs == GeneratedAlwaysAsRow.None:
                    // The period clause comes ahead of any NULL / NOT NULL:
                    // `datetime2 NOT NULL GENERATED …` is Msg 102 at GENERATED
                    // (probed 2026-09-25 against SQL Server 2025).
                    // A table variable takes the ledger kinds (probed 2026-10-06
                    // against SQL Server 2025), only a period being refused.
                    var generatedCheckpoint = context.SaveCheckpoint();
                    var alwaysToken = context.GetNextOptional();
                    var asToken = context.GetNextOptional();
                    var kindToken = context.GetNextOptional();
                    var declaresLedger = alwaysToken is UnquotedString { ContextualKeyword: ContextualKeyword.Always }
                        && asToken is ReservedKeyword { Keyword: Keyword.As }
                        && kindToken is UnquotedString { ContextualKeyword: ContextualKeyword.Transaction_Id or ContextualKeyword.Sequence_Number };
                    context.RestoreCheckpoint(generatedCheckpoint);
                    if (isTableVariable && !declaresLedger)
                        throw SimulatedSqlException.TableVariableWithPeriod();
                    if (isTableType || nullable.HasValue)
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
                    // TRANSACTION_ID and SEQUENCE_NUMBER take any table, an
                    // ALTER TABLE … ADD included (probed 2026-10-06 against
                    // SQL Server 2025).
                    var ledgerKind = context.GetNextRequired() switch
                    {
                        UnquotedString { ContextualKeyword: ContextualKeyword.Transaction_Id } => GeneratedAlwaysAsRow.TransactionIdStart,
                        UnquotedString { ContextualKeyword: ContextualKeyword.Sequence_Number } => GeneratedAlwaysAsRow.SequenceNumberStart,
                        _ => GeneratedAlwaysAsRow.None,
                    };
                    if (ledgerKind == GeneratedAlwaysAsRow.None && context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Row })
                    {
                        throw context.Token is ReservedKeyword notRow
                            ? SimulatedSqlException.SyntaxErrorNearKeyword(notRow)
                            : SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    if (pendingPeriod is null && ledgerKind == GeneratedAlwaysAsRow.None)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    generatedAs = context.GetNextRequired() switch
                    {
                        UnquotedString { ContextualKeyword: ContextualKeyword.Start } => ledgerKind == GeneratedAlwaysAsRow.None ? GeneratedAlwaysAsRow.Start : ledgerKind,
                        ReservedKeyword { Keyword: Keyword.End } => ledgerKind == GeneratedAlwaysAsRow.None ? GeneratedAlwaysAsRow.End : ledgerKind + 1,
                        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                    };
                    context.MoveNextRequired();
                    if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Hidden })
                    {
                        isHidden = true;
                        context.MoveNextRequired();
                    }
                    // A period column takes no COLLATE or SPARSE: either is a
                    // syntax error at itself (probed 2026-10-04 against SQL
                    // Server 2025).
                    if (context.Token is ReservedKeyword { Keyword: Keyword.Collate } collateKeyword)
                        throw SimulatedSqlException.SyntaxErrorNearKeyword(collateKeyword);
                    if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Sparse })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
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
                        case ReservedKeyword { Keyword: Keyword.Null }:
                            throw SimulatedSqlException.MultipleNullConstraints(columnName.Value, tableName);
                        case ReservedKeyword { Keyword: Keyword.For } when identitySpec is not null && !identityNotForReplication:
                            // IDENTITY(s, i) NOT FOR REPLICATION — replication
                            // isn't modeled, so the clause round-trips as
                            // metadata only. REPLICATION classifies as either a
                            // reserved or contextual keyword; accept both.
                            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Replication }
                                and not UnquotedString { ContextualKeyword: ContextualKeyword.Replication })
                            {
                                throw SimulatedSqlException.SyntaxErrorNear(context);
                            }
                            identityNotForReplication = true;
                            break;
                        default:
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    context.MoveNextOptional();
                    continue;
                // SPARSE: a storage marker the row encoder has nothing to buy
                // from (it already omits a NULL), validated once the type
                // resolves. After IDENTITY it's Msg 102, as on real.
                case UnquotedString { ContextualKeyword: ContextualKeyword.Sparse } when !isSparse && identitySpec is null && maskingFunctionText is null && !isTableType && !context.RefusesSparseColumns:
                    isSparse = true;
                    context.MoveNextOptional();
                    continue;
                // COLUMN_SET FOR ALL_SPARSE_COLUMNS: the table's sparse column
                // set. A NOT NULL ahead of it is the plain syntax error at it
                // (probed 2026-10-06 against SQL Server 2025).
                case StringToken columnSet when columnSet.Span.Equals("COLUMN_SET", StringComparison.OrdinalIgnoreCase) && !isColumnSet && !isTableType && !context.RefusesSparseColumns:
                    if (nullable == false)
                        throw SimulatedSqlException.SyntaxErrorNear(columnSet);
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.For })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    if (context.GetNextRequired() is not StringToken allSparse || !allSparse.Span.Equals("ALL_SPARSE_COLUMNS", StringComparison.OrdinalIgnoreCase))
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    isColumnSet = true;
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
                // A second nullability marker, either spelling (probed
                // 2026-10-02 against SQL Server 2025).
                case ReservedKeyword { Keyword: Keyword.Null }:
                    throw SimulatedSqlException.MultipleNullConstraints(columnName.Value, tableName);
                case ReservedKeyword { Keyword: Keyword.Default } when defaultExpression is null:
                    context.MoveNextRequired();
                    var defaultStart = context.Token.StartIndex;
                    // A table type's default is one of the stored expressions
                    // Msg 11719 names (probed 2026-10-04 against SQL Server 2025).
                    if (isTableType)
                    {
                        using (context.EnterNextValueForScope(NextValueForScope.Nested))
                            defaultExpression = ParseDefaultClauseExpression(context);
                    }
                    else
                    {
                        defaultExpression = ParseDefaultClauseExpression(context, tempTable: !isTableVariable && tableName.StartsWith('#'), tableVariable: isTableVariable);
                    }
                    defaultDefinition = context.CanonicalDefinitionFrom(defaultStart, predicate: false) ?? $"({context.SourceTextFrom(defaultStart)})";
                    continue;
                case ReservedKeyword { Keyword: Keyword.Default }:
                    throw SimulatedSqlException.MultipleColumnConstraints("DEFAULT", columnName.Value, tableName);
                case ReservedKeyword { Keyword: Keyword.With } when !withValues:
                    {
                        var beforeWith = context.SaveCheckpoint();
                        if (context.GetNextOptional() is not ReservedKeyword { Keyword: Keyword.Values } values)
                        {
                            context.RestoreCheckpoint(beforeWith);
                            break;
                        }
                        if (withValuesColumns is null || defaultExpression is null)
                            throw SimulatedSqlException.SyntaxErrorNearKeyword(values);
                        withValues = true;
                        context.MoveNextOptional();
                        continue;
                    }
                case ReservedKeyword { Keyword: Keyword.Constraint } inlineConstraintKw when inlineFkName is null:
                    if (isTableType || isTableVariable)
                        throw SimulatedSqlException.SyntaxErrorNearKeyword("CONSTRAINT");
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
                            var namedCheck = ParseInlineCheckPredicate(context, refusesNotForReplication: false);
                            pendingChecks.Add((namedConstraint.Value, namedCheck.Predicate, columnName.Value, namedCheck.Definition, namedCheck.NotForReplication));
                            inlineCheckSeen = true;
                            continue;
                        case ReservedKeyword { Keyword: Keyword.Foreign or Keyword.References }:
                            inlineFkName = namedConstraint.Value;
                            continue;
                        // A key clause the column already carries — same rules
                        // as the unnamed form below, since a name on the second
                        // one doesn't make it a separate constraint.
                        case ReservedKeyword { Keyword: Keyword.Primary } when inlineKeyKind == KeyConstraintKind.PrimaryKey:
                            throw SimulatedSqlException.MultipleColumnConstraints("PRIMARY KEY", columnName.Value, tableName);
                        case ReservedKeyword { Keyword: Keyword.Unique } when inlineKeyKind == KeyConstraintKind.Unique:
                            throw SimulatedSqlException.MultipleColumnConstraints("UNIQUE", columnName.Value, tableName);
                        case ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique } when inlineKeyKind is not null:
                            throw SimulatedSqlException.BothPrimaryKeyAndUniqueOnColumn(columnName.Value, tableName);
                        case ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique }:
                            inlineKeyName = namedConstraint.Value;
                            (inlineKeyKind, inlineKeyClustered, inlineKeyOptions) = ParseInlineKeyKindAndModifiers(context, inlineKeyColumns);
                            continue;
                        default:
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                case ReservedKeyword { Keyword: Keyword.Index } when pendingIndexes is not null:
                    // Column-level inline index: `INDEX name [UNIQUE] [CLUSTERED
                    // | NONCLUSTERED]` and the tail bar INCLUDE — a single-column
                    // index on this column.
                    if (context.GetNextRequired() is not Name indexNameToken)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextOptional();
                    var columnLevelIndex = ParseInlineIndexBody(context, indexNameToken.Value, tableName, columnLevelKey: columnName.Value, isTableVariable || isTableType);
                    columnLevelIndex.KeysBefore = pendingKeys.Count;
                    pendingIndexes.Add(columnLevelIndex);
                    continue;
                case ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique } when inlineKeyKind is null:
                    (inlineKeyKind, inlineKeyClustered, inlineKeyOptions) = ParseInlineKeyKindAndModifiers(context, inlineKeyColumns);
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
                    var inlineCheck = ParseInlineCheckPredicate(context, isTableVariable || isTableType);
                    pendingChecks.Add((null, inlineCheck.Predicate, columnName.Value, inlineCheck.Definition, inlineCheck.NotForReplication));
                    inlineCheckSeen = true;
                    continue;
                // Real spells the refused keyword in capitals however it was
                // written (probed 2026-09-24).
                case ReservedKeyword { Keyword: Keyword.Foreign or Keyword.References } referencesKw when isTableVariable || isTableType:
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(referencesKw.Keyword == Keyword.Foreign ? "FOREIGN" : "REFERENCES");
                case ReservedKeyword { Keyword: Keyword.Foreign or Keyword.References }:
                    if (ConsumeOptionalForeignKeyNoisePhrase(context, tableName) is { } listedFkColumn)
                    {
                        ParseInlineForeignKeyTail(context, tableName, listedFkColumn, ResolveColumnLevelKeyList(context, heapColumns, pendingComputed, existingColumns, columnName.Value, [(listedFkColumn, false)])[0], inlineFkName: inlineFkName, pendingForeignKeys);
                    }
                    else
                    {
                        ParseInlineForeignKeyTail(context, tableName, columnName.Value, heapColumns.Count, inlineFkName: inlineFkName, pendingForeignKeys);
                    }
                    inlineFkName = null;
                    continue;
            }
            break;
        }

        // A sparse column must be nullable as written; the NOT NULL an inline
        // PRIMARY KEY implies is the key's refusal (Msg 1919) instead.
        var writtenNotNull = nullable == false;
        if (inlineKeyKind == KeyConstraintKind.PrimaryKey && inlineKeyColumns.Count == 0)
        {
            if (nullable == true)
                throw SimulatedSqlException.PrimaryKeyOnNullableColumn(tableName);
            nullable = false;
        }

        var (resolvedType, maxLength, aliasIsNullable, aliasType) = ResolveTypeReference(
            context.Batch, qualifiedTypeName, typeName, declaredMaxLength, declaredScale,
            index: ordinalOffset + heapColumns.Count + 1, TypeSpecSite.Column, columnName: columnName.Value);
        // A #temp table's types resolve in tempdb, where the current
        // database's alias types don't exist (probed 2026-09-26 against SQL
        // Server 2025); a table variable's resolve here.
        if (aliasType is not null && tableName.StartsWith('#'))
            throw SimulatedSqlException.CannotFindDataType(qualifiedTypeName.ToString(), ordinalOffset + heapColumns.Count + 1);
        // Alias-type-declared nullability propagates as the column default
        // when the column declaration omits an explicit NULL / NOT NULL. A
        // period column defaults to NOT NULL instead (probed 2026-09-25 against
        // SQL Server 2025); only a NULL written after it is Msg 13587.
        if (generatedAs.IsLedger())
        {
            // A ledger column is a bigint, its START never NULL and its END
            // nullable whatever the declaration omits (probed 2026-10-06
            // against SQL Server 2025).
            if (resolvedType is not BigIntSqlType || aliasType is not null)
                throw SimulatedSqlException.LedgerColumnInvalidType(generatedAs.Spelling(), columnName.Value, (byte)(generatedAs - GeneratedAlwaysAsRow.TransactionIdStart + 1));
            var isStart = generatedAs is GeneratedAlwaysAsRow.TransactionIdStart or GeneratedAlwaysAsRow.SequenceNumberStart;
            if (isStart && nullable == true)
                throw SimulatedSqlException.LedgerStartColumnNullable(generatedAs.Spelling(), columnName.Value);
            if (!isStart && nullable == false)
                throw SimulatedSqlException.LedgerEndColumnNotNullable(generatedAs.Spelling(), columnName.Value);
            nullable ??= !isStart;
        }
        else if (generatedAs != GeneratedAlwaysAsRow.None)
        {
            nullable ??= false;
        }
        nullable ??= aliasIsNullable;
        var actualNullable = nullable ?? (identitySpec is null
            && (isTableVariable || isTableType || withValuesColumns is not null || DefaultsColumnsToNull(context, tableName)));

        if (inlineKeyKind is KeyConstraintKind kind)
        {
            pendingKeys.Add(inlineKeyColumns.Count == 0
                ? (kind, inlineKeyName, [heapColumns.Count], inlineKeyClustered, inlineKeyOptions, [])
                : (kind, inlineKeyName, ResolveColumnLevelKeyList(context, heapColumns, pendingComputed, existingColumns, columnName.Value, inlineKeyColumns), inlineKeyClustered, inlineKeyOptions, [.. inlineKeyColumns.Select(column => column.Descending)]));
        }

        IdentityState? identity = null;
        if (identitySpec is { } spec)
        {
            if (++identityCount > 1)
                throw SimulatedSqlException.MultipleIdentityColumns(tableName);
            if (actualNullable)
                throw SimulatedSqlException.IdentityOnNullableColumn(columnName.Value, tableName);
            if (!IdentityState.IsIdentityType(resolvedType))
                throw SimulatedSqlException.IdentityInvalidType(columnName.Value);
            identity = spec.Resolve(resolvedType, columnName.Value, identityNotForReplication);
            if (defaultExpression is not null)
                throw SimulatedSqlException.DefaultOnIdentityColumn(tableName, columnName.Value);
        }

        if (isSparse && (writtenNotNull || isRowGuidCol || !SparseEligible(resolvedType)))
            throw SimulatedSqlException.CannotCreateSparseColumn(columnName.Value, tableName);
        if (isSparse && defaultExpression is not null)
            throw SimulatedSqlException.SparseColumnWithDefault(columnName.Value, tableName);
        if (isColumnSet && (resolvedType is not XmlSqlType || writtenNotNull))
            throw SimulatedSqlException.ColumnSetNotNullableXml(columnName.Value, tableName);

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

        // Any non-string column is Msg 447 at state 1 (probed 2026-10-01).
        if (columnCollation is not null && resolvedType.Category != SqlTypeCategory.String)
            throw SimulatedSqlException.CollateClauseRequiresString(resolvedType.SqlServerName, 1);
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

        // A DEFAULT takes its column's type as an assignment does (probed
        // 2026-09-24: a datetime DEFAULT on a decimal column is Msg 257).
        if (defaultExpression is not null)
        {
            // No DEFAULT may sit on a vector column, NULL included (probed
            // 2026-09-26 against SQL Server 2025).
            if (resolvedType is VectorSqlType)
                throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.DefaultColumnInvalid(columnName.Value, tableName, 1), state: 0);
            AssignmentRules.RequireAssignable(defaultExpression, defaultExpression.GetSqlType(context.Batch, NoColumnTypeResolver), resolvedType);
        }
        var maskingFunction = maskingFunctionText is null ? null : MaskingFunction.Parse(maskingFunctionText, columnName.Value, resolvedType);
        var newColumn = new HeapColumn(columnName.Value, resolvedType, maxLength, isColumnSet || actualNullable, identity, defaultExpression, isColumnSet ? new Parser.Expressions.ColumnSetValue() : null, generatedAs: generatedAs, isHidden: isHidden, collation: columnCollation, isRowGuidCol: isRowGuidCol,
            spelledNumeric: SqlType.IsNumericSpelling(qualifiedTypeName, context.Batch.TryResolveAliasType(qualifiedTypeName, out var spellingAlias) ? spellingAlias : null))
        {
            IsColumnSet = isColumnSet,
        };
        if (isColumnSet)
        {
            // One column set per table (Msg 1732, naming the second).
            foreach (var existing in heapColumns)
            {
                if (existing is { IsColumnSet: true })
                    throw SimulatedSqlException.SecondColumnSet(columnName.Value, tableName);
            }
            if (existingColumns is not null && Array.Exists(existingColumns, static column => column.IsColumnSet))
                throw SimulatedSqlException.SecondColumnSet(columnName.Value, tableName);
            if (existingColumns is not null && Array.Exists(existingColumns, static column => column.IsSparse))
                throw SimulatedSqlException.ColumnSetOverExistingSparseColumns(columnName.Value, tableName);
        }
        if (xmlSchemaCollection is not null)
        {
            newColumn.XmlSchemaCollection = xmlSchemaCollection;
            newColumn.XmlDocument = xmlDocument;
        }
        newColumn.AliasType = aliasType;
        newColumn.IsSparse = isSparse;
        if (maskingFunction is not null)
        {
            newColumn.MaskingFunction = maskingFunction;
            context.Batch.Connection.Simulation.DeclaresDataMasks = true;
        }
        if (aliasType is { BoundRule: not null } or { BoundDefault: not null })
        {
            if (isTableVariable || isTableType)
                throw SimulatedSqlException.BoundAliasTypeInTableVariable(aliasType.Name, aliasType.BoundRule is not null);
            InheritAliasTypeBindings(newColumn);
        }
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
                definition: defaultDefinition,
                createDate: context.Batch.CurrentStatement.UtcNow);
        }
        if (withValues)
            withValuesColumns!.Add(heapColumns.Count);
        heapColumns.Add(newColumn);
        explicitNull.Add(nullable == true);
    }

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
                // Optional advance: `ALTER TABLE t ADD c AS a + 1 PERSISTED`
                // may be a batch's final token, and everything the loop looks
                // for past the keyword is optional — the same tolerance
                // <see cref="ParseOneColumnIntoLists"/> applies after a
                // no-argument type name.
                context.MoveNextOptional();
                continue;
            }
            if (persisted && !nullable.HasValue && context.Token is ReservedKeyword { Keyword: Keyword.Not })
            {
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                nullable = false;
                context.MoveNextOptional();
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
        List<HeapColumn?> heapColumns,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed,
        HeapColumn[]? existingColumns,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)> pendingKeys,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition, bool NotForReplication)> pendingChecks,
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
                    var keyColumns = new List<(string Name, bool Descending)>();
                    var (inlineKind, inlineClustered, inlineOptions) = ParseInlineKeyKindAndModifiers(context, keyColumns);
                    pendingKeys.Add(keyColumns.Count == 0
                        ? (inlineKind, constraintName, [computedIndex], inlineClustered, inlineOptions, [])
                        : (inlineKind, constraintName, ResolveColumnLevelKeyList(context, heapColumns, pendingComputed, existingColumns, columnName, keyColumns), inlineClustered, inlineOptions, [.. keyColumns.Select(column => column.Descending)]));
                    continue;
                case ReservedKeyword { Keyword: Keyword.Check }:
                    if (!persisted)
                        throw SimulatedSqlException.ComputedColumnConstraintRequiresPersisted();
                    if (checkSeen)
                        throw SimulatedSqlException.MultipleColumnConstraints("CHECK", columnName, tableName);
                    var inlineCheck = ParseInlineCheckPredicate(context, pendingForeignKeys is null);
                    pendingChecks.Add((constraintName, inlineCheck.Predicate, columnName, inlineCheck.Definition, inlineCheck.NotForReplication));
                    checkSeen = true;
                    continue;
                case ReservedKeyword { Keyword: Keyword.Foreign or Keyword.References }:
                    if (!persisted)
                        throw SimulatedSqlException.ComputedColumnConstraintRequiresPersisted();
                    var listedColumn = ConsumeOptionalForeignKeyNoisePhrase(context, tableName);
                    ParseInlineForeignKeyTail(context, tableName, listedColumn ?? columnName, listedColumn is null ? computedIndex : ResolveColumnLevelKeyList(context, heapColumns, pendingComputed, existingColumns, columnName, [(listedColumn, false)])[0], constraintName, pendingForeignKeys);
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
    /// Resolves the column list a column-level <c>PRIMARY KEY</c> / <c>UNIQUE</c>
    /// names to ordinals: a column declared before it, the column carrying it
    /// (<paramref name="currentColumn"/>, not yet in
    /// <paramref name="heapColumns"/> unless computed), or — for <c>ALTER
    /// TABLE … ADD</c> — one the table already has
    /// (<paramref name="existingColumns"/>, numbered below zero so the
    /// caller's shift by the existing count lands it). A name none of them
    /// holds is Msg 1911, and a repeated one Msg 1909, each followed by Msg
    /// 1750 (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    private static int[] ResolveColumnLevelKeyList(
        ParserContext context,
        List<HeapColumn?> heapColumns,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed,
        HeapColumn[]? existingColumns,
        string currentColumn,
        List<(string Name, bool Descending)> keyColumns)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        var ordinals = new int[keyColumns.Count];
        for (var i = 0; i < keyColumns.Count; i++)
        {
            var name = keyColumns[i].Name;
            var found = FindDeclaredColumnOrdinal(context, heapColumns, pendingComputed, name, out _);
            if (found < 0 && collation.Equals(name, currentColumn))
                found = heapColumns.Count;
            var existing = found < 0 && existingColumns is not null ? Array.FindIndex(existingColumns, column => collation.Equals(column.Name, name)) : -1;
            if (existing >= 0)
                found = existing - existingColumns!.Length;
            else if (found < 0)
                throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.IndexColumnMissing(name));
            for (var j = 0; j < i; j++)
            {
                if (ordinals[j] == found)
                    throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.DuplicateIndexColumn(name, state: 1));
            }
            ordinals[i] = found;
        }
        return ordinals;
    }

    /// <summary>
    /// Consumes the optional <c>FOREIGN KEY</c> noise phrase an inline
    /// column-level foreign key may carry ahead of <c>REFERENCES</c>, leaving
    /// the cursor on <c>REFERENCES</c> either way.
    /// </summary>
    private static string? ConsumeOptionalForeignKeyNoisePhrase(ParserContext context, string tableName)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Foreign })
            return null;
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Key })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        // The phrase may name its column as the table-level form does; more
        // than one is Msg 8140 (probed 2026-10-02 against SQL Server 2025).
        string? listed = null;
        if (context.GetNextRequired() is Operator { Character: '(' })
        {
            if (context.GetNextRequired() is not Name column)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            listed = column.Value;
            if (context.GetNextRequired() is Operator { Character: ',' })
                throw SimulatedSqlException.ColumnForeignKeyHasManyKeys(tableName);
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
        return context.Token is ReservedKeyword { Keyword: Keyword.References }
            ? listed
            : throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Parses the <c>IDENTITY [(seed, increment)]</c> property after a column's
    /// data type. Enters with <see cref="ParserContext.Token"/> on the
    /// <c>IDENTITY</c> keyword and leaves it on the next non-identity token
    /// (a nullability keyword, comma, or the column-list's closing paren).
    /// Bare <c>IDENTITY</c> is shorthand for <c>IDENTITY(1, 1)</c>.
    /// </summary>
    private static IdentitySpec ParseIdentitySpec(ParserContext context)
    {
        // A bare IDENTITY may end the statement: `ALTER TABLE t ADD c int IDENTITY`.
        if (context.GetNextOptional() is not Operator { Character: '(' })
            return IdentitySpec.Default;
        context.MoveNextRequired();
        var spec = IdentitySpec.ReadArguments(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return spec;
    }


    /// <summary>
    /// Parses a CHECK constraint's parenthesized predicate body. Entered with
    /// <see cref="ParserContext.Token"/> on the <c>CHECK</c> keyword; consumes
    /// the keyword, the opening <c>(</c>, the inner predicate via
    /// <see cref="BooleanExpression.Parse"/>, and the closing <c>)</c>. Leaves
    /// the token on the next un-consumed token (typically a comma or the
    /// column-list's closing paren). A <c>NOT FOR REPLICATION</c> between the
    /// keyword and the predicate is read and reported.
    /// </summary>
    private static (BooleanExpression Predicate, string Definition, bool NotForReplication) ParseInlineCheckPredicate(ParserContext context, bool refusesNotForReplication)
    {
        context.MoveNextRequired();
        // A table variable's or table type's grammar has no NOT FOR
        // REPLICATION: Msg 102 on the NOT (probed 2026-09-26).
        if (refusesNotForReplication && context.Token is ReservedKeyword { Keyword: Keyword.Not })
            throw SimulatedSqlException.SyntaxErrorNear(context.Token);
        var notForReplication = TryConsumeNotForReplication(context);
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var predicateStart = context.Token.StartIndex;
        // A CHECK constraint is the first construct real's Msg 11719 names
        // (probe-confirmed 2026-08-05 for the inline column form).
        BooleanExpression predicate;
        var checkAggregates = new List<Parser.Expressions.AggregateExpression>();
        using (context.EnterNextValueForScope(NextValueForScope.Nested))
        using (ParserScope.Enter(ref context.InScalarDefinition, true))
        using (ParserScope.Enter(ref context.AggregateCollector, checkAggregates))
        {
            predicate = BooleanExpression.Parse(context);
        }
        Selection.RefuseClauseAggregates(context.Batch, checkAggregates, SimulatedSqlException.AggregateInComputedOrCheck());

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        // Token sits on the closing `)`, so capture the predicate up to it.
        var definition = context.CanonicalDefinitionFrom(predicateStart, predicate: true) ?? $"({context.SourceTextFrom(predicateStart)})";
        // Optional advance: CREATE TABLE always has a `)` or constraint after
        // a CHECK predicate; ALTER TABLE ADD COLUMN's inline CHECK may end
        // the statement.
        context.MoveNextOptional();
        return (predicate, definition, notForReplication);
    }

    /// <summary>
    /// Consumes <c>NOT FOR REPLICATION</c> at the cursor, leaving it on the
    /// token after; answers whether the clause was there. A CHECK or foreign
    /// key declared with it is still enforced, but never trusted (probed
    /// 2026-09-26 against SQL Server 2025).
    /// </summary>
    internal static bool TryConsumeNotForReplication(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Not })
            return false;
        var checkpoint = context.SaveCheckpoint();
        if (context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.For })
        {
            var replication = context.GetNextOptional();
            if (replication is ReservedKeyword { Keyword: Keyword.Replication } or UnquotedString { ContextualKeyword: ContextualKeyword.Replication })
            {
                context.MoveNextOptional();
                return true;
            }
        }
        context.RestoreCheckpoint(checkpoint);
        return false;
    }

    /// <summary>
    /// Refuses a second column of one <c>TRANSACTION_ID</c> /
    /// <c>SEQUENCE_NUMBER</c> kind (Msg 37345, its state the kind's place in
    /// <c>TRANSACTION_ID START</c>, <c>END</c>, <c>SEQUENCE_NUMBER START</c>,
    /// <c>END</c>; probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    private static void RejectRepeatedLedgerColumns(IEnumerable<HeapColumn?> columns)
    {
        var seen = 0;
        foreach (var column in columns)
        {
            if (column is null || !column.GeneratedAs.IsLedger())
                continue;
            var bit = 1 << (column.GeneratedAs - GeneratedAlwaysAsRow.TransactionIdStart);
            if ((seen & bit) != 0)
                throw SimulatedSqlException.LedgerColumnRepeated(column.GeneratedAs.Spelling(), (byte)(column.GeneratedAs - GeneratedAlwaysAsRow.TransactionIdStart + 1));
            seen |= bit;
        }
    }

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
            if (heapColumns[i] is not { } column || !column.GeneratedAs.IsPeriod())
                continue;
            var start = column.GeneratedAs == GeneratedAlwaysAsRow.Start;
            if (column.Type is not DateTime2SqlType)
                throw SimulatedSqlException.TemporalGeneratedColumnInvalidType(column.Name);
            // An end column's nullability is reported at state 2 (probed
            // 2026-10-04 against SQL Server 2025).
            if (column.Nullable)
                throw SimulatedSqlException.TemporalPeriodColumnNullable(column.Name, start ? (byte)1 : (byte)2);
            if ((start ? generatedStartOrdinal : generatedEndOrdinal) >= 0)
                throw SimulatedSqlException.TemporalGeneratedColumnRepeated(start);
            if (start)
                generatedStartOrdinal = i;
            else
                generatedEndOrdinal = i;
        }

        if (pendingPeriod.Count > 1)
            throw SimulatedSqlException.TemporalPeriodRepeated();
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
        if (!collation.Equals(declaredStart, heapColumns[generatedStartOrdinal]!.Name))
            throw SimulatedSqlException.TemporalPeriodStartNotMatching();
        if (!collation.Equals(declaredEnd, heapColumns[generatedEndOrdinal]!.Name))
            throw SimulatedSqlException.TemporalPeriodEndNotMatching();
        return heapColumns[generatedStartOrdinal]!.Type is DateTime2SqlType startType && heapColumns[generatedEndOrdinal]!.Type is DateTime2SqlType endType && startType.precision != endType.precision
            ? throw SimulatedSqlException.TemporalPeriodColumnPrecisionMismatch()
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
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)> pendingKeys,
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
    /// <see cref="Expression.VisitColumnReferences(Action{MultiPartName})"/> with that gate and so
    /// inherits its container-coverage limits.
    /// </summary>
    internal static void RejectChecksOverNonPersistedComputedColumns(
        Collation collation,
        string tableName,
        List<HeapColumn?> columns,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition, bool NotForReplication)> pendingChecks)
    {
        foreach (var pending in pendingChecks)
            RejectCheckOverNonPersistedComputedColumn(collation, tableName, columns, pending.Predicate);
    }

    /// <summary>
    /// Binds each CHECK predicate against the table's columns while compiling,
    /// as real does: a name no column carries is Msg 207, and any other binder
    /// error — an illegal conversion (Msg 529), say — arrives with the Msg 1750
    /// trailer. Real's 207 beats the Msg 8141 peer-reference walk and carries
    /// no trailer (probed 2026-09-24 against SQL Server 2025).
    /// </summary>
    internal static void BindCheckConstraints(
        BatchContext batch,
        List<HeapColumn?> columns,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition, bool NotForReplication)> pendingChecks)
    {
        foreach (var pending in pendingChecks)
            BindCheckConstraint(batch, columns, pending.Predicate);
    }

    internal static void BindCheckConstraint(BatchContext batch, IReadOnlyList<HeapColumn?> columns, BooleanExpression predicate)
    {
        SqlType ResolveColumnType(MultiPartName name)
        {
            foreach (var column in columns)
            {
                if (column is not null && batch.CurrentDatabase.Collation.Equals(column.Name, name.Leaf))
                    return column.Type;
            }
            throw SimulatedSqlException.InvalidColumnName(name);
        }

        try
        {
            predicate.Bind(batch, ResolveColumnType);
        }
        catch (SimulatedSqlException error) when (error.Number != 207)
        {
            throw SimulatedSqlException.FollowedByConstraintNotCreated(error, state: 0);
        }
    }

    internal static void RejectCheckOverNonPersistedComputedColumn(
        Collation collation,
        string tableName,
        IReadOnlyList<HeapColumn?> columns,
        BooleanExpression predicate)
    {
        if (Parser.Expressions.XmlMethodCall.AppearsIn(predicate))
            throw SimulatedSqlException.XmlMethodInCheckConstraint(tableName);
        predicate.VisitOperandExpressions(op =>
            op.VisitColumnReferences(name =>
            {
                foreach (var column in columns)
                {
                    if (column is { Computed: not null, IsPersisted: false } && collation.Equals(column.Name, name.Leaf))
                        throw SimulatedSqlException.CheckConstraintOnNonPersistedComputedColumn(column.Name, tableName);
                    // No CHECK may read a vector column at all (probed
                    // 2026-09-26 against SQL Server 2025).
                    if (column is { Type: VectorSqlType } && collation.Equals(column.Name, name.Leaf))
                        throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.CheckConstraintOnVectorColumn(), state: 0);
                }
            }));
    }

    /// <summary>
    /// The one column a table-level CHECK reads, or null when it reads none or
    /// several. Real files such a constraint as that column's — its auto-name
    /// carries the column, <c>sys.check_constraints.parent_column_id</c> and
    /// <c>sp_helpconstraint</c> name it, and its Msg 547 ends <c>column 'a'</c>
    /// (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    internal static string? SingleCheckedColumn(Collation collation, BooleanExpression predicate)
    {
        string? single = null;
        var several = false;
        predicate.VisitOperandExpressions(operand => operand.VisitColumnReferences(reference =>
        {
            if (single is null)
                single = reference.Leaf;
            else if (!collation.Equals(single, reference.Leaf))
                several = true;
        }));
        return several ? null : single;
    }

    /// <summary>
    /// Materializes pending CHECK declarations into <see cref="CheckConstraint"/>
    /// records, generating SQL-Server-shaped auto-names for any without a
    /// caller-supplied <c>CONSTRAINT name</c>: <c>CK__&lt;table8&gt;__&lt;col8&gt;__&lt;8hex&gt;</c>
    /// for inline constraints, <c>CK__&lt;table8&gt;__&lt;8hex&gt;</c> for
    /// table-level. The 8-hex suffix is a stable FNV-1a hash of the
    /// constraint shape, same convention as <see cref="AutoConstraintName"/>.
    /// </summary>
    internal static CheckConstraint[] ResolveCheckConstraints(
        string tableName,
        IReadOnlyList<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition, bool NotForReplication)> pendingChecks,
        Database database,
        DateTime createDate,
        int tempNamePadding = 16)
    {
        if (pendingChecks.Count == 0)
            return [];

        var resolved = new CheckConstraint[pendingChecks.Count];
        for (var c = 0; c < pendingChecks.Count; c++)
        {
            var pending = pendingChecks[c];
            var column = pending.InlineColumn ?? SingleCheckedColumn(database.Collation, pending.Predicate);
            var name = pending.Name ?? AutoCheckName(tableName, column, c, tempNamePadding);
            resolved[c] = new CheckConstraint(name, pending.Predicate, column, database.AllocateObjectId(), createDate)
            {
                Definition = pending.Definition,
                IsSystemNamed = pending.Name is null,
                NotForReplication = pending.NotForReplication,
                IsNotTrusted = pending.NotForReplication,
            };
        }
        return resolved;
    }

    /// <summary>
    /// Generates an auto-name for an unnamed CHECK constraint. SQL Server
    /// uses <c>CK__&lt;table&gt;__&lt;col&gt;__&lt;8hex&gt;</c> for inline
    /// and <c>CK__&lt;table&gt;__&lt;8hex&gt;</c> for table-level, cut as
    /// <see cref="FormatAutoConstraintName"/> describes; the
    /// simulator matches the structure with a deterministic 32-bit FNV-1a
    /// hash of <c>tableName + column + index</c> driving the hex slot. Stable
    /// across runs but non-cryptographic.
    /// </summary>
    private static string AutoCheckName(string tableName, string? inlineColumn, int declarationIndex, int tempNamePadding = 16)
    {
        var h = Fnv1a32.Initial;
        h.MixTableSeed(tableName);
        if (inlineColumn is not null)
            h.Mix(inlineColumn);
        h.Mix((byte)declarationIndex);
        return FormatAutoConstraintName("CK__", tableName, inlineColumn, h.Value, tempNamePadding);
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
    /// / DF: <c>&lt;prefix&gt;&lt;table&gt;__[&lt;column&gt;__]&lt;hash:X8&gt;</c>,
    /// thirty characters at most. Without a column the table keeps sixteen
    /// characters; with one the two share fourteen, the table keeping at
    /// least nine and the column the rest (probed 2026-10-01 against SQL
    /// Server 2025: <c>DF__tabletwel__colum__…</c>, <c>CK__t__abcdefghijklm__…</c>,
    /// <c>CK__abcdefghijklm__x__…</c>). A local temp table's name is its
    /// underscore-padded name inside tempdb, padded here to
    /// <paramref name="tempNamePadding"/>; a table variable's eight-hex name
    /// passes its own length, which pads nothing.
    /// </summary>
    internal static string FormatAutoConstraintName(string prefix, string tableName, string? optionalColumn, uint hash, int tempNamePadding = 16)
    {
        if (BatchContext.IsLocalTempName(tableName))
            tableName = tableName.PadRight(tempNamePadding, '_');
        if (optionalColumn is null)
            return $"{prefix}{(tableName.Length > 16 ? tableName[..16] : tableName)}__{hash:X8}";
        var tableLength = tableName.Length <= 9 ? tableName.Length : Math.Min(tableName.Length, Math.Max(9, 14 - optionalColumn.Length));
        var columnLength = Math.Min(optionalColumn.Length, 14 - tableLength);
        return $"{prefix}{tableName[..tableLength]}__{optionalColumn[..columnLength]}__{hash:X8}";
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
    /// comma, or closing paren — or null, since <c>ALTER TABLE … ADD c int
    /// UNIQUE</c> puts the clause at the end of the batch where CREATE TABLE's
    /// closing paren always follows it.
    /// </summary>
    private static (KeyConstraintKind Kind, bool? Clustered, IndexOptions Options) ParseInlineKeyKindAndModifiers(ParserContext context, List<(string Name, bool Descending)>? keyColumns = null)
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
        context.MoveNextOptional();
        bool? clustered = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Clustered or Keyword.NonClustered } modifier)
        {
            clustered = modifier.Keyword == Keyword.Clustered;
            context.MoveNextOptional();
        }
        var hash = ConsumeHashKeyword(context);
        // A column-level key may name its columns as a table-level one does —
        // `b bigint UNIQUE (a, b)` keys on both, the column carrying it only
        // when listed (probed 2026-10-02 against SQL Server 2025).
        if (keyColumns is not null && context.Token is Operator { Character: '(' })
        {
            do
            {
                if (context.GetNextRequired() is not Name keyColumn)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                keyColumns.Add((keyColumn.Value, context.Token is ReservedKeyword { Keyword: Keyword.Desc }));
                if (context.Token is ReservedKeyword { Keyword: Keyword.Asc or Keyword.Desc })
                    context.MoveNextRequired();
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        // A column-level key takes its own ON clause as a table-level one does.
        var options = ParseOptionalIndexWithClause(context, context.ColumnIndexOptions, rangeIndex: !hash).WithDataSpace(ParseOptionalDataSpaceClause(context, out _));
        return (kind, clustered, hash ? options.AsHash() : options);
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
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)> pendingKeys,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition, bool NotForReplication)> pendingChecks,
        List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable, string Definition)> pendingComputed,
        List<PendingForeignKey>? pendingForeignKeys = null,
        List<PendingEdgeConstraint>? pendingEdgeConstraints = null)
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
                var tableCheck = ParseInlineCheckPredicate(context, pendingForeignKeys is null);
                pendingChecks.Add((constraintName, tableCheck.Predicate, null, tableCheck.Definition, tableCheck.NotForReplication));
                return;
            case ReservedKeyword { Keyword: Keyword.Foreign }:
                ParseTableLevelForeignKey(context, constraintName, heapColumns, pendingComputed, pendingForeignKeys);
                return;
            case ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique }:
                break;
            case UnquotedString { Value: var connection } when pendingEdgeConstraints is not null && connection.Equals("CONNECTION", StringComparison.OrdinalIgnoreCase):
                pendingEdgeConstraints.Add(ParseEdgeConstraint(context, constraintName));
                return;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        // The table-level form's WITH clause follows the column list, so the
        // inline parser's own lookahead finds nothing here; it is read below.
        var (kind, clustered, modifiers) = ParseInlineKeyKindAndModifiers(context);

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
        // ON [PRIMARY]` for inline table-level PK / UNIQUE constraints; the
        // ON clause places the key's index.
        var indexOptions = ParseOptionalIndexWithClause(context, context.ColumnIndexOptions, rangeIndex: !modifiers.IsHash).WithDataSpace(ParseOptionalDataSpaceClause(context, out _));
        if (modifiers.IsHash)
            indexOptions = indexOptions.AsHash();

        pendingKeys.Add((kind, constraintName, [.. ordinals], clustered, indexOptions, [.. descending]));
    }

    /// <summary>
    /// Whether key <paramref name="index"/> of one declaration is clustered: as
    /// written, else a PRIMARY KEY is — unless another key of the same
    /// declaration asks for CLUSTERED, which leaves the primary key
    /// nonclustered rather than colliding (probed 2026-10-02 against SQL
    /// Server 2025: <c>id int PRIMARY KEY, u int UNIQUE CLUSTERED</c> creates
    /// both; an explicit pair is Msg 8112).
    /// </summary>
    private static bool IsClusteredKey(
        IReadOnlyList<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)> pendingKeys,
        int index)
    {
        if (pendingKeys[index].Clustered is bool declared)
            return declared;
        if (pendingKeys[index].Kind != KeyConstraintKind.PrimaryKey)
            return false;
        for (var i = 0; i < pendingKeys.Count; i++)
        {
            if (i != index && pendingKeys[i].Clustered == true)
                return false;
        }
        return true;
    }

    /// <summary>The most key columns an index or key takes (Msg 1904 past it).</summary>
    private const int MaxIndexKeyColumns = 32;

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
    /// <para>
    /// Object ids go out in real's own order for one declaration: the
    /// clustered constraint first, then the rest in <b>reverse</b> declaration
    /// order (probe-confirmed for <c>CREATE TABLE</c> — inline and table-level
    /// alike — and for <c>ALTER TABLE ADD</c> of several constrained columns).
    /// Index ids follow, since <see cref="HeapTable.IndexIdentities"/> hands
    /// out <c>index_id</c> in object-id order, so
    /// <c>create table t (id int primary key nonclustered, u int unique)</c>
    /// answers UNIQUE 2 / PRIMARY KEY 3.
    /// </para>
    /// </summary>
    internal static KeyConstraint[] ResolveKeyConstraints(
        string tableName,
        IReadOnlyList<HeapColumn> heapColumns,
        IReadOnlyList<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)> pendingKeys,
        Database database,
        DateTime createDate,
        int[]? objectIds = null,
        string? writtenTableName = null)
    {
        if (pendingKeys.Count == 0)
            return [];

        var primaryKeyCount = 0;
        var clusteredCount = 0;
        var clusteredAt = -1;
        var prepared = new (string Name, int[] StorageOrdinals, bool IsClustered)[pendingKeys.Count];
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
            if (IsClusteredKey(pendingKeys, c) && ++clusteredCount > 1)
                throw SimulatedSqlException.MultipleClusteredConstraints(tableName);

            // A key of more than 32 columns names an empty index (probed
            // 2026-10-05 against SQL Server 2025).
            if (pending.FullOrdinals.Length > MaxIndexKeyColumns)
                throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.TooManyIndexKeyColumns("", writtenTableName ?? tableName, pending.FullOrdinals.Length), state: 0);
            var constraintName = pending.Name ?? AutoConstraintName(tableName, pending.Kind, pending.FullOrdinals, heapColumns);
            var storageOrdinals = new int[pending.FullOrdinals.Length];
            for (var i = 0; i < pending.FullOrdinals.Length; i++)
            {
                var fullOrdinal = pending.FullOrdinals[i];
                var column = heapColumns[fullOrdinal];
                // A PRIMARY KEY needs the value stored and non-nullable; UNIQUE
                // takes a non-persisted computed column, subject to the same
                // determinism / precision gate CREATE INDEX applies.
                if (column.Computed is not null && !column.IsPersisted && pending.Kind == KeyConstraintKind.PrimaryKey)
                    throw SimulatedSqlException.ComputedColumnPkRequiresPersisted(column.Name, tableName);
                // A vector or json key is refused with the constraint's own Msg
                // 1750 after it (probed 2026-09-26 against SQL Server 2025).
                if (column.Type is VectorSqlType or JsonSqlType or ClrUdtSqlType { Udt.IsByteOrdered: false })
                    throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.KeyColumnInvalidType(column.Name, tableName), state: 0);
                if (column.IsLob)
                    throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.KeyColumnInvalidType(column.Name, tableName), state: 0);
                // A sparse key is refused at state 2 in a UNIQUE constraint and
                // at state 3 in a PRIMARY KEY, whose explicitly nullable column
                // raises Msg 8111 first (probed 2026-09-30 against SQL Server 2025).
                if (pending.Kind == KeyConstraintKind.PrimaryKey && column.Nullable)
                    throw SimulatedSqlException.PrimaryKeyOnNullableColumn(tableName);
                if (column.IsSparse)
                {
                    throw pending.Kind == KeyConstraintKind.PrimaryKey
                        ? SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.KeyColumnInvalidType(column.Name, tableName, state: 3))
                        : SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.KeyColumnInvalidType(column.Name, tableName, state: 2), state: 0);
                }
                RejectComputedKeyColumnNotIndexable(
                    database, heapColumns, tableName, column, constraintName, viaConstraint: true);

                // A non-persisted computed column occupies no storage slot, so
                // its key entry is -1 — the same sentinel HeapTable's own
                // projection uses, and what tells the enforcement paths to
                // evaluate the expression instead of decoding bytes.
                var storageOrdinal = column.IsStored ? 0 : -1;
                for (var k = 0; storageOrdinal >= 0 && k < fullOrdinal; k++)
                {
                    if (heapColumns[k].IsStored)
                        storageOrdinal++;
                }
                storageOrdinals[i] = storageOrdinal;
            }

            var isClustered = IsClusteredKey(pendingKeys, c);
            if (isClustered)
                clusteredAt = c;
            prepared[c] = (constraintName, storageOrdinals, isClustered);
        }

        // The clustered constraint takes the first object id, then the rest go
        // out in reverse declaration order — real's allocation order, which
        // index-id assignment reads back through IndexIdentities.
        var resolved = new KeyConstraint[pendingKeys.Count];
        if (clusteredAt >= 0)
            resolved[clusteredAt] = Materialize(clusteredAt);
        for (var c = pendingKeys.Count - 1; c >= 0; c--)
        {
            if (c != clusteredAt)
                resolved[c] = Materialize(c);
        }

        return resolved;

        KeyConstraint Materialize(int c)
        {
            var (name, storageOrdinals, isClustered) = prepared[c];
            return new KeyConstraint(
                pendingKeys[c].Kind, name, storageOrdinals, pendingKeys[c].FullOrdinals, objectIds?[c] ?? database.AllocateObjectId(),
                isClustered, pendingKeys[c].Options, createDate, pendingKeys[c].Descending);
        }
    }

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
        string tableName,
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
            if (referencedColumns.Count > 1)
                throw SimulatedSqlException.ColumnForeignKeyHasManyKeys(tableName);
        }
        var (delAction, updAction, notForReplication) = ParseOnDeleteOnUpdateActions(context);
        pendingForeignKeys.Add(new PendingForeignKey(
            inlineFkName,
            childColumnNames: [columnName],
            childFullOrdinals: [childFullOrdinal],
            referencedTable: referencedTable,
            referencedColumnNames: [.. referencedColumns],
            deleteAction: delAction,
            updateAction: updAction,
            notForReplication: notForReplication));
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

        // A node's or edge's identifier keys a constraint as the graph id it
        // renders (probed 2026-10-05 against SQL Server 2025: PRIMARY KEY
        // ($node_id) keys graph_id).
        if (GraphColumns.IdentifierKeyOrdinal(heapColumns, columnName) is var graphId and >= 0)
        {
            declaredName = heapColumns[graphId]!.Name;
            return graphId;
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
        var (delAction, updAction, notForReplication) = ParseOnDeleteOnUpdateActions(context);
        pendingForeignKeys.Add(new PendingForeignKey(
            constraintName,
            childColumnNames: [.. childColumnNames],
            childFullOrdinals: [.. childOrdinals],
            referencedTable: referencedTable,
            referencedColumnNames: [.. referencedColumns],
            deleteAction: delAction,
            updateAction: updAction,
            notForReplication: notForReplication));
    }

    /// <summary>
    /// Parses optional <c>ON DELETE</c> / <c>ON UPDATE</c> action suffixes
    /// (any order, each at most once). Returns the resolved action pair,
    /// defaulting to <see cref="ReferentialAction.NoAction"/> when omitted —
    /// matching SQL Server's default. Leaves the cursor on the first non-ON
    /// token.
    /// </summary>
    private static (ReferentialAction Delete, ReferentialAction Update, bool NotForReplication) ParseOnDeleteOnUpdateActions(ParserContext context)
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
        return (delete, update, TryConsumeNotForReplication(context));
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
    internal sealed class PendingForeignKey(
        string? constraintName,
        string[] childColumnNames,
        int[] childFullOrdinals,
        MultiPartName referencedTable,
        string[] referencedColumnNames,
        ReferentialAction deleteAction,
        ReferentialAction updateAction,
        bool notForReplication)
    {
        public readonly string? ConstraintName = constraintName;
        public readonly string[] ChildColumnNames = childColumnNames;
        public readonly int[] ChildFullOrdinals = childFullOrdinals;
        public readonly MultiPartName ReferencedTable = referencedTable;
        public readonly string[] ReferencedColumnNames = referencedColumnNames;
        public readonly ReferentialAction DeleteAction = deleteAction;
        public readonly ReferentialAction UpdateAction = updateAction;
        public readonly bool NotForReplication = notForReplication;

        /// <summary>
        /// A copy carrying <paramref name="ordinals"/> in place of
        /// <see cref="ChildFullOrdinals"/> — the shift <c>ALTER TABLE … ADD
        /// COLUMN</c> applies when the columns already on the table move a
        /// pending key's ordinals along.
        /// </summary>
        public PendingForeignKey WithChildFullOrdinals(int[] ordinals) =>
            new(this.ConstraintName, this.ChildColumnNames, ordinals, this.ReferencedTable,
                this.ReferencedColumnNames, this.DeleteAction, this.UpdateAction, this.NotForReplication);
    }

    /// <summary>
    /// An index an inline <c>INDEX</c> clause declared, kept until the table it
    /// lands on exists — once for a CREATE TABLE or table variable, once per
    /// instance for a table type.
    /// </summary>
    internal sealed class PendingInlineIndex(
        string name,
        bool isUnique,
        bool isClustered,
        (string ColumnName, bool IsDescending)[] columns,
        List<string> includeColumnNames,
        BooleanExpression? filter,
        string? filterDefinition,
        IndexOptions options)
    {
        public readonly string Name = name;
        public readonly bool IsUnique = isUnique;
        public readonly bool IsClustered = isClustered;
        public readonly (string ColumnName, bool IsDescending)[] Columns = columns;
        public readonly List<string> IncludeColumnNames = includeColumnNames;
        public readonly BooleanExpression? Filter = filter;
        public readonly string? FilterDefinition = filterDefinition;
        public readonly IndexOptions Options = options;

        /// <summary>A columnstore index, whose <see cref="Columns"/> it holds rather than keys on.</summary>
        public bool IsColumnstore;

        /// <summary>A columnstore index's <c>ORDER</c> column names.</summary>
        public List<string> ColumnstoreOrder = [];

        /// <summary>
        /// How many PRIMARY KEY / UNIQUE constraints the declaration wrote
        /// ahead of this index, which places it in the one sequence
        /// <see cref="AllocateDeclarationObjectIds"/> hands object ids out over.
        /// </summary>
        public int KeysBefore;
    }

    /// <summary>
    /// Object ids for one declaration's key constraints and inline indexes,
    /// in real's order: the clustered one first, then the rest in
    /// <b>reverse</b> declaration order, keys and indexes interleaved as
    /// written (probed 2026-09-26 against SQL Server 2025). Index ids follow,
    /// since <see cref="HeapTable.IndexIdentities"/> numbers in object-id order.
    /// </summary>
    internal static (int[] KeyIds, int[] IndexIds) AllocateDeclarationObjectIds(
        Database database,
        IReadOnlyList<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)> pendingKeys,
        IReadOnlyList<PendingInlineIndex> pendingIndexes)
    {
        List<(bool IsIndex, int Position)> sequence = [];
        var keys = 0;
        for (var i = 0; i < pendingIndexes.Count; i++)
        {
            while (keys < pendingIndexes[i].KeysBefore && keys < pendingKeys.Count)
                sequence.Add((false, keys++));
            sequence.Add((true, i));
        }
        while (keys < pendingKeys.Count)
            sequence.Add((false, keys++));

        var keyIds = new int[pendingKeys.Count];
        var indexIds = new int[pendingIndexes.Count];
        var clustered = sequence.FindIndex(entry => entry.IsIndex
            ? pendingIndexes[entry.Position].IsClustered
            : IsClusteredKey(pendingKeys, entry.Position));
        if (clustered >= 0)
            Assign(sequence[clustered]);
        for (var s = sequence.Count - 1; s >= 0; s--)
        {
            if (s != clustered)
                Assign(sequence[s]);
        }
        return (keyIds, indexIds);

        void Assign((bool IsIndex, int Position) entry) =>
            (entry.IsIndex ? indexIds : keyIds)[entry.Position] = database.AllocateObjectId();
    }

    /// <summary>
    /// Parses what follows an inline index's name — <c>[UNIQUE] [CLUSTERED |
    /// NONCLUSTERED]</c>, the key list where <paramref name="columnLevelKey"/>
    /// doesn't already supply it, then the standalone index's tail (no
    /// <c>INCLUDE</c> at column level) — and settles the shape checks real
    /// runs on the statement: an inline Msg 10601, then Msg 1916 and a
    /// filtered <c>IGNORE_DUP_KEY</c>'s Msg 10618, each followed by Msg 1750
    /// state 0 (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    private static PendingInlineIndex ParseInlineIndexBody(ParserContext context, string indexName, string tableName, string? columnLevelKey, bool refusesColumnstore)
    {
        var isUnique = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Unique })
        {
            isUnique = true;
            context.MoveNextRequired();
        }
        var isClustered = ParseOptionalIndexClustering(context);
        var isHash = ConsumeHashKeyword(context);
        if (context.Token is UnquotedString { Span: var word } && word.Equals("COLUMNSTORE", StringComparison.OrdinalIgnoreCase))
            return ParseInlineColumnstoreIndexBody(context, indexName, tableName, isUnique, isClustered, columnLevelKey, refusesColumnstore);
        (string, bool)[] columns;
        if (columnLevelKey is not null)
        {
            columns = [(columnLevelKey, false)];
        }
        else
        {
            if (context.Token is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var keyList = new List<(string, bool)>();
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
                keyList.Add((keyColumn.Value, isDescending));
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            // ALTER TABLE … ADD INDEX may end the batch here.
            context.MoveNextOptional();
            columns = [.. keyList];
        }

        var (includeColumnNames, filter, filterDefinition, options) = ParseIndexTail(context, indexName, tableName, acceptsInclude: columnLevelKey is null, context.ColumnIndexOptions, rangeIndex: !isHash);
        if (isHash)
            options = options.AsHash();
        if (isClustered && includeColumnNames.Count > 0)
            throw SimulatedSqlException.IncludedColumnsOnClusteredIndex(inline: true);
        if (options.IgnoreDupKey && !isUnique)
            throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.IgnoreDupKeyOnNonUniqueIndex(), state: 0);
        if (options.IgnoreDupKey && filter is not null)
            throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.IgnoreDupKeyOnFilteredIndex("create", indexName, tableName), state: 0);
        return new PendingInlineIndex(indexName, isUnique, isClustered, columns, includeColumnNames, filter, filterDefinition, options);
    }

    /// <summary>
    /// The inline counterpart of <see cref="ParseCreateColumnstoreIndex"/>,
    /// cursor on the <c>COLUMNSTORE</c> word; a table variable or table type
    /// refuses the index outright (Msg 35310).
    /// </summary>
    private static PendingInlineIndex ParseInlineColumnstoreIndexBody(
        ParserContext context, string indexName, string tableName, bool isUnique, bool isClustered, string? columnLevelKey, bool refusesColumnstore)
    {
        if (refusesColumnstore)
            throw SimulatedSqlException.ColumnstoreIndexOnTableVariable();
        if (isUnique)
            throw SimulatedSqlException.ColumnstoreIndexCannotBeUnique();
        context.MoveNextRequired();
        List<string> columns = columnLevelKey is null ? [] : [columnLevelKey];
        if (context.Token is Operator { Character: '(' })
        {
            if (isClustered)
                throw SimulatedSqlException.ClusteredColumnstoreKeyList();
            columns = ParseColumnstoreColumnList(context);
        }
        else if (!isClustered && columns.Count == 0)
        {
            throw SimulatedSqlException.ColumnstoreKeyListMissing();
        }
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Include })
            throw SimulatedSqlException.ColumnstoreIndexIncludedColumns();
        List<string> order = [];
        if (context.Token is ReservedKeyword { Keyword: Keyword.Order })
        {
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            order = ParseColumnstoreColumnList(context);
        }
        var (_, filter, filterDefinition, options) = ParseIndexTail(context, indexName, tableName, acceptsInclude: false, IndexOptionStatement.CreateColumnstoreIndex, indexName);
        return new PendingInlineIndex(indexName, isUnique: false, isClustered, [.. columns.Select(static c => (c, false))], [], filter, filterDefinition, options)
        {
            IsColumnstore = true,
            ColumnstoreOrder = order,
        };
    }

    /// <summary>
    /// Parses a table-level inline index element <c>INDEX name [CLUSTERED |
    /// NONCLUSTERED] (col [ASC | DESC], …)</c>. Cursor on entry: the
    /// <c>INDEX</c> keyword; on exit: the trailing comma / closing paren of
    /// the table's column-element list.
    /// </summary>
    private static PendingInlineIndex ParseTableLevelInlineIndex(ParserContext context, string tableName, bool refusesColumnstore)
    {
        if (context.GetNextRequired() is not Name indexName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        return ParseInlineIndexBody(context, indexName.Value, tableName, columnLevelKey: null, refusesColumnstore);
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

    /// <summary>
    /// Rejects a <c>CREATE TABLE</c> that names two of its constraints alike
    /// (Msg 8168), or names one after an object <paramref name="schema"/>
    /// already holds or after the table itself (Msg 2714 then Msg 1750) —
    /// probed 2026-09-24 against SQL Server 2025. A temp table passes no schema
    /// but its session, <paramref name="tempSession"/>: its constraints share
    /// tempdb's namespace with the session's other temp tables and every
    /// global one (probed 2026-10-01); other sessions' aren't checked.
    /// </summary>
    private static void RejectTakenConstraintNames(
        Schema? schema,
        string tableName,
        List<HeapColumn?> heapColumns,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals, bool? Clustered, IndexOptions Options, bool[] Descending)> pendingKeys,
        List<(string? Name, BooleanExpression Predicate, string? InlineColumn, string Definition, bool NotForReplication)> pendingChecks,
        List<PendingForeignKey> pendingForeignKeys,
        SimulatedDbConnection? tempSession,
        List<PendingEdgeConstraint> pendingEdgeConstraints)
    {
        List<string> names = [];
        // An edge constraint's name shares the namespace too (probed
        // 2026-10-05 against SQL Server 2025).
        foreach (var edgeConstraint in pendingEdgeConstraints)
        {
            if (edgeConstraint.Name is { } name)
                names.Add(name);
        }
        foreach (var key in pendingKeys)
        {
            if (key.Name is { } name)
                names.Add(name);
        }
        foreach (var check in pendingChecks)
        {
            if (check.Name is { } name)
                names.Add(name);
        }
        foreach (var foreignKey in pendingForeignKeys)
        {
            if (foreignKey.ConstraintName is { } name)
                names.Add(name);
        }
        foreach (var column in heapColumns)
        {
            if (column?.DefaultConstraint is { IsSystemNamed: false } def)
                names.Add(def.Name);
        }

        for (var i = 0; i < names.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if ((schema?.Database.Collation ?? Collation.Baseline).Equals(names[j], names[i]))
                    throw SimulatedSqlException.DuplicateNameInStatement(names[i]);
            }
            if (schema is not null && (schema.HasNameInSharedNamespace(names[i]) || schema.Database.Collation.Equals(names[i], tableName)))
                throw SimulatedSqlException.ConstraintNameTaken(names[i]);
            if (tempSession is not null && TempdbHoldsConstraintNamed(tempSession, names[i]))
                throw SimulatedSqlException.ConstraintNameTaken(names[i]);
        }
    }

    /// <summary>Whether a temp table the session sees carries a constraint named <paramref name="name"/>.</summary>
    private static bool TempdbHoldsConstraintNamed(SimulatedDbConnection session, string name)
    {
        foreach (var (_, table) in session.TempTables)
        {
            if (Schema.TableHasConstraintNamed(table, name, Collation.Baseline))
                return true;
        }
        foreach (var (_, table) in session.Simulation.GlobalTempTables)
        {
            if (Schema.TableHasConstraintNamed(table, name, Collation.Baseline))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A schema whose smallest row can't fit SQL Server's 8060-byte in-row
    /// limit is Msg 1701. The smallest row is every stored non-sparse
    /// fixed-width column — <c>bit</c>s eight to a byte — plus 6 bytes of row
    /// header and column count and the null bitmap's byte per eight stored
    /// columns; a variable-length column adds nothing to it (probed
    /// 2026-10-01 against SQL Server 2025). Non-persisted computed columns
    /// have no row storage.
    /// </summary>
    internal static void RejectOversizedMinimumRow(HeapColumn[] columns, string tableName)
    {
        int fixedWidth = 0, bits = 0, stored = 0;
        foreach (var column in columns)
        {
            if (!column.IsStored)
                continue;
            stored++;
            if (column.IsSparse || !column.Type.IsFixedLength)
                continue;
            if (column.Type is BitSqlType)
                bits++;
            else
                fixedWidth += column.Type.FixedLength;
        }
        var overhead = 6 + ((stored + 7) / 8);
        var minimum = fixedWidth + ((bits + 7) / 8) + overhead;
        if (minimum > Heap.MaxRowSize)
            throw SimulatedSqlException.RowSizeExceedsMaximum(tableName, minimum, overhead, Heap.MaxRowSize);
    }

    /// <summary>
    /// Sends Msg 1708 when a table's largest row can pass the 8060-byte in-row
    /// limit — <paramref name="state"/> 2 creating it or adding columns, 1
    /// altering one. The largest row is the smallest
    /// (<see cref="RejectOversizedMinimumRow"/>) plus, once it has any
    /// variable-length column, 2 bytes for their count, 2 for each one's
    /// offset and each one's own largest value — at most 24 bytes, the
    /// pointer a value pushed off the row leaves behind, and 24 for a
    /// <c>max</c> or LOB type; a sparse column adds nothing (probed 2026-10-06
    /// against SQL Server 2025: <c>char(8000), char(40), varchar(10)</c> warns
    /// at 8061 bytes, while two <c>varchar(8000)</c> columns don't).
    /// </summary>
    internal static void WarnOfOversizedMaximumRow(BatchContext batch, HeapColumn[] columns, string tableName, byte state)
    {
        if (!batch.IsSkipping && MaximumRowExceedsLimit(columns))
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.MaximumRowSizeExceededMessage(batch, tableName, state));
    }

    /// <summary>Whether the largest row <paramref name="columns"/> can hold passes the in-row limit (see <see cref="WarnOfOversizedMaximumRow"/>).</summary>
    internal static bool MaximumRowExceedsLimit(HeapColumn[] columns)
    {
        int fixedWidth = 0, bits = 0, stored = 0, variable = 0, variableBytes = 0;
        foreach (var column in columns)
        {
            if (!column.IsStored)
                continue;
            stored++;
            if (column.IsSparse)
                continue;
            if (column.Type.IsFixedLength)
            {
                if (column.Type is BitSqlType)
                    bits++;
                else
                    fixedWidth += column.Type.FixedLength;
                continue;
            }
            variable++;
            var maxLength = BuiltInResources.GetSysColumnMetadata(column).MaxLength;
            variableBytes += maxLength < 0 ? 24 : Math.Min((int)maxLength, 24);
        }
        var maximum = fixedWidth + ((bits + 7) / 8) + 6 + ((stored + 7) / 8) + (variable > 0 ? 2 + (2 * variable) + variableBytes : 0);
        return maximum > Heap.MaxRowSize;
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
    internal static string AutoConstraintName(string tableName, KeyConstraintKind kind, int[] fullOrdinals, IReadOnlyList<HeapColumn> heapColumns)
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
        return $"{prefix}{AutoNameTablePart(tableName)}__{h:X16}";
    }

    /// <summary>
    /// The table's part of a system-generated constraint name: its first eight
    /// characters, which for a local temp table come from its underscore-padded
    /// tempdb name (<c>PK__#t______…</c>; probed 2026-09-26 against SQL Server
    /// 2025). A table variable's CHECK and DEFAULT keep all nine characters of
    /// its <c>#</c>-and-hex name where its keys keep eight
    /// (<c>CK__#B5F13E88__…</c>, <c>PK__#B5F13E8__…</c>; probed 2026-09-28).
    /// </summary>
    private static string AutoNameTablePart(string tableName, int length = 8)
    {
        if (BatchContext.IsLocalTempName(tableName))
            tableName = tableName.PadRight(length, '_');
        return tableName.Length > length ? tableName[..length] : tableName;
    }

    /// <summary>
    /// Refuses a FOREIGN KEY whose column pairs differ in type (Msg 1778),
    /// length, precision or scale (1753) or collation (1757).
    /// </summary>
    private static void RejectForeignKeyColumnMismatch(HeapTable childTable, int[] childOrdinals, HeapTable referencedTable, int[] referencedOrdinals, string foreignKeyName)
    {
        for (var i = 0; i < childOrdinals.Length; i++)
        {
            var child = childTable.Columns[childOrdinals[i]];
            var parent = referencedTable.Columns[referencedOrdinals[i]];
            var number = child.SystemTypeId != parent.SystemTypeId || (child.SystemTypeId == 240 && child.Type != parent.Type) ? 1778
                : BuiltInResources.GetSysColumnMetadata(child) != BuiltInResources.GetSysColumnMetadata(parent) ? 1753
                : SqlType.IsCollatedString(child.Type) && !string.Equals(child.Type.Collation?.Name, parent.Type.Collation?.Name, StringComparison.OrdinalIgnoreCase) ? 1757
                : 0;
            if (number != 0)
                throw SimulatedSqlException.ForeignKeyColumnMismatch(number, $"{referencedTable.Name}.{parent.Name}", $"{childTable.Name}.{child.Name}", foreignKeyName);
        }
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
            // Another database, or a temporary table from a permanent one, is
            // refused before the name resolves (probed 2026-10-01).
            if (pf.ReferencedTable.Count >= 3 && pf.ReferencedTable[pf.ReferencedTable.Count - 3] is { Length: > 0 } databaseName
                && !context.Batch.CurrentDatabase.Collation.Equals(databaseName, (childTable.OwningDatabase ?? context.Batch.CurrentDatabase).Name))
            {
                throw SimulatedSqlException.ForeignKeyCrossDatabase(pf.ReferencedTable.ToString());
            }
            if (pf.ReferencedTable.Leaf.StartsWith('#') && !childTable.Name.StartsWith('#'))
                throw SimulatedSqlException.ForeignKeyToTemporaryTable(pf.ConstraintName ?? pf.ChildColumnNames[0]);
            if (!context.Batch.TryResolveTable(pf.ReferencedTable, out var referencedTable) || referencedTable.IsTableVariable)
            {
                // Self-referencing FK: the table being created is referenced
                // by 1-/2-part name with the table's own leaf. The table is
                // already in its dict at this point, so TryResolveTable
                // succeeds for the self-reference path; falling through means
                // the referenced name truly doesn't resolve.
                throw SimulatedSqlException.ForeignKeyReferencesInvalidTable(
                    pf.ConstraintName ?? AutoForeignKeyName(childTable.Name, pf.ChildColumnNames, pending.IndexOf(pf)),
                    pf.ReferencedTable.ToString());
            }
            // A memory-optimized table's foreign key reaches only another
            // memory-optimized table, takes no referential action and no NOT
            // FOR REPLICATION (probed 2026-10-02 against SQL Server 2025).
            if (childTable.IsMemoryOptimized || referencedTable.IsMemoryOptimized)
            {
                if (childTable.IsMemoryOptimized != referencedTable.IsMemoryOptimized)
                    throw SimulatedSqlException.ForeignKeyAcrossTableKinds();
                if ((pf.DeleteAction != ReferentialAction.NoAction ? pf.DeleteAction : pf.UpdateAction) is var action and not ReferentialAction.NoAction)
                    throw SimulatedSqlException.NotSupportedWithMemoryOptimized($"The option '{action switch { ReferentialAction.Cascade => "CASCADE", ReferentialAction.SetNull => "SET NULL", _ => "SET DEFAULT" }}'", 134);
                if (pf.NotForReplication)
                    throw SimulatedSqlException.NotSupportedWithMemoryOptimized("The option 'NOT FOR REPLICATION'", 128);
            }
            // FK column count = referenced column count. If the referenced
            // column list was omitted, default to the parent's PRIMARY KEY
            // columns (real SQL Server's behavior).
            int[] refOrdinals;
            if (pf.ReferencedColumnNames.Length == 0)
            {
                // The implied column list is the referenced table's primary
                // key, so a table carrying no primary key — a UNIQUE
                // constraint included — reports real's Msg 1773 rather than
                // the explicit-list Msg 1776, naming the object as the
                // statement wrote it.
                var pk = ResolvePrimaryKey(referencedTable)
                    ?? throw SimulatedSqlException.ForeignKeyImplicitReferenceWithoutPrimaryKey(
                        pf.ConstraintName ?? AutoForeignKeyName(childTable.Name, pf.ChildColumnNames, pending.IndexOf(pf)),
                        pf.ReferencedTable.ToString());
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
                    {
                        throw SimulatedSqlException.ForeignKeyReferencesInvalidColumn(
                            pf.ConstraintName ?? AutoForeignKeyName(childTable.Name, pf.ChildColumnNames, pending.IndexOf(pf)),
                            pf.ReferencedColumnNames[i],
                            pf.ReferencedTable.Leaf);
                    }
                    refOrdinals[i] = found;
                }
            }

            if (refOrdinals.Length != pf.ChildFullOrdinals.Length)
            {
                throw pf.ReferencedColumnNames.Length > 0
                    ? SimulatedSqlException.ForeignKeyColumnCountMismatch(childTable.Name)
                    : SimulatedSqlException.ForeignKeyNoMatchingKey(
                        referencedTable.Name,
                        pf.ConstraintName ?? AutoForeignKeyName(childTable.Name, pf.ChildColumnNames, pending.IndexOf(pf)));
            }

            // A memory-optimized table's foreign key must reference the primary
            // key itself (Msg 10780).
            if (childTable.IsMemoryOptimized && (ResolvePrimaryKey(referencedTable) is not { } referencedKey || !referencedKey.FullOrdinals.AsSpan().SequenceEqual(refOrdinals)))
            {
                throw SimulatedSqlException.MemoryOptimizedForeignKeyNeedsPrimaryKey(
                    referencedTable.Name,
                    pf.ConstraintName ?? AutoForeignKeyName(childTable.Name, pf.ChildColumnNames, pending.IndexOf(pf)));
            }

            // Referenced columns must form a PRIMARY KEY or UNIQUE constraint,
            // or an enabled unfiltered unique index (Msg 1776), matched in
            // declared order — see ReferencedColumnsFormKey.
            // The message names the table as the key wrote it (probed
            // 2026-10-04 against SQL Server 2025: 'dbo.t' for dbo.t).
            if (!ReferencedColumnsFormKey(referencedTable, refOrdinals))
            {
                throw SimulatedSqlException.ForeignKeyNoMatchingKey(
                    pf.ReferencedTable.ToString(),
                    pf.ConstraintName ?? AutoForeignKeyName(childTable.Name, pf.ChildColumnNames, pending.IndexOf(pf)));
            }

            var fkName = pf.ConstraintName ?? AutoForeignKeyName(childTable.Name, pf.ChildColumnNames, pending.IndexOf(pf));
            RejectForeignKeyColumnMismatch(childTable, pf.ChildFullOrdinals, referencedTable, refOrdinals, fkName);

            // The referenced columns need REFERENCES, checked once the key is
            // known to match (probed 2026-10-04 against SQL Server 2025: Msg
            // 1776 comes first, then Msg 230 per column, or Msg 229 with no
            // column reachable), each followed by Msg 1088 naming the table as
            // written and Msg 1750.
            if (!context.Batch.IsSkipping)
            {
                var referenced = new ColumnReadTarget(referencedTable);
                foreach (var ordinal in refOrdinals)
                    _ = referenced.Ordinals.Add(ordinal + 1);
                if (PermissionEnforcement.ColumnsDenial(context.Batch, Permission.References, referenced) is { } referencesDenied)
                    throw SimulatedSqlException.ForeignKeyReferencesDenied(referencesDenied, pf.ReferencedTable.ToString());
            }

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

            if (pf.DeleteAction == ReferentialAction.SetNull || pf.UpdateAction == ReferentialAction.SetNull)
            {
                foreach (var childOrdinal in pf.ChildFullOrdinals)
                {
                    if (!childTable.Columns[childOrdinal].Nullable)
                        throw SimulatedSqlException.ForeignKeySetNullOnNotNullColumn(fkName);
                }
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

            // The mirror of the Msg 2113 gate on CREATE TRIGGER, matched the
            // same way: only a CASCADE conflicts, and only with an INSTEAD OF
            // trigger covering that same verb.
            var cascadingVerbs =
                (pf.DeleteAction == ReferentialAction.Cascade ? TriggerActions.Delete : 0)
                | (pf.UpdateAction == ReferentialAction.Cascade ? TriggerActions.Update : 0);
            if (cascadingVerbs != 0 && HasInsteadOfTriggerFor(context.CurrentDatabase, childTable, cascadingVerbs))
                throw SimulatedSqlException.CascadingForeignKeyOnInsteadOfTriggerTable(fkName, childTable.Name);

            var fk = new ForeignKey(
                fkName,
                context.CurrentDatabase.AllocateObjectId(),
                childTable,
                pf.ChildFullOrdinals,
                referencedTable,
                refOrdinals,
                pf.DeleteAction,
                pf.UpdateAction,
                isSystemNamed: pf.ConstraintName is null,
                createDate: context.Batch.CurrentStatement.UtcNow)
            {
                NotForReplication = pf.NotForReplication,
                IsNotTrusted = pf.NotForReplication,
            };
            resolved.Add(fk);

            // Cascade-cycle / multiple-cascade-paths check (Msg 1785).
            if (fk.DeleteAction != ReferentialAction.NoAction || fk.UpdateAction != ReferentialAction.NoAction)
            {
                if (CascadeWouldFormCycleOrDuplicate(fk, resolved, context))
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

        // A unique index stands in for a constraint when it is enabled and
        // unfiltered, its keys matched in the same declared order; DESC keys
        // and INCLUDE columns don't matter (probed 2026-10-02 against SQL
        // Server 2025).
        foreach (var index in referencedTable.Indexes)
        {
            if (index is { IsUnique: true, Filter: null, IsDisabled: false }
                && index.KeyFullOrdinals.Length == refFullOrdinals.Length
                && SameSequence(index.KeyFullOrdinals, refFullOrdinals))
            {
                return true;
            }
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
    /// True when <paramref name="newFk"/>'s referential actions would make the
    /// database's cascade graph one real SQL Server refuses with Msg 1785.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The graph runs parent → child: deleting (or updating the key of) a
    /// parent row fires the action on the matching child rows. An edge
    /// <em>reaches</em> its child when its action for that operation isn't
    /// NO_ACTION, but only CASCADE <em>propagates</em> — SET NULL and SET
    /// DEFAULT write the child row rather than deleting it, so nothing fires
    /// from the child in turn. One operation on one root table then has to
    /// land on each table at most once: a second arrival is the "multiple
    /// cascade paths" half of the message, and an arrival back at the root is
    /// the "cycles" half.
    /// </para>
    /// <para>
    /// Delete and update are analysed as separate trees, since a constraint
    /// can cascade on one and not the other. Roots are restricted to the
    /// tables that can reach <paramref name="newFk"/>'s child: the graph was
    /// valid before this edge, so any new violation has to run through it.
    /// </para>
    /// <para>
    /// Probed against SQL Server 2025 over independently-built graphs — a
    /// shared one lets an earlier rejection change what the next case is even
    /// asking. What the boundary turns on is propagation, and both halves of
    /// the rule show it. A parent whose child is reached by SET NULL and whose
    /// grandchild is reached by CASCADE from that child is accepted, while the
    /// same shape with CASCADE on the first hop is refused. A two-table loop of
    /// SET NULLs is likewise accepted, where the same loop with one edge
    /// CASCADE is refused: there the delete travels round it and the SET NULL
    /// closes the circle. Both accepted shapes turn up in production schemas,
    /// so treating every non-NO_ACTION edge as propagating refuses graphs real
    /// creates.
    /// </para>
    /// </remarks>
    private static bool CascadeWouldFormCycleOrDuplicate(ForeignKey newFk, List<ForeignKey> resolvedDuringThisStatement, ParserContext context)
    {
        // Every committed FK in the database plus the ones queued earlier in
        // this statement — CREATE TABLE resolves its whole FK list before
        // committing any of it, so neither set alone is the full graph.
        var edges = new List<ForeignKey>();
        foreach (var (_, schema) in context.CurrentDatabase.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
                edges.AddRange(table.OutgoingForeignKeys);
        }
        edges.AddRange(resolvedDuringThisStatement);

        return CascadeTreeIsInvalid(edges, newFk, forDelete: true)
            || CascadeTreeIsInvalid(edges, newFk, forDelete: false);
    }

    private static ReferentialAction ActionFor(ForeignKey fk, bool forDelete) =>
        forDelete ? fk.DeleteAction : fk.UpdateAction;

    /// <summary>
    /// Runs both Msg 1785 rules over the delete tree or the update tree of
    /// <paramref name="edges"/>.
    /// </summary>
    private static bool CascadeTreeIsInvalid(List<ForeignKey> edges, ForeignKey newFk, bool forDelete)
    {
        var reaching = new List<ForeignKey>();
        foreach (var e in edges)
        {
            if (ActionFor(e, forDelete) != ReferentialAction.NoAction)
                reaching.Add(e);
        }
        if (reaching.Count == 0)
            return false;
        if (ActionFor(newFk, forDelete) == ReferentialAction.NoAction)
            return false;

        var childrenOf = new Dictionary<HeapTable, List<ForeignKey>>(ReferenceEqualityComparer.Instance);
        foreach (var e in reaching)
        {
            if (!childrenOf.TryGetValue(e.ReferencedTable, out var list))
                childrenOf[e.ReferencedTable] = list = [];
            list.Add(e);
        }

        foreach (var root in CascadeAncestorsOf(newFk.ChildTable, reaching))
        {
            if (CascadeReachesATableTwice(root, childrenOf, forDelete))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Every table from which <paramref name="target"/> is reachable, plus
    /// <paramref name="target"/> itself — the only roots whose traversal can
    /// involve a newly added edge into it.
    /// </summary>
    private static List<HeapTable> CascadeAncestorsOf(HeapTable target, List<ForeignKey> reaching)
    {
        var parentsOf = new Dictionary<HeapTable, List<HeapTable>>(ReferenceEqualityComparer.Instance);
        foreach (var e in reaching)
        {
            if (!parentsOf.TryGetValue(e.ChildTable, out var list))
                parentsOf[e.ChildTable] = list = [];
            list.Add(e.ReferencedTable);
        }
        var seen = new HashSet<HeapTable>(ReferenceEqualityComparer.Instance) { target };
        var order = new List<HeapTable> { target };
        for (var i = 0; i < order.Count; i++)
        {
            foreach (var parent in parentsOf.GetValueOrDefault(order[i]) ?? [])
            {
                if (seen.Add(parent))
                    order.Add(parent);
            }
        }
        return order;
    }

    /// <summary>
    /// Whether one operation on <paramref name="root"/> would fire referential
    /// actions at the same table twice, or arrive back at the root. Traversal
    /// leaves a table only when it was CASCADE-reached — a SET NULL / SET
    /// DEFAULT arrival writes the row and stops there, which is why a loop of
    /// them never travels.
    /// </summary>
    private static bool CascadeReachesATableTwice(HeapTable root, Dictionary<HeapTable, List<ForeignKey>> childrenOf, bool forDelete)
    {
        var reachedBy = new Dictionary<HeapTable, int>(ReferenceEqualityComparer.Instance);
        var expanded = new HashSet<HeapTable>(ReferenceEqualityComparer.Instance) { root };
        var frontier = new Queue<HeapTable>();
        frontier.Enqueue(root);
        while (frontier.Count > 0)
        {
            var table = frontier.Dequeue();
            foreach (var edge in childrenOf.GetValueOrDefault(table) ?? [])
            {
                var child = edge.ChildTable;
                // Coming back to the root closes a loop the operation would
                // travel — a self-reference is the one-edge case.
                if (ReferenceEquals(child, root))
                    return true;
                var count = reachedBy.GetValueOrDefault(child) + 1;
                if (count > 1)
                    return true;
                reachedBy[child] = count;
                if (ActionFor(edge, forDelete) == ReferentialAction.Cascade && expanded.Add(child))
                    frontier.Enqueue(child);
            }
        }
        return false;
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

    /// <summary>
    /// Parses a <c>DEFAULT</c> clause's expression — the inline column form and
    /// the <c>ALTER TABLE … ADD CONSTRAINT … DEFAULT … FOR</c> form alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two flags ride the parse. <see cref="ParserContext.InDefaultClause"/> is
    /// what <c>NEWSEQUENTIALID()</c>'s grammar gate reads — the flag is the only
    /// thing distinguishing a legal DEFAULT context from an illegal scalar use
    /// of the function.
    /// </para>
    /// <para>
    /// <see cref="ParserContext.ScalarOnlyOperand"/> is the other: a DEFAULT
    /// expression has no column scope at all, so a name inside it is
    /// <b>Msg 128</b> and a subquery is <b>Msg 1046</b>, whichever the
    /// left-to-right reading meets first. Real settles both while parsing, so
    /// they fire whatever the table holds — an empty table, a name that <i>is</i>
    /// a column of the table, and a name that is nothing at all all report the
    /// same Msg 128 (probe-confirmed at all three sites: <c>CREATE TABLE</c>'s
    /// inline form, <c>ALTER TABLE … ADD &lt;column&gt; DEFAULT</c>, and the
    /// named-constraint <c>… DEFAULT (v) FOR w</c>).
    /// </para>
    /// </remarks>
    private static Expression ParseDefaultClauseExpression(ParserContext context, bool tempTable = false, bool tableVariable = false)
    {
        using var defaultClause = ParserScope.Enter(ref context.InDefaultClause, true);
        using var inTempdb = ParserScope.Enter(ref context.DefaultResolvesInTempdb, tempTable);
        using var ofTableVariable = ParserScope.Enter(ref context.DefaultOfTableVariable, tableVariable);
        using var scalarOnly = context.EnterScalarOnlyOperand();
        var expression = Expression.Parse(context);
        return context.ScalarOnlyColumnReference is null
            ? expression
            : throw Expression.ScalarOnlyOperandError(context);
    }

    /// <summary>
    /// Whether <paramref name="table"/> carries an INSTEAD OF trigger covering
    /// any of <paramref name="verbs"/> — the half of the cascade conflict a
    /// foreign-key declaration has to check.
    /// </summary>
    private static bool HasInsteadOfTriggerFor(Database database, HeapTable table, TriggerActions verbs)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, trigger) in schema.Triggers)
            {
                if (ReferenceEquals(trigger.Parent, table)
                    && trigger.Timing == TriggerTiming.InsteadOf
                    && (trigger.Actions & verbs) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The two preconditions real puts on a <b>non-persisted</b> computed
    /// column named as an index, statistics or constraint key: the expression
    /// has to be deterministic (<b>Msg 2729</b>) and precise (<b>Msg 2799</b>).
    /// Together they are what make keying on one well defined — the value is
    /// then a reproducible function of the row's stored columns, which is what
    /// the enforcement paths evaluate per row.
    /// </summary>
    /// <remarks>
    /// A persisted computed column stores its value and takes neither check.
    /// <paramref name="viaConstraint"/> appends the sentence real adds when the
    /// failure arrived through a PRIMARY KEY / UNIQUE constraint rather than a
    /// bare CREATE INDEX; <paramref name="indexName"/> is what Msg 2799 quotes,
    /// which for a constraint is the constraint's own name.
    /// Probe-confirmed against SQL Server 2025, including that the pair gates a
    /// <em>non-unique</em> index and <c>CREATE STATISTICS</c> just as it gates a
    /// unique one.
    /// </remarks>
    internal static void RejectComputedKeyColumnNotIndexable(
        BatchContext batch, HeapTable table, HeapColumn column, string indexName, bool viaConstraint) =>
        RejectComputedKeyColumnNotIndexable(
            batch.CurrentDatabase,
            table.Columns,
            // Bare here, unlike most of ALTER TABLE's three-part messages — real
            // names `t` in both Msg 2729 and Msg 2799 (probed 2026-10-01).
            table.Name,
            column,
            indexName,
            viaConstraint);

    /// <inheritdoc cref="RejectComputedKeyColumnNotIndexable(BatchContext, HeapTable, HeapColumn, string, bool)"/>
    internal static void RejectComputedKeyColumnNotIndexable(
        Database database,
        IReadOnlyList<HeapColumn> scopeColumns,
        string tableName,
        HeapColumn column,
        string indexName,
        bool viaConstraint)
    {
        if (column.Computed is null || column.IsPersisted || column.ComputedDefinition is not { } definition)
            return;

        HeapColumn[] scope = [.. scopeColumns];
        if (!Schemas.ModuleDeterminism.IsComputedColumnDeterministic(database, scope, definition))
            throw SimulatedSqlException.ComputedColumnNotDeterministicForIndex(column.Name, tableName, viaConstraint);
        if (Schemas.ModuleDeterminism.ComputedColumnAccessesData(database, definition))
            throw SimulatedSqlException.ComputedColumnAccessesDataForIndex(column.Name, tableName, viaConstraint);
        if (!Schemas.ComputedColumnPrecision.IsPrecise(scope, column.Type, definition))
            throw SimulatedSqlException.ComputedColumnImpreciseForIndex(indexName, tableName, column.Name, viaConstraint);
    }

    /// <summary>
    /// Raises <b>Msg 4936</b> for a <c>PERSISTED</c> computed column whose
    /// expression real classifies nondeterministic. The determinism question is
    /// the same one <c>OBJECTPROPERTY(…, 'IsDeterministic')</c> answers for a
    /// schema-bound module, and it is asked at every declaration site —
    /// <c>CREATE TABLE</c>'s inline form and <c>ALTER TABLE … ADD</c> alike
    /// (probe-confirmed, including that <c>CONVERT(varchar(20), &lt;datetime
    /// col&gt;, 112)</c> is persistable where style 0 is not, which is the
    /// conversion-style rule the shared walk already carries).
    /// </summary>
    private static void RejectNondeterministicPersisted(
        ParserContext context, List<HeapColumn?> scope, string columnName, string tableName, string definition)
    {
        var resolved = new List<HeapColumn>(scope.Count);
        foreach (var column in scope)
        {
            if (column is not null)
                resolved.Add(column);
        }
        if (!Schemas.ModuleDeterminism.IsComputedColumnDeterministic(context.CurrentDatabase, [.. resolved], definition))
            throw SimulatedSqlException.ComputedColumnCannotBePersisted(columnName, tableName);
    }

    /// <summary>A column a table-level <c>PRIMARY KEY</c> promotes to NOT NULL, otherwise as declared.</summary>
    private static HeapColumn WithNotNull(HeapColumn column) =>
        new(
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
            collation: column.Collation,
            computedDefinition: column.ComputedDefinition,
            isRowGuidCol: column.IsRowGuidCol,
            spelledNumeric: column.SpelledNumeric)
        {
            IsSparse = column.IsSparse,
            DefaultConstraint = column.DefaultConstraint,
            BoundDefault = column.BoundDefault,
            BoundRule = column.BoundRule,
            XmlSchemaCollection = column.XmlSchemaCollection,
            XmlDocument = column.XmlDocument,
            AliasType = column.AliasType,
            MaskingFunction = column.MaskingFunction,
        };

    /// <summary>
    /// A permanent table's column takes REFERENCES on its alias type (Msg
    /// 15247 state 4) and on its XML schema collection (Msg 229 then Msg 15247),
    /// checked once the CREATE TABLE gates pass (probed 2026-10-04 against SQL
    /// Server 2025).
    /// </summary>
    private static void RequireColumnTypeReferences(BatchContext batch, List<HeapColumn?> columns)
    {
        if (batch.Connection.Security.EffectiveIsDbo)
            return;
        foreach (var column in columns)
        {
            if (column is null)
                continue;
            if (column.AliasType is { } aliasType
                && !PermissionEnforcement.HoldsPermission(batch, aliasType.Schema.Database, Permission.References, PermissionChecker.ClassType, aliasType.UserTypeId, aliasType.Schema.SchemaId))
            {
                throw SimulatedSqlException.UserDoesNotHavePermission(state: 4);
            }
            if (column.XmlSchemaCollection is { } collection)
            {
                try
                {
                    PermissionEnforcement.CheckXmlSchemaCollection(batch, collection, "REFERENCES");
                }
                catch (SimulatedSqlException denied)
                {
                    throw SimulatedSqlException.Aggregate([denied, SimulatedSqlException.UserDoesNotHavePermission()]);
                }
            }
        }
    }
}
