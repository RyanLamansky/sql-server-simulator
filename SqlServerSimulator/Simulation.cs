using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Collections.Concurrent;
using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Simulates a SQL Server instance.
/// </summary>
public sealed class Simulation
{
    /// <summary>
    /// Creates a new simulated SQL Server instance with no tables or data.
    /// </summary>
    public Simulation()
    {
    }

    /// <summary>
    /// Creates a simulated database connection.
    /// </summary>
    /// <returns>A new simulated database connection instance.</returns>
    public DbConnection CreateDbConnection() => new SimulatedDbConnection(this);

    /// <summary>User tables, keyed by name.</summary>
    internal readonly ConcurrentDictionary<string, HeapTable> HeapTables = new(Collation.Default);

    /// <summary>
    /// Database compatibility level. New simulations default to the most recent
    /// supported level; user code switches via
    /// <c>ALTER DATABASE … SET COMPATIBILITY_LEVEL = N</c>.
    /// </summary>
    internal CompatibilityLevel CompatibilityLevel = CompatibilityLevel.Sql170;

    /// <summary>
    /// Active session-scoped trace flags (the simulator doesn't model separate
    /// global vs session scope yet — flags set here apply simulation-wide).
    /// Toggled via <c>DBCC TRACEON(N)</c> / <c>DBCC TRACEOFF(N)</c>.
    /// </summary>
    internal readonly HashSet<int> TraceFlags = [];

    /// <summary>
    /// Last identity value produced by an INSERT in this simulation —
    /// the source for both <c>SCOPE_IDENTITY()</c> and <c>@@IDENTITY</c>.
    /// SQL Server scopes these per session/scope; the simulator collapses
    /// both to a single simulation-wide slot for the same reason
    /// <see cref="TraceFlags"/> does.
    /// </summary>
    /// <remarks>
    /// Cleared (set to <c>null</c>) by every INSERT that doesn't generate
    /// or accept an identity value — matching SQL Server's behavior of
    /// resetting <c>SCOPE_IDENTITY()</c> and <c>@@IDENTITY</c> when the
    /// most recent statement didn't touch an identity column.
    /// </remarks>
    internal decimal? LastIdentity;

    /// <summary>
    /// Name of the table currently under <c>SET IDENTITY_INSERT ... ON</c>,
    /// or <c>null</c> when no table is in that mode. SQL Server allows only
    /// one table at a time per session; the simulator enforces the same.
    /// </summary>
    internal string? IdentityInsertTable;

    /// <summary>
    /// Explicit override of the per-database <c>VERBOSE_TRUNCATION_WARNINGS</c>
    /// scoped configuration; <c>null</c> means follow the compatibility-level
    /// default. Set via
    /// <c>ALTER DATABASE SCOPED CONFIGURATION SET VERBOSE_TRUNCATION_WARNINGS = ON|OFF</c>.
    /// </summary>
    internal bool? VerboseTruncationWarnings;

    /// <summary>
    /// Decides whether string truncation should raise the verbose Msg 2628
    /// (with table, column, and truncated value) or the legacy Msg 8152
    /// (single line, no detail). Precedence: an explicit
    /// <see cref="VerboseTruncationWarnings"/> setting wins; otherwise trace
    /// flag 460 forces verbose; otherwise the compatibility level decides
    /// (verbose iff &gt;= <see cref="CompatibilityLevel.Sql160"/>, the level
    /// at which it became default in SQL Server 2022).
    /// </summary>
    internal bool IsVerboseTruncationActive() =>
        this.VerboseTruncationWarnings
        ?? (this.TraceFlags.Contains(460)
            || this.CompatibilityLevel >= CompatibilityLevel.Sql160);

    /// <summary>
    /// System tables (e.g. <c>systypes</c>). Materialized once per process and
    /// shared across all <see cref="Simulation"/> instances; the bytes are
    /// immutable.
    /// </summary>
    internal static Dictionary<string, HeapTable> SystemHeapTables => BuiltInResources.SystemHeapTables.Value;

