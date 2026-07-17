using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>ALTER TABLE [schema.]name [WITH CHECK | WITH NOCHECK] ADD
    /// [CONSTRAINT name] (PRIMARY KEY | UNIQUE | FOREIGN KEY | CHECK | DEFAULT)
    /// …</c>. Cursor on the <c>ADD</c> keyword on entry. Single constraint
    /// per ADD — comma-separated multi-constraint ADD raises
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each family branch parses inline (no pending list intermediate),
    /// resolves the live <see cref="HeapTable"/> via
    /// <see cref="BatchContext.TryResolveTable"/> with the ALTER-TABLE error
    /// path (Msg 4902), validates names + existing data, then appends to the
    /// table's mutable constraint lists.
    /// </para>
    /// <para>
    /// <c>WITH NOCHECK</c> applies only to FK and CHECK adds — it bypasses
    /// existing-row validation and sets the constraint's <c>IsNotTrusted</c>
    /// flag. <c>WITH CHECK</c> is the default. PK / UQ always validate
    /// existing data; PK additionally rejects nullable columns (Msg 8111)
    /// and rejects a second PK on the table (Msg 1779).
    /// </para>
    /// </remarks>
    private static bool TryParseAlterTableAddConstraint(ParserContext context, MultiPartName tableName, bool withNoCheck)
    {
        // Cursor on ADD; advance to the next element.
        context.MoveNextRequired();

        // Optional explicit CONSTRAINT name.
        string? explicitName = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Constraint })
        {
            if (context.GetNextRequired() is not Name nameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            explicitName = nameToken.Value;
            context.MoveNextRequired();
        }

        return context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Check } => ParseAddCheckConstraint(context, tableName, explicitName, withNoCheck),
            ReservedKeyword { Keyword: Keyword.Foreign } => ParseAddForeignKeyConstraint(context, tableName, explicitName, withNoCheck),
            ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique } => ParseAddKeyConstraint(context, tableName, explicitName),
            ReservedKeyword { Keyword: Keyword.Default } => ParseAddDefaultConstraint(context, tableName, explicitName),
            // ADD COLUMN routes to the column-list parser. The grammar is
            // ambiguous at the leading-token level: any non-constraint
            // identifier starts a column-name declaration. The leading
            // explicit-CONSTRAINT-name fork is incompatible with ADD COLUMN
            // (real SQL Server's grammar doesn't allow naming a column-add
            // with CONSTRAINT), so explicitName must be null to enter this
            // branch.
            Name when explicitName is null => ParseAddColumns(context, tableName),
            UnquotedString when explicitName is null => ParseAddColumns(context, tableName),
            ReservedKeyword { Keyword: Keyword.Column } when explicitName is null => ParseAddColumns(context, tableName),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
    }

    /// <summary>
    /// Parses a CHECK clause inline and applies it. <c>WITH NOCHECK</c>
    /// bypasses existing-row validation.
    /// </summary>
    private static bool ParseAddCheckConstraint(ParserContext context, MultiPartName tableName, string? explicitName, bool withNoCheck)
    {
        // Inline equivalent of ParseInlineCheckPredicate without the trailing
        // MoveNextRequired — ADD CHECK at end-of-batch has no follow-on
        // token, so the required advance would throw.
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var predicateStart = context.Token!.StartIndex;
        var predicate = BooleanExpression.Parse(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var definition = $"({context.SourceTextFrom(predicateStart)})";
        context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return true;
        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
        AssertConstraintNameUnique(context, explicitName);
        var name = explicitName ?? AutoCheckName(table.Name, null, table.CheckConstraints.Count);
        var constraint = new CheckConstraint(name, predicate, null, context.CurrentDatabase.AllocateObjectId())
        {
            IsSystemNamed = explicitName is null,
            IsNotTrusted = withNoCheck,
            Definition = definition,
        };
        if (!withNoCheck)
            ValidateExistingRowsForCheckConstraint(context, table, constraint);
        table.CheckConstraints.Add(constraint);
        return true;
    }

    /// <summary>
    /// Parses a <c>FOREIGN KEY (cols) REFERENCES parent(cols) [ON DELETE …]
    /// [ON UPDATE …]</c> clause and routes through the existing
    /// <see cref="ResolveForeignKeys"/> pipeline (a single-element pending
    /// list). The resolver handles parent-table / referenced-key validation
    /// and the cascade-cycle gate. Existing-data validation runs afterward
    /// unless <c>WITH NOCHECK</c>.
    /// </summary>
    private static bool ParseAddForeignKeyConstraint(ParserContext context, MultiPartName tableName, string? explicitName, bool withNoCheck)
    {
        // FOREIGN KEY (cols) REFERENCES parent (cols)
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Key })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var childColumnNames = new List<string>();
        do
        {
            if (context.GetNextRequired() is not Name childCol)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            childColumnNames.Add(childCol.Value);
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
            context.MoveNextOptional();
        }
        var (delAction, updAction) = ParseOnDeleteOnUpdateActions(context);

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
        AssertConstraintNameUnique(context, explicitName);

        // Resolve child column names → full ordinals against the live table.
        var childOrdinals = new int[childColumnNames.Count];
        for (var c = 0; c < childColumnNames.Count; c++)
        {
            var found = -1;
            for (var i = 0; i < table.Columns.Length; i++)
            {
                if (context.Batch.CurrentDatabase.Collation.Equals(table.Columns[i].Name, childColumnNames[c]))
                {
                    found = i;
                    break;
                }
            }
            if (found < 0)
            {
                var fkName = explicitName ?? AutoForeignKeyName(table.Name, [.. childColumnNames], 0);
                throw SimulatedSqlException.ForeignKeyInvalidColumn(childColumnNames[c], table.Name, fkName);
            }
            childOrdinals[c] = found;
        }

        var pending = new PendingForeignKey(
            explicitName,
            [.. childColumnNames],
            childOrdinals,
            referencedTable,
            [.. referencedColumns],
            delAction,
            updAction);

        var beforeCount = table.OutgoingForeignKeys.Count;
        ResolveForeignKeys(table, [pending], context);
        var newFk = table.OutgoingForeignKeys[beforeCount];
        newFk.IsNotTrusted = withNoCheck;
        if (!withNoCheck)
            ValidateExistingRowsForForeignKey(context, table, newFk);
        return true;
    }

    /// <summary>
    /// Parses a <c>PRIMARY KEY (cols)</c> or <c>UNIQUE (cols)</c> clause and
    /// applies it. PK rejects nullable columns (Msg 8111) and a second PK on
    /// the same table (Msg 1779). Existing data is scanned for duplicates
    /// (Msg 1505 on collision).
    /// </summary>
    private static bool ParseAddKeyConstraint(ParserContext context, MultiPartName tableName, string? explicitName)
    {
        var (kind, clustered) = ParseInlineKeyKindAndModifiers(context);
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var columnNames = new List<string>();
        do
        {
            if (context.GetNextRequired() is not Name col)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            columnNames.Add(col.Value);
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Asc or Keyword.Desc })
                context.MoveNextRequired();
        } while (context.Token is Operator { Character: ',' });
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        // SSMS emits `ADD CONSTRAINT name UNIQUE NONCLUSTERED (cols) WITH
        // (PAD_INDEX = OFF, …) ON [PRIMARY]`. Both trailers are no-ops in
        // the simulator (no B-tree storage, no filegroup model).
        SkipOptionalIndexWithClause(context);
        SkipOptionalFilegroupClause(context);

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
        AssertConstraintNameUnique(context, explicitName);

        // Resolve column names → full ordinals; UQ/PK column missing → Msg 1911.
        var fullOrdinals = new int[columnNames.Count];
        for (var c = 0; c < columnNames.Count; c++)
        {
            var found = -1;
            for (var i = 0; i < table.Columns.Length; i++)
            {
                if (context.Batch.CurrentDatabase.Collation.Equals(table.Columns[i].Name, columnNames[c]))
                {
                    found = i;
                    break;
                }
            }
            if (found < 0)
                throw SimulatedSqlException.IndexColumnMissing(columnNames[c]);
            fullOrdinals[c] = found;
        }

        if (kind == KeyConstraintKind.PrimaryKey)
        {
            foreach (var existing in table.KeyConstraints)
            {
                if (existing.Kind == KeyConstraintKind.PrimaryKey)
                    throw SimulatedSqlException.PrimaryKeyAlreadyExists(table.Name);
            }
            foreach (var ord in fullOrdinals)
            {
                if (table.Columns[ord].Nullable)
                    throw SimulatedSqlException.PrimaryKeyOnNullableColumn(table.Name);
            }
        }

        var storageOrdinals = new int[fullOrdinals.Length];
        for (var i = 0; i < fullOrdinals.Length; i++)
        {
            var col = table.Columns[fullOrdinals[i]];
            if (col.IsLob)
                throw SimulatedSqlException.KeyColumnInvalidType(col.Name, table.Name);
            // Non-persisted computed columns have no storage ordinal — UNIQUE
            // on one would need expression evaluation in the enforcement loop
            // (not modeled). PRIMARY KEY on a non-persisted computed already
            // raised Msg 8111 above (computed columns are nullable by default
            // and the existing nullable check catches it — probe-confirmed
            // at ALTER ADD, the wording differs from CREATE TABLE's Msg 1711).
            if (col.Computed is not null && !col.IsPersisted)
                throw new NotSupportedException("UNIQUE on a non-persisted computed column isn't modeled.");
            storageOrdinals[i] = table.StorageOrdinals[fullOrdinals[i]];
        }

        var name = explicitName ?? AutoConstraintName(table.Name, kind, fullOrdinals, table.Columns);
        var isClustered = clustered ?? (kind == KeyConstraintKind.PrimaryKey);
        var constraint = new KeyConstraint(kind, name, storageOrdinals, context.CurrentDatabase.AllocateObjectId(), isClustered);
        ValidateExistingRowsForKeyConstraint(table, constraint);
        table.KeyConstraints.Add(constraint);
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER TABLE … ADD CONSTRAINT name DEFAULT (expr) FOR col</c>
    /// and applies it. The column must exist (Msg 1752 otherwise) and must
    /// not already have a default (Msg 1781). Auto-names when CONSTRAINT name
    /// is omitted (matches inline-DEFAULT-at-CREATE naming).
    /// </summary>
    private static bool ParseAddDefaultConstraint(ParserContext context, MultiPartName tableName, string? explicitName)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var expressionStart = context.Token!.StartIndex;
        // Mark the expression body as a DEFAULT clause so NEWSEQUENTIALID()'s
        // grammar gate accepts it (parity with the inline-DEFAULT path in
        // ParseOneColumnIntoLists). The flag is the only thing distinguishing
        // a legal DEFAULT context from an illegal scalar use of the function.
        context.InDefaultClause = true;
        Expression expression;
        try { expression = Expression.Parse(context); }
        finally { context.InDefaultClause = false; }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var definition = $"({context.SourceTextFrom(expressionStart)})";
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.For })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name columnNameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var columnName = columnNameToken.Value;
        context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
        AssertConstraintNameUnique(context, explicitName);

        HeapColumn? targetColumn = null;
        foreach (var c in table.Columns)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(c.Name, columnName))
            {
                targetColumn = c;
                break;
            }
        }
        if (targetColumn is null)
            throw SimulatedSqlException.DefaultColumnInvalid(columnName, table.Name);
        if (targetColumn.Default is not null)
            throw SimulatedSqlException.ColumnAlreadyHasDefault();

        var name = explicitName ?? AutoDefaultName(table.Name, targetColumn.Name);
        targetColumn.Default = expression;
        targetColumn.DefaultConstraint = new DefaultConstraint(
            name,
            expression,
            context.CurrentDatabase.AllocateObjectId(),
            isSystemNamed: explicitName is null,
            definition: definition);
        return true;
    }

    /// <summary>
    /// Rejects a duplicate constraint name within the database's object
    /// namespace (Msg 2714). PK / UQ / FK / CHECK / DEFAULT all share the
    /// same shared-namespace check — probe-confirmed against SQL Server 2025.
    /// </summary>
    private static void AssertConstraintNameUnique(ParserContext context, string? candidateName)
    {
        if (candidateName is null)
            return;
        foreach (var schema in context.CurrentDatabase.Schemas.Values)
        {
            foreach (var t in schema.HeapTables.Values)
            {
                foreach (var k in t.KeyConstraints)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(k.Name, candidateName))
                        throw SimulatedSqlException.ThereIsAlreadyAnObject(candidateName);
                }
                foreach (var ck in t.CheckConstraints)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(ck.Name, candidateName))
                        throw SimulatedSqlException.ThereIsAlreadyAnObject(candidateName);
                }
                foreach (var fk in t.OutgoingForeignKeys)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(fk.Name, candidateName))
                        throw SimulatedSqlException.ThereIsAlreadyAnObject(candidateName);
                }
                foreach (var col in t.Columns)
                {
                    if (col.DefaultConstraint is { } df && context.Batch.CurrentDatabase.Collation.Equals(df.Name, candidateName))
                        throw SimulatedSqlException.ThereIsAlreadyAnObject(candidateName);
                }
            }
        }
    }

    private static string AutoDefaultName(string tableName, string columnName)
    {
        var h = Fnv1a32.Initial;
        h.MixTableSeed(tableName);
        h.Mix(columnName);
        return FormatAutoConstraintName("DF__", tableName, columnName, h.Value);
    }

    /// <summary>
    /// Linear-scan validation of existing rows against a new PK / UQ
    /// constraint. Raises Msg 1505 on the first duplicate key tuple.
    /// </summary>
    private static void ValidateExistingRowsForKeyConstraint(HeapTable table, KeyConstraint constraint)
    {
        var seen = new List<SqlValue[]>();
        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;
        foreach (var rowBytes in table.Heap.EnumerateRows())
        {
            var key = new SqlValue[constraint.StorageOrdinals.Length];
            for (var k = 0; k < constraint.StorageOrdinals.Length; k++)
                key[k] = RowDecoder.DecodeColumn(storedColumns, rowBytes, constraint.StorageOrdinals[k], lobStore);

            foreach (var prior in seen)
            {
                var match = true;
                for (var k = 0; k < key.Length; k++)
                {
                    if (!prior[k].Equals(key[k]))
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    throw SimulatedSqlException.DuplicateKeyOnCreate("dbo." + table.Name, constraint.Name, FormatIndexKeyValues(key));
            }
            seen.Add(key);
        }
    }

    /// <summary>
    /// Linear-scan validation against a new CHECK predicate. Raises Msg 547
    /// with the "ALTER TABLE statement" prefix variant when any existing row
    /// evaluates the predicate to <c>false</c>. UNKNOWN passes (matches the
    /// existing CHECK-NULL-semantics rule).
    /// </summary>
    private static void ValidateExistingRowsForCheckConstraint(ParserContext context, HeapTable table, CheckConstraint constraint)
    {
        if (table.Heap.RowCount == 0)
            return;
        foreach (var rowBytes in table.Heap.EnumerateRows())
        {
            var rowValues = new SqlValue[table.Columns.Length];
            for (var c = 0; c < table.Columns.Length; c++)
            {
                var so = table.StorageOrdinals[c];
                rowValues[c] = so < 0
                    ? SqlValue.Null(table.Columns[c].Type)
                    : RowDecoder.DecodeColumn(table.StoredColumns, rowBytes, so, table.Heap);
            }

            SqlValue ResolveByName(MultiPartName reference)
            {
                for (var k = 0; k < table.Columns.Length; k++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(table.Columns[k].Name, reference.Leaf))
                        return rowValues[k];
                }
                throw SimulatedSqlException.InvalidColumnName(reference);
            }

            var runtime = new RuntimeContext(ResolveByName, context.Batch);
            if (constraint.Predicate.Run(runtime) == false)
                throw SimulatedSqlException.AlterCheckConstraintConflict(constraint.Name, table.Name, constraint.InlineColumn);
        }
    }

    /// <summary>
    /// Linear-scan validation against a new FK. Raises Msg 547 with the
    /// "ALTER TABLE statement" prefix variant on the first orphan child row.
    /// NULL in any FK column skips the check (matches existing FK
    /// enforcement rule).
    /// </summary>
    private static void ValidateExistingRowsForForeignKey(ParserContext context, HeapTable table, ForeignKey fk)
    {
        if (table.Heap.RowCount == 0)
            return;
        foreach (var rowBytes in table.Heap.EnumerateRows())
        {
            var childKey = new SqlValue[fk.ChildColumnOrdinals.Length];
            var anyNull = false;
            for (var k = 0; k < fk.ChildColumnOrdinals.Length; k++)
            {
                var fullOrdinal = fk.ChildColumnOrdinals[k];
                var so = table.StorageOrdinals[fullOrdinal];
                var v = so < 0
                    ? SqlValue.Null(table.Columns[fullOrdinal].Type)
                    : RowDecoder.DecodeColumn(table.StoredColumns, rowBytes, so, table.Heap);
                if (v.IsNull)
                {
                    anyNull = true;
                    break;
                }
                childKey[k] = v;
            }
            if (anyNull)
                continue;
            if (!ParentRowMatches(fk, childKey))
            {
                var (parentQualified, childColumnPhrase) = FormatForeignKeyTarget(fk, context.CurrentDatabase);
                throw SimulatedSqlException.AlterForeignKeyConflict(fk.Name, parentQualified, childColumnPhrase, fk.IsSelfReferencing);
            }
        }
    }

    private static bool ParentRowMatches(ForeignKey fk, SqlValue[] childKey)
    {
        var parent = fk.ReferencedTable;
        foreach (var parentRowBytes in parent.Heap.EnumerateRows())
        {
            var match = true;
            for (var k = 0; k < fk.ReferencedColumnOrdinals.Length; k++)
            {
                var parentOrdinal = fk.ReferencedColumnOrdinals[k];
                var so = parent.StorageOrdinals[parentOrdinal];
                var v = so < 0
                    ? SqlValue.Null(parent.Columns[parentOrdinal].Type)
                    : RowDecoder.DecodeColumn(parent.StoredColumns, parentRowBytes, so, parent.Heap);
                if (!v.Equals(childKey[k]))
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return true;
        }
        return false;
    }

    private static (string Parent, string? ColumnPhrase) FormatForeignKeyTarget(ForeignKey fk, Database database)
    {
        var parentName = QualifyTableName(fk.ReferencedTable, database);
        if (fk.ReferencedColumnOrdinals.Length == 1)
        {
            var col = fk.ReferencedTable.Columns[fk.ReferencedColumnOrdinals[0]].Name;
            return (parentName, col);
        }
        return (parentName, null);
    }

    /// <summary>
    /// Parses <c>ALTER TABLE [schema.]name DROP CONSTRAINT [IF EXISTS] name
    /// [, name…]</c>. Cursor on <c>DROP</c> on entry. Atomic: all names are
    /// resolved first and any failure prevents mutation
    /// (probe-confirmed against SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// Name lookup walks all four constraint families on the target table:
    /// <see cref="HeapTable.KeyConstraints"/>, <see cref="HeapTable.CheckConstraints"/>,
    /// <see cref="HeapTable.OutgoingForeignKeys"/>, and each column's
    /// <see cref="HeapColumn.DefaultConstraint"/>. Probe-confirmed error
    /// paths: Msg 3728 (not a constraint), Msg 3725 (PK/UQ referenced by an
    /// incoming FK).
    /// </remarks>
    private static bool TryParseAlterTableDropConstraint(ParserContext context, MultiPartName tableName)
    {
        var afterDrop = context.GetNextRequired();
        // DROP COLUMN routes through the same dispatch. COLUMN is a reserved
        // keyword in the simulator's grammar.
        if (afterDrop is ReservedKeyword { Keyword: Keyword.Column })
            return ParseDropColumns(context, tableName);
        if (afterDrop is not ReservedKeyword { Keyword: Keyword.Constraint })
            throw new NotSupportedException("ALTER TABLE supports only DROP CONSTRAINT and DROP COLUMN among its DROP shapes.");

        var ifExists = false;
        context.MoveNextRequired();
        if (context.Token is ReservedKeyword { Keyword: Keyword.If })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Exists })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ifExists = true;
            context.MoveNextRequired();
        }

        var names = new List<string>();
        if (context.Token is not Name firstName)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        names.Add(firstName.Value);

        context.MoveNextOptional();
        while (context.Token is Operator { Character: ',' })
        {
            if (context.GetNextRequired() is not Name next)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            names.Add(next.Value);
            context.MoveNextOptional();
        }

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());

        // Two-pass: resolve every name first; any unresolved-without-IF-EXISTS
        // triggers Msg 3728 before mutation. Block PK / UQ referenced by an FK
        // (Msg 3725).
        var planned = new List<DropConstraintAction>();
        foreach (var name in names)
        {
            var action = FindConstraintByName(context.Batch.CurrentDatabase.Collation, table, name);
            if (action.Family == DropConstraintFamily.None)
            {
                if (ifExists)
                    continue;
                throw SimulatedSqlException.NotAConstraint(name);
            }
            if (action.Family == DropConstraintFamily.Key && IsKeyReferencedByForeignKey(table, action.Key!, out var refTable, out var refFkName))
                throw SimulatedSqlException.ConstraintReferencedByForeignKey(action.Key!.Name, refTable, refFkName);
            planned.Add(action);
        }

        foreach (var a in planned)
            ApplyDropConstraint(table, a);
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER TABLE … (CHECK | NOCHECK) CONSTRAINT (ALL | name [,…])</c>.
    /// Cursor on <c>CHECK</c> or <c>NOCHECK</c> on entry. <paramref name="disable"/>
    /// flips <see cref="ForeignKey.IsDisabled"/> / <see cref="CheckConstraint.IsDisabled"/>;
    /// <paramref name="revalidate"/> (only true under <c>WITH CHECK</c>
    /// prefix) re-runs the existing-row scan and clears <see cref="ForeignKey.IsNotTrusted"/>
    /// / <see cref="CheckConstraint.IsNotTrusted"/> on success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Behavior matrix (probe-confirmed against SQL Server 2025):
    /// </para>
    /// <list type="bullet">
    /// <item><c>NOCHECK CONSTRAINT name</c> → IsDisabled = true, IsNotTrusted = true.</item>
    /// <item><c>CHECK CONSTRAINT name</c> (bare) → IsDisabled = false; IsNotTrusted untouched (stays true if the constraint had been disabled before).</item>
    /// <item><c>WITH CHECK CHECK CONSTRAINT name</c> → IsDisabled = false; revalidate existing rows. On Msg 547 conflict, raise; on success, IsNotTrusted = false.</item>
    /// </list>
    /// <para>
    /// <c>ALL</c> targets every FK + CHECK on the table. Multi-name
    /// resolution is atomic: any missing name (Msg 4917) prevents all
    /// mutations.
    /// </para>
    /// </remarks>
    private static bool TryParseAlterTableTrustToggle(ParserContext context, MultiPartName tableName, bool disable, bool revalidate)
    {
        // Cursor on CHECK or NOCHECK; advance to CONSTRAINT.
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Constraint })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var allMode = false;
        var names = new List<string>();
        if (context.Token is ReservedKeyword { Keyword: Keyword.All })
        {
            allMode = true;
            context.MoveNextOptional();
        }
        else
        {
            if (context.Token is not Name firstName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            names.Add(firstName.Value);
            context.MoveNextOptional();
            while (context.Token is Operator { Character: ',' })
            {
                if (context.GetNextRequired() is not Name next)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                names.Add(next.Value);
                context.MoveNextOptional();
            }
        }

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());

        // Two-pass resolve: gather targets first; any miss raises Msg 4917
        // before any mutation (atomicity).
        var fkTargets = new List<ForeignKey>();
        var ckTargets = new List<CheckConstraint>();
        if (allMode)
        {
            fkTargets.AddRange(table.OutgoingForeignKeys);
            ckTargets.AddRange(table.CheckConstraints);
        }
        else
        {
            foreach (var name in names)
            {
                var matchedFk = false;
                foreach (var fk in table.OutgoingForeignKeys)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(fk.Name, name))
                    {
                        fkTargets.Add(fk);
                        matchedFk = true;
                        break;
                    }
                }
                if (matchedFk)
                    continue;
                var matchedCk = false;
                foreach (var ck in table.CheckConstraints)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(ck.Name, name))
                    {
                        ckTargets.Add(ck);
                        matchedCk = true;
                        break;
                    }
                }
                if (!matchedCk)
                    throw SimulatedSqlException.ConstraintDoesNotExist(name);
            }
        }

        // Apply. For revalidate, the scan runs against each target with
        // IsDisabled cleared so the FK / CHECK enforcement walks would
        // normally fire — but we use the AlterAdd-bundle's validation
        // helpers, which scan independently.
        foreach (var fk in fkTargets)
        {
            if (disable)
            {
                fk.IsDisabled = true;
                fk.IsNotTrusted = true;
            }
            else
            {
                if (revalidate)
                {
                    // Temporarily lift IsDisabled so the validation scan
                    // doesn't accidentally early-exit on the table's other
                    // disabled constraints (the helper only scans this FK).
                    ValidateExistingRowsForForeignKey(context, table, fk);
                    fk.IsNotTrusted = false;
                }
                fk.IsDisabled = false;
            }
        }
        foreach (var ck in ckTargets)
        {
            if (disable)
            {
                ck.IsDisabled = true;
                ck.IsNotTrusted = true;
            }
            else
            {
                if (revalidate)
                {
                    ValidateExistingRowsForCheckConstraint(context, table, ck);
                    ck.IsNotTrusted = false;
                }
                ck.IsDisabled = false;
            }
        }
        return true;
    }

    private enum DropConstraintFamily { None, Key, Check, ForeignKey, Default }

    private sealed record DropConstraintAction(DropConstraintFamily Family, KeyConstraint? Key, CheckConstraint? Check, ForeignKey? ForeignKey, HeapColumn? DefaultColumn);

    private static DropConstraintAction FindConstraintByName(Collation collation, HeapTable table, string name)
    {
        foreach (var k in table.KeyConstraints)
        {
            if (collation.Equals(k.Name, name))
                return new DropConstraintAction(DropConstraintFamily.Key, k, null, null, null);
        }
        foreach (var ck in table.CheckConstraints)
        {
            if (collation.Equals(ck.Name, name))
                return new DropConstraintAction(DropConstraintFamily.Check, null, ck, null, null);
        }
        foreach (var fk in table.OutgoingForeignKeys)
        {
            if (collation.Equals(fk.Name, name))
                return new DropConstraintAction(DropConstraintFamily.ForeignKey, null, null, fk, null);
        }
        foreach (var col in table.Columns)
        {
            if (col.DefaultConstraint is { } df && collation.Equals(df.Name, name))
                return new DropConstraintAction(DropConstraintFamily.Default, null, null, null, col);
        }
        return new DropConstraintAction(DropConstraintFamily.None, null, null, null, null);
    }

    /// <summary>
    /// True iff a PK / UQ on the given table is referenced by some incoming
    /// FK. Returns the offending child-table name + FK name for the Msg 3725
    /// payload via out params on the first match.
    /// </summary>
    private static bool IsKeyReferencedByForeignKey(HeapTable table, KeyConstraint key, out string referencingTable, out string referencingFkName)
    {
        var keyFullOrdinals = new int[key.StorageOrdinals.Length];
        for (var i = 0; i < key.StorageOrdinals.Length; i++)
        {
            for (var f = 0; f < table.Columns.Length; f++)
            {
                if (table.StorageOrdinals[f] == key.StorageOrdinals[i])
                {
                    keyFullOrdinals[i] = f;
                    break;
                }
            }
        }
        foreach (var fk in table.IncomingForeignKeys)
        {
            if (fk.ReferencedColumnOrdinals.Length != keyFullOrdinals.Length)
                continue;
            var allMatch = true;
            foreach (var keyOrd in keyFullOrdinals)
            {
                var found = false;
                foreach (var refOrd in fk.ReferencedColumnOrdinals)
                {
                    if (refOrd == keyOrd)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch)
            {
                referencingTable = fk.ChildTable.Name;
                referencingFkName = fk.Name;
                return true;
            }
        }
        referencingTable = "";
        referencingFkName = "";
        return false;
    }

    private static void ApplyDropConstraint(HeapTable table, DropConstraintAction action)
    {
        switch (action.Family)
        {
            case DropConstraintFamily.Key:
                _ = table.KeyConstraints.Remove(action.Key!);
                break;
            case DropConstraintFamily.Check:
                _ = table.CheckConstraints.Remove(action.Check!);
                break;
            case DropConstraintFamily.ForeignKey:
                _ = table.OutgoingForeignKeys.Remove(action.ForeignKey!);
                _ = action.ForeignKey!.ReferencedTable.IncomingForeignKeys.Remove(action.ForeignKey!);
                break;
            case DropConstraintFamily.Default:
                action.DefaultColumn!.Default = null;
                action.DefaultColumn.DefaultConstraint = null;
                break;
        }
    }
}
