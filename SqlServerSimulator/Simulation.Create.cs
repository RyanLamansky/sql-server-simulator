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
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Table })
            return false;

        if (context.GetNextRequired() is not Name tableName)
            return false;

        if (context.GetNextRequired() is not Operator { Character: '(' })
            return false;

        // Two-pass column resolution: regular columns build a HeapColumn during
        // pass 1; computed columns leave a placeholder entry plus an entry in
        // pendingComputed to be resolved after the column list is closed (so
        // forward column references inside computed expressions can bind).
        var heapColumns = new List<HeapColumn?>();
        var pendingComputed = new List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable)>();
        var pendingKeys = new List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals)>();
        var identityCount = 0;
        do
        {
            context.MoveNextRequired();

            // Table-level constraint: `CONSTRAINT name PRIMARY KEY|UNIQUE (cols)`
            // or unnamed `PRIMARY KEY|UNIQUE (cols)`. Forks before the column
            // path because PRIMARY/UNIQUE/CONSTRAINT are reserved keywords and
            // would otherwise collide with the leading-name expectation.
            if (context.Token is ReservedKeyword { Keyword: Keyword.Constraint or Keyword.Primary or Keyword.Unique })
            {
                ParseTableLevelKeyConstraint(context, heapColumns, pendingKeys, pendingComputed);
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
                    : context.MatchContextual(ContextualKeyword.Max)
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
            // DEFAULT, PRIMARY KEY/UNIQUE) in any order. Each branch leaves
            // Token at the first un-consumed token; the loop exits when that
            // token isn't a recognized constraint keyword (typically the comma
            // separating columns or the column-list's closing paren).
            IdentityState? identity = null;
            bool? nullable = null;
            Expression? defaultExpression = null;
            var inlineKeyKind = (KeyConstraintKind?)null;
            string? inlineKeyName = null;
            while (true)
            {
                switch (context.Token)
                {
                    case ReservedKeyword { Keyword: Keyword.Identity } when identity is null:
                        identity = ParseIdentitySpec(context, columnName.Value);
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
                    case ReservedKeyword { Keyword: Keyword.Constraint } when inlineKeyKind is null:
                        if (context.GetNextRequired() is not Name namedConstraint)
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        inlineKeyName = namedConstraint.Value;
                        context.MoveNextRequired();
                        if (context.Token is not ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        inlineKeyKind = ParseInlineKeyKindAndModifiers(context);
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Primary or Keyword.Unique } when inlineKeyKind is null:
                        inlineKeyKind = ParseInlineKeyKindAndModifiers(context);
                        continue;
                }
                break;
            }

            if (inlineKeyKind == KeyConstraintKind.PrimaryKey)
            {
                if (nullable == true)
                    throw SimulatedSqlException.PrimaryKeyOnNullableColumn(tableName.Value);
                nullable = false;
            }

            var actualNullable = nullable ?? (identity is null);
            var (resolvedType, maxLength) = SqlType.GetByName(typeName, declaredMaxLength, declaredScale, heapColumns.Count + 1, columnName.Value);

            if (inlineKeyKind is KeyConstraintKind kind)
                pendingKeys.Add((kind, inlineKeyName, [heapColumns.Count]));

            if (identity is not null)
            {
                if (++identityCount > 1)
                    throw SimulatedSqlException.MultipleIdentityColumns(tableName.Value);
                if (actualNullable)
                    throw SimulatedSqlException.IdentityOnNullableColumn(columnName.Value, tableName.Value);
                if (resolvedType != SqlType.Int32 && resolvedType != SqlType.BigInt && resolvedType != SqlType.SmallInt && resolvedType != SqlType.TinyInt)
                    throw SimulatedSqlException.IdentityInvalidType(columnName.Value);
            }

            heapColumns.Add(new HeapColumn(columnName.Value, resolvedType, maxLength, actualNullable, identity, defaultExpression));
        } while (context.Token is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            return false;

        // Pass 2: resolve computed columns now that every column's name has
        // been seen. The resolver throws Msg 1759 for any reference to another
        // computed column (including persisted) and Msg 207 for an unknown
        // name; valid references resolve to the source column's SqlType so
        // <see cref="Expression.GetSqlType"/> can infer the computed column's
        // own type.
        SqlType ResolveComputedReference(List<string> reference)
        {
            var leaf = reference[^1];
            for (var i = 0; i < heapColumns.Count; i++)
            {
                if (heapColumns[i] is { } existing && Collation.Default.Equals(existing.Name, leaf))
                {
                    return existing.Computed is not null
                        ? throw SimulatedSqlException.ComputedColumnReferencedInComputed(existing.Name, tableName.Value)
                        : existing.Type;
                }
                if (heapColumns[i] is null)
                {
                    foreach (var pending in pendingComputed)
                    {
                        if (pending.Index == i && Collation.Default.Equals(pending.Name, leaf))
                            throw SimulatedSqlException.ComputedColumnReferencedInComputed(pending.Name, tableName.Value);
                    }
                }
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }

        foreach (var pending in pendingComputed)
        {
            var resolvedType = pending.Expression.GetSqlType(ResolveComputedReference);
            heapColumns[pending.Index] = new HeapColumn(
                pending.Name,
                resolvedType,
                maxLength: null,
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
            throw SimulatedSqlException.RowSizeExceedsMaximum(tableName.Value, fixedWidthSum, Heap.MaxRowSize);

        var keyConstraints = ResolveKeyConstraints(tableName.Value, heapColumns!, pendingKeys);
        var heapTable = new HeapTable(tableName.Value, [.. heapColumns!], keyConstraints);
        return this.HeapTables.TryAdd(heapTable.Name, heapTable)
            ? true
            : throw SimulatedSqlException.ThereIsAlreadyAnObject(heapTable.Name);
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
            if (!persisted && context.MatchContextual(ContextualKeyword.Persisted))
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
            seed = EvaluateLiteralBigInt(Expression.Parse(context));
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            increment = EvaluateLiteralBigInt(Expression.Parse(context));
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
        return increment == 0
            ? throw SimulatedSqlException.IdentityInvalidIncrement(columnName)
            : new IdentityState(seed, increment);
    }

    private static long EvaluateLiteralBigInt(Expression expression) =>
        expression.Run(name => throw SimulatedSqlException.InvalidColumnName(name)).CoerceTo(SqlType.BigInt).AsInt64;

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
    /// Parses a table-level <c>[CONSTRAINT name] (PRIMARY KEY|UNIQUE) [CLUSTERED|NONCLUSTERED] (col [, col ...])</c>
    /// element, entered with <see cref="ParserContext.Token"/> on the leading
    /// <c>CONSTRAINT</c>, <c>PRIMARY</c>, or <c>UNIQUE</c>. Resolves each named
    /// column to its index in <paramref name="heapColumns"/> and queues the
    /// constraint into <paramref name="pendingKeys"/>; final validation
    /// (multiple-PK, key-on-LOB, PK NULL flip) runs after the column list
    /// closes. Leaves <see cref="ParserContext.Token"/> on the trailing comma
    /// or closing paren of the column-element list.
    /// </summary>
    private static void ParseTableLevelKeyConstraint(
        ParserContext context,
        List<HeapColumn?> heapColumns,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals)> pendingKeys,
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
    private static KeyConstraint[] ResolveKeyConstraints(
        string tableName,
        List<HeapColumn> heapColumns,
        List<(KeyConstraintKind Kind, string? Name, int[] FullOrdinals)> pendingKeys)
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

            resolved[c] = new KeyConstraint(pending.Kind, pending.Name ?? AutoConstraintName(tableName, pending.Kind, pending.FullOrdinals, heapColumns), storageOrdinals);
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
    private static string AutoConstraintName(string tableName, KeyConstraintKind kind, int[] fullOrdinals, List<HeapColumn> heapColumns)
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