    /// <summary>
    /// Top-level statement dispatch. Iterates through the command's tokens,
    /// dispatching each statement to its dedicated parser by leading keyword.
    /// Yields outcomes for data-producing statements (SELECT, INSERT) and runs
    /// schema/control statements for side-effect only (CREATE, SET, ALTER,
    /// DBCC). The shape mirrors <c>Expression.ResolveBuiltIn</c>: a single
    /// switch with one case per keyword, each delegating to a focused method.
    /// </summary>
    internal IEnumerable<SimulatedStatementOutcome> CreateResultSetsForCommand(SimulatedDbCommand command)
    {
        var context = new ParserContext(command);

        while (context.MoveNext())
        {
            switch (context.Token)
            {
                case Operator { Character: ';' }:
                    continue;

                case ReservedKeyword { Keyword: Keyword.Select }:
                    yield return Selection.Parse(context, 0).Results;
                    continue;

                case ReservedKeyword { Keyword: Keyword.Insert }:
                    yield return ParseInsert(context);
                    continue;

                case ReservedKeyword { Keyword: Keyword.Merge }:
                    yield return ParseMerge(context);
                    continue;

                case ReservedKeyword { Keyword: Keyword.Create } when TryParseCreate(context):
                case ReservedKeyword { Keyword: Keyword.Set } when TryParseSet(context):
                case ReservedKeyword { Keyword: Keyword.Alter } when TryParseAlter(context):
                case ReservedKeyword { Keyword: Keyword.Dbcc } when TryParseDbcc(context):
                    continue;
            }

            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    /// <summary>
    /// Parses the INSERT preamble (<c>INTO</c> keyword, table name, optional
    /// column list, and VALUES tuples) and writes the resulting rows to the
    /// destination table's heap.
    /// </summary>
    private static SimulatedStatementOutcome ParseInsert(ParserContext context)
    {
        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Into })
            context.MoveNextRequired();

        var destinationTableToken = context.Token as StringToken
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        return context.Simulation.HeapTables.TryGetValue(destinationTableToken.Value, out var destinationTable)
            ? ProcessHeapInsert(destinationTable, context)
            : throw SimulatedSqlException.InvalidObjectName(destinationTableToken);
    }

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

