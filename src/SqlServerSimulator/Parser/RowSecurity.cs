using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The row-level security one table carries as of one
/// <see cref="Simulation.SchemaVersion"/>: the filter predicate its reads
/// apply and the block predicate each write checks, from the one enabled
/// security policy that names it. Settled at execution, never while a
/// statement parses, so a cached plan holds no decision about it and each
/// replay applies the predicates current then, as its own principal (see
/// docs/claude/row-level-security.md).
/// </summary>
/// <remarks>
/// Every entry point reads <see cref="Simulation.DeclaresSecurityPolicies"/>
/// first, so a simulation that never created a policy pays one field read per
/// table scan or written row. The answer is memoized on the target
/// (<see cref="SchemaObject.RowSecurity"/>) per schema version, which every
/// policy <c>CREATE</c>, <c>ALTER</c> and <c>DROP</c> — and their rollback —
/// advances.
/// </remarks>
internal sealed class RowSecurity
{
    private RowSecurity(long schemaVersion, SecurityPredicate? filter, SecurityPredicate?[]? blocks)
    {
        this.SchemaVersion = schemaVersion;
        this.Filter = filter;
        this.blocks = blocks;
    }

    /// <summary>The schema version this answer was settled under.</summary>
    public readonly long SchemaVersion;

    /// <summary>The filter predicate reads of the target apply; null when none.</summary>
    public readonly SecurityPredicate? Filter;

    // Indexed by BlockOperation's value; null when the target carries no block predicate.
    private readonly SecurityPredicate?[]? blocks;

    private bool IsEmpty => this.Filter is null && this.blocks is null;

    /// <summary>The block predicate guarding <paramref name="operation"/>, or null.</summary>
    public SecurityPredicate? BlockFor(BlockOperation operation) => this.blocks?[(int)operation];

    /// <summary>
    /// The row-level security <paramref name="target"/> carries for a statement
    /// <paramref name="batch"/> runs, or null when it carries none — and inside
    /// a predicate function's own body, which reads its tables unfiltered.
    /// </summary>
    public static RowSecurity? For(BatchContext batch, SchemaObject target)
    {
        if (!batch.Connection.Simulation.DeclaresSecurityPolicies || batch.SuppressesRowSecurity)
            return null;
        // A temp table, table variable or trigger pseudo-table can't be a
        // predicate's target (Msg 33269).
        if (target is HeapTable { OwningDatabase: null } or HeapTable { IsTableVariable: true })
            return null;
        var version = Volatile.Read(ref batch.Connection.Simulation.SchemaVersion);
        var memo = target.RowSecurity;
        if (memo is null || memo.SchemaVersion != version)
            target.RowSecurity = memo = Settle(batch.DatabaseFor(target), target, version);
        return memo.IsEmpty ? null : memo;
    }

