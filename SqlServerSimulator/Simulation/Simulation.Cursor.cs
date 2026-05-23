using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>DECLARE &lt;name&gt; [INSENSITIVE] [SCROLL] CURSOR
    /// [LOCAL|GLOBAL] [FORWARD_ONLY|SCROLL] [STATIC|KEYSET|DYNAMIC|FAST_FORWARD]
    /// [READ_ONLY|SCROLL_LOCKS|OPTIMISTIC] [TYPE_WARNING] FOR &lt;select&gt;
    /// [FOR {READ ONLY | UPDATE [OF cols]}]</c> — both the SQL-92 and T-SQL
    /// extended forms. The effective sensitivity is resolved here: a
    /// non-updatable query (anything but a single base table with a unique
    /// key) is forced to STATIC, matching SQL Server's silent conversion.
    /// Cursor variables (<c>DECLARE @c CURSOR</c>) aren't modeled. On entry the
    /// cursor is on the <c>DECLARE</c> keyword.
    /// </summary>
    private static void ParseDeclareCursor(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume DECLARE

        if (context.Token is AtPrefixedString)
            throw new NotSupportedException("Cursor variables (DECLARE @c CURSOR / cursor-typed parameters) aren't modeled; use a named cursor.");
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var cursorName = nameToken.Value;
        context.MoveNextRequired();

        var reqStatic = false;   // STATIC / INSENSITIVE
        var reqKeyset = false;
        var reqDynamic = false;
        var reqFastForward = false;
        var forwardOnly = false;
        var scroll = false;
        var readOnlyOption = false;

        // SQL-92 pre-CURSOR options: [INSENSITIVE] [SCROLL].
        while (context.Token is UnquotedString)
        {
            if (IsWord(context.Token, "INSENSITIVE"))
            {
                reqStatic = true;
            }
            else if (IsWord(context.Token, "SCROLL"))
            {
                scroll = true;
            }
            else
            {
                break;
            }
            context.MoveNextRequired();
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Cursor })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired(); // consume CURSOR

        // T-SQL post-CURSOR options, in any order, until FOR. LOCAL / GLOBAL
        // (scope) and SCROLL_LOCKS / OPTIMISTIC / TYPE_WARNING (concurrency /
        // warning) are accepted and discarded.
        while (context.Token is not ReservedKeyword { Keyword: Keyword.For })
        {
            if (context.Token is null)
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            else if (IsWord(context.Token, "FORWARD_ONLY"))
            {
                forwardOnly = true;
            }
            else if (IsWord(context.Token, "SCROLL"))
            {
                scroll = true;
            }
            else if (IsWord(context.Token, "STATIC") || IsWord(context.Token, "INSENSITIVE"))
            {
                reqStatic = true;
            }
            else if (IsWord(context.Token, "KEYSET"))
            {
                reqKeyset = true;
            }
            else if (IsWord(context.Token, "DYNAMIC"))
            {
                reqDynamic = true;
            }
            else if (IsWord(context.Token, "FAST_FORWARD"))
            {
                reqFastForward = true;
            }
            else if (IsWord(context.Token, "READ_ONLY"))
            {
                readOnlyOption = true;
            }
            else if (!IsWord(context.Token, "LOCAL") && !IsWord(context.Token, "GLOBAL")
                && !IsWord(context.Token, "SCROLL_LOCKS") && !IsWord(context.Token, "OPTIMISTIC")
                && !IsWord(context.Token, "TYPE_WARNING"))
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
        }

        context.MoveNextRequired(); // consume FOR
        var selection = Selection.Parse(context, 0);

        // Trailing SQL-92 updatability clause: FOR READ ONLY | FOR UPDATE [OF cols].
        if (context.Token is ReservedKeyword { Keyword: Keyword.For })
        {
            context.MoveNextRequired();
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Read }:
                    context.MoveNextRequired();
                    if (!IsWord(context.Token, "ONLY"))
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    readOnlyOption = true;
                    context.MoveNextOptional();
                    break;
                case ReservedKeyword { Keyword: Keyword.Update }:
                    context.MoveNextOptional();
                    // Optional OF <col> [, <col>]… — parsed and discarded (the
                    // simulator doesn't enforce per-column updatability).
                    if (context.Token is ReservedKeyword { Keyword: Keyword.Of })
                    {
                        do
                        {
                            context.MoveNextRequired();
                            if (context.Token is not Name)
                                throw SimulatedSqlException.SyntaxErrorNear(context);
                            context.MoveNextOptional();
                        } while (context.Token is Operator { Character: ',' });
                    }
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }

        if (batch.IsSkipping)
            return;

        // Resolve updatability + effective sensitivity. A non-updatable query
        // (no single base table with a unique key) is forced to STATIC, as is
        // an explicit STATIC / INSENSITIVE / FAST_FORWARD request. Otherwise
        // honor the requested type; an unspecified type defaults to KEYSET
        // when SCROLL was asked for and DYNAMIC for the forward-only default.
        var baseTable = selection.CursorBaseTable;
        var keyOrdinals = baseTable is null ? null : Selection.CursorUniqueKeyOrdinals(baseTable);
        var updatable = baseTable is not null && keyOrdinals is not null;

        var sensitivity = !updatable || reqStatic || reqFastForward
            ? CursorSensitivity.Static
            : reqKeyset
                ? CursorSensitivity.Keyset
                : reqDynamic
                    ? CursorSensitivity.Dynamic
                    : scroll ? CursorSensitivity.Keyset : CursorSensitivity.Dynamic;

        var readOnly = sensitivity == CursorSensitivity.Static || reqFastForward || readOnlyOption;
        var scrollable = scroll
            || ((sensitivity is CursorSensitivity.Static or CursorSensitivity.Keyset) && !forwardOnly && !reqFastForward);

        if (batch.Connection.Cursors.ContainsKey(cursorName))
            throw SimulatedSqlException.CursorAlreadyExists(cursorName);

        batch.Connection.Cursors[cursorName] = new Cursor(
            cursorName,
            selection,
            sensitivity,
            scrollable,
            readOnly,
            updatable ? baseTable : null,
            updatable ? keyOrdinals : null);
    }

    /// <summary>Parses and runs <c>OPEN [GLOBAL] &lt;cursor&gt;</c>.</summary>
    private static void ParseOpenCursor(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume OPEN
        var name = ReadCursorName(context);
        if (batch.IsSkipping)
            return;
        GetCursor(batch, name).Open(batch);
    }

    /// <summary>Parses and runs <c>CLOSE [GLOBAL] &lt;cursor&gt;</c>.</summary>
    private static void ParseCloseCursor(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume CLOSE
        var name = ReadCursorName(context);
        if (batch.IsSkipping)
            return;
        GetCursor(batch, name).Close();
    }

    /// <summary>
    /// Parses and runs <c>DEALLOCATE [GLOBAL] &lt;cursor&gt;</c>: removes the
    /// cursor from the session (implicitly closing it if open). Msg 16916 on a
    /// name that was never declared.
    /// </summary>
    private static void ParseDeallocateCursor(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume DEALLOCATE
        var name = ReadCursorName(context);
        if (batch.IsSkipping)
            return;
        if (!batch.Connection.Cursors.Remove(name))
            throw SimulatedSqlException.CursorDoesNotExist(name);
    }

    /// <summary>
    /// Parses and runs <c>FETCH [&lt;direction&gt; [&lt;n&gt;]] [FROM]
    /// &lt;cursor&gt; [INTO @v [, @v]…]</c>. With INTO, assigns the projected
    /// columns to the variables (Msg 16924 on a count mismatch) and yields no
    /// result set; without INTO, yields a single-row result set when the FETCH
    /// lands on a row. Sets <c>@@FETCH_STATUS</c>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> ParseFetchCursor(BatchContext batch)
    {
        var context = batch.Parser;
        var connection = context.Connection;
        context.MoveNextRequired(); // consume FETCH

        var direction = FetchDirection.Next;
        long offset = 0;
        if (context.Token is UnquotedString dirToken && TryParseFetchDirection(dirToken.Span, out direction))
        {
            context.MoveNextRequired();
            if (direction is FetchDirection.Absolute or FetchDirection.Relative)
            {
                var offsetExpr = Expression.Parse(context);
                if (!batch.IsSkipping)
                {
                    var offsetValue = offsetExpr.Run(new RuntimeContext(NoColumnResolver, batch));
                    offset = offsetValue.IsNull ? 0 : offsetValue.CoerceTo(SqlType.Int32).AsInt32;
                }
            }
        }

        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
            context.MoveNextRequired();

        var cursorName = ReadCursorName(context);

        List<string>? intoVariables = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Into })
        {
            intoVariables = [];
            do
            {
                if (context.GetNextRequired() is not AtPrefixedString variable)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                intoVariables.Add(variable.Value);
                context.MoveNextOptional();
            } while (context.Token is Operator { Character: ',' });
        }

        if (batch.IsSkipping)
            yield break;

        var cursor = GetCursor(batch, cursorName);

        // The INTO-list cardinality check fires regardless of whether the
        // FETCH lands on a row (probe-confirmed Msg 16924).
        if (intoVariables is not null && intoVariables.Count != cursor.Selection.Schema.Length)
            throw SimulatedSqlException.CursorFetchVariableCountMismatch();

        var (status, values) = cursor.Fetch(batch, direction, offset);
        connection.LastFetchStatus = status;
        connection.LastStatementRowCount = status == 0 ? 1 : 0;

        if (intoVariables is not null)
        {
            // Variables are written only on a successful fetch; on -1 (past
            // end) they retain their prior value (probe-confirmed). The rare
            // keyset -2 case leaves them unchanged too (minor divergence: real
            // SQL Server zeroes/NULLs them).
            if (status == 0 && values is not null)
            {
                for (var i = 0; i < intoVariables.Count; i++)
                {
                    if (!batch.Variables.TryGetValue(intoVariables[i], out var slot))
                        throw SimulatedSqlException.MustDeclareScalarVariable(intoVariables[i]);
                    slot.Value = Cast.ApplyCoercion(values[i], slot.DeclaredType, slot.DeclaredMaxLength);
                }
            }
            yield break;
        }

        // No INTO: a landed fetch produces a single-row result set.
        if (status == 0 && values is not null)
        {
            yield return new SimulatedSqlResultSet(
                cursor.Selection.Schema,
                cursor.Selection.ColumnNames,
                [RowEncoder.EncodeRow(cursor.Selection.Schema, values)]);
        }
    }

    /// <summary>
    /// Reads a cursor name at the current position (optionally preceded by a
    /// <c>GLOBAL</c> scope keyword), advancing past it. Rejects cursor
    /// variables (<c>@c</c>).
    /// </summary>
    private static string ReadCursorName(ParserContext context)
    {
        if (IsWord(context.Token, "GLOBAL"))
            context.MoveNextRequired();
        if (context.Token is AtPrefixedString)
            throw new NotSupportedException("Cursor variables (DECLARE @c CURSOR / cursor-typed parameters) aren't modeled; use a named cursor.");
        if (context.Token is not Name name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var value = name.Value;
        context.MoveNextOptional();
        return value;
    }

    /// <summary>
    /// Parses the <c>CURRENT OF &lt;cursor&gt;</c> tail of a positioned
    /// <c>WHERE CURRENT OF</c> clause (cursor on entry: the <c>CURRENT</c>
    /// keyword) and validates the cursor against the DML target
    /// <paramref name="table"/>: Msg 16929 when the cursor is read-only,
    /// Msg 16931 when it isn't positioned on a live row (or is positioned on a
    /// different table). Returns the validated cursor whose
    /// <see cref="Cursor.CurrentKey"/> identifies the row to mutate.
    /// </summary>
    internal static Cursor ParseWhereCurrentOf(ParserContext context, HeapTable table)
    {
        context.MoveNextRequired(); // consume CURRENT
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Of })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired(); // consume OF
        var name = ReadCursorName(context);

        var cursor = GetCursor(context.Batch, name);
        return cursor.ReadOnly
            ? throw SimulatedSqlException.CursorIsReadOnly()
            : cursor.CurrentKey is null || !ReferenceEquals(cursor.BaseTable, table)
                ? throw SimulatedSqlException.CursorNoCurrentRow()
                : cursor;
    }

    /// <summary>
    /// True when the heap row <paramref name="rowBytes"/> is the one the
    /// positioned <paramref name="cursor"/> is sitting on — i.e. its unique-key
    /// columns equal the cursor's <see cref="Cursor.CurrentKey"/>.
    /// </summary>
    internal static bool CursorRowMatches(Cursor cursor, HeapTable table, ReadOnlySpan<byte> rowBytes)
    {
        var keyOrdinals = cursor.KeyStorageOrdinals!;
        var rowKey = new SqlValue[keyOrdinals.Length];
        for (var i = 0; i < keyOrdinals.Length; i++)
            rowKey[i] = RowDecoder.DecodeColumn(table.StoredColumns, rowBytes, keyOrdinals[i], table.Heap);
        return Selection.CompareKeyTuples(rowKey, cursor.CurrentKey!) == 0;
    }

    /// <summary>
    /// Resolves a declared cursor by name, raising Msg 16916 when no cursor of
    /// that name exists on the connection.
    /// </summary>
    internal static Cursor GetCursor(BatchContext batch, string name) =>
        batch.Connection.Cursors.TryGetValue(name, out var cursor)
            ? cursor
            : throw SimulatedSqlException.CursorDoesNotExist(name);

    private static readonly (string Word, FetchDirection Direction)[] FetchDirectionWords =
    [
        ("NEXT", FetchDirection.Next),
        ("PRIOR", FetchDirection.Prior),
        ("FIRST", FetchDirection.First),
        ("LAST", FetchDirection.Last),
        ("ABSOLUTE", FetchDirection.Absolute),
        ("RELATIVE", FetchDirection.Relative),
    ];

    private static bool TryParseFetchDirection(ReadOnlySpan<char> span, out FetchDirection direction)
    {
        foreach (var (word, dir) in FetchDirectionWords)
        {
            if (span.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                direction = dir;
                return true;
            }
        }
        direction = FetchDirection.Next;
        return false;
    }

    private static bool IsWord(Token? token, string word) =>
        token is UnquotedString u && u.Span.Equals(word, StringComparison.OrdinalIgnoreCase);
}
