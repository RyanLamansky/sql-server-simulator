using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// DBCC's consistency checks and table maintenance: CHECKDB / CHECKFILEGROUP /
// CHECKTABLE / CHECKALLOC / CHECKCATALOG, CHECKCONSTRAINTS, UPDATEUSAGE,
// CLEANTABLE, DBREINDEX and INDEXDEFRAG. A simulated database is always
// consistent, so each check reports a healthy one; what depends on physical
// storage (page counts, extents, tempdb estimates) is read off the simulator's
// own heap pages. Probed 2026-09-28 against SQL Server 2025.
partial class Simulation
{
    private static readonly string[] CheckResultColumnNames =
    [
        "Error", "Level", "State", "MessageText", "RepairLevel", "Status", "DbId", "DbFragId", "ObjectId", "IndexId", "PartitionId",
        "AllocUnitId", "RidDbId", "RidPruId", "File", "Page", "Slot", "RefDbId", "RefPruId", "RefFile", "RefPage", "RefSlot", "Allocation",
    ];

    /// <summary>
    /// The tempdb estimates <c>WITH ESTIMATEONLY</c> reports for a database
    /// with no user tables — what a freshly created database reports on SQL
    /// Server 2025, its system tables' share — to which each user table's pages
    /// are added.
    /// </summary>
    private const long CheckDatabaseEstimateBaseKilobytes = 2435, CheckAllocEstimateBaseKilobytes = 351;

    /// <summary>
    /// <c>DBCC CHECKDB [( database [, NOINDEX] )]</c> and <c>DBCC CHECKFILEGROUP
    /// [( filegroup [, NOINDEX] )]</c> over a healthy database: the database's
    /// heading, the eight Service Broker lines (<c>CHECKDB</c> only), each user
    /// table's row and page count in object-id order, and the zero-error
    /// summary. Real also lists its system base tables, which have no
    /// counterpart here. <c>PHYSICAL_ONLY</c> keeps the heading and summary,
    /// <c>ESTIMATEONLY</c> answers Msg 5281 instead, and <c>TABLERESULTS</c>
    /// returns the count and summary lines as rows.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccCheckDatabase(BatchContext batch, DbccInvocation dbcc, string command)
    {
        var isDatabase = command == "CHECKDB";
        dbcc.AllowOptions(DbccOptions.NoInfoMessages | DbccOptions.AllErrorMessages | DbccOptions.TabLock | DbccOptions.EstimateOnly
            | DbccOptions.PhysicalOnly | DbccOptions.TableResults | DbccOptions.MaxDop
            | (isDatabase ? DbccOptions.DataPurity | DbccOptions.ExtendedLogicalChecks : DbccOptions.None));
        if (dbcc.Has(DbccOptions.PhysicalOnly) && dbcc.Has(DbccOptions.DataPurity | DbccOptions.ExtendedLogicalChecks))
            throw SimulatedSqlException.DbccWithOptionNotValid(5);
        dbcc.RequireArgumentCount(0, 2);
        RejectRepairArgument(dbcc, 1);

        var database = isDatabase ? ResolveDbccDatabase(batch, dbcc.Arguments.Count > 0 ? dbcc.Arguments[0] : null, 1) : batch.CurrentDatabase;
        RequireDbccDatabaseOwner(batch, database, isDatabase ? "checkdb" : "checkfilegroup");
        var filegroupId = isDatabase ? (int?)null : ResolveDbccFilegroup(batch, database, dbcc);
        batch.Connection.LastStatementRowCount = 0;

        var informational = !dbcc.Has(DbccOptions.NoInfoMessages);
        var tables = UserTablesByObjectId(database, filegroupId);
        if (dbcc.Has(DbccOptions.EstimateOnly))
        {
            // The estimate is the answer, so NO_INFOMSGS doesn't silence it.
            if (informational)
                Info(batch, SimulatedSqlException.DbccResultsForMessage(batch, database.Name));
            Info(batch, SimulatedSqlException.EstimatedTempdbSpaceMessage(batch, command, database.Name, CheckDatabaseEstimateBaseKilobytes + TotalPages(tables)));
            return DbccCompleted(batch, dbcc, []);
        }
        if (!informational)
            return [];

        var physicalOnly = dbcc.Has(DbccOptions.PhysicalOnly);
        var databaseId = DatabaseIdOf(batch.Connection.Simulation, database);
        if (dbcc.Has(DbccOptions.TableResults))
        {
            var rows = new List<SqlValue[]>();
            if (isDatabase && !physicalOnly)
            {
                foreach (var text in SimulatedSqlException.ServiceBrokerCheckTexts)
                    rows.Add(CheckResultRow(8997, text, databaseId, objectId: 0, indexId: -1, locatesRow: true));
            }
            if (!physicalOnly)
            {
                foreach (var (table, display) in tables)
                    rows.Add(CheckResultRow(2593, SimulatedSqlException.RowsInPagesText(table.Heap.RowCount, table.Heap.Pages.Count, display), databaseId, table.ObjectId, table.HasClusteredIndex() ? 1 : 0, locatesRow: true));
            }
            rows.Add(CheckResultRow(8989, SimulatedSqlException.CheckFoundErrorsText(command, database.Name), databaseId, objectId: 0, indexId: 0, locatesRow: false));
            var outcomes = DbccRows(batch, CheckResultSchema(batch), CheckResultColumnNames, rows);
            batch.Connection.LastStatementRowCount = 0;
            return DbccCompleted(batch, dbcc, outcomes);
        }

        Info(batch, SimulatedSqlException.DbccResultsForMessage(batch, database.Name));
        if (isDatabase && dbcc.Arguments.Count > 1)
            Info(batch, SimulatedSqlException.NoIndexCheckWarningMessage(batch));
        if (!physicalOnly)
        {
            if (isDatabase)
            {
                foreach (var text in SimulatedSqlException.ServiceBrokerCheckTexts)
                    Info(batch, SimulatedSqlException.ServiceBrokerCheckMessage(batch, text));
            }
            foreach (var (table, display) in tables)
            {
                Info(batch, SimulatedSqlException.DbccResultsForMessage(batch, display));
                Info(batch, SimulatedSqlException.RowsInPagesMessage(batch, table.Heap.RowCount, table.Heap.Pages.Count, display));
            }
        }
        Info(batch, SimulatedSqlException.CheckFoundErrorsMessage(batch, command, database.Name));
        return DbccCompleted(batch, dbcc, []);
    }