    private static RowSecurity Settle(Database database, SchemaObject target, long version)
    {
        SecurityPredicate? filter = null;
        SecurityPredicate?[]? blocks = null;
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, policy) in schema.SecurityPolicies)
            {
                var state = policy.State;
                if (!state.IsEnabled)
                    continue;
                foreach (var predicate in state.Predicates)
                {
                    if (!ReferenceEquals(predicate.Target, target))
                        continue;
                    if (predicate.Kind == SecurityPredicateKind.Filter)
                    {
                        filter = predicate;
                        continue;
                    }
                    blocks ??= new SecurityPredicate?[5];
                    if (predicate.Operation is { } operation)
                    {
                        blocks[(int)operation] = predicate;
                    }
                    else
                    {
                        for (var i = 1; i < blocks.Length; i++)
                            blocks[i] = predicate;
                    }
                }
            }
        }
        return new RowSecurity(version, filter, blocks);
    }

    /// <summary>
    /// <paramref name="rows"/>, a scan or seek of <paramref name="table"/>'s
    /// heap for a statement's read, past the table's filter predicate. The
    /// predicate is compiled before the first row is asked for, so its
    /// binding and permission errors reach an empty table too, and it runs
    /// ahead of anything the reading statement evaluates over the row: real's
    /// plan applies it at the scan, so a <c>WHERE 1 / a = 0</c> never sees a
    /// row the predicate hides (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    public static IEnumerable<byte[]> FilterRows(HeapTable table, IEnumerable<byte[]> rows, BatchContext batch) =>
        For(batch, table) is { Filter: { } filter } ? SecurityPredicateRunner.For(batch, filter).FilterStored(rows) : rows;

    /// <summary>
    /// <see cref="FilterRows"/> over a scan read under <paramref name="plan"/>:
    /// a row lock held to the transaction's end — REPEATABLE READ, an
    /// <c>UPDLOCK</c> — is let go again on a row the predicate hides, as real
    /// keeps only the rows its scan returns (probed 2026-10-04 against SQL
    /// Server 2025). The scan records each row's address for that, as it does
    /// for a row locator.
    /// </summary>
    public static IEnumerable<byte[]> FilterLockedRows(HeapTable table, DataLockPlan plan, IEnumerable<byte[]> rows, BatchContext batch)
    {
        if (For(batch, table) is not { Filter: { } filter })
            return rows;
        var runner = SecurityPredicateRunner.For(batch, filter);
        if (!plan.RowTxScoped || plan.RowMode is not { } mode)
            return runner.FilterStored(rows);
        var addresses = batch.CurrentStatement.RowAddresses ??= new();
        return ReleasingRejected();

        IEnumerable<byte[]> ReleasingRejected()
        {
            foreach (var row in rows)
            {
                if (runner.AdmitsStored(row))
                    yield return row;
                else if (addresses.TryGet(row, out var address))
                    batch.ReleaseRowLockAcquisition(table, address.Page, address.Slot, mode);
            }
        }
    }

    /// <summary>
    /// <see cref="FilterRows"/> for a write's target walk, whose rows carry
    /// their heap addresses.
    /// </summary>
    public static IEnumerable<(int Page, int Slot, byte[] Bytes)> FilterAddressedRows(HeapTable table, IEnumerable<(int Page, int Slot, byte[] Bytes)> rows, BatchContext batch) =>
        For(batch, table) is { Filter: { } filter } ? SecurityPredicateRunner.For(batch, filter).FilterStored(rows) : rows;

    /// <summary>
    /// Notes on the session that the running statement writes a table some
    /// enabled security policy names — a filter or a block predicate alike —
    /// which settles how its conversion errors read (see
    /// <see cref="SecurityPredicateRunner.For"/>).
    /// </summary>
    public static void NoteWrite(BatchContext batch, HeapTable table)
    {
        if (!batch.IsSkipping && For(batch, table) is not null)
            batch.Connection.RowSecurityMarks++;
    }

    /// <summary>
    /// Notes on the session that the running query reads a table whose filter
    /// predicate applies, ahead of anything its plan evaluates as it starts.
    /// </summary>
    public static void NoteReads(FromSource[] sources, BatchContext batch)
    {
        if (!batch.Connection.Simulation.DeclaresSecurityPolicies)
            return;
        foreach (var source in sources)
        {
            if (source is { BackingTable: { } table, LateralPlan: null } && For(batch, table) is { Filter: not null })
                batch.Connection.RowSecurityMarks++;
        }
    }

    /// <summary>
    /// <paramref name="rows"/>, a read of <paramref name="view"/> whose rows
    /// carry <paramref name="columns"/>, past a filter predicate on the view —
    /// a schema-bound view takes one as a table does.
    /// </summary>
    public static IEnumerable<byte[]> FilterViewRows(View view, HeapColumn[] columns, IEnumerable<byte[]> rows, BatchContext batch) =>
        For(batch, view) is { Filter: { } filter } ? SecurityPredicateRunner.For(batch, filter).FilterProjected(rows, columns) : rows;

    /// <summary>
    /// Whether <paramref name="table"/>'s filter predicate admits the row whose
    /// logical column values are <paramref name="fullValues"/>; true when it
    /// carries none.
    /// </summary>
    public static bool Admits(BatchContext batch, HeapTable table, SqlValue[] fullValues) =>
        For(batch, table) is not { Filter: { } filter } || SecurityPredicateRunner.For(batch, filter).AdmitsValues(fullValues);

    /// <summary>
    /// Raises Msg 33504 when <paramref name="table"/>'s block predicate for
    /// <paramref name="operation"/> refuses the row whose logical column values
    /// are <paramref name="fullValues"/> — the new row for an <c>AFTER</c>
    /// operation, the row as it stood for a <c>BEFORE</c> one. A foreign key's
    /// cascade never reaches here: real lets a cascade past every block
    /// predicate (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    public static void EnforceBlock(BatchContext batch, HeapTable table, BlockOperation operation, SqlValue[] fullValues)
    {
        if (For(batch, table)?.BlockFor(operation) is { } predicate && !SecurityPredicateRunner.For(batch, predicate).AdmitsValues(fullValues))
            throw SimulatedSqlException.BlockPredicateConflict(Simulation.QualifyTableName(table, batch.DatabaseFor(table)));
    }
}

