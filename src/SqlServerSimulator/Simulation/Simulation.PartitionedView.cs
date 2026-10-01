using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// Writes through a partitioned view: a stored UNION ALL view each of whose
// branches reads one table plainly (View.PartitionedBase), whose members real
// routes an INSERT, UPDATE or DELETE to by the CHECK constraints on a
// partitioning column (probed 2026-10-01 against SQL Server 2025). Whether the
// members qualify is settled at each write, in real's order, since their keys,
// constraints and triggers may have changed since the view was created.
partial class Simulation
{
    /// <summary>The kind of write a partitioned view's refusals depend on.</summary>
    private enum PartitionedWrite : byte
    {
        Insert,
        Update,

        /// <summary>An <c>UPDATE</c> whose <c>SET</c> list names the partitioning column, which may move a row to another member.</summary>
        UpdatePartitionColumn,

        Delete,
    }

    /// <summary>One member table of a partitioned view, as a write reads it.</summary>
    private sealed class PartitionedMember(HeapTable table, Expression[] projections, int[] bareOrdinals, int[] ordinals)
    {
        public readonly HeapTable Table = table;

        /// <summary>The branch's projections, one per view column.</summary>
        public readonly Expression[] Projections = projections;

        /// <summary>Per view column, the table column the branch projects bare, whatever its type; -1 for an expression.</summary>
        public readonly int[] BareOrdinals = bareOrdinals;

        /// <summary>
        /// Per view column, the table column it is — a bare column of the
        /// view's own type, lengths aside; -1 for a column the union derives,
        /// which a write can't name (Msg 271).
        /// </summary>
        public readonly int[] Ordinals = ordinals;

        /// <summary>
        /// The values the member's first-created CHECK constraint over the
        /// partitioning column admits, which is what real routes a row by.
        /// </summary>
        public ValueDomain? Partition;

        /// <summary>The view row a row of the member's table shows.</summary>
        public SqlValue[] ViewRow(SqlValue[] tableRow, HeapColumn[] viewColumns, BatchContext batch)
        {
            var row = new SqlValue[viewColumns.Length];
            RuntimeContext? runtime = null;
            for (var i = 0; i < row.Length; i++)
            {
                SqlValue value;
                if (this.BareOrdinals[i] >= 0)
                {
                    value = tableRow[this.BareOrdinals[i]];
                }
                else
                {
                    runtime ??= new RuntimeContext(name =>
                        Array.FindIndex(this.Table.Columns, column => batch.CurrentDatabase.Collation.Equals(column.Name, name.Leaf)) is var ordinal and >= 0
                            ? tableRow[ordinal]
                            : throw SimulatedSqlException.InvalidColumnName(name), batch);
                    value = this.Projections[i].Run(runtime.Value);
                }
                var type = viewColumns[i].Type;
                row[i] = value.Type == type ? value : value.IsNull ? SqlValue.Null(type) : value.CoerceTo(type);
            }
            return row;
        }

        /// <summary>The member's columns in view order, which a row routed to it is inserted as.</summary>
        public HeapColumn[] InsertColumns()
        {
            var columns = new HeapColumn[this.Ordinals.Length];
            for (var i = 0; i < columns.Length; i++)
                columns[i] = this.Table.Columns[this.Ordinals[i]];
            return columns;
        }
    }

    /// <summary>A partitioned view's members once a write has found them qualifying.</summary>
    private sealed class PartitionedPlan(PartitionedMember[] members, int partitionColumn, HeapColumn[] columns)
    {
        public readonly PartitionedMember[] Members = members;

        /// <summary>The partitioning column's ordinal among the view's columns.</summary>
        public readonly int PartitionColumn = partitionColumn;

        /// <summary>The partitioned view's own columns.</summary>
        public readonly HeapColumn[] Columns = columns;

        /// <summary>The member whose constraints admit <paramref name="viewRow"/>'s partitioning value, or -1.</summary>
        public int Route(SqlValue[] viewRow)
        {
            var value = viewRow[this.PartitionColumn];
            if (value.IsNull)
                return -1;
            var type = this.Columns[this.PartitionColumn].Type;
            if (value.Type != type)
                value = value.CoerceTo(type);
            for (var m = 0; m < this.Members.Length; m++)
            {
                if (this.Members[m].Partition!.Admits(value, type))
                    return m;
            }
            return -1;
        }
    }

    /// <summary>
    /// The member tables of <paramref name="partitioned"/>, from a parse of its
    /// body; a branch whose table carries a computed column makes the view a
    /// plain union to real, refused as one (Msg 4406, or 4426 for a
    /// <c>DELETE</c>, naming <paramref name="written"/>).
    /// </summary>
    private static List<(HeapTable Table, Selection Branch)> PartitionedMembers(BatchContext batch, View partitioned, string written, PartitionedWrite write)
    {
        var body = batch.Connection.Simulation.ParseViewBodyPlan(batch, partitioned);
        var members = new List<(HeapTable, Selection)>();
        foreach (var branch in body.UnionAllBranches!)
        {
            var table = branch.UpdatabilityProfile!.Sources[0].BackingTable!;
            if (Array.Exists(table.Columns, column => column.Computed is not null))
            {
                throw write == PartitionedWrite.Delete
                    ? SimulatedSqlException.ViewWithUnionNotUpdatable(written)
                    : SimulatedSqlException.ViewDmlTouchesDerivedField(written);
            }
            members.Add((table, branch));
        }
        return members;
    }