        var rawColumns = new List<(Name Name, Name TypeName, int? DeclaredMaxLength, int? DeclaredScale, bool Nullable, IdentityState? Identity)>();
        bool suppressAdvanceToken;
        do
        {
            suppressAdvanceToken = false;
            var columnName = context.GetNextRequired<Name>();
            var type = context.GetNextRequired<Name>();

            int? declaredMaxLength = null;
            int? declaredScale = null;
            context.MoveNextRequired();
            if (context.Token is Operator { Character: '(' })
            {
                var lengthToken = context.GetNextRequired();
                if (lengthToken is Numeric { Value: { IsNull: false } numericValue })
                {
                    declaredMaxLength = numericValue.AsInt32;
                }
                else if (context.MatchContextual(ContextualKeyword.Max))
                {
                    throw new NotSupportedException($"{type}(MAX) and other LOB types aren't modeled yet.");
                }
                else
                {
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                }

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

            IdentityState? identity = null;
            if (context.Token is ReservedKeyword { Keyword: Keyword.Identity })
            {
                identity = ParseIdentitySpec(context, columnName.Value);
            }

            bool nullable;
            if (context.Token is ReservedKeyword next)
            {
                switch (next.Keyword)
                {
                    case Keyword.Not:
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                            throw SimulatedSqlException.SyntaxErrorNear(context);

                        nullable = false;
                        break;
                    case Keyword.Null:
                        nullable = true;
                        break;
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
            }
            else
            {
                suppressAdvanceToken = true;
                nullable = identity is null;
            }

            rawColumns.Add((columnName, type, declaredMaxLength, declaredScale, nullable, identity));
        } while ((suppressAdvanceToken ? context.Token : context.GetNextRequired()) is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            return false;

        var heapColumns = new HeapColumn[rawColumns.Count];
        var fixedWidthSum = 0;
        var identityCount = 0;
        for (var i = 0; i < rawColumns.Count; i++)
        {
            var raw = rawColumns[i];
            var (resolvedType, maxLength) = SqlType.GetByName(raw.TypeName, raw.DeclaredMaxLength, raw.DeclaredScale, i + 1, raw.Name.Value);
            if (raw.Identity is not null)
            {
                if (++identityCount > 1)
                    throw SimulatedSqlException.MultipleIdentityColumns(tableName.Value);
                if (raw.Nullable)
                    throw SimulatedSqlException.IdentityOnNullableColumn(raw.Name.Value, tableName.Value);
                if (resolvedType != SqlType.Int32 && resolvedType != SqlType.BigInt && resolvedType != SqlType.SmallInt && resolvedType != SqlType.TinyInt)
                    throw SimulatedSqlException.IdentityInvalidType(raw.Name.Value);
            }
            heapColumns[i] = new(raw.Name.Value, resolvedType, maxLength, raw.Nullable, raw.Identity);
            if (resolvedType.IsFixedLength)
                fixedWidthSum += resolvedType.FixedLength;
        }

        // Schemas whose fixed-width columns alone exceed SQL Server's 8060-byte
        // in-row limit can never hold a row; reject at CREATE TABLE (Msg 1701).
        // The variable-width-aware warning path is deferred until warning
        // infrastructure exists.
        if (fixedWidthSum > Heap.MaxRowSize)
            throw SimulatedSqlException.RowSizeExceedsMaximum(tableName.Value, fixedWidthSum, Heap.MaxRowSize);

        var heapTable = new HeapTable(tableName.Value, heapColumns);
        return this.HeapTables.TryAdd(heapTable.Name, heapTable)
            ? true
            : throw SimulatedSqlException.ThereIsAlreadyAnObject(heapTable.Name);
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
    /// INSERT processor. Parses the column subset, optional <c>OUTPUT</c>
    /// clause, and VALUES tuples; converts each value token to a
    /// <see cref="SqlValue"/> typed to its target column; encodes each row
    /// via <see cref="RowEncoder.EncodeRow"/> and appends the bytes to
    /// <paramref name="destinationTable"/>'s heap. When <c>OUTPUT</c> is
    /// present, the projected per-row results stream out as a
    /// <see cref="SimulatedSqlResultSet"/> (consumed by
    /// <c>ExecuteReader</c>); otherwise a plain <see cref="SimulatedNonQuery"/>
    /// is returned.
    /// </summary>
    private static SimulatedStatementOutcome ProcessHeapInsert(HeapTable destinationTable, ParserContext context)
    {
        var identityOrdinal = destinationTable.IdentityOrdinal;
        var identityColumn = identityOrdinal >= 0 ? destinationTable.Columns[identityOrdinal] : null;
        var identityInsertOn = identityColumn is not null
            && context.Simulation.IdentityInsertTable is string activeTable
            && Collation.Default.Equals(activeTable, destinationTable.Name);

        HeapColumn[] destinationColumns;
        if (context.GetNextRequired() is Operator { Character: '(' })
        {
            var usedColumns = new List<HeapColumn>();
            while (true)
            {
                if (context.GetNextRequired() is not StringToken column)
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                var columnName = column.Value;
                var tableColumn = destinationTable.Columns.FirstOrDefault(c => Collation.Default.Equals(c.Name, columnName))
                    ?? throw SimulatedSqlException.InvalidColumnName(columnName);
                usedColumns.Add(tableColumn);

                var separator = context.GetNextRequired();
                if (separator is Operator { Character: ')' })
                    break;
                if (separator is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            destinationColumns = [.. usedColumns];

            context.MoveNextRequired();
        }
        else
        {
            // No column list: target every column except an identity one when
            // IDENTITY_INSERT is OFF, matching SQL Server's "VALUES supplies
            // non-identity columns" shorthand.
            destinationColumns = (identityColumn is not null && !identityInsertOn)
                ? [.. destinationTable.Columns.Where(c => c.Identity is null)]
                : [.. destinationTable.Columns];
        }

        if (identityColumn is not null)
        {
            var identityListed = destinationColumns.Any(c => ReferenceEquals(c, identityColumn));
            if (identityListed && !identityInsertOn)
                throw SimulatedSqlException.CannotInsertExplicitIdentity(destinationTable.Name);
            if (!identityListed && identityInsertOn)
                throw SimulatedSqlException.ExplicitIdentityRequired(destinationTable.Name);
        }

        var output = TryParseOutputClause(context, destinationTable, sourceColumnNames: null);

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Values })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var sourceRows = new List<Expression[]>();

        do
        {
            if (context.GetNextRequired<Operator>() is not { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var sourceValues = new List<Expression>();
            while (true)
            {
                // Position context.Token at the start of the value expression
                // (just past the '(' or the previous ','), then let
                // Expression.Parse consume it. Parse leaves context.Token at
                // the first un-consumed token, which must be ',' or ')' here.
                context.MoveNextRequired();
                if (context.Token is Operator { Character: ',' or ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                sourceValues.Add(Expression.Parse(context));

                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            sourceRows.Add([.. sourceValues]);

        } while (context.GetNextOptional() is Operator { Character: ',' });

        decimal? lastIdentityValue = null;
        var outputRows = output is null ? null : new List<byte[]>(sourceRows.Count);
        foreach (var sourceRow in sourceRows)
        {
            var rowValues = new SqlValue[destinationTable.Columns.Length];
            for (var i = 0; i < rowValues.Length; i++)
                rowValues[i] = SqlValue.Null(destinationTable.Schema[i]);

            for (var i = 0; i < destinationColumns.Length; i++)
            {
                var targetColumn = destinationColumns[i];
                var ordinal = -1;
                for (var j = 0; j < destinationTable.Columns.Length; j++)
                {
                    if (ReferenceEquals(destinationTable.Columns[j], targetColumn))
                    {
                        ordinal = j;
                        break;
                    }
                }

                var source = sourceRow[i].Run(name => throw SimulatedSqlException.InvalidColumnName(name));
                EnforceMaxLength(source, targetColumn, destinationTable.Name, context.Simulation);
                var coerced = CoerceForInsert(source, targetColumn.Type);
                rowValues[ordinal] = coerced;

                if (ReferenceEquals(targetColumn, identityColumn))
                {
                    var explicitValue = coerced.CoerceTo(SqlType.BigInt).AsInt64;
                    identityColumn.Identity!.ObserveExplicit(explicitValue);
                    lastIdentityValue = explicitValue;
                }
            }

            if (identityColumn is not null && !destinationColumns.Any(c => ReferenceEquals(c, identityColumn)))
            {
                long generated;
                try
                {
                    generated = identityColumn.Identity!.GenerateNext();
                }
                catch (OverflowException)
                {
                    throw SimulatedSqlException.IdentityOverflow(identityColumn.Type.ToString()!);
                }

                rowValues[identityOrdinal] = CoerceForIdentity(generated, identityColumn);
                lastIdentityValue = generated;
            }

            destinationTable.Heap.Insert(RowEncoder.EncodeRow(destinationTable.Schema, rowValues));

            if (output is { } o)
                outputRows!.Add(o.ProjectRow(rowValues, sourceRowValues: null));
        }

        // Per SQL Server: any INSERT updates SCOPE_IDENTITY/@@IDENTITY —
        // to the generated/explicit identity if the table has one, or to
        // NULL otherwise (resetting state from a prior identity insert).
        context.Simulation.LastIdentity = lastIdentityValue;

        return output is { } o2
            ? new SimulatedSqlResultSet(o2.Schema, o2.ColumnNames, outputRows!)
            : new SimulatedNonQuery(sourceRows.Count);
    }

    /// <summary>
    /// Detects the contextual <c>OUTPUT</c> keyword on the current token and,
    /// if present, parses the comma-separated projection list following the
    /// rules documented on <see cref="OutputProjection"/>. Returns
    /// <see langword="null"/> when <c>OUTPUT</c> is absent (the surrounding
    /// statement just continues with VALUES).
    /// </summary>
    /// <param name="context">Parser state, positioned on the token after the column-list closer.</param>
    /// <param name="destinationTable">The INSERT target — supplies the columns reachable through <c>INSERTED</c>.</param>
    /// <param name="sourceColumnNames">For MERGE only: the source alias's column names. <see langword="null"/> for plain INSERT.</param>
    private static OutputProjection? TryParseOutputClause(ParserContext context, HeapTable destinationTable, (string SourceAlias, string[] SourceColumns, SqlType[] SourceTypes)? sourceColumnNames)
    {
        if (!context.MatchContextual(ContextualKeyword.Output))
            return null;

        var expressions = new List<Expression>();
        var columnNames = new List<string>();

        SqlType ResolveOutputType(List<string> name)
        {
            if (name.Count >= 2 && Collation.Default.Equals(name[0], "INSERTED"))
            {
                var lastPart = name[^1];
                for (var i = 0; i < destinationTable.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(destinationTable.Columns[i].Name, lastPart))
                        return destinationTable.Columns[i].Type;
                }
            }
            else if (sourceColumnNames is var (sourceAlias, sourceCols, sourceTypes) && Collation.Default.Equals(name[0], sourceAlias))
            {
                var lastPart = name[^1];
                for (var i = 0; i < sourceCols.Length; i++)
                {
                    if (Collation.Default.Equals(sourceCols[i], lastPart))
                        return sourceTypes[i];
                }
            }
            throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(string.Join('.', name));
        }

        do
        {
            context.MoveNextRequired();
            var expr = Expression.Parse(context);
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.As }:
                    expr = Expression.AssignName(expr, context.GetNextRequired<Name>());
                    context.MoveNextOptional();
                    break;
                case Name aliasName:
                    expr = Expression.AssignName(expr, aliasName);
                    context.MoveNextOptional();
                    break;
            }
            expressions.Add(expr);
            columnNames.Add(expr.Name);
        }
        while (context.Token is Operator { Character: ',' });

        var schema = new SqlType[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
            schema[i] = expressions[i].GetSqlType(ResolveOutputType);

        return new OutputProjection(expressions, [.. columnNames], schema, destinationTable, sourceColumnNames);
    }

    /// <summary>
    /// Holds the parsed <c>OUTPUT</c> projection together with its statically
    /// resolved schema and the column-name resolvers it needs at row time.
    /// Backs both <c>INSERT ... OUTPUT</c> and <c>MERGE ... OUTPUT</c>; the
    /// MERGE source-alias plumbing is opt-in via the constructor.
    /// </summary>
    private sealed class OutputProjection(
        List<Expression> expressions,
        string[] columnNames,
        SqlType[] schema,
        HeapTable destinationTable,
        (string SourceAlias, string[] SourceColumns, SqlType[] SourceTypes)? source)
    {
        public readonly SqlType[] Schema = schema;
        public readonly string[] ColumnNames = columnNames;

        /// <summary>
        /// Evaluates each projection expression against the just-inserted row
        /// (the <c>INSERTED</c> virtual table) and, for MERGE, the matching
        /// source-row values addressed via the source alias. Returns the
        /// encoded output row in <see cref="Schema"/> shape.
        /// </summary>
        public byte[] ProjectRow(SqlValue[] insertedRow, SqlValue[]? sourceRowValues)
        {
            SqlValue Resolve(List<string> name)
            {
                if (name.Count >= 2 && Collation.Default.Equals(name[0], "INSERTED"))
                {
                    var lastPart = name[^1];
                    for (var i = 0; i < destinationTable.Columns.Length; i++)
                    {
                        if (Collation.Default.Equals(destinationTable.Columns[i].Name, lastPart))
                            return insertedRow[i];
                    }
                }
                else if (source is var (sourceAlias, sourceCols, _) && sourceRowValues is not null && Collation.Default.Equals(name[0], sourceAlias))
                {
                    var lastPart = name[^1];
                    for (var i = 0; i < sourceCols.Length; i++)
                    {
                        if (Collation.Default.Equals(sourceCols[i], lastPart))
                            return sourceRowValues[i];
                    }
                }
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(string.Join('.', name));
            }

            var projected = new SqlValue[expressions.Count];
            for (var i = 0; i < expressions.Count; i++)
                projected[i] = expressions[i].Run(Resolve);
            return RowEncoder.EncodeRow(this.Schema, projected);
        }
    }

    /// <summary>
    /// Parses <c>MERGE</c>, narrowly scoped to the shape EF Core emits for a
    /// multi-row batch insert: <c>USING (VALUES ...) AS alias (cols) ON
    /// predicate WHEN NOT MATCHED THEN INSERT (cols) VALUES (alias.col, ...)
    /// [OUTPUT ...]</c>. The <c>ON</c> predicate is evaluated per source row
    /// against an alias-only resolver — column references into the target
    /// table aren't supported, since modeling that requires a JOIN-style scan.
    /// EF's shape always emits <c>ON 1=0</c>, which cleanly degenerates to
    /// "insert every source row." A <c>WHEN MATCHED</c> branch parses
    /// syntactically (so the grammar shape isn't a surprise) but throws if
    /// the per-row predicate ever evaluates to true.
    /// </summary>
    private static SimulatedStatementOutcome ParseMerge(ParserContext context)
    {
        // Optional INTO: real SQL Server accepts both, EF drops it. Either form lands on the table name.
        var afterMerge = context.GetNextRequired();
        if (afterMerge is ReservedKeyword { Keyword: Keyword.Into })
            context.MoveNextRequired();

        var destinationTableToken = context.Token as StringToken
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        var destinationTable = context.Simulation.HeapTables.TryGetValue(destinationTableToken.Value, out var table)
            ? table
            : throw SimulatedSqlException.InvalidObjectName(destinationTableToken);

        context.MoveNextRequired();
        if (!context.MatchContextual(ContextualKeyword.Using))
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Values })
            throw new NotSupportedException("MERGE source must be a VALUES clause; subqueries aren't supported.");

        var sourceTuples = ParseValuesTuples(context);

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Optional AS (EF emits it).
        var afterUsingClose = context.GetNextRequired();
        if (afterUsingClose is ReservedKeyword { Keyword: Keyword.As })
            afterUsingClose = context.GetNextRequired();

        var sourceAlias = (afterUsingClose as Name)?.Value
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var sourceColumnNames = new List<string>();
        while (true)
        {
            if (context.GetNextRequired() is not Name srcCol)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            sourceColumnNames.Add(srcCol.Value);
            var sep = context.GetNextRequired();
            if (sep is Operator { Character: ')' })
                break;
            if (sep is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        if (sourceColumnNames.Count != sourceTuples[0].Length)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var onPredicate = BooleanExpression.Parse(Expression.Parse(context), context);

        // Compute source schema by static type-of'ing the first tuple's
        // expressions. Source tuples can't reference any columns yet (they're
        // literals or parameters in EF's emit), so the resolver throws.
        var sourceSchema = new SqlType[sourceColumnNames.Count];
        for (var i = 0; i < sourceColumnNames.Count; i++)
            sourceSchema[i] = sourceTuples[0][i].GetSqlType(name => throw SimulatedSqlException.InvalidColumnName(name));

        // WHEN clauses. EF only emits a single WHEN NOT MATCHED THEN INSERT;
        // anything else (WHEN MATCHED branches with UPDATE/DELETE) parses
        // syntactically but throws if the predicate ever picks that branch.
        Expression[]? insertColumnExprs = null;
        Expression[]? insertValueExprs = null;
        var whenMatchedSeen = false;
        while (context.Token is ReservedKeyword { Keyword: Keyword.When })
        {
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Not })
            {
                context.MoveNextRequired();
                if (!context.MatchContextual(ContextualKeyword.Matched))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Then })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Insert })
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                // Optional column list.
                List<Expression> insertColumns = [];
                if (context.GetNextRequired() is Operator { Character: '(' })
                {
                    while (true)
                    {
                        context.MoveNextRequired();
                        var colExpr = Expression.Parse(context);
                        insertColumns.Add(colExpr);
                        if (context.Token is Operator { Character: ')' })
                            break;
                        if (context.Token is not Operator { Character: ',' })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    context.MoveNextRequired();
                }

                if (context.Token is not ReservedKeyword { Keyword: Keyword.Values })
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                if (context.GetNextRequired() is not Operator { Character: '(' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                List<Expression> insertValues = [];
                while (true)
                {
                    context.MoveNextRequired();
                    insertValues.Add(Expression.Parse(context));
                    if (context.Token is Operator { Character: ')' })
                        break;
                    if (context.Token is not Operator { Character: ',' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }

                insertColumnExprs = [.. insertColumns];
                insertValueExprs = [.. insertValues];
                context.MoveNextOptional();
            }
            else
            {
                // WHEN MATCHED — parse and discard until next clause boundary.
                whenMatchedSeen = true;
                while (context.Token is not (null
                    or ReservedKeyword { Keyword: Keyword.When }
                    or Operator { Character: ';' }
                    or UnquotedString))
                {
                    context.MoveNextOptional();
                }
                if (context.Token is UnquotedString && !context.MatchContextual(ContextualKeyword.Output))
                    context.MoveNextOptional();
            }
        }

        if (insertColumnExprs is null || insertValueExprs is null)
            throw new NotSupportedException("MERGE without a WHEN NOT MATCHED THEN INSERT branch isn't supported.");

        // Resolve insert columns against destination schema.
        var insertColumns2 = new HeapColumn[insertColumnExprs.Length];
        for (var i = 0; i < insertColumnExprs.Length; i++)
        {
            var colName = (insertColumnExprs[i] as Reference)?.Name
                ?? throw SimulatedSqlException.SyntaxErrorNear(context);
            insertColumns2[i] = destinationTable.Columns.FirstOrDefault(c => Collation.Default.Equals(c.Name, colName))
                ?? throw SimulatedSqlException.InvalidColumnName(colName);
        }

        var output = TryParseOutputClause(context, destinationTable, (sourceAlias, [.. sourceColumnNames], sourceSchema));

        // Identity wiring (mirrors ProcessHeapInsert).
        var identityOrdinal = destinationTable.IdentityOrdinal;
        var identityColumn = identityOrdinal >= 0 ? destinationTable.Columns[identityOrdinal] : null;
        var identityInsertOn = identityColumn is not null
            && context.Simulation.IdentityInsertTable is string activeTable
            && Collation.Default.Equals(activeTable, destinationTable.Name);
        if (identityColumn is not null)
        {
            var identityListed = insertColumns2.Any(c => ReferenceEquals(c, identityColumn));
            if (identityListed && !identityInsertOn)
                throw SimulatedSqlException.CannotInsertExplicitIdentity(destinationTable.Name);
            if (!identityListed && identityInsertOn)
                throw SimulatedSqlException.ExplicitIdentityRequired(destinationTable.Name);
        }

        var outputRows = output is null ? null : new List<byte[]>(sourceTuples.Count);
        decimal? lastIdentityValue = null;
        var insertedCount = 0;
        foreach (var sourceTuple in sourceTuples)
        {
            // Evaluate the source tuple to concrete values.
            var sourceRowValues = new SqlValue[sourceColumnNames.Count];
            for (var i = 0; i < sourceTuple.Length; i++)
                sourceRowValues[i] = sourceTuple[i].Run(name => throw SimulatedSqlException.InvalidColumnName(name));

            // Resolver for the ON predicate and the INSERT value expressions:
            // matches references to the source alias and falls back to error
            // for anything else. Targeting the destination table is rejected
            // (see method-level remarks).
            SqlValue ResolveSource(List<string> name)
            {
                if (name.Count >= 2 && Collation.Default.Equals(name[0], sourceAlias))
                {
                    var lastPart = name[^1];
                    for (var i = 0; i < sourceColumnNames.Count; i++)
                    {
                        if (Collation.Default.Equals(sourceColumnNames[i], lastPart))
                            return sourceRowValues[i];
                    }
                }
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(string.Join('.', name));
            }

            if (onPredicate.Run(_ => SqlValue.Null(SqlType.Int32)))
            {
                // Predicate matched — would route to WHEN MATCHED.
                if (whenMatchedSeen)
                    throw new NotSupportedException("MERGE's WHEN MATCHED branch isn't supported.");
                continue;
            }

            // WHEN NOT MATCHED: insert one row.
            var rowValues = new SqlValue[destinationTable.Columns.Length];
            for (var i = 0; i < rowValues.Length; i++)
                rowValues[i] = SqlValue.Null(destinationTable.Schema[i]);

            for (var i = 0; i < insertColumns2.Length; i++)
            {
                var targetColumn = insertColumns2[i];
                var ordinal = -1;
                for (var j = 0; j < destinationTable.Columns.Length; j++)
                {
                    if (ReferenceEquals(destinationTable.Columns[j], targetColumn))
                    {
                        ordinal = j;
                        break;
                    }
                }

                var source = insertValueExprs[i].Run(ResolveSource);
                EnforceMaxLength(source, targetColumn, destinationTable.Name, context.Simulation);
                var coerced = CoerceForInsert(source, targetColumn.Type);
                rowValues[ordinal] = coerced;

                if (ReferenceEquals(targetColumn, identityColumn))
                {
                    var explicitValue = coerced.CoerceTo(SqlType.BigInt).AsInt64;
                    identityColumn.Identity!.ObserveExplicit(explicitValue);
                    lastIdentityValue = explicitValue;
                }
            }

            if (identityColumn is not null && !insertColumns2.Any(c => ReferenceEquals(c, identityColumn)))
            {
                long generated;
                try
                {
                    generated = identityColumn.Identity!.GenerateNext();
                }
                catch (OverflowException)
                {
                    throw SimulatedSqlException.IdentityOverflow(identityColumn.Type.ToString()!);
                }

                rowValues[identityOrdinal] = CoerceForIdentity(generated, identityColumn);
                lastIdentityValue = generated;
            }

            destinationTable.Heap.Insert(RowEncoder.EncodeRow(destinationTable.Schema, rowValues));
            insertedCount++;

            if (output is { } o)
                outputRows!.Add(o.ProjectRow(rowValues, sourceRowValues));
        }

        context.Simulation.LastIdentity = lastIdentityValue;

        return output is { } o2
            ? new SimulatedSqlResultSet(o2.Schema, o2.ColumnNames, outputRows!)
            : new SimulatedNonQuery(insertedCount);
    }

    /// <summary>
    /// Parses one or more comma-separated <c>(...)</c> tuples following a
    /// <c>VALUES</c> keyword. Enters with <see cref="ParserContext.Token"/>
    /// on <c>VALUES</c>; on return <see cref="ParserContext.Token"/> sits on
    /// the first token after the last tuple's closing paren — typically a
    /// surrounding <c>)</c> for MERGE or a clause keyword for INSERT.
    /// </summary>
    private static List<Expression[]> ParseValuesTuples(ParserContext context)
    {
        var tuples = new List<Expression[]>();
        do
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var values = new List<Expression>();
            while (true)
            {
                context.MoveNextRequired();
                if (context.Token is Operator { Character: ',' or ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                values.Add(Expression.Parse(context));
                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            tuples.Add([.. values]);
        }
        while (context.GetNextOptional() is Operator { Character: ',' });

        return tuples;
    }

    /// <summary>
    /// Coerces an auto-generated identity <see cref="long"/> to the column's
    /// declared integer type, raising the IDENTITY-specific Msg 8115 if the
    /// next value won't fit.
    /// </summary>
    private static SqlValue CoerceForIdentity(long value, HeapColumn identityColumn)
    {
        try
        {
            return SqlValue.FromInt64(value).CoerceTo(identityColumn.Type);
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.IdentityOverflow(identityColumn.Type.ToString()!);
        }
    }

    /// <summary>
    /// Raises a truncation error when the SOURCE value's natural length would
    /// exceed <paramref name="column"/>'s declared maximum. The check fires
    /// pre-coerce so that <c>char(N)</c> / <c>nchar(N)</c> / <c>binary(N)</c>
    /// columns — whose CoerceTo silently truncates to match SQL Server's CAST
    /// semantics — still raise the bind-time truncation error. NULL values
    /// and columns without a declared max are no-ops. Selects between the
    /// verbose Msg 2628 (with table/column/value) and the legacy Msg 8152 via
    /// <see cref="IsVerboseTruncationActive"/>.
    /// </summary>
    /// <remarks>
    /// Length unit follows the column's storage encoding: CP1252 byte count
    /// for <c>varchar</c> / <c>char(N)</c>, raw byte count for <c>varbinary</c>
    /// / <c>binary(N)</c>, UCS-2 code units (<see cref="string.Length"/>) for
    /// <c>nvarchar</c> / <c>nchar(N)</c> / <c>sysname</c>. Non-string sources
    /// fall through (e.g. <c>INSERT INTO varchar(5) VALUES (12345)</c>): the
    /// integer-to-string format path inside <c>CoerceTo</c> produces a value
    /// the column can hold for the common cases, and any genuine overflow
    /// surfaces as a coercion error instead.
    /// </remarks>
    private static void EnforceMaxLength(SqlValue source, HeapColumn column, string tableName, Simulation simulation)
    {
        if (source.IsNull || column.MaxLength is not int max)
            return;

        int actual;
        if (column.Type == SqlType.Varbinary || column.Type is BinarySqlType)
        {
            if (source.Type is not (VarbinarySqlType or BinarySqlType))
                return;
            actual = source.AsBytes.Length;
        }
        else if (column.Type == SqlType.Varchar || column.Type is CharSqlType)
        {
            if (source.Type.Category != SqlTypeCategory.String)
                return;
            actual = SqlType.Varchar.GetVariableByteCount(SqlValue.FromVarchar(source.AsString));
        }
        else
        {
            if (source.Type.Category != SqlTypeCategory.String)
                return;
            actual = source.AsString.Length;
        }

        if (actual <= max)
            return;

        if (!simulation.IsVerboseTruncationActive())
            throw SimulatedSqlException.StringOrBinaryWouldBeTruncatedLegacy();

        throw column.Type == SqlType.Varbinary || column.Type is BinarySqlType
            ? SimulatedSqlException.StringOrBinaryWouldBeTruncated(tableName, column.Name, source.AsBytes, max)
            : SimulatedSqlException.StringOrBinaryWouldBeTruncated(tableName, column.Name, source.AsString, max);
    }

    /// <summary>
    /// Coerces an INSERT source value to the destination column's type,
    /// converting any overflow into the SQL Server-shaped Msg 8115. Truncation
    /// of strings/bytes is handled separately by <see cref="EnforceMaxLength"/>
    /// before this method runs.
    /// </summary>
    private static SqlValue CoerceForInsert(SqlValue source, SqlType targetType)
    {
        try
        {
            return source.CoerceTo(targetType);
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(targetType.ToString()!);
        }
    }

    private static bool TryParseSet(ParserContext context)
    {
        var afterSet = context.GetNextRequired();

        if (afterSet is ReservedKeyword { Keyword: Keyword.Identity_Insert })
            return TryParseSetIdentityInsert(context);

        if (afterSet is not UnquotedString unquoted)
            return false;

        var setTarget = unquoted.Value;
        Span<char> upper = stackalloc char[setTarget.Length];
        return setTarget.ToUpperInvariant(upper) switch
        {
            7 => upper switch
            {
                "NOCOUNT" => context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.On or Keyword.Off },
                _ => false
            },
            21 => upper switch
            {
                "IMPLICIT_TRANSACTIONS" => context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.On or Keyword.Off },
                _ => false
            },
            _ => false
        };
    }

    /// <summary>
    /// Parses <c>SET IDENTITY_INSERT &lt;table&gt; ON|OFF</c>. ON sets the
    /// session's active <c>IDENTITY_INSERT</c> target after verifying no
    /// other table holds it (Msg 8107); OFF clears the target if it matches.
    /// </summary>
    private static bool TryParseSetIdentityInsert(ParserContext context)
    {
        if (context.GetNextRequired() is not StringToken tableNameToken)
            return false;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: var onOff } || onOff is not (Keyword.On or Keyword.Off))
            return false;

        var tableName = tableNameToken.Value;
        if (!context.Simulation.HeapTables.TryGetValue(tableName, out var heapTable))
            throw SimulatedSqlException.InvalidObjectName(tableNameToken);

        if (onOff == Keyword.On)
        {
            if (context.Simulation.IdentityInsertTable is string held && !Collation.Default.Equals(held, heapTable.Name))
                throw SimulatedSqlException.IdentityInsertAlreadyOn(held, heapTable.Name);
            context.Simulation.IdentityInsertTable = heapTable.Name;
        }
        else if (Collation.Default.Equals(context.Simulation.IdentityInsertTable, heapTable.Name))
        {
            context.Simulation.IdentityInsertTable = null;
        }
        return true;
    }

    /// <summary>
    /// Parses the two ALTER DATABASE forms the simulator currently models:
    /// <c>ALTER DATABASE … SET COMPATIBILITY_LEVEL = N</c> (per-database
    /// compat) and
    /// <c>ALTER DATABASE SCOPED CONFIGURATION SET VERBOSE_TRUNCATION_WARNINGS = ON|OFF</c>.
    /// The simulator has a single database, so any database name (including
    /// <c>CURRENT</c>) is accepted and ignored.
    /// </summary>
    private bool TryParseAlter(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Database })
            return false;

        var afterDatabase = context.GetNextRequired();
        if (context.MatchContextual(ContextualKeyword.Scoped))
            return TryParseAlterDatabaseScopedConfiguration(context);

        // Otherwise a database name (or CURRENT). The simulator has one
        // database; accept anything that looks like an identifier.
        return afterDatabase is Name or ReservedKeyword { Keyword: Keyword.Current }
            && TryParseAlterDatabaseSet(context);
    }

    private bool TryParseAlterDatabaseSet(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            return false;

        context.MoveNextRequired();
        if (!context.MatchContextual(ContextualKeyword.Compatibility_Level))
            return false;

        if (context.GetNextRequired() is not Operator { Character: '=' })
            return false;

        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } numericValue })
            return false;

        var requested = numericValue.AsInt32;
        if (!Enum.IsDefined((CompatibilityLevel)requested))
            throw SimulatedSqlException.InvalidCompatibilityLevel();

        this.CompatibilityLevel = (CompatibilityLevel)requested;
        return true;
    }

    private bool TryParseAlterDatabaseScopedConfiguration(ParserContext context)
    {
        context.MoveNextRequired();
        if (!context.MatchContextual(ContextualKeyword.Configuration))
            return false;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            return false;

        context.MoveNextRequired();
        if (!context.MatchContextual(ContextualKeyword.Verbose_Truncation_Warnings))
            return false;

        if (context.GetNextRequired() is not Operator { Character: '=' })
            return false;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: var on } || on is not (Keyword.On or Keyword.Off))
            return false;

        this.VerboseTruncationWarnings = on == Keyword.On;
        return true;
    }

    /// <summary>
    /// Parses <c>DBCC TRACEON(N)</c> / <c>DBCC TRACEOFF(N)</c>. The optional
    /// <c>, -1</c> suffix that promotes the flag to global scope isn't modeled
    /// — the simulator has a single connection so session vs global doesn't
    /// matter today.
    /// </summary>
    private bool TryParseDbcc(ParserContext context)
    {
        context.MoveNextRequired();
        bool turningOn;
        switch (context.AsContextual())
        {
            case ContextualKeyword.TraceOn: turningOn = true; break;
            case ContextualKeyword.TraceOff: turningOn = false; break;
            default: return false;
        }

        if (context.GetNextRequired() is not Operator { Character: '(' })
            return false;

        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } numericValue })
            return false;

        if (context.GetNextRequired() is not Operator { Character: ')' })
            return false;

        var flag = numericValue.AsInt32;
        _ = turningOn ? this.TraceFlags.Add(flag) : this.TraceFlags.Remove(flag);
        return true;
    }
}