/// <summary>
/// One security predicate compiled for the statements of one batch: its
/// function's body parsed once into a plan whose parameters are variable
/// slots, which each row's arguments fill before the plan is asked for a
/// row. Kept in <see cref="BatchContext.RowSecurityRunners"/> and compiled
/// again when the schema version moves. The body runs in a batch of its own
/// that applies no row-level security, as real reads a predicate function's
/// tables — the predicate's own target included — unfiltered.
/// </summary>
internal sealed class SecurityPredicateRunner
{
    private readonly BatchContext outer;
    private readonly InlineTableValuedFunction function;
    private readonly BatchContext inner;
    private readonly Selection body;
    private readonly VariableSlot[] slots;
    private readonly Expression[] arguments;
    private readonly HeapTable? table;
    private readonly HeapColumn[] columns;
    private readonly Collation collation;
    private readonly Func<MultiPartName, SqlValue> resolve;
    private byte[]? currentBytes;
    private SqlValue[]? currentValues;
    private HeapColumn[] currentColumns;
    private int checkedPrincipal = int.MinValue;

    /// <summary>The schema version the runner was compiled under.</summary>
    public readonly long SchemaVersion;

    /// <summary>
    /// A runner over <paramref name="body"/>, parsed in <paramref name="inner"/>
    /// with one slot per parameter. <paramref name="table"/> is the target when
    /// it is a table, whose stored rows the runner decodes, and null for a
    /// view; <paramref name="columns"/> are the target's columns, which the
    /// arguments name.
    /// </summary>
    public SecurityPredicateRunner(
        BatchContext outer, InlineTableValuedFunction function, BatchContext inner, Selection body,
        VariableSlot[] slots, Expression[] arguments, HeapTable? table, HeapColumn[] columns, Collation collation, long schemaVersion)
    {
        this.outer = outer;
        this.function = function;
        this.inner = inner;
        this.body = body;
        this.slots = slots;
        this.arguments = arguments;
        this.table = table;
        this.columns = columns;
        this.currentColumns = columns;
        this.collation = collation;
        this.SchemaVersion = schemaVersion;
        this.resolve = this.ResolveColumn;
    }

    /// <summary>
    /// The runner for <paramref name="predicate"/> in <paramref name="batch"/>,
    /// compiled on first use, and the note on the session that the running
    /// statement applies row-level security, which settles how its conversion
    /// errors read (<see cref="SimulatedSqlException.RedactedForRowSecurity"/>).
    /// </summary>
    public static SecurityPredicateRunner For(BatchContext batch, SecurityPredicate predicate)
    {
        batch.Connection.RowSecurityMarks++;
        var version = Volatile.Read(ref batch.Connection.Simulation.SchemaVersion);
        var runners = batch.RowSecurityRunners ??= [];
        if (!runners.TryGetValue(predicate, out var runner) || runner.SchemaVersion != version)
            runners[predicate] = runner = batch.Connection.Simulation.CompileSecurityPredicate(batch, predicate, version);
        // Under a policy that isn't schema bound the reading principal needs
        // SELECT on the function, checked again for each principal the batch
        // runs as.
        if (!predicate.IsSchemaBound && batch.Connection.Security.Effective.DatabasePrincipalId is var principal && principal != runner.checkedPrincipal)
        {
            PermissionEnforcement.CheckSchemaObject(batch, "SELECT", runner.function);
            runner.checkedPrincipal = principal;
        }
        return runner;
    }