    /// <summary>
    /// Whether a stored view's body is a partitioned view's: a <c>UNION ALL</c>
    /// chain each of whose branches reads one base table with no filter, row
    /// limit, window, <c>DISTINCT</c> or grouping. Any other <c>UNION ALL</c>
    /// view is a plain union to a write (probed 2026-10-01 against SQL Server
    /// 2025).
    /// </summary>
    private static bool IsPartitionedViewShape(Selection body)
    {
        if (body.UnionAllBranches is not { } branches)
            return false;
        foreach (var branch in branches)
        {
            if (branch.UpdatabilityProfile is not { Sources: [{ BackingTable: { IsTableVariable: false } table }], Excluders.Length: 0 }
                || branch.HasTopOrOffsetOrFetch
                || branch.HasWindows
                || table.Name.StartsWith('#'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The member column <paramref name="projection"/> converts to the type it
    /// already has — <c>CAST(v AS int)</c> over an <c>int</c> column — or -1.
    /// </summary>
    private static int KeptColumnOrdinal(Expression projection, HeapTable table, Collation collation)
    {
        while (projection is NamedExpression named)
            projection = named.Inner;
        SqlType? TypeOf(MultiPartName name) => Array.Find(table.Columns, column => collation.Equals(column.Name, name.Leaf))?.Type;
        var kept = projection switch
        {
            Cast cast => cast.ColumnKeptAsIs(TypeOf),
            ConvertExpression convert => convert.ColumnKeptAsIs(TypeOf),
            _ => null,
        };
        return kept is null ? -1 : Array.FindIndex(table.Columns, column => collation.Equals(column.Name, kept.ReferencedName.Leaf));
    }

    /// <summary>
    /// The view column carrying a member's key column <paramref name="ordinal"/>
    /// bare, which the key checks require, or -1.
    /// </summary>
    private static int KeyPosition(PartitionedMember member, int ordinal)
    {
        for (var i = 0; i < member.Ordinals.Length; i++)
        {
            if (member.Ordinals[i] == ordinal && member.BareOrdinals[i] == ordinal)
                return i;
        }
        return -1;
    }

    /// <summary>A partitioned view as its refusals name it: <c>db.schema.view</c>.</summary>
    private static string PartitionedViewLabel(View view) => $"{view.Schema.Database.Name}.{view.Schema.Name}.{view.Name}";

    /// <summary>A member table as a partitioned view's refusals name it: <c>[db].[schema].[table]</c>.</summary>
    private static string PartitionedMemberLabel(BatchContext batch, HeapTable table)
    {
        var database = batch.DatabaseFor(table);
        foreach (var (schemaName, schema) in database.Schemas)
        {
            if (schema.SchemaId == table.SchemaId)
                return $"[{database.Name}].[{schemaName}].[{table.Name}]";
        }
        return $"[{database.Name}].[{table.Name}]";
    }

    /// <summary>
    /// Refuses a write through <paramref name="partitioned"/> whose statement
    /// also reads one of its members, as <paramref name="reads"/> recorded them
    /// (Msg 4439): a member table, the view itself or a view over it — the
    /// first member then — or a view over a member.
    /// </summary>
    private static void RefuseMemberReads(BatchContext batch, View partitioned, List<(HeapTable Table, Selection Branch)> members, List<SchemaObject> reads)
    {
        foreach (var read in reads)
        {
            var member = read switch
            {
                HeapTable table => members.FindIndex(candidate => ReferenceEquals(candidate.Table, table)),
                View { PartitionedBase: { } readBase } when ReferenceEquals(readBase, partitioned) => 0,
                View { BaseTable: { } viewTable } => members.FindIndex(candidate => ReferenceEquals(candidate.Table, viewTable)),
                _ => -1,
            };
            if (member >= 0)
                throw SimulatedSqlException.PartitionedViewSourceReadsMember(PartitionedViewLabel(partitioned), PartitionedMemberLabel(batch, members[member].Table));
        }
    }

    /// <summary>
    /// Whether two member columns' types are the same to a partitioned view:
    /// the same type and collation, a string's or binary's length aside.
    /// </summary>
    private static bool SameTypeLengthAside(SqlType left, SqlType right) =>
        left == right
        || (left.GetType() == right.GetType()
            && left is VarcharSqlType or NVarcharSqlType or CharSqlType or NCharSqlType or VarbinarySqlType or BinarySqlType
            && Equals(left.Collation, right.Collation));

    /// <summary>
    /// Settles whether <paramref name="partitioned"/>'s members make it a
    /// partitioned view a write may pass through, raising real's refusals in
    /// real's order: a table two members read (Msg 4442), a member projecting
    /// one column twice (4443), a member without a primary key (4440), a key
    /// the union doesn't carry as it stands (4444) or carries in other columns
    /// than the first member's (4445), and no column whose trusted CHECK
    /// constraints keep the members' values apart (4436). What the write
    /// itself may do is <see cref="RefusePartitionedWrite"/>'s.
    /// </summary>
    private static PartitionedPlan AnalyzePartitionedView(BatchContext batch, View partitioned, List<(HeapTable Table, Selection Branch)> found)
    {
        var label = PartitionedViewLabel(partitioned);
        var columns = partitioned.OutputColumns;
        var width = columns.Length;
        var collation = batch.CurrentDatabase.Collation;

        for (var i = 0; i < found.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (ReferenceEquals(found[i].Table, found[j].Table))
                    throw SimulatedSqlException.UnionAllViewTableUsedTwice(label, PartitionedMemberLabel(batch, found[j].Table));
            }
        }

        var members = new PartitionedMember[found.Count];
        for (var m = 0; m < members.Length; m++)
        {
            var (table, branch) = found[m];
            var projections = branch.UpdatabilityProfile!.Projections;
            var bare = new int[width];
            var direct = new int[width];
            for (var i = 0; i < width; i++)
            {
                bare[i] = UnwrapDirectRef(projections[i]) is { ReferencedName: var name }
                    ? Array.FindIndex(table.Columns, column => collation.Equals(column.Name, name.Leaf))
                    : -1;
                // A conversion to the type the column already has is the column
                // itself to a write, though not to the key checks below
                // (probed 2026-10-01 against SQL Server 2025).
                var written = bare[i] >= 0 ? bare[i] : KeptColumnOrdinal(projections[i], table, collation);
                direct[i] = written >= 0 && SameTypeLengthAside(table.Columns[written].Type, columns[i].Type) ? written : -1;
            }
            members[m] = new PartitionedMember(table, projections, bare, direct);
        }

        // Real names the column a member projects twice, but the last member's
        // table, whichever member it found it in.
        foreach (var member in members)
        {
            for (var i = 0; i < width; i++)
            {
                if (member.BareOrdinals[i] >= 0 && Array.IndexOf(member.BareOrdinals, member.BareOrdinals[i], i + 1) > i)
                    throw SimulatedSqlException.UnionAllViewColumnUsedTwice(label, member.Table.Columns[member.BareOrdinals[i]].Name, PartitionedMemberLabel(batch, members[^1].Table));
            }
        }

        var keyPositions = new List<int>[members.Length];
        for (var m = 0; m < members.Length; m++)
        {
            var member = members[m];
            if (member.Table.KeyConstraints.Find(key => key.Kind == KeyConstraintKind.PrimaryKey) is not { } primaryKey)
                throw SimulatedSqlException.UnionAllViewMemberWithoutPrimaryKey(label, PartitionedMemberLabel(batch, member.Table));
            var positions = new List<int>();
            foreach (var ordinal in primaryKey.FullOrdinals)
            {
                var position = KeyPosition(member, ordinal);
                if (position < 0)
                    throw SimulatedSqlException.UnionAllViewPrimaryKeyNotProjected(label, PartitionedMemberLabel(batch, member.Table));
                positions.Add(position);
            }
            positions.Sort();
            keyPositions[m] = positions;
        }
        for (var m = 1; m < members.Length; m++)
        {
            if (!keyPositions[m].SequenceEqual(keyPositions[0]))
                throw SimulatedSqlException.UnionAllViewPrimaryKeyMisaligned(label, PartitionedMemberLabel(batch, members[m].Table));
        }

        var partitionColumn = -1;
        var outsideKey = false;
        for (var i = 0; i < width && partitionColumn < 0; i++)
        {
            if (!PartitionsOn(i))
                continue;
            if (keyPositions[0].Contains(i))
                partitionColumn = i;
            else
                outsideKey = true;
        }
        if (partitionColumn < 0)
            throw SimulatedSqlException.UnionAllViewNoPartitioningColumn(label, outsideKey ? (byte)13 : (byte)12);
        foreach (var member in members)
            member.Partition = MemberDomains(member, partitionColumn)[0];
        return new PartitionedPlan(members, partitionColumn, columns);

        // Whether view column i is one each member carries as itself, and
        // whose values — NULL included — each two members' trusted CHECK
        // constraints keep apart: real reads each constraint alone, never
        // their intersection, so one constraint of each must do it (probed
        // 2026-10-01 against SQL Server 2025: k < 12 beside
        // k NOT BETWEEN 10 AND 11 partitions nothing from 10 to 19).
        bool PartitionsOn(int i)
        {
            var domains = new List<ValueDomain>[members.Length];
            for (var m = 0; m < members.Length; m++)
            {
                if (members[m].Ordinals[i] < 0)
                    return false;
                domains[m] = MemberDomains(members[m], i);
                for (var other = 0; other < m; other++)
                {
                    if (!domains[m].Exists(domain => domains[other].Exists(domain.IsDisjointFrom)))
                        return false;
                }
            }
            return true;
        }

        // What each of a member's constraints over view column i admits, in
        // the order they were created, read in the view's type so members
        // whose lengths differ compare; a member with none admits anything.
        // The first is what real routes a row by, so a row it admits goes to
        // that member and meets the others there (Msg 547).
        List<ValueDomain> MemberDomains(PartitionedMember member, int i)
        {
            var column = member.Table.Columns[member.Ordinals[i]];
            var asViewColumn = new HeapColumn(column.Name, columns[i].Type, maxLength: null, nullable: column.Nullable);
            var domains = new List<ValueDomain>();
            foreach (var check in member.Table.CheckConstraints)
            {
                if (!check.IsDisabled && !check.IsNotTrusted && SoleColumn(batch, check.Predicate, member.Table) == column)
                    domains.Add(ValueDomain.OfPredicate(batch, check.Predicate, asViewColumn, asPartitionedView: true));
            }
            if (domains.Count == 0)
                domains.Add(ValueDomain.All(asViewColumn));
            return domains;
        }
    }

    /// <summary>
    /// What a write of <paramref name="write"/> through a partitioned view may
    /// not do, in real's order: name a column the union derives (Msg 271 —
    /// for a write that may move a row between members, any column, since the
    /// row is inserted whole), reach a view whose members carry columns it
    /// doesn't deliver (4438), and then the write's own: an identity,
    /// rowversion or <c>INSTEAD OF</c> trigger on a member, and — for a write
    /// that may move a row — a cascading key, a DML trigger or members whose
    /// column types differ. <paramref name="written"/> is the view columns a
    /// statement names, by ordinal and as written.
    /// </summary>
    private static void RefusePartitionedWrite(BatchContext batch, View partitioned, PartitionedPlan plan, PartitionedWrite write, List<(int ViewOrdinal, string Name)> written)
    {
        var label = PartitionedViewLabel(partitioned);
        var members = plan.Members;
        var columns = plan.Columns;
        var width = columns.Length;
        var partitionColumn = plan.PartitionColumn;

        // A write that may move a row inserts it whole, so it names every
        // column; real checks those after the columns the members carry.
        if (write != PartitionedWrite.UpdatePartitionColumn)
            RefuseDerivedWrites(written);
        foreach (var member in members)
        {
            if (member.Table.Columns.Length != width)
            {
                throw SimulatedSqlException.PartitionedViewMissingColumns(label, write switch
                {
                    PartitionedWrite.Insert => 17,
                    PartitionedWrite.Update => 16,
                    PartitionedWrite.UpdatePartitionColumn => 14,
                    _ => 18,
                });
            }
        }
        if (write == PartitionedWrite.UpdatePartitionColumn)
        {
            var everyColumn = new List<(int ViewOrdinal, string Name)>(width);
            for (var i = 0; i < width; i++)
                everyColumn.Add((i, columns[i].Name));
            RefuseDerivedWrites(everyColumn);
        }

        bool TypesDiffer(int i) =>
            Array.Exists(members, member => member.Table.Columns[member.Ordinals[i]].Type != members[0].Table.Columns[members[0].Ordinals[i]].Type);
        switch (write)
        {
            case PartitionedWrite.Insert:
                if (TypesDiffer(partitionColumn))
                    throw SimulatedSqlException.PartitionedViewPartitionTypesDiffer(label, 18);
                if (Array.Find(members, member => member.Table.IdentityOrdinal >= 0) is { } identityMember)
                    throw SimulatedSqlException.PartitionedViewInsertIdentity(label, PartitionedMemberLabel(batch, identityMember.Table));
                RefuseMemberTimestampAndInsteadOf(batch, members, label, state: 4);
                break;
            case PartitionedWrite.Update:
                RefuseMemberTimestampAndInsteadOf(batch, members, label, state: 1);
                break;
            case PartitionedWrite.UpdatePartitionColumn:
                RefuseMemberTimestampAndInsteadOf(batch, members, label, state: 1);
                if (TypesDiffer(partitionColumn))
                    throw SimulatedSqlException.PartitionedViewPartitionTypesDiffer(label, 17);
                var partitionName = columns[partitionColumn].Name;
                foreach (var member in members)
                {
                    var memberLabel = PartitionedMemberLabel(batch, member.Table);
                    if (member.Table.IdentityOrdinal is >= 0 and var identity)
                        throw SimulatedSqlException.PartitionedViewUpdateIdentity(label, columns[Array.IndexOf(member.Ordinals, identity)].Name, memberLabel);
                    if (member.Table.IncomingForeignKeys.Exists(key => key.DeleteAction == ReferentialAction.Cascade || key.UpdateAction == ReferentialAction.Cascade))
                        throw SimulatedSqlException.PartitionedViewUpdateCascade(partitionName, label, memberLabel);
                    if (HasAfterTrigger(batch, member.Table, TriggerActions.Insert | TriggerActions.Update | TriggerActions.Delete))
                        throw SimulatedSqlException.PartitionedViewUpdateTrigger(partitionName, label, memberLabel);
                }
                for (var i = 0; i < width; i++)
                {
                    if (i != partitionColumn && TypesDiffer(i))
                        throw SimulatedSqlException.PartitionedViewColumnTypesDiffer(label);
                }
                break;
            default:
                if (Array.Find(members, member => HasInsteadOfTrigger(batch, member.Table, TriggerActions.Insert | TriggerActions.Update | TriggerActions.Delete)) is { } insteadOfMember)
                    throw SimulatedSqlException.PartitionedViewInsteadOfTrigger(label, PartitionedMemberLabel(batch, insteadOfMember.Table), 2);
                break;
        }

        void RefuseDerivedWrites(List<(int ViewOrdinal, string Name)> names)
        {
            foreach (var (ordinal, name) in names)
            {
                if (Array.Exists(members, member => member.Ordinals[ordinal] < 0))
                    throw SimulatedSqlException.ColumnCannotBeModified(name, write == PartitionedWrite.Insert ? (byte)3 : (byte)2);
            }
        }
    }

    /// <summary>A member's <c>rowversion</c> column (Msg 4431) or <c>INSTEAD OF</c> trigger (Msg 4434), the first member carrying one.</summary>
    private static void RefuseMemberTimestampAndInsteadOf(BatchContext batch, PartitionedMember[] members, string label, byte state)
    {
        if (Array.Find(members, member => Array.Exists(member.Table.Columns, column => column.Type is RowVersionSqlType)) is { } timestampMember)
            throw SimulatedSqlException.PartitionedViewTimestampColumn(label, PartitionedMemberLabel(batch, timestampMember.Table), state);
        if (Array.Find(members, member => HasInsteadOfTrigger(batch, member.Table, TriggerActions.Insert | TriggerActions.Update | TriggerActions.Delete)) is { } insteadOfMember)
            throw SimulatedSqlException.PartitionedViewInsteadOfTrigger(label, PartitionedMemberLabel(batch, insteadOfMember.Table), state);
    }

    /// <summary>
    /// <c>INSERT</c> through a partitioned view, or a view, CTE or derived
    /// table over one, entered on the token after its name. Every column must
    /// take a value, none of them <c>DEFAULT</c>; each row goes to the member
    /// whose CHECK constraints admit its partitioning value, the members
    /// written in turn, and a row no member admits is Msg 4457 — after the
    /// members' own errors, which real raises first whatever the row order.
    /// </summary>
    private static SimulatedNonQuery ProcessPartitionedViewInsert(View view, ParserContext context, MultiPartName name)
    {
        var batch = context.Batch;
        var partitioned = view.PartitionedBase!;
        var collation = batch.CurrentDatabase.Collation;
        var viewColumns = ViewColumnsFor(batch, view, name);

        var listed = new List<int>();
        var hasExplicitColumnList = context.Token is Operator { Character: '(' };
        if (hasExplicitColumnList)
        {
            while (true)
            {
                if (context.GetNextRequired() is not StringToken column)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                var ordinal = Array.FindIndex(viewColumns, candidate => collation.Equals(candidate.Name, column.Value));
                if (ordinal < 0)
                    throw SimulatedSqlException.InvalidColumnName(column.Value);
                if (listed.Contains(ordinal))
                    throw SimulatedSqlException.ColumnAssignedMoreThanOnce(viewColumns[ordinal].Name);
                listed.Add(ordinal);
                var separator = context.GetNextRequired();
                if (separator is Operator { Character: ')' })
                    break;
                if (separator is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
        }
        else
        {
            for (var i = 0; i < viewColumns.Length; i++)
                listed.Add(i);
        }
        var destinationColumns = listed.ConvertAll(ordinal => viewColumns[ordinal]).ToArray();

        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw SimulatedSqlException.OutputOnPartitionedView(name.ToString());

        List<SqlValue[]> sourceRows;
        var reads = new List<SchemaObject>();
        using (ParserScope.Enter(ref context.PartitionedWriteReads, reads))
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Default }:
                    throw SimulatedSqlException.DefaultsThroughSetOperatorView();
                case ReservedKeyword { Keyword: Keyword.Values }:
                    var tuples = ParseValuesTuples(context, allowDefault: true);
                    if (tuples.Exists(tuple => Array.Exists(tuple, cell => cell is DefaultValueExpression)))
                        throw SimulatedSqlException.DefaultsThroughSetOperatorView();
                    RejectTooManyValueRows(tuples);
                    RejectValuesArityMismatch(tuples, destinationColumns, hasExplicitColumnList, identityColumn: null, destinationTable: null);
                    RejectRaggedValueTuples(tuples);
                    sourceRows = batch.IsSkipping ? [] : EvaluateParsedTuples(tuples, batch, out _);
                    break;
                case ReservedKeyword { Keyword: Keyword.Select }:
                    sourceRows = ExecuteSelectSource(context, destinationColumns, hasExplicitColumnList);
                    break;
                case Operator { Character: '(' }:
                    sourceRows = ExecuteParenthesizedSelectSource(context, destinationColumns, hasExplicitColumnList);
                    break;
                case ReservedKeyword { Keyword: Keyword.Exec or Keyword.Execute }:
                    sourceRows = ExecuteExecSource(context, destinationColumns, out _);
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }

        var found = PartitionedMembers(batch, partitioned, name.ToString(), PartitionedWrite.Insert);
        RefuseMemberReads(batch, partitioned, found, reads);
        var plan = AnalyzePartitionedView(batch, partitioned, found);

        // Each listed column lands on a view column every member carries as
        // itself, and together they reach them all.
        var viewOrdinals = new int[listed.Count];
        var written = new List<(int ViewOrdinal, string Name)>(listed.Count);
        var covered = new bool[plan.Columns.Length];
        for (var i = 0; i < listed.Count; i++)
        {
            viewOrdinals[i] = view.BaseColumnOrdinals[listed[i]];
            if (viewOrdinals[i] < 0)
                throw SimulatedSqlException.ViewDmlTouchesDerivedField(name.ToString());
            covered[viewOrdinals[i]] = true;
            written.Add((viewOrdinals[i], destinationColumns[i].Name));
        }
        RefusePartitionedWrite(batch, partitioned, plan, PartitionedWrite.Insert, written);
        if (Array.IndexOf(covered, false) >= 0)
            throw SimulatedSqlException.PartitionedViewInsertMissingValues(PartitionedViewLabel(partitioned));
        foreach (var member in plan.Members)
            FunctionBodyShape.NoteTableWrite(batch, "INSERT", member.Table);
        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var routed = new List<SqlValue[]>[plan.Members.Length];
        var unrouted = false;
        foreach (var sourceRow in sourceRows)
        {
            var row = new SqlValue[plan.Columns.Length];
            for (var i = 0; i < viewOrdinals.Length; i++)
                row[viewOrdinals[i]] = CoerceForInsert(sourceRow[i], plan.Columns[viewOrdinals[i]]);
            if (plan.Route(row) is >= 0 and var target)
                (routed[target] ??= []).Add(row);
            else
                unrouted = true;
        }

        // Real writes the members last to first, which is the order their
        // triggers fire in (probed 2026-10-01 against SQL Server 2025).
        var inserted = 0;
        for (var m = plan.Members.Length - 1; m >= 0; m--)
        {
            if (routed[m] is not { } rows)
                continue;
            var member = plan.Members[m];
            _ = InsertRows(context, new InsertPlan(member.Table, destinationView: null, joinViewPlan: null, member.InsertColumns(), output: null, top: null, valueTuples: null), rows, valueTupleStamps: null);
            inserted += rows.Count;
        }
        if (unrouted)
            throw SimulatedSqlException.PartitionedViewValueFitsNoMember();
        return new SimulatedNonQuery(inserted);
    }

    /// <summary>
    /// <c>UPDATE</c> through a partitioned view, or a view, CTE or derived
    /// table over one: with <paramref name="from"/>, the joined form whose
    /// target is source <paramref name="targetIndex"/>, else the statement's
    /// one target. Entered with the <c>SET</c> list read, on the token after
    /// it, the tables and views it read in <paramref name="reads"/>. Each member's rows the view shows are read and written as the
    /// member's own; a row whose partitioning column the <c>SET</c> list moves
    /// out of its member is deleted there and inserted into the member that
    /// admits it — every move's delete ahead of the updates and inserts, so
    /// rows trading members pass (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    private static SimulatedNonQuery ExecutePartitionedViewUpdate(
        ParserContext context,
        MultiPartName name,
        View view,
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        Selection.DmlTopLimit? top,
        Selection.PreParsedFrom? from,
        int targetIndex,
        List<SchemaObject> reads)
    {
        var batch = context.Batch;
        var partitioned = view.PartitionedBase!;
        var collation = batch.CurrentDatabase.Collation;
        if (top is not null)
            throw SimulatedSqlException.TopOnPartitionedView();
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw SimulatedSqlException.OutputOnPartitionedView(name.ToString());

        var viewColumns = ViewColumnsFor(batch, view, name);
        var setColumns = new List<(int ViewOrdinal, string Name)>();
        foreach (var (columnName, expr) in rawAssignments)
        {
            if (columnName is null)
                continue;
            if (expr == ColumnDefaultValue.Unbound)
                throw SimulatedSqlException.DefaultsThroughSetOperatorView();
            var ordinal = Array.FindIndex(viewColumns, column => collation.Equals(column.Name, columnName));
            if (ordinal < 0)
                throw SimulatedSqlException.InvalidColumnName(columnName);
            if (view.BaseColumnOrdinals[ordinal] < 0)
                throw SimulatedSqlException.ViewDmlTouchesDerivedField(name.ToString());
            setColumns.Add((view.BaseColumnOrdinals[ordinal], columnName));
        }

        var (sources, joins, where, positioned) = ReadPartitionedTarget(context, name, view, viewColumns, from, ref targetIndex, reads);
        var found = PartitionedMembers(batch, partitioned, name.ToString(), PartitionedWrite.Update);
        RefuseMemberReads(batch, partitioned, found, reads);
        RefuseJoinedMemberReads(batch, partitioned, found, sources, targetIndex);
        var plan = AnalyzePartitionedView(batch, partitioned, found);
        var movesRows = setColumns.Exists(set => set.ViewOrdinal == plan.PartitionColumn);
        RefusePartitionedWrite(batch, partitioned, plan, movesRows ? PartitionedWrite.UpdatePartitionColumn : PartitionedWrite.Update, setColumns);

        var assignments = new List<(int Ordinal, Expression Expr)>[plan.Members.Length];
        for (var m = 0; m < plan.Members.Length; m++)
        {
            var member = plan.Members[m];
            var memberAssignments = new List<(int Ordinal, Expression Expr)>(rawAssignments.Count);
            var set = 0;
            foreach (var (columnName, expr) in rawAssignments)
            {
                if (columnName is null)
                {
                    memberAssignments.Add((-1, expr));
                    continue;
                }
                memberAssignments.Add((member.Ordinals[setColumns[set].ViewOrdinal], expr));
                set++;
            }
            assignments[m] = memberAssignments;
        }
        var resolver = Selection.ColumnTypeResolverFor(sources);
        BindSetValues(batch, plan.Members[0].Table, assignments[0], resolver, _ => false);
        CheckPartitionedWritePermissions(batch, name, view, "UPDATE", where is not null || rawAssignments.Exists(static assignment => assignment.Expr.ReadsAnyColumn()));
        foreach (var member in plan.Members)
        {
            FunctionBodyShape.NoteTableWrite(batch, "UPDATE", member.Table);
            LockWriteTable(batch, member.Table, "UPDATE", checkFilegroup: true);
        }
        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);
        RefusePositionedPartitionedWrite(batch, partitioned, view, positioned);

        var walk = WalkPartitionedTarget(context, view, plan, sources, joins, targetIndex, where, RowLockPurpose.UpdatePreImage, (m, fullValues, resolve) =>
        {
            var member = plan.Members[m];
            batch.BumpRowStamp();
            // A row the SET list moves to another member is checked as that
            // member's insert checks it.
            var newValues = ComputeUpdatedRow(context, member.Table, fullValues, assignments[m], resolve, setMasks: null, enforceConstraints: false);
            var viewRow = member.ViewRow(newValues, plan.Columns, batch);
            if (!movesRows || plan.Route(viewRow) == m)
            {
                EnforceNotNull(member.Table, newValues, "UPDATE");
                EnforceCheckConstraints(member.Table, newValues, batch, "UPDATE");
            }
            return newValues;
        });

        // Every move's delete first, then the members' own updates, then the
        // moved rows' inserts, each pass over the members last to first.
        var inPlace = new List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)>[plan.Members.Length];
        var movedOut = new List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)>[plan.Members.Length];
        var movedIn = new List<SqlValue[]>[plan.Members.Length];
        var unrouted = false;
        var total = 0;
        for (var m = 0; m < plan.Members.Length; m++)
        {
            inPlace[m] = [];
            foreach (var (page, slot, newValues, oldValues) in walk[m])
            {
                total++;
                var target = m;
                if (movesRows)
                {
                    var viewRow = plan.Members[m].ViewRow(newValues, plan.Columns, batch);
                    target = plan.Route(viewRow);
                    if (target < 0)
                    {
                        unrouted = true;
                        continue;
                    }
                    if (target != m)
                    {
                        (movedOut[m] ??= []).Add((page, slot, oldValues));
                        (movedIn[target] ??= []).Add(viewRow);
                        continue;
                    }
                }
                inPlace[m].Add((page, slot, newValues, oldValues));
            }
        }
        if (unrouted)
            throw SimulatedSqlException.PartitionedViewValueFitsNoMember();
        for (var m = plan.Members.Length - 1; m >= 0; m--)
        {
            if (movedOut[m] is { } leaving)
                _ = CommitDelete(context, plan.Members[m].Table, leaving, output: null, rowsLocked: true);
        }
        for (var m = plan.Members.Length - 1; m >= 0; m--)
        {
            if (inPlace[m].Count > 0)
                _ = CommitUpdate(context, plan.Members[m].Table, inPlace[m], output: null, [.. SetColumnOrdinals(assignments[m])], rowsLocked: true);
        }
        for (var m = plan.Members.Length - 1; m >= 0; m--)
        {
            if (movedIn[m] is { } arriving)
            {
                var member = plan.Members[m];
                _ = InsertRows(context, new InsertPlan(member.Table, destinationView: null, joinViewPlan: null, member.InsertColumns(), output: null, top: null, valueTuples: null, verb: "UPDATE"), arriving, valueTupleStamps: null);
            }
        }
        return new SimulatedNonQuery(total);
    }

    /// <summary>
    /// <c>DELETE</c> through a partitioned view, or a view, CTE or derived
    /// table over one, as <see cref="ExecutePartitionedViewUpdate"/> reads its
    /// target: each member's rows the view shows and the statement picks are
    /// deleted from it. Entered on the token after the target (or, with
    /// <paramref name="from"/>, its <c>OUTPUT</c> slot).
    /// </summary>
    private static SimulatedNonQuery ExecutePartitionedViewDelete(
        ParserContext context,
        MultiPartName name,
        View view,
        Selection.DmlTopLimit? top,
        Selection.PreParsedFrom? from,
        int targetIndex)
    {
        var batch = context.Batch;
        var partitioned = view.PartitionedBase!;
        if (top is not null)
            throw SimulatedSqlException.TopOnPartitionedView();
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw SimulatedSqlException.OutputOnPartitionedView(name.ToString());

        var viewColumns = ViewColumnsFor(batch, view, name);
        var reads = new List<SchemaObject>();
        var (sources, joins, where, positioned) = ReadPartitionedTarget(context, name, view, viewColumns, from, ref targetIndex, reads);
        var found = PartitionedMembers(batch, partitioned, name.ToString(), PartitionedWrite.Delete);
        RefuseMemberReads(batch, partitioned, found, reads);
        RefuseJoinedMemberReads(batch, partitioned, found, sources, targetIndex);
        var plan = AnalyzePartitionedView(batch, partitioned, found);
        RefusePartitionedWrite(batch, partitioned, plan, PartitionedWrite.Delete, []);
        CheckPartitionedWritePermissions(batch, name, view, "DELETE", where is not null);
        foreach (var member in plan.Members)
        {
            FunctionBodyShape.NoteTableWrite(batch, "DELETE", member.Table);
            LockWriteTable(batch, member.Table, "DELETE", checkFilegroup: true);
            batch.RejectReferentialDeleteIntoVectorIndex(member.Table);
        }
        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);
        RefusePositionedPartitionedWrite(batch, partitioned, view, positioned);

        var walk = WalkPartitionedTarget(context, view, plan, sources, joins, targetIndex, where, RowLockPurpose.Delete, (_, fullValues, _) => fullValues);
        var total = 0;
        for (var m = plan.Members.Length - 1; m >= 0; m--)
        {
            if (walk[m].Count == 0)
                continue;
            var deleted = walk[m].ConvertAll(row => (row.PageIndex, row.SlotIndex, (SqlValue[]?)row.FullOld));
            _ = CommitDelete(context, plan.Members[m].Table, deleted, output: null, rowsLocked: true);
            total += deleted.Count;
        }
        return new SimulatedNonQuery(total);
    }

    /// <summary>
    /// The target of an <c>UPDATE</c> / <c>DELETE</c> through a partitioned
    /// view as the statement reads it: the joined form's own sources, or a
    /// lone source standing for the target, and the <c>WHERE</c> bound against
    /// them while <paramref name="reads"/> records what the clause reads.
    /// </summary>
    private static (FromSource[] Sources, JoinSpec[] Joins, BooleanExpression? Where, CursorReference? Positioned) ReadPartitionedTarget(
        ParserContext context,
        MultiPartName name,
        View view,
        HeapColumn[] viewColumns,
        Selection.PreParsedFrom? from,
        ref int targetIndex,
        List<SchemaObject> reads)
    {
        FromSource[] sources;
        JoinSpec[] joins;
        if (from is not null)
        {
            sources = [.. from.Sources];
            joins = [.. from.Joins];
        }
        else
        {
            var columnNames = Array.ConvertAll(viewColumns, column => column.Name);
            sources = [new FromSource(
                qualifier: name.Leaf,
                columnNames: columnNames,
                columns: viewColumns,
                storedSchema: viewColumns,
                storageOrdinals: null,
                lobStore: null,
                rows: [],
                backingView: view.UnstoredBody is null ? view : null,
                unaliasedName: FromSource.Resolved(name, context.CurrentDatabase))];
            joins = [];
            targetIndex = 0;
        }

        using var recording = ParserScope.Enter(ref context.PartitionedWriteReads, reads);
        if (from is not null)
            return (sources, joins, ParseJoinedViewTargetWhere(context, from.After, sources, joins, Selection.ColumnTypeResolverFor(sources)), null);
        if (IsWhereCurrentOf(context))
        {
            // WHERE CURRENT OF [GLOBAL] cursor, which the statement binds and
            // only its run refuses.
            context.MoveNextRequired();
            context.MoveNextRequired();
            if (context.Token is not ReservedKeyword { Keyword: Keyword.Of })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            return (sources, joins, null, ReadCursorReference(context));
        }
        BooleanExpression? where = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.Batch.BindErrors?.EnterClause(context.Token, BindClause.Where);
            context.MoveNextRequired();
            where = Selection.ParseAndBindPredicate(context, Selection.ColumnTypeResolverFor(sources), sources, joins);
        }
        return (sources, joins, where, null);
    }

    /// <summary>
    /// A positioned <c>UPDATE</c> / <c>DELETE</c> through a partitioned view,
    /// refused as the statement runs so a module holding one still binds. It
    /// never passes: a cursor reading a partitioned view is a read-only
    /// snapshot, so naming a level over the view is Msg 16929 and another
    /// table Msg 16933 as for any cursor — but naming the partitioned view
    /// itself while the cursor reads it ends the session (probed 2026-10-01
    /// against SQL Server 2025).
    /// </summary>
    private static void RefusePositionedPartitionedWrite(BatchContext batch, View partitioned, View view, CursorReference? positioned)
    {
        if (positioned is not { } reference)
            return;
        var cursor = ResolveCursor(batch, reference);
        if (ReferenceEquals(view, partitioned) && Array.IndexOf(cursor.PartitionedViewsRead, partitioned) >= 0)
        {
            batch.Connection.SessionEnding = true;
            throw SimulatedSqlException.PositionedWriteThroughPartitionedViewEndsSession();
        }
        throw cursor.ReadOnly ? SimulatedSqlException.CursorIsReadOnly() : SimulatedSqlException.CursorTableNotIncluded();
    }

    /// <summary>A joined write through a partitioned view reading a member table as another of its sources (Msg 4439).</summary>
    private static void RefuseJoinedMemberReads(BatchContext batch, View partitioned, List<(HeapTable Table, Selection Branch)> members, FromSource[] sources, int targetIndex)
    {
        var reads = new List<SchemaObject>();
        for (var s = 0; s < sources.Length; s++)
        {
            if (s == targetIndex)
                continue;
            if (sources[s].BackingTable is { } table)
                reads.Add(table);
            else if (sources[s].BackingView is { } view)
                reads.Add(view);
        }
        RefuseMemberReads(batch, partitioned, members, reads);
    }

    /// <summary>
    /// The permissions a write through a partitioned view checks: SELECT on
    /// the view when the statement reads it, and the write itself — on the
    /// view, or, through a CTE or derived table, which is no securable, on the
    /// partitioned view it reaches.
    /// </summary>
    private static void CheckPartitionedWritePermissions(BatchContext batch, MultiPartName name, View view, string verb, bool reads)
    {
        if (batch.IsSkipping)
            return;
        var securable = view.UnstoredBody is null ? PermissionEnforcement.SecurableFor(batch, name, view) : view.PartitionedBase!;
        if (!PermissionEnforcement.Applies(batch, batch.DatabaseFor(securable)))
            return;
        if (reads)
            PermissionEnforcement.CheckSchemaObject(batch, "SELECT", securable);
        PermissionEnforcement.CheckSchemaObject(batch, verb, securable);
    }

    /// <summary>
    /// Walks an <c>UPDATE</c> / <c>DELETE</c> through a partitioned view: the
    /// target source stands as every member's rows the view shows, each
    /// carrying its member and address; each row the join and <c>WHERE</c>
    /// pass is written once, off the first partner that passes it, judged as
    /// another session's write leaves it and held under its X as a table
    /// target's rows are. <paramref name="judge"/> turns a member row's full
    /// image into the image written. Answers each member's rows.
    /// </summary>
    private static List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[] FullOld)>[] WalkPartitionedTarget(
        ParserContext context,
        View view,
        PartitionedPlan plan,
        FromSource[] sources,
        JoinSpec[] joins,
        int targetIndex,
        BooleanExpression? where,
        RowLockPurpose purpose,
        Func<int, SqlValue[], Func<MultiPartName, SqlValue>, SqlValue[]> judge)
    {
        var batch = context.Batch;
        var members = plan.Members;
        var source = sources[targetIndex];
        var addresses = new Dictionary<byte[], (int Member, int Page, int Slot)>(ReferenceEqualityComparer.Instance);
        var images = new Dictionary<(int Member, int Page, int Slot), byte[]>();
        var rows = new List<byte[]>();
        var generations = new long[members.Length];

        // A level that limits its rows, projects a window or derives a column
        // shows each member's rows as its body yields them for that member
        // alone, which is how real evaluates it: a TOP picks each member's own
        // rows and a window numbers each member's rows apart (probed
        // 2026-10-01 against SQL Server 2025).
        var partitioned = view.PartitionedBase!;
        var levelRows = !ReferenceEquals(view, partitioned) && (view.IsRowLimited || view.IsWindowed || Array.IndexOf(view.BaseColumnOrdinals, -1) >= 0)
            ? new Dictionary<(int Page, int Slot), SqlValue[]>?[members.Length]
            : null;
        for (var m = 0; m < members.Length; m++)
        {
            var table = members[m].Table;
            if (!table.SupersededKeyImages.IsEmptyLockFree())
                _ = AwaitSupersededTargetRows(batch, table, (_, prior) => ShownRow(m, prior) is not null);
            generations[m] = Volatile.Read(ref table.Heap.MutationGeneration);
            var waits = Volatile.Read(ref table.ActiveDataWriters) != 0 || Volatile.Read(ref table.ActiveUpdateLocks) != 0;
            foreach (var (page, slot, scanned) in table.Heap.EnumerateRowsWithAddress())
            {
                var bytes = scanned;
                if (waits && !batch.AwaitTargetRowWriters(table, page, slot, ref bytes))
                    continue;
                if (ShownRowAt(m, page, slot, bytes, reread: !ReferenceEquals(bytes, scanned)) is not { } shown)
                    continue;
                addresses[shown] = (m, page, slot);
                images[(m, page, slot)] = bytes;
                rows.Add(shown);
            }
        }
        sources = (FromSource[])sources.Clone();
        sources[targetIndex] = source.WithMaterializedRows(rows);
        sources = Selection.PrepareMutationJoinSources(sources, joins, where, targetIndex, batch);

        var walked = new List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[] FullOld)>[members.Length];
        var judged = new List<(int PageIndex, int SlotIndex, byte[] Bytes)>[members.Length];
        for (var m = 0; m < members.Length; m++)
        {
            walked[m] = [];
            judged[m] = [];
        }
        var seen = new HashSet<(int Member, int Page, int Slot)>();

        // Hoisted per-row scaffolding: one mutable tuple slot, one cached
        // delegate and one runtime, so the per-row loop allocates none.
        byte[]?[] currentTuple = [];
        SqlValue resolveAcrossTuple(MultiPartName name) => ResolveAcrossMutationTuple(sources, currentTuple, name, batch);
        Func<MultiPartName, SqlValue> resolveTuple = resolveAcrossTuple;
        var runtime = new RuntimeContext(resolveTuple, batch);

        foreach (var tuple in Selection.EnumerateJoinedRows(sources, joins, batch, outerResolver: null))
        {
            context.Batch.PollCancellation();
            currentTuple = tuple;
            if (where is not null && where.Run(runtime) != true)
                continue;
            if (tuple[targetIndex] is not { } shown || !addresses.TryGetValue(shown, out var at) || !seen.Add(at))
                continue;
            var (m, page, slot) = at;
            var image = images[at];
            var rowBytes = image;
            if (!batch.AwaitTargetRowWriters(members[m].Table, page, slot, ref rowBytes))
                continue;
            if (!ReferenceEquals(rowBytes, image) && !rowBytes.AsSpan().SequenceEqual(image) && !Requalifies(m, page, slot, rowBytes))
                continue;
            walked[m].Add(Judge(m, page, slot, rowBytes));
            judged[m].Add((page, slot, rowBytes));
        }

        for (var m = 0; m < members.Length; m++)
        {
            var member = m;
            var memberRows = walked[member];
            HoldQualifyingRows(batch, members[member].Table, memberRows, judged[member], generations[member], purpose, (i, rowBytes) =>
            {
                if (!Requalifies(member, memberRows[i].PageIndex, memberRows[i].SlotIndex, rowBytes))
                    return false;
                memberRows[i] = Judge(member, memberRows[i].PageIndex, memberRows[i].SlotIndex, rowBytes);
                return true;
            });
        }
        return walked;

        // The target-slot row the member row at (page, slot) shows as image,
        // or null when the level hides it; reread says the image changed since
        // the level's rows were read, which then reads them again.
        byte[]? ShownRowAt(int member, int page, int slot, byte[] image, bool reread)
        {
            if (levelRows is null)
                return ShownRow(member, image);
            if (reread || levelRows[member] is null)
            {
                var memberRows = new Dictionary<(int Page, int Slot), SqlValue[]>();
                using (ParserScope.Enter(ref batch.PartitionedMemberRun, new PartitionedMemberRun(partitioned, member)))
                {
                    foreach (var (row, address) in ViewRowsWithAddresses(batch, view))
                    {
                        if (address is { } at)
                            memberRows[at] = row;
                    }
                }
                levelRows[member] = memberRows;
            }
            return levelRows[member]!.TryGetValue((page, slot), out var levelRow) ? EncodeFor(source, levelRow) : null;
        }

        // The target-slot row a member row shows, or null when the view's
        // filter hides it, its derived columns NULL.
        byte[]? ShownRow(int member, byte[] image)
        {
            var full = DecodeFullRow(members[member].Table, image);
            var viewRow = members[member].ViewRow(full, plan.Columns, batch);
            if (view.VisibilityCheck is { } isVisible && !isVisible(viewRow, batch))
                return null;
            var values = new SqlValue[source.Columns.Length];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = view.BaseColumnOrdinals[i] is >= 0 and var ordinal
                    ? viewRow[ordinal]
                    : SqlValue.Null(source.Columns[i].Type);
            }
            return EncodeFor(source, values);
        }

        // Whether the member row as rowBytes still shows and passes the join
        // and WHERE with some partner, which is left current.
        bool Requalifies(int member, int page, int slot, byte[] rowBytes)
        {
            if (ShownRowAt(member, page, slot, rowBytes, reread: true) is not { } shown)
                return false;
            foreach (var tuple in Selection.EnumerateJoinedRows(WithTargetNarrowedTo(sources, targetIndex, shown), joins, batch, outerResolver: null))
            {
                currentTuple = tuple;
                if (tuple[targetIndex] is not null && (where is null || where.Run(runtime) == true))
                    return true;
            }
            return false;
        }

        (int, int, SqlValue[], SqlValue[]) Judge(int member, int page, int slot, byte[] rowBytes)
        {
            var full = DecodeFullRow(members[member].Table, rowBytes);
            return (page, slot, judge(member, full, resolveTuple), full);
        }
    }
}

/// <summary>
/// One member of a partitioned view whose rows a level over it is evaluated
/// against (<see cref="Parser.BatchContext.PartitionedMemberRun"/>).
/// </summary>
internal sealed class PartitionedMemberRun(View partitioned, int member)
{
    public readonly View Partitioned = partitioned;

    /// <summary>The member's branch among the partitioned view's <c>UNION ALL</c> branches.</summary>
    public readonly int Member = member;
}