    /// <summary>
    /// <c>DBCC CHECKTABLE ( table [, NOINDEX | index_id] )</c>: the table's row
    /// and page count, or with <c>TABLERESULTS</c> the same as a row. A
    /// <c>#temp</c> table goes by its internal padded name. A view is Msg 5239,
    /// a missing name Msg 2501, and a principal that doesn't own the table
    /// Msg 2557.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccCheckTable(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages | DbccOptions.AllErrorMessages | DbccOptions.TabLock | DbccOptions.EstimateOnly
            | DbccOptions.PhysicalOnly | DbccOptions.TableResults | DbccOptions.MaxDop | DbccOptions.DataPurity | DbccOptions.ExtendedLogicalChecks);
        if (dbcc.Has(DbccOptions.PhysicalOnly) && dbcc.Has(DbccOptions.DataPurity | DbccOptions.ExtendedLogicalChecks))
            throw SimulatedSqlException.DbccWithOptionNotValid(5);
        dbcc.RequireArgumentCount(1, 2);
        RejectRepairArgument(dbcc, 1);
        var (table, display, database) = ResolveDbccTable(batch, dbcc, 0, allowTemporary: true);
        if (!PermissionEnforcement.HasObjectControl(batch, database, table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.DbccObjectPermissionDenied(batch.Connection.Security.Effective.DatabasePrincipalName, "checktable", table.Name, 3);
        batch.Connection.LastStatementRowCount = 0;
        if (dbcc.Has(DbccOptions.EstimateOnly))
        {
            Info(batch, SimulatedSqlException.EstimatedTempdbSpaceMessage(batch, "CHECKTABLE", batch.CurrentDatabase.Name, Math.Max(1, table.Heap.Pages.Count)));
            return DbccCompleted(batch, dbcc, []);
        }
        if (dbcc.Has(DbccOptions.NoInfoMessages))
            return [];
        if (dbcc.Has(DbccOptions.PhysicalOnly))
            return DbccCompleted(batch, dbcc, []);
        if (dbcc.Has(DbccOptions.TableResults))
        {
            var databaseId = DatabaseIdOf(batch.Connection.Simulation, database);
            var outcomes = DbccRows(batch, CheckResultSchema(batch), CheckResultColumnNames,
                [CheckResultRow(2593, SimulatedSqlException.RowsInPagesText(table.Heap.RowCount, table.Heap.Pages.Count, display), databaseId, table.ObjectId, table.HasClusteredIndex() ? 1 : 0, locatesRow: true)]);
            batch.Connection.LastStatementRowCount = 0;
            return DbccCompleted(batch, dbcc, outcomes);
        }
        Info(batch, SimulatedSqlException.DbccResultsForMessage(batch, display));
        Info(batch, SimulatedSqlException.RowsInPagesMessage(batch, table.Heap.RowCount, table.Heap.Pages.Count, display));
        return DbccCompleted(batch, dbcc, []);
    }

    /// <summary>
    /// <c>DBCC CHECKALLOC [( database [, NOINDEX] )]</c>: the heading, the
    /// data file's and the database's extent and page totals, and the
    /// zero-error summary. The totals count every user table's data and LOB
    /// pages, in whole uniform extents of eight; real's per-object and
    /// per-allocation-unit lines have no counterpart here.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccCheckAlloc(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages | DbccOptions.AllErrorMessages | DbccOptions.TabLock | DbccOptions.EstimateOnly | DbccOptions.TableResults);
        dbcc.RequireArgumentCount(0, 2);
        RejectRepairArgument(dbcc, 1);
        var database = ResolveDbccDatabase(batch, dbcc.Arguments.Count > 0 ? dbcc.Arguments[0] : null, 1);
        RequireDbccDatabaseOwner(batch, database, "checkalloc");
        batch.Connection.LastStatementRowCount = 0;
        var tables = UserTablesByObjectId(database, filegroupId: null);
        long used = 0;
        foreach (var (table, _) in tables)
            used += table.Heap.Pages.Count + table.Heap.LobPages.Count;
        if (dbcc.Has(DbccOptions.EstimateOnly))
        {
            Info(batch, SimulatedSqlException.EstimatedTempdbSpaceMessage(batch, "CHECKALLOC", database.Name, CheckAllocEstimateBaseKilobytes + used));
            return DbccCompleted(batch, dbcc, []);
        }
        if (dbcc.Has(DbccOptions.NoInfoMessages))
            return [];
        if (dbcc.Has(DbccOptions.TableResults))
            throw new NotSupportedException("DBCC CHECKALLOC WITH TABLERESULTS isn't modeled.");
        var extents = (used + 7) / 8;
        Info(batch, SimulatedSqlException.DbccResultsForMessage(batch, database.Name));
        foreach (var message in SimulatedSqlException.AllocationTotalsMessages(batch, extents, used, extents * 8))
            Info(batch, message);
        Info(batch, SimulatedSqlException.CheckFoundErrorsMessage(batch, "CHECKALLOC", database.Name));
        return DbccCompleted(batch, dbcc, []);
    }

    /// <summary><c>DBCC CHECKCATALOG [( database )]</c>: a consistent catalog reports nothing but Msg 2528.</summary>
    private static List<SimulatedStatementOutcome> RunDbccCheckCatalog(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        dbcc.RequireArgumentCount(0, 1);
        var database = ResolveDbccDatabase(batch, dbcc.Arguments.Count > 0 ? dbcc.Arguments[0] : null, 1);
        RequireDbccDatabaseOwner(batch, database, "checkcatalog");
        return DbccCompleted(batch, dbcc, []);
    }

    /// <summary>
    /// <c>DBCC UPDATEUSAGE ( database | 0 [, table [, index]] ) [WITH
    /// NO_INFOMSGS, COUNT_ROWS]</c>: the simulator's page and row counts are
    /// never stale, so there is nothing to correct and no correction to report.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccUpdateUsage(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages | DbccOptions.CountRows);
        dbcc.RequireArgumentCount(1, 3);
        var database = ResolveDbccDatabase(batch, dbcc.Arguments[0], 1);
        RequireDbccDatabaseOwner(batch, database, "updateusage");
        if (dbcc.Arguments.Count > 1)
        {
            var (table, _, _) = ResolveDbccTable(batch, dbcc, 1, allowTemporary: false);
            if (dbcc.Arguments.Count > 2 && dbcc.StringArgument(batch, 2) is { } indexName && FindIndex(table, indexName) is null)
                throw SimulatedSqlException.DbccIndexNotFound(indexName, table.Name, 8);
        }
        return DbccCompleted(batch, dbcc, []);
    }

    /// <summary>
    /// <c>DBCC CLEANTABLE ( database | 0, table [, batch_size] )</c>: a dropped
    /// variable-length column's space is reclaimed as it drops here, so there
    /// is nothing left to clean. Takes <c>ALTER</c> on the table (Msg 229).
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccCleanTable(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        dbcc.RequireArgumentCount(2, 3);
        _ = ResolveDbccDatabase(batch, dbcc.Arguments[0], 1);
        var (table, _, database) = ResolveDbccTable(batch, dbcc, 1, allowTemporary: false);
        RequireDbccTableAlter(batch, database, table);
        return DbccCompleted(batch, dbcc, []);
    }

    /// <summary>
    /// <c>DBCC DBREINDEX ( table [, index_name [, fillfactor]] )</c>: the
    /// simulator's indexes never fragment, so rebuilding changes nothing, but
    /// <c>@@ROWCOUNT</c> reads the rows the rebuild would have read, as real's
    /// does. An empty index name means every index; one the table lacks is
    /// Msg 7999.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccDbReindex(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        dbcc.RequireArgumentCount(1, 3);
        var (table, _, database) = ResolveDbccTable(batch, dbcc, 0, allowTemporary: false);
        if (!PermissionEnforcement.HasObjectAlter(batch, database, table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.DbccObjectPermissionDenied(batch.Connection.Security.Effective.DatabasePrincipalName, "DBREINDEX", table.Name, 1);
        if (dbcc.Arguments.Count > 1 && dbcc.StringArgument(batch, 1) is { Length: > 0 } indexName && FindIndex(table, indexName) is null)
            throw SimulatedSqlException.DbccIndexNotFound(indexName, table.Name, 4);
        batch.Connection.LastStatementRowCount = table.Heap.RowCount;
        return DbccCompleted(batch, dbcc, []);
    }

    private static readonly string[] IndexDefragColumnNames = ["Pages Scanned", "Pages Moved", "Pages Removed"];

    private static readonly string[] IndexDefragAllColumnNames = ["Index Name", "Pages Scanned", "Pages Moved", "Pages Removed"];

    /// <summary>
    /// <c>DBCC INDEXDEFRAG ( database | 0, table [, index [, partition]] )</c>:
    /// the pages scanned, none moved or removed — one row for the named index,
    /// or a row per index (the heap's with a NULL name) when none is named.
    /// <c>NO_INFOMSGS</c> silences the rows too. Refused inside a user
    /// transaction (Msg 8920).
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccIndexDefrag(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        dbcc.RequireArgumentCount(2, 4);
        _ = ResolveDbccDatabase(batch, dbcc.Arguments[0], 1);
        var (table, _, database) = ResolveDbccTable(batch, dbcc, 1, allowTemporary: true);
        RequireDbccTableAlter(batch, database, table);
        if (batch.Connection.CurrentTransaction is not null)
            throw SimulatedSqlException.IndexDefragInsideTransaction();
        var scanned = SqlValue.FromInt64(table.Heap.Pages.Count);
        var zero = SqlValue.FromInt64(0);
        var identities = table.IndexIdentities();
        if (dbcc.Arguments.Count > 2)
        {
            var argument = dbcc.Arguments[2];
            var value = argument.Kind == DbccArgumentKind.Name ? SqlValue.FromNVarchar(argument.Name.Leaf) : argument.Evaluate(batch);
            var found = !value.IsNull && (SqlType.IsIntegerCategory(value.Type)
                ? identities.Exists(identity => identity.IndexId == value.CoerceTo(SqlType.Int32).AsInt32)
                : SqlType.IsStringCategory(value.Type) && FindIndex(table, value.AsString) is not null);
            if (!found)
                throw SimulatedSqlException.DbccIndexNotFound(value.IsNull ? "" : value.CoerceTo(SqlType.NVarchar).AsString, table.Name, 8);
            if (dbcc.Has(DbccOptions.NoInfoMessages))
                return [];
            return DbccCompleted(batch, dbcc, DbccRows(batch, [SqlType.BigInt, SqlType.BigInt, SqlType.BigInt], IndexDefragColumnNames, [[scanned, zero, zero]]));
        }
        if (dbcc.Has(DbccOptions.NoInfoMessages))
            return [];
        var nameType = NVarcharSqlType.Get(128, batch.CurrentDatabase.Collation, Coercibility.Implicit);
        var rows = new List<SqlValue[]>(identities.Count);
        foreach (var identity in identities)
            rows.Add([identity.Name is { } name ? SqlValue.FromNVarchar(name) : SqlValue.Null(nameType), scanned, zero, zero]);
        var outcomes = DbccRows(batch, [nameType, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt], IndexDefragAllColumnNames, rows);
        return DbccCompleted(batch, dbcc, outcomes);
    }

    private static readonly string[] CheckConstraintsColumnNames = ["Table", "Constraint", "Where"];

    /// <summary>The rows a constraint group reports without <c>ALL_ERRORMSGS</c>.</summary>
    private const int CheckConstraintsDefaultRowCap = 200;

    /// <summary>
    /// <c>DBCC CHECKCONSTRAINTS [( table | constraint )] [WITH ALL_CONSTRAINTS |
    /// ALL_ERRORMSGS, NO_INFOMSGS]</c>: the rows that violate the table's
    /// <c>FOREIGN KEY</c> and <c>CHECK</c> constraints, as real reports them.
    /// Without <c>ALL_CONSTRAINTS</c> a disabled constraint is skipped unless
    /// named. Per table, foreign keys and check constraints each form one
    /// group: a row is reported once per group, against the first constraint
    /// (by creation) it violates, as <c>[col] = 'value'</c> over that
    /// constraint's columns — a check constraint's in table order — joined by
    /// <c>AND</c>, NULL when any of them is NULL. A group's rows are distinct
    /// under the database collation, ordered by constraint name then text, and
    /// capped at 200 unless <c>ALL_ERRORMSGS</c>. Tables go in object-id order;
    /// <c>Table</c> and <c>Constraint</c> are as wide as their longest value
    /// plus one, and a check finding nothing sends no result set.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccCheckConstraints(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages | DbccOptions.AllConstraints | DbccOptions.AllErrorMessages);
        dbcc.RequireArgumentCount(0, 1);
        var database = batch.CurrentDatabase;
        RequireDbccDatabaseOwner(batch, database, "checkconstraints");

        List<(HeapTable Table, string Display)> tables;
        object? namedConstraint = null;
        if (dbcc.Arguments.Count == 0)
        {
            tables = UserTablesByObjectId(database, filegroupId: null);
        }
        else
        {
            var (table, constraint) = ResolveCheckConstraintsTarget(batch, dbcc.Arguments[0]);
            namedConstraint = constraint;
            tables = [(table, table.Name)];
        }

        var allConstraints = dbcc.Has(DbccOptions.AllConstraints) || namedConstraint is not null;
        var cap = dbcc.Has(DbccOptions.AllErrorMessages) ? int.MaxValue : CheckConstraintsDefaultRowCap;
        var collation = database.Collation;
        var found = new List<(string Table, string Constraint, string? Where)>();
        foreach (var (table, _) in tables)
        {
            var tableText = $"{QuoteDbccName(SchemaNameOf(database, table))}.{QuoteDbccName(table.Name)}";
            var foreignKeys = table.OutgoingForeignKeys.FindAll(fk => namedConstraint is null ? allConstraints || !fk.IsDisabled : ReferenceEquals(fk, namedConstraint));
            var checks = table.CheckConstraints.FindAll(ck => namedConstraint is null ? allConstraints || !ck.IsDisabled : ReferenceEquals(ck, namedConstraint));
            if (foreignKeys.Count > 0)
                AddConstraintGroup(found, tableText, CollectForeignKeyViolations(table, foreignKeys), collation, cap);
            if (checks.Count > 0)
                AddConstraintGroup(found, tableText, CollectCheckViolations(batch, table, checks), collation, cap);
        }

        batch.Connection.LastStatementRowCount = 0;
        if (found.Count == 0)
            return DbccCompleted(batch, dbcc, []);
        int tableWidth = 0, constraintWidth = 0;
        foreach (var (table, constraint, _) in found)
        {
            tableWidth = Math.Max(tableWidth, table.Length);
            constraintWidth = Math.Max(constraintWidth, constraint.Length);
        }
        SqlType[] schema =
        [
            NVarcharSqlType.Get(tableWidth + 1, collation, Coercibility.Implicit),
            NVarcharSqlType.Get(constraintWidth + 1, collation, Coercibility.Implicit),
            NVarcharSqlType.Get(SqlType.MaxLengthSentinel, collation, Coercibility.Implicit),
        ];
        var rows = new List<SqlValue[]>(found.Count);
        foreach (var (table, constraint, where) in found)
            rows.Add([SqlValue.FromNVarchar(table), SqlValue.FromNVarchar(constraint), where is null ? SqlValue.Null(schema[2]) : SqlValue.FromNVarchar(where)]);
        // Real closes these rows without a count, Msg 2528 ahead of the DONE.
        var outcomes = DbccRows(batch, schema, CheckConstraintsColumnNames, rows);
        outcomes[0].CountSuppressed = true;
        batch.Connection.LastStatementRowCount = 0;
        if (!dbcc.Has(DbccOptions.NoInfoMessages))
            outcomes.Add(new SimulatedInfoOutcome(SimulatedSqlException.DbccExecutionCompletedMessage(batch), followsRows: true));
        return outcomes;
    }

    /// <summary>
    /// One group's violations: distinct under <paramref name="collation"/>,
    /// ordered by constraint name then text (a NULL text first), capped.
    /// </summary>
    private static void AddConstraintGroup(List<(string Table, string Constraint, string? Where)> found, string tableText, List<(string Constraint, string? Where)> violations, Collation collation, int cap)
    {
        var distinct = new List<(string Constraint, string? Where)>(violations.Count);
        foreach (var violation in violations)
        {
            if (!distinct.Exists(kept => collation.Equals(kept.Constraint, violation.Constraint)
                && (kept.Where is null ? violation.Where is null : violation.Where is not null && collation.Equals(kept.Where, violation.Where))))
            {
                distinct.Add(violation);
            }
        }
        distinct.Sort((a, b) => collation.Compare(a.Constraint, b.Constraint) is var byName and not 0
            ? byName
            : a.Where is null ? (b.Where is null ? 0 : -1) : b.Where is null ? 1 : collation.Compare(a.Where, b.Where));
        for (var i = 0; i < distinct.Count && i < cap; i++)
            found.Add((tableText, distinct[i].Constraint, distinct[i].Where));
    }

    /// <summary>Each row's first violated foreign key among <paramref name="foreignKeys"/>, over its child columns in key order.</summary>
    private static List<(string Constraint, string? Where)> CollectForeignKeyViolations(HeapTable table, List<ForeignKey> foreignKeys)
    {
        var violations = new List<(string, string?)>();
        foreach (var rowBytes in table.Heap.EnumerateRows())
        {
            foreach (var fk in foreignKeys)
            {
                var key = new SqlValue[fk.ChildColumnOrdinals.Length];
                var anyNull = false;
                for (var k = 0; k < key.Length; k++)
                {
                    key[k] = ReadFullColumn(table, rowBytes, fk.ChildColumnOrdinals[k]);
                    anyNull |= key[k].IsNull;
                }
                if (anyNull || ParentRowMatches(fk, key))
                    continue;
                violations.Add((QuoteDbccName(fk.Name), WhereText(table, fk.ChildColumnOrdinals, key)));
                break;
            }
        }
        return violations;
    }

    /// <summary>Each row's first violated check constraint among <paramref name="checks"/>, over the columns it names in table order.</summary>
    private static List<(string Constraint, string? Where)> CollectCheckViolations(BatchContext batch, HeapTable table, List<CheckConstraint> checks)
    {
        var collation = batch.CurrentDatabase.Collation;
        var referenced = new int[checks.Count][];
        for (var c = 0; c < checks.Count; c++)
        {
            var ordinals = new SortedSet<int>();
            checks[c].Predicate.VisitOperandExpressions(operand => operand.VisitColumnReferences(name =>
            {
                var ordinal = Array.FindIndex(table.Columns, column => collation.Equals(column.Name, name.Leaf));
                if (ordinal >= 0)
                    _ = ordinals.Add(ordinal);
            }));
            referenced[c] = [.. ordinals];
        }

        var violations = new List<(string, string?)>();
        var values = new SqlValue[table.Columns.Length];
        var runtime = new RuntimeContext(name =>
        {
            var ordinal = Array.FindIndex(table.Columns, column => collation.Equals(column.Name, name.Leaf));
            return ordinal >= 0 ? values[ordinal] : throw SimulatedSqlException.InvalidColumnName(name);
        }, batch);
        foreach (var rowBytes in table.Heap.EnumerateRows())
        {
            for (var c = 0; c < values.Length; c++)
                values[c] = ReadFullColumn(table, rowBytes, c);
            for (var c = 0; c < checks.Count; c++)
            {
                if (checks[c].Predicate.Run(runtime) != false)
                    continue;
                var key = new SqlValue[referenced[c].Length];
                for (var k = 0; k < key.Length; k++)
                    key[k] = values[referenced[c][k]];
                violations.Add((QuoteDbccName(checks[c].Name), WhereText(table, referenced[c], key)));
                break;
            }
        }
        return violations;
    }

    /// <summary>A full-ordinal column's value in a stored row; a computed column's that isn't stored reads NULL.</summary>
    private static SqlValue ReadFullColumn(HeapTable table, byte[] rowBytes, int ordinal)
    {
        var storageOrdinal = table.StorageOrdinals[ordinal];
        return storageOrdinal < 0
            ? SqlValue.Null(table.Columns[ordinal].Type)
            : RowDecoder.DecodeColumn(table.StoredColumns, rowBytes, storageOrdinal, table.Heap);
    }

    /// <summary>
    /// <c>[col] = 'value' AND …</c> — each value as <c>CONVERT(nvarchar(max),
    /// value)</c> renders it, style 121 for a date or time and 1 for binary, a
    /// fixed-length string without its padding — or null when any value is
    /// NULL, as real's string concatenation makes it.
    /// </summary>
    private static string? WhereText(HeapTable table, int[] ordinals, SqlValue[] values)
    {
        var text = new StringBuilder();
        for (var i = 0; i < ordinals.Length; i++)
        {
            var value = values[i];
            if (value.IsNull)
                return null;
            var rendered = value.Type switch
            {
                { Category: SqlTypeCategory.DateTime } => value.CoerceDateTimeToStringWithStyle(SqlType.NVarcharMax, 121).AsString,
                VarbinarySqlType or BinarySqlType => value.CoerceBinaryToStringWithStyle(SqlType.NVarcharMax, 1).AsString,
                CharSqlType or NCharSqlType => value.CoerceTo(SqlType.NVarcharMax).AsString.TrimEnd(' '),
                _ => value.CoerceTo(SqlType.NVarcharMax).AsString,
            };
            if (i > 0)
                _ = text.Append(" AND ");
            _ = text.Append(QuoteDbccName(table.Columns[ordinals[i]].Name)).Append(" = '").Append(rendered.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
        }
        return text.ToString();
    }

    /// <summary>A name in brackets, a closing bracket doubled.</summary>
    private static string QuoteDbccName(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";

    /// <summary>
    /// <c>DBCC CHECKCONSTRAINTS</c>' argument: a table in the current database
    /// (by name or object id), else a check or foreign-key constraint there,
    /// which is then checked alone. A <c>#temp</c> table isn't found (Msg
    /// 2501), and a primary key or unique constraint checks nothing.
    /// </summary>
    private static (HeapTable Table, object? Constraint) ResolveCheckConstraintsTarget(BatchContext batch, DbccArgument argument)
    {
        var database = batch.CurrentDatabase;
        var value = argument.Kind == DbccArgumentKind.Name ? default : argument.Evaluate(batch);
        if (argument.Kind != DbccArgumentKind.Name && !value.IsNull && SqlType.IsIntegerCategory(value.Type))
        {
            var id = value.CoerceTo(SqlType.Int32).AsInt32;
            foreach (var (table, _) in UserTablesByObjectId(database, filegroupId: null))
            {
                if (table.ObjectId == id)
                    return (table, null);
                if (table.CheckConstraints.Find(ck => ck.ObjectId == id) is { } check)
                    return (table, check);
                if (table.OutgoingForeignKeys.Find(fk => fk.ObjectId == id) is { } foreignKey)
                    return (table, foreignKey);
            }
            throw SimulatedSqlException.CouldNotFindObjectId(id);
        }

        var (name, written) = DbccObjectName(batch, argument, 1);
        if (!name.Leaf.StartsWith('#') && TryResolveDbccObject(batch, name, written) is { } resolved)
            return (resolved, null);
        if (name.Count <= 2)
        {
            foreach (var (table, schemaName) in UserTablesByObjectId(database, filegroupId: null))
            {
                if (name.ImmediateQualifier is { } qualifier && !database.Collation.Equals(qualifier, SchemaNameOf(database, table)))
                    continue;
                _ = schemaName;
                if (table.CheckConstraints.Find(ck => database.Collation.Equals(ck.Name, name.Leaf)) is { } check)
                    return (table, check);
                if (table.OutgoingForeignKeys.Find(fk => database.Collation.Equals(fk.Name, name.Leaf)) is { } foreignKey)
                    return (table, foreignKey);
                if (table.KeyConstraints.Exists(key => database.Collation.Equals(key.Name, name.Leaf)))
                    return (table, table);
            }
        }
        throw SimulatedSqlException.CannotFindTableOrObject(written);
    }

    /// <summary>
    /// A DBCC table argument, by name — a string holding one or the name
    /// written bare — in the current database: a view or other non-table object
    /// is Msg 5239, a miss Msg 2501. Returns the table, the name its messages
    /// use (<c>schema.table</c> outside <c>dbo</c>, a <c>#temp</c> table's
    /// internal name) and its database.
    /// </summary>
    private static (HeapTable Table, string Display, Database Database) ResolveDbccTable(BatchContext batch, DbccInvocation dbcc, int index, bool allowTemporary)
    {
        var argument = dbcc.Arguments[index];
        if (argument.Kind != DbccArgumentKind.Name && argument.Evaluate(batch) is { IsNull: false } id && SqlType.IsIntegerCategory(id.Type))
        {
            var objectId = id.CoerceTo(SqlType.Int32).AsInt32;
            foreach (var (table, display) in UserTablesByObjectId(batch.CurrentDatabase, filegroupId: null))
            {
                if (table.ObjectId == objectId)
                    return (table, display, batch.CurrentDatabase);
            }
            throw SimulatedSqlException.CouldNotFindObjectId(objectId);
        }
        var (name, written) = DbccObjectName(batch, argument, index + 1);
        if (!allowTemporary && name.Leaf.StartsWith('#'))
            throw SimulatedSqlException.CannotFindTableOrObject(written);
        var found = TryResolveDbccObject(batch, name, written) ?? throw SimulatedSqlException.CannotFindTableOrObject(written);
        if (found.InternalName is { } internalName)
            return (found, internalName, batch.CurrentDatabase);
        var database = found.OwningDatabase ?? batch.CurrentDatabase;
        var schemaName = SchemaNameOf(database, found);
        return (found, schemaName == Database.DefaultSchemaName ? found.Name : $"{schemaName}.{found.Name}", database);
    }

    /// <summary>The name a DBCC object argument spells, and its text for Msg 2501.</summary>
    private static (MultiPartName Name, string Written) DbccObjectName(BatchContext batch, DbccArgument argument, int position)
    {
        if (argument.Kind == DbccArgumentKind.Name)
            return (argument.Name, argument.Name.ToString());
        var value = argument.Evaluate(batch);
        if (value.IsNull || !SqlType.IsStringCategory(value.Type))
            throw SimulatedSqlException.DbccParameterIsIncorrect(position);
        var text = value.AsString;
        return Parser.Expressions.ObjectId.TryParseObjectName(text, out var parsed)
            ? (parsed, text)
            : throw SimulatedSqlException.CannotFindTableOrObject(text);
    }

    /// <summary>The table <paramref name="name"/> names; Msg 5239 when it names a view, function or procedure instead.</summary>
    private static HeapTable? TryResolveDbccObject(BatchContext batch, MultiPartName name, string written)
    {
        _ = written;
        if (batch.TryResolveTable(name, out var table))
            return table;
        var database = batch.CurrentDatabase;
        var schemaName = name.Count >= 2 ? name.ImmediateQualifier! : Database.DefaultSchemaName;
        if (database.Schemas.TryGetValue(schemaName, out var schema))
        {
            if (schema.Views.TryGetValue(name.Leaf, out var view))
                throw SimulatedSqlException.DbccObjectTypeNotSupported(view.ObjectId, view.Name);
            if (schema.Procedures.TryGetValue(name.Leaf, out var procedure))
                throw SimulatedSqlException.DbccObjectTypeNotSupported(procedure.ObjectId, procedure.Name);
            if (schema.Functions.TryGetValue(name.Leaf, out var function))
                throw SimulatedSqlException.DbccObjectTypeNotSupported(function.ObjectId, function.Name);
        }
        return null;
    }

    /// <summary>
    /// <c>CHECKFILEGROUP</c>'s filegroup: named or by id, the primary one when
    /// absent; one the database lacks is Msg 3027.
    /// </summary>
    private static int ResolveDbccFilegroup(BatchContext batch, Database database, DbccInvocation dbcc)
    {
        if (dbcc.Arguments.Count == 0)
            return Database.PrimaryFilegroupId;
        var argument = dbcc.Arguments[0];
        var value = argument.Kind == DbccArgumentKind.Name ? SqlValue.FromNVarchar(argument.Name.Leaf) : argument.Evaluate(batch);
        if (value.IsNull)
            throw SimulatedSqlException.DbccParameterIsIncorrect(1);
        if (SqlType.IsIntegerCategory(value.Type))
        {
            var id = value.CoerceTo(SqlType.Int32).AsInt32;
            if (id == 0)
                return Database.PrimaryFilegroupId;
            foreach (var entry in database.Filegroups)
            {
                if (entry.Value == id)
                    return id;
            }
            throw SimulatedSqlException.FilegroupNotInDatabase(id.ToString(CultureInfo.InvariantCulture), database.Name);
        }
        var name = value.CoerceTo(SqlType.NVarchar).AsString;
        return database.Filegroups.TryGetValue(name, out var filegroupId)
            ? filegroupId
            : throw SimulatedSqlException.FilegroupNotInDatabase(name, database.Name);
    }

    /// <summary>A <c>REPAIR_*</c> second argument isn't built; anything but <c>NOINDEX</c> or an index id there is Msg 2560.</summary>
    private static void RejectRepairArgument(DbccInvocation dbcc, int index)
    {
        if (dbcc.Arguments.Count <= index)
            return;
        var argument = dbcc.Arguments[index];
        if (argument.Kind == DbccArgumentKind.Name)
        {
            var word = argument.Name.Leaf;
            if (BuiltInToken.EqualsAny(word, "REPAIR_ALLOW_DATA_LOSS", "REPAIR_FAST", "REPAIR_REBUILD"))
                throw new NotSupportedException($"DBCC {dbcc.Command.ToUpperInvariant()} with {word.ToUpperInvariant()} isn't modeled.");
            if (BuiltInToken.Equals(word, "NOINDEX"))
                return;
        }
        else if (argument.Kind == DbccArgumentKind.Literal && !argument.Literal.IsNull && SqlType.IsIntegerCategory(argument.Literal.Type)
            && dbcc.Command.Equals("CHECKTABLE", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw SimulatedSqlException.DbccParameterIsIncorrect(index + 1);
    }

    /// <summary>Msg 7983 unless the principal is a <c>db_owner</c> of <paramref name="database"/>.</summary>
    private static void RequireDbccDatabaseOwner(BatchContext batch, Database database, string command)
    {
        if (!IsSysadminSession(batch) && !PermissionEnforcement.IsOwner(batch, database))
            throw SimulatedSqlException.DbccDatabasePermissionDenied(batch.Connection.Security.Effective.DatabasePrincipalName, command, database.Name);
    }

    /// <summary>Msg 229 unless the principal holds <c>ALTER</c> on the table.</summary>
    private static void RequireDbccTableAlter(BatchContext batch, Database database, HeapTable table)
    {
        if (!PermissionEnforcement.HasObjectAlter(batch, database, table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.DbccPermissionDeniedOnObject(table.Name, database.Name, SchemaNameOf(database, table));
    }

    /// <summary>
    /// The database's user tables in object-id order, each with the name a
    /// check's messages give it — bare in <c>dbo</c>, <c>schema.table</c>
    /// elsewhere — optionally only those on one filegroup.
    /// </summary>
    private static List<(HeapTable Table, string Display)> UserTablesByObjectId(Database database, int? filegroupId)
    {
        var tables = new List<(HeapTable, string)>();
        foreach (var (schemaName, schema) in database.Schemas)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (filegroupId is { } only && table.FilegroupId != only)
                    continue;
                tables.Add((table, schemaName == Database.DefaultSchemaName ? table.Name : $"{schemaName}.{table.Name}"));
            }
        }
        tables.Sort(static (a, b) => a.Item1.ObjectId.CompareTo(b.Item1.ObjectId));
        return tables;
    }

    private static long TotalPages(List<(HeapTable Table, string Display)> tables)
    {
        long pages = 0;
        foreach (var (table, _) in tables)
            pages += table.Heap.Pages.Count;
        return pages;
    }

    /// <summary>The index named <paramref name="name"/> on <paramref name="table"/>, key-backed or not.</summary>
    private static string? FindIndex(HeapTable table, string name)
    {
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.Name is { } indexName && BuiltInToken.Equals(indexName, name))
                return indexName;
        }
        return null;
    }

    private static short DatabaseIdOf(Simulation simulation, Database database) => SmallDatabaseId(simulation, database);

    /// <summary>Queues an informational message ahead of the statement's outcomes.</summary>
    private static void Info(BatchContext batch, SimulatedError message) => batch.Connection.PendingMessages.Enqueue(message);

    /// <summary>The <c>TABLERESULTS</c> schema of the consistency checks.</summary>
    private static SqlType[] CheckResultSchema(BatchContext batch)
    {
        var collation = batch.CurrentDatabase.Collation;
        return
        [
            SqlType.Int32, SqlType.Int32, SqlType.Int32, NVarcharSqlType.Get(2048, collation, Coercibility.Implicit), NVarcharSqlType.Get(22, collation, Coercibility.Implicit),
            SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.BigInt, SqlType.BigInt, SqlType.SmallInt, SqlType.SmallInt,
            SqlType.SmallInt, SqlType.Int32, SqlType.Int32, SqlType.SmallInt, SqlType.SmallInt, SqlType.SmallInt, SqlType.Int32, SqlType.Int32, SqlType.SmallInt,
        ];
    }

    /// <summary>
    /// One <c>TABLERESULTS</c> row: a level-10 message with no repair level,
    /// located in its database — and, for an object's line, in its row
    /// identifier columns too, which the summary line leaves 0.
    /// </summary>
    private static SqlValue[] CheckResultRow(int number, string text, short databaseId, int objectId, int indexId, bool locatesRow)
    {
        var zeroInt = SqlValue.FromInt32(0);
        var zeroBig = SqlValue.FromInt64(0);
        var zeroSmall = SqlValue.FromInt16(0);
        var rowDatabase = locatesRow ? SqlValue.FromInt16(databaseId) : zeroSmall;
        return
        [
            SqlValue.FromInt32(number), SqlValue.FromInt32(10), SqlValue.FromInt32(1), SqlValue.FromNVarchar(text), SqlValue.Null(SqlType.NVarchar),
            zeroInt, SqlValue.FromInt32(databaseId), SqlValue.FromInt32(1), SqlValue.FromInt32(objectId), SqlValue.FromInt32(indexId), zeroBig, zeroBig,
            rowDatabase, zeroSmall, zeroSmall, zeroInt, zeroInt, rowDatabase, zeroSmall, zeroSmall, zeroInt, zeroInt, SqlValue.FromInt16(1),
        ];
    }
}
