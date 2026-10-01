using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// An <c>INSERT</c> / <c>UPDATE</c> / <c>DELETE</c> / <c>MERGE</c>
    /// target, resolved once where the statement reads its name, so that each
    /// verb routes on what it found rather than resolving the name its own
    /// way. One of four kinds, told apart by which fields are set:
    /// <list type="bullet">
    /// <item><b>Remote</b> — <see cref="Remote"/>: a linked server's table,
    /// named four-part or through <c>OPENQUERY</c> / <c>OPENROWSET</c>, which
    /// the statement writes through <see cref="RemoteWrite"/>'s local
    /// stand-in, <see cref="Table"/>.</item>
    /// <item><b>View</b> — <see cref="View"/>: a view, through a synonym or
    /// not, or a CTE the statement writes through; <see cref="Table"/> is its
    /// base table when its chain reaches one.</item>
    /// <item><b>Table</b> — <see cref="Table"/> alone: a table, <c>#temp</c>
    /// table or table variable, in any database, through a synonym or not.</item>
    /// <item><b>Missing</b> — none: an <c>UPDATE</c> / <c>DELETE</c> alias
    /// its <c>FROM</c> clause may yet define, else
    /// <see cref="MissingDmlTargetError"/>.</item>
    /// </list>
    /// A value on the parsing stack, never stored: a cached plan holds what it
    /// resolved to, not this.
    /// </summary>
    private readonly struct DmlTarget
    {
        /// <summary>The target as written; an <c>OPENQUERY</c> / <c>OPENROWSET</c> target is named by its stand-in.</summary>
        public readonly MultiPartName Name;

        /// <summary>A remote target's write, whose stand-in is <see cref="Table"/>.</summary>
        public readonly RemoteWrite? Remote;

        /// <summary>A view target.</summary>
        public readonly View? View;

        /// <summary>
        /// The heap the statement writes: the table, a view's base table (null
        /// for a view with none), or the remote stand-in; null for a missing
        /// target.
        /// </summary>
        public readonly HeapTable? Table;

        private DmlTarget(MultiPartName name, RemoteWrite? remote, View? view, HeapTable? table)
        {
            this.Name = name;
            this.Remote = remote;
            this.View = view;
            this.Table = table;
        }

        public static DmlTarget ForRemote(MultiPartName name, RemoteWrite remote) => new(name, remote, view: null, remote.Proxy);

        public static DmlTarget ForView(MultiPartName name, View view) => new(name, remote: null, view, view.BaseTable);

        /// <summary>A table target, or a missing one when <paramref name="table"/> is null.</summary>
        public static DmlTarget ForTable(MultiPartName name, HeapTable? table) => new(name, remote: null, view: null, table);
    }

    /// <summary>
    /// Reads a DML statement's target and resolves it: <see cref="ParseDmlTargetName"/>
    /// then, for a local name, <see cref="ResolveDmlTarget"/>. Entered on the
    /// target's first token, left on its last.
    /// </summary>
    private static DmlTarget ParseDmlTarget(ParserContext context, RemoteWriteKind? remoteKind)
    {
        var name = ParseDmlTargetName(context, remoteKind, out var remote);
        return remote is not null ? DmlTarget.ForRemote(name, remote) : ResolveDmlTarget(context, name, remoteKind);
    }

    /// <summary>
    /// Reads a DML statement's target name — a one- to four-part name, or an
    /// <c>OPENQUERY</c> / <c>OPENROWSET</c> / <c>OPENDATASOURCE</c> form —
    /// and, when it names a linked server's table, builds the
    /// <paramref name="remote"/> write of <paramref name="remoteKind"/>.
    /// A null <paramref name="remoteKind"/> is <c>MERGE</c>'s, whose remote
    /// target is <strong>Msg 5315</strong> however it is spelled — ahead of
    /// anything a remote write would check of the name. Entered on the
    /// target's first token, left on its last.
    /// </summary>
    private static MultiPartName ParseDmlTargetName(ParserContext context, RemoteWriteKind? remoteKind, out RemoteWrite? remote)
    {
        remote = null;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.OpenQuery } when remoteKind is { } kind:
                {
                    var (serverName, query) = Selection.ParseOpenQueryArguments(context);
                    remote = RemoteWrite.ForOpenQuery(context.Batch, serverName, query, kind);
                    return new MultiPartName(remote.Proxy.Name);
                }
            case ReservedKeyword { Keyword: Keyword.OpenRowSet }:
                remote = Selection.ParseAdHocWriteTarget(context, remoteKind ?? RemoteWriteKind.Insert);
                return remoteKind is null
                    ? throw SimulatedSqlException.MergeTargetIsRemote()
                    : new MultiPartName(remote.Proxy.Name);
            case ReservedKeyword { Keyword: Keyword.OpenDataSource } when remoteKind is not null:
                throw Selection.ParseOpenDataSource(context);
        }
        var name = BatchContext.ParseObjectName(context, acceptTableVariable: true);
        if (remoteKind is { } writeKind)
            remote = RemoteWrite.ForTarget(context.Batch, name, writeKind);
        else if (context.Batch.ExpandSynonym(name).Count >= 4)
            throw SimulatedSqlException.MergeTargetIsRemote();
        return name;
    }

    /// <summary>
    /// Resolves a local DML target name: a CTE the statement writes through,
    /// then a view, then a table. When none answers, an <c>UPDATE</c> or
    /// <c>DELETE</c> one-part name is noted as the alias a <c>FROM</c> clause may
    /// define over a linked server's table, those being the two verbs with an
    /// alias form.
    /// </summary>
    private static DmlTarget ResolveDmlTarget(ParserContext context, MultiPartName name, RemoteWriteKind? remoteKind)
    {
        if (TryResolveCteTarget(context, name, out var view) || context.Batch.TryResolveView(name, out view))
            return DmlTarget.ForView(name, view);
        _ = context.Batch.TryResolveTable(name, out var table);
        if (table is null && name.Count == 1 && remoteKind is { } aliasKind and (RemoteWriteKind.Update or RemoteWriteKind.Delete))
        {
            context.Batch.CurrentStatement.RemoteWriteAlias = name.Leaf;
            context.Batch.CurrentStatement.RemoteWriteAliasKind = aliasKind;
        }
        return DmlTarget.ForTable(name, table);
    }

    /// <summary>
    /// A missing DML target's error: Msg 1087 for a table variable, otherwise
    /// the batch's Msg 208 (or a synonym's Msg 5313) for <paramref name="name"/>.
    /// </summary>
    private static SimulatedSqlException MissingDmlTargetError(BatchContext batch, MultiPartName name) =>
        BatchContext.IsTableVariableName(name.Leaf)
            ? SimulatedSqlException.MustDeclareTableVariable(name.Leaf)
            : batch.UnresolvableObjectName(name);

    /// <summary>
    /// What an <c>UPDATE</c>, <c>DELETE</c> or <c>MERGE</c> settles of the
    /// table it writes once resolved: a table-valued parameter is read-only
    /// (Msg 10700), a table variable's write stays outside the transaction
    /// <c>@@TRANCOUNT</c> reads, and a function body may write nothing but a
    /// table variable (Msg 443) — <paramref name="persistent"/> when the
    /// write reaches a persistent object whatever <paramref name="table"/>
    /// is, as a <c>MERGE</c> matching a view's own rows does.
    /// </summary>
    private static void NoteWriteTable(BatchContext batch, MultiPartName name, HeapTable table, string verb, bool persistent = false)
    {
        if (BatchContext.IsTableVariableName(name.Leaf))
            batch.CurrentStatement.TransactedWrite = false;
        if (table.IsTableValuedParameter)
            throw SimulatedSqlException.TableValuedParameterIsReadOnly(name.Leaf);
        FunctionBodyShape.NoteTableWrite(batch, verb, persistent ? null : table);
    }

    /// <summary>
    /// Readies the table an <c>UPDATE</c>, <c>DELETE</c> or <c>MERGE</c>
    /// writes, in the order real refuses it: a disabled clustered index, the
    /// SET options its indexed views, filtered indexes or persisted computed
    /// columns want, and — where <paramref name="checkFilegroup"/> settles it
    /// here rather than per written column — a filegroup it can't write;
    /// then its table-level write lock, taken transaction-scoped inside an
    /// explicit transaction. An <c>INSERT</c> locks first and checks after.
    /// </summary>
    private static void LockWriteTable(BatchContext batch, HeapTable table, string verb, bool checkFilegroup = false)
    {
        RejectDisabledClusteredIndex(table);
        RejectIncorrectSetOptionsForWrite(table, batch, verb);
        if (checkFilegroup)
            RejectWriteToUnwritableFilegroup(table, batch, verb);
        _ = batch.AcquireDataLockIfApplicable(table, default, isWrite: true);
    }

    /// <summary>How an <c>INSERT</c>, <c>UPDATE</c> or <c>DELETE</c> writes through a view.</summary>
    private enum DmlViewRoute : byte
    {
        /// <summary>The view's <c>INSTEAD OF</c> trigger for the action takes the write, whatever the view's shape.</summary>
        InsteadOf,

        /// <summary>Through the one base table the view's chain reaches.</summary>
        BaseTable,

        /// <summary>Through the one base table of a join view the statement's columns name.</summary>
        JoinView,

        /// <summary>Real's refusal: Msg 4403 / 4405, or 4406 for a derived column.</summary>
        Refused,
    }

    /// <summary>
    /// Routes a write of <paramref name="action"/> through <paramref name="view"/>.
    /// A <c>DELETE</c> through a join view is refused: it removes a whole row
    /// and so reaches every base table (Msg 4405).
    /// </summary>
    private static DmlViewRoute RouteViewWrite(BatchContext batch, View view, TriggerActions action) =>
        HasInsteadOfTrigger(batch, view, action) ? DmlViewRoute.InsteadOf
        : view.BaseTable is not null ? DmlViewRoute.BaseTable
        : view.IsJoinUpdatable && action != TriggerActions.Delete ? DmlViewRoute.JoinView
        : DmlViewRoute.Refused;

    /// <summary>
    /// A view real can't write through, named as the statement wrote it: Msg
    /// 4405 when its body reads several sources, Msg 4426 when a <c>UNION</c>
    /// tops it — what a <c>DELETE</c> meets, an <c>UPDATE</c> or <c>INSERT</c>
    /// naming one of its derived columns first — and Msg 4403 otherwise.
    /// </summary>
    private static SimulatedSqlException NonUpdatableViewError(View view, string writtenName) =>
        view.RejectionReason switch
        {
            ViewUpdatabilityRejection.MultipleSources => SimulatedSqlException.ViewUpdateAffectsMultipleTables(writtenName),
            ViewUpdatabilityRejection.Union or ViewUpdatabilityRejection.UnionAll => SimulatedSqlException.ViewWithUnionNotUpdatable(writtenName),
            _ => SimulatedSqlException.CannotUpdateNonUpdatableView(writtenName),
        };

    /// <summary>
    /// The name Msg 334 gives a write's target: the table as written, or,
    /// through a view, the base table whose triggers refuse the
    /// <c>OUTPUT</c> (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    private static string TriggeredOutputTargetName(MultiPartName writtenName, HeapTable table, View? view) =>
        view is null ? writtenName.ToString() : table.Name;

    /// <summary>
    /// The <c>OUTPUT</c> clause of an <c>UPDATE</c> or <c>DELETE</c>, which
    /// binds against a resolved target — through a view, <c>INSERTED</c> /
    /// <c>DELETED</c> take the view's columns, read off the base rows — and
    /// is Msg 334 to the client over a triggered one. An alias form's target
    /// is the table its <c>FROM</c> clause names, read ahead by the caller; a
    /// target still unresolved has its clause stepped over, and one a
    /// <c>FROM</c> follows — an alias naming no table — isn't modeled.
    /// </summary>
    private static OutputProjection? ParseMutationOutput(ParserContext context, MultiPartName name, HeapTable? table, View? view, TriggerActions action)
    {
        if (table is not null)
        {
            var viewShape = view is not null && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output }
                ? SingleBaseViewOutputShape(context.Batch, view, name, table)
                : null;
            var output = TryParseOutputClauseForMutation(context, table, allowInserted: action != TriggerActions.Delete, allowDeleted: true, viewShape);
            RejectClientOutputOnTriggeredTarget(context.Batch, table, action, TriggeredOutputTargetName(name, table, view), output is { HasTarget: false });
            return output;
        }
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
        {
            // Without a FROM the target is a missing object, whose OUTPUT has
            // nothing to bind.
            SkipOutputClause(context);
            if (context.Token is ReservedKeyword { Keyword: Keyword.From })
                throw new NotSupportedException($"OUTPUT with an alias-form {(action == TriggerActions.Delete ? "DELETE" : "UPDATE")} whose alias names no table isn't modeled.");
        }
        return null;
    }

    /// <summary>
    /// An <c>UPDATE</c> / <c>DELETE</c> writing <paramref name="remote"/>,
    /// read up to its <c>OUTPUT</c> slot: real's provider refuses an
    /// <c>OUTPUT</c> clause outright (Msg 405), and only a target the
    /// statement <paramref name="named"/> four-part, with no <c>FROM</c>
    /// clause, replays as one statement — a joined write, like every
    /// <c>OPENQUERY</c> one, goes row by row.
    /// </summary>
    private static void SettleRemoteMutation(ParserContext context, RemoteWrite remote, RemoteWrite? named)
    {
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw SimulatedSqlException.RemoteDmlTargetWithOutput();
        remote.SingleStatement = named is { WrittenName: not null } && context.Token is not ReservedKeyword { Keyword: Keyword.From };
    }

    /// <summary>
    /// The single-table <c>UPDATE</c> / <c>DELETE</c> target once any
    /// <c>FROM</c> clause is ruled out: a missing one reads the rest of the
    /// statement in the compile pass (<see cref="ParseMissingTargetTail"/>)
    /// and is <see cref="MissingDmlTargetError"/>, and a resolved one takes
    /// <see cref="NoteWriteTable"/>.
    /// </summary>
    private static HeapTable RequireMutationTable(ParserContext context, MultiPartName name, HeapTable? table, string verb)
    {
        if (table is null)
        {
            ParseMissingTargetTail(context);
            throw MissingDmlTargetError(context.Batch, name);
        }
        NoteWriteTable(context.Batch, name, table, verb);
        return table;
    }

    /// <summary>
    /// The table a joined <c>UPDATE</c> / <c>DELETE</c> writes, once its
    /// <c>FROM</c> clause has named it — so the write is classified for a
    /// function body's Msg 443 here rather than at the leading identifier. The
    /// statement reads every <c>FROM</c> source, target included, and real
    /// requires SELECT on each, checked before the write permission (probe
    /// M2); reads inside a <c>WHERE</c> subquery route through the standard
    /// read-source sink. The table-level lock, which the leading identifier
    /// couldn't take before the <c>FROM</c> clause identified the target, is
    /// taken here; row locks follow at the mutation site, as on the
    /// single-table path.
    /// </summary>
    private static HeapTable BindJoinedMutationTable(ParserContext context, FromSource[] sources, int targetIndex, string verb)
    {
        var table = sources[targetIndex].BackingTable
            ?? throw new NotSupportedException("UPDATE / DELETE target must be a table — derived-table targets aren't modeled.");
        FunctionBodyShape.NoteTableWrite(context.Batch, verb, table);
        if (!context.Batch.IsSkipping)
        {
            CheckJoinedReadSources(context.Batch, sources, targetIndex);
            PermissionEnforcement.CheckSchemaObject(context.Batch, verb, (SchemaObject?)sources[targetIndex].ViaSynonym ?? table);
        }
        RejectIncorrectSetOptionsForWrite(table, context.Batch, verb);
        _ = context.Batch.AcquireDataLockIfApplicable(table, default, isWrite: true);
        return table;
    }

    /// <summary>
    /// An <c>UPDATE</c> or <c>DELETE</c> through a single-table view whose
    /// owner differs from its base table's checks the base table too, after
    /// the view: SELECT on the base columns the <c>WHERE</c> and <c>SET</c>
    /// expressions read, then the write — <c>UPDATE</c> on the columns
    /// assigned, or <c>DELETE</c>, which isn't column-grantable, on the table
    /// (probed 2026-09-27 against SQL Server 2025). Under the action's
    /// <c>INSTEAD OF</c> trigger nothing is written through the view, but its
    /// pseudo-tables still read every base column, which takes SELECT on the
    /// whole base table and nothing more.
    /// </summary>
    private static void CheckBrokenChainMutation(
        BatchContext batch,
        View? sourceView,
        TriggerActions action,
        BooleanExpression? where,
        List<(string? ColumnName, Expression Expr)>? rawAssignments)
    {
        if (sourceView is not { BaseTable: { } baseTable })
            return;
        if (HasInsteadOfTrigger(batch, sourceView, action))
        {
            PermissionEnforcement.CheckBrokenChainColumns(batch, Permission.Select, sourceView, viewColumns: null);
            return;
        }
        // Each branch's captured reader lives in the branch's own scope, so
        // a table target's early return allocates no closure.
        if (action == TriggerActions.Delete)
        {
            if (where is not null)
            {
                var baseRead = new ColumnReadTarget(sourceView);
                where.VisitOperandExpressions(op => op.VisitColumnReferences(baseRead.Add));
                PermissionEnforcement.CheckBrokenChainColumns(batch, Permission.Select, sourceView, baseRead);
            }
            PermissionEnforcement.CheckBrokenChainWrite(batch, "DELETE", sourceView, baseTable);
        }
        else
        {
            var read = new ColumnReadTarget(sourceView);
            where?.VisitOperandExpressions(op => op.VisitColumnReferences(read.Add));
            foreach (var (_, expr) in rawAssignments!)
                expr.VisitColumnReferences(read.Add);
            PermissionEnforcement.CheckBrokenChainColumns(batch, Permission.Select, sourceView, read);
            var assigned = new ColumnReadTarget(sourceView);
            foreach (var columnName in SetColumnNames(rawAssignments))
                assigned.Add(columnName);
            PermissionEnforcement.CheckBrokenChainColumns(batch, Permission.Update, sourceView, assigned);
        }
    }

    /// <summary>
    /// Reads column <paramref name="name"/> of a single-table <c>UPDATE</c> /
    /// <c>DELETE</c> target's row: through a view by the view's own column
    /// names — off <paramref name="viewRow"/> for a windowed or row-limited
    /// body, else off the base row, where a derived column is Msg 207 — and
    /// otherwise by the table's.
    /// </summary>
    private static SqlValue ReadTargetRowColumn(BatchContext batch, HeapTable table, View? view, SqlValue[] row, SqlValue[]? viewRow, (int Page, int Slot) address, MultiPartName name)
    {
        if (view is not null)
        {
            for (var v = 0; v < view.OutputColumns.Length; v++)
            {
                if (batch.CurrentDatabase.Collation.Equals(view.OutputColumns[v].Name, name.Leaf))
                {
                    var baseOrd = view.BaseColumnOrdinals[v];
                    return viewRow is not null ? viewRow[v]
                        : baseOrd < 0 ? throw SimulatedSqlException.InvalidColumnName(name)
                        : row[baseOrd];
                }
            }
            throw RowLocator.IsLocatorName(name)
                ? SimulatedSqlException.OnlyBaseTableColumnsInTextPtr()
                : SimulatedSqlException.InvalidColumnName(name);
        }
        for (var k = 0; k < table.Columns.Length; k++)
        {
            if (batch.CurrentDatabase.Collation.Equals(table.Columns[k].Name, name.Leaf))
                return row[k];
        }
        return RowLocator.IsLocatorName(name)
            ? ReadTargetRowLocator(batch, table, row, address, name)
            : throw SimulatedSqlException.InvalidColumnName(name);
    }

    /// <summary>
    /// A <see cref="RowLocator"/> read of the target row the write is
    /// visiting — a <c>TEXTPTR</c> in its <c>SET</c> or <c>WHERE</c>, or the
    /// row's address.
    /// </summary>
    private static SqlValue ReadTargetRowLocator(BatchContext batch, HeapTable table, SqlValue[] row, (int Page, int Slot) address, MultiPartName name)
    {
        if (name.Leaf[0] == RowLocator.AddressMarker)
            return SqlValue.FromInt64(RowLocator.Pack(address));
        var leaf = name.Leaf[1..];
        for (var k = 0; k < table.Columns.Length; k++)
        {
            if (batch.CurrentDatabase.Collation.Equals(table.Columns[k].Name, leaf))
                return LegacyTextPointer.For(table, k, address, row[k]);
        }
        throw SimulatedSqlException.InvalidColumnName(name.WithLeaf(leaf));
    }

    /// <summary>
    /// The shape half of whether a DML statement's plan may be cached: a
    /// table target (not a view), an <c>OUTPUT … INTO</c> target a plan can
    /// hold, and nothing <see cref="BlocksDmlPlan"/> names — the client
    /// <c>OUTPUT</c> half of it only when rows go to the client.
    /// </summary>
    private static bool AdmitsDmlPlan(BatchContext batch, HeapTable table, View? view, OutputProjection? output) =>
        view is null
        && output is not { TargetBlocksDmlPlan: true }
        && !BlocksDmlPlan(batch, table, clientOutput: output is { HasTarget: false });
}