    /// <summary>The rows of <paramref name="rows"/> the predicate admits.</summary>
    public IEnumerable<byte[]> FilterStored(IEnumerable<byte[]> rows)
    {
        foreach (var row in rows)
        {
            if (this.AdmitsStored(row))
                yield return row;
        }
    }

    /// <summary>The addressed rows of <paramref name="rows"/> the predicate admits.</summary>
    public IEnumerable<(int Page, int Slot, byte[] Bytes)> FilterStored(IEnumerable<(int Page, int Slot, byte[] Bytes)> rows)
    {
        foreach (var row in rows)
        {
            if (this.AdmitsStored(row.Bytes))
                yield return row;
        }
    }

    /// <summary>The rows of <paramref name="rows"/>, encoded per <paramref name="rowColumns"/>, the predicate admits.</summary>
    public IEnumerable<byte[]> FilterProjected(IEnumerable<byte[]> rows, HeapColumn[] rowColumns)
    {
        foreach (var row in rows)
        {
            if (this.AdmitsValues(RowDecoder.DecodeRow(rowColumns, row), rowColumns))
                yield return row;
        }
    }

    /// <summary>Whether the predicate admits the stored row <paramref name="bytes"/> of its table.</summary>
    public bool AdmitsStored(byte[] bytes)
    {
        this.currentBytes = bytes;
        this.currentValues = null;
        this.currentColumns = this.columns;
        return this.Evaluate();
    }

    /// <summary>
    /// Whether the predicate admits the row whose values are
    /// <paramref name="values"/>, one per column of <paramref name="rowColumns"/>
    /// — by default the target's own logical columns.
    /// </summary>
    public bool AdmitsValues(SqlValue[] values, HeapColumn[]? rowColumns = null)
    {
        this.currentValues = values;
        this.currentBytes = null;
        this.currentColumns = rowColumns ?? this.columns;
        return this.Evaluate();
    }

    private bool Evaluate()
    {
        var runtime = new RuntimeContext(this.resolve, this.outer);
        var parameters = this.function.Parameters;
        for (var i = 0; i < this.slots.Length; i++)
        {
            var parameter = parameters[i];
            var value = this.arguments[i].Run(runtime).CoerceTo(parameter.Type);
            this.slots[i].Value = Expressions.Cast.ApplyCoercion(value, parameter.Type, parameter.DeclaredMaxLength);
        }

        var connection = this.outer.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();
        this.inner.AdoptStatementFreezeFrom(this.outer);
        var savedQuotedIdentifiers = connection.QuotedIdentifiers;
        var savedAnsiNulls = connection.AnsiNulls;
        connection.QuotedIdentifiers = this.function.UsesQuotedIdentifier;
        connection.AnsiNulls = this.function.UsesAnsiNulls;
        connection.NestingLevel++;
        var scope = ModuleDatabaseScope.Enter(connection, this.function.Schema.Database, bindsIdentity: false);
        try
        {
            return this.body.HasAnyRow(this.inner, outerResolver: null);
        }
        finally
        {
            scope.Exit();
            connection.NestingLevel--;
            connection.QuotedIdentifiers = savedQuotedIdentifiers;
            connection.AnsiNulls = savedAnsiNulls;
            this.inner.ReleaseStatementSchemaLocks();
        }
    }

    // An argument's column, matched by name against the target's columns as
    // they stand: the predicate's binding was checked when the runner compiled.
    private SqlValue ResolveColumn(MultiPartName name)
    {
        var columns = this.currentColumns;
        for (var ordinal = 0; ordinal < columns.Length; ordinal++)
        {
            if (this.collation.Equals(columns[ordinal].Name, name.Leaf))
                return this.currentValues is { } values ? values[ordinal] : this.DecodeStored(ordinal, this.currentBytes!);
        }
        throw SimulatedSqlException.InvalidColumnName(name);
    }

    private SqlValue DecodeStored(int ordinal, byte[] bytes)
    {
        var table = this.table!;
        var column = table.Columns[ordinal];
        if (column.Computed is not { } computed || column.IsPersisted)
            return RowDecoder.DecodeColumn(table.StoredColumns, bytes, table.StorageOrdinals[ordinal], table.Heap);
        // A non-persisted computed column reads the stored ones beside it.
        return computed.Run(new RuntimeContext(this.resolve, this.outer));
    }
}
