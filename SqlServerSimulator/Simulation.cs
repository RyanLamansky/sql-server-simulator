using SqlServerSimulator.Parser;
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
    private static SimulatedNonQuery ParseInsert(ParserContext context)
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

        var rawColumns = new List<(Name Name, Name TypeName, int? DeclaredMaxLength, int? DeclaredScale, bool Nullable)>();
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
                else if (lengthToken is UnquotedString unquoted && unquoted.Span.Equals("MAX", StringComparison.OrdinalIgnoreCase))
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
                nullable = true;
            }

            rawColumns.Add((columnName, type, declaredMaxLength, declaredScale, nullable));
        } while ((suppressAdvanceToken ? context.Token : context.GetNextRequired()) is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            return false;

        var heapColumns = new HeapColumn[rawColumns.Count];
        var fixedWidthSum = 0;
        for (var i = 0; i < rawColumns.Count; i++)
        {
            var (resolvedType, maxLength) = SqlType.GetByName(rawColumns[i].TypeName, rawColumns[i].DeclaredMaxLength, rawColumns[i].DeclaredScale, i + 1, rawColumns[i].Name.Value);
            heapColumns[i] = new(rawColumns[i].Name.Value, resolvedType, maxLength, rawColumns[i].Nullable);
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
    /// INSERT processor. Parses the column subset and VALUES tuples, converts
    /// each value token to a <see cref="SqlValue"/> typed to its target column,
    /// encodes each row via <see cref="RowEncoder.EncodeRow"/>, and appends
    /// the bytes to <paramref name="destinationTable"/>'s heap.
    /// </summary>
    private static SimulatedNonQuery ProcessHeapInsert(HeapTable destinationTable, ParserContext context)
    {
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
            destinationColumns = [.. destinationTable.Columns];
        }

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

                var coerced = EvaluateInsertValue(sourceRow[i], targetColumn.Type);
                EnforceMaxLength(coerced, targetColumn, destinationTable.Name, context.Simulation);
                rowValues[ordinal] = coerced;
            }

            destinationTable.Heap.Insert(RowEncoder.EncodeRow(destinationTable.Schema, rowValues));
        }

        return new SimulatedNonQuery(sourceRows.Count);
    }

    /// <summary>
    /// Raises a truncation error when <paramref name="value"/> would exceed
    /// <paramref name="column"/>'s declared maximum length. NULL values and
    /// columns without a declared max length are no-ops. Selects between the
    /// verbose Msg 2628 (with table/column/value) and the legacy Msg 8152 via
    /// <see cref="IsVerboseTruncationActive"/>.
    /// </summary>
    /// <remarks>
    /// Length unit follows the column's storage encoding: CP1252 byte count
    /// for <c>varchar</c>, raw byte count for <c>varbinary</c> (both delegate
    /// to the type's encoder), UCS-2 code units (<see cref="string.Length"/>)
    /// for <c>nvarchar</c> and <c>sysname</c>.
    /// </remarks>
    private static void EnforceMaxLength(SqlValue value, HeapColumn column, string tableName, Simulation simulation)
    {
        if (value.IsNull || column.MaxLength is not int max)
            return;

        var actual = column.Type == SqlType.Varchar || column.Type == SqlType.Varbinary
            ? column.Type.GetVariableByteCount(value)
            : value.AsString.Length;

        if (actual <= max)
            return;

        if (!simulation.IsVerboseTruncationActive())
            throw SimulatedSqlException.StringOrBinaryWouldBeTruncatedLegacy();

        throw column.Type == SqlType.Varbinary
            ? SimulatedSqlException.StringOrBinaryWouldBeTruncated(tableName, column.Name, value.AsBytes, max)
            : SimulatedSqlException.StringOrBinaryWouldBeTruncated(tableName, column.Name, value.AsString, max);
    }

    /// <summary>
    /// Evaluates a single INSERT VALUES expression and coerces it to the
    /// destination column's type. Column references aren't valid here (no row
    /// is in scope), so the resolver throws.
    /// </summary>
    private static SqlValue EvaluateInsertValue(Expression expression, SqlType targetType)
    {
        var source = expression.Run(name => throw SimulatedSqlException.InvalidColumnName(name));
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
        var setTarget = context.GetNextRequired<UnquotedString>().Value;
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
        if (afterDatabase is UnquotedString unquoted && unquoted.Span.Equals("SCOPED", StringComparison.OrdinalIgnoreCase))
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

        if (context.GetNextRequired() is not UnquotedString option)
            return false;

        if (!option.Span.Equals("COMPATIBILITY_LEVEL", StringComparison.OrdinalIgnoreCase))
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
        if (context.GetNextRequired() is not UnquotedString configToken
            || !configToken.Span.Equals("CONFIGURATION", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            return false;

        if (context.GetNextRequired() is not UnquotedString option)
            return false;

        if (!option.Span.Equals("VERBOSE_TRUNCATION_WARNINGS", StringComparison.OrdinalIgnoreCase))
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
        if (context.GetNextRequired() is not UnquotedString action)
            return false;

        bool turningOn;
        if (action.Span.Equals("TRACEON", StringComparison.OrdinalIgnoreCase))
            turningOn = true;
        else if (action.Span.Equals("TRACEOFF", StringComparison.OrdinalIgnoreCase))
            turningOn = false;
        else
            return false;

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
