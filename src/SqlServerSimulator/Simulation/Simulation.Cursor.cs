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

        // DECLARE @c CURSOR (cursor variable) routes through TryParseDeclare;
        // this path is the bare-identifier form `DECLARE name … CURSOR … FOR …`.
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var cursorName = nameToken.Value;
        context.MoveNextRequired();

        var reqStatic = false;   // INSENSITIVE (pre-CURSOR)
        var scroll = false;

        // SQL-92 pre-CURSOR options: [INSENSITIVE] [SCROLL].
        while (context.Token is UnquotedString)
        {
            if (IsWord(context.Token, "INSENSITIVE"))
                reqStatic = true;
            else if (IsWord(context.Token, "SCROLL"))
                scroll = true;
            else
                break;
            context.MoveNextRequired();
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Cursor })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var built = BuildCursorDefinition(batch, cursorName, reqStatic, scroll);
        if (built is not { } definition)
            return; // skipping — tokens consumed, nothing registered
        DeclareCursorInScope(batch, cursorName, definition.Cursor, definition.Local);
    }

    /// <summary>
    /// Shared parser for a cursor definition — <c>CURSOR [options] FOR
    /// &lt;select&gt; [FOR {READ ONLY | UPDATE [OF cols]}]</c> — used by both
    /// <c>DECLARE name CURSOR …</c> and <c>SET @c = CURSOR …</c>. On entry the
    /// cursor sits on the <c>CURSOR</c> keyword; <paramref name="reqStatic"/> /
    /// <paramref name="scroll"/> carry any SQL-92 pre-CURSOR INSENSITIVE / SCROLL
    /// flags. Resolves effective sensitivity, scrollability, read-only,
    /// concurrency (SCROLL_LOCKS / OPTIMISTIC) and the FOR UPDATE OF list, and
    /// emits the TYPE_WARNING info message. Returns the built <see cref="Cursor"/>
    /// plus whether LOCAL scope was requested, or null under
    /// <see cref="BatchContext.IsSkipping"/> (tokens consumed, nothing built).
    /// </summary>
    private static (Cursor Cursor, bool Local)? BuildCursorDefinition(BatchContext batch, string cursorName, bool reqStatic, bool scroll)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume CURSOR

        var reqKeyset = false;
        var reqDynamic = false;
        var reqFastForward = false;
        var forwardOnly = false;
        var readOnlyOption = false;
        var scrollLocks = false;
        var optimistic = false;
        var typeWarning = false;
        var localScope = false;

        // T-SQL post-CURSOR options, in any order, until FOR. LOCAL / GLOBAL
        // (scope) and SCROLL_LOCKS / OPTIMISTIC / TYPE_WARNING (concurrency /
        // warning) are accepted and discarded.
        Span<char> buffer = stackalloc char[12];
        while (context.Token is not ReservedKeyword { Keyword: Keyword.For })
        {
            if (context.Token is not UnquotedString option || option.Span.Length > buffer.Length)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var upper = buffer[..option.Span.Length];
            _ = option.Span.ToUpperInvariant(upper);
            switch (upper)
            {
                case "DYNAMIC": reqDynamic = true; break;
                case "FAST_FORWARD": reqFastForward = true; break;
                case "FORWARD_ONLY": forwardOnly = true; break;
                case "GLOBAL": break;
                case "INSENSITIVE": reqStatic = true; break;
                case "KEYSET": reqKeyset = true; break;
                case "LOCAL": localScope = true; break;
                case "OPTIMISTIC": optimistic = true; break;
                case "READ_ONLY": readOnlyOption = true; break;
                case "SCROLL": scroll = true; break;
                case "SCROLL_LOCKS": scrollLocks = true; break;
                case "STATIC": reqStatic = true; break;
                case "TYPE_WARNING": typeWarning = true; break;
                default: throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
        }

        context.MoveNextRequired(); // consume FOR
        var selection = Selection.Parse(context, 0);

        // Trailing SQL-92 updatability clause: FOR READ ONLY | FOR UPDATE [OF cols].
        List<string>? forUpdateColumns = null;
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
                    // Optional OF <col> [, <col>]… — captured so a positioned
                    // UPDATE of a column outside the list raises Msg 16932.
                    if (context.Token is ReservedKeyword { Keyword: Keyword.Of })
                    {
                        forUpdateColumns = [];
                        do
                        {
                            if (context.GetNextRequired() is not Name ofColumn)
                                throw SimulatedSqlException.SyntaxErrorNear(context);
                            forUpdateColumns.Add(ofColumn.Value);
                            context.MoveNextOptional();
                        } while (context.Token is Operator { Character: ',' });
                    }
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }

        if (batch.IsSkipping)
            return null;

        // Resolve updatability + effective sensitivity. A non-updatable query
        // (no single base table) is forced to STATIC, as is an explicit
        // STATIC / INSENSITIVE / FAST_FORWARD request. Otherwise honor the
        // requested type; an unspecified type defaults to KEYSET when SCROLL
        // was asked for and DYNAMIC for the forward-only default. A base
        // table without a unique key is fine — cursor identity rides the
        // heap's stable <c>(page, slot)</c> address, not the row's values.
        var baseTable = selection.CursorBaseTable;
        var updatable = baseTable is not null;

        var sensitivity = !updatable || reqStatic || reqFastForward
            ? CursorSensitivity.Static
            : reqKeyset
                ? CursorSensitivity.Keyset
                : reqDynamic
                    ? CursorSensitivity.Dynamic
                    : scroll ? CursorSensitivity.Keyset : CursorSensitivity.Dynamic;

        var readOnly = sensitivity == CursorSensitivity.Static || reqFastForward || readOnlyOption;
        // Naming a sensitivity implies SCROLL unless FORWARD_ONLY says otherwise
        // — probe-confirmed for DYNAMIC as well as STATIC / KEYSET. A cursor
        // that names none defaults to dynamic sensitivity *and* forward-only,
        // so the check is on the requested keyword, not the resolved
        // sensitivity.
        var scrollable = scroll
            || ((sensitivity is CursorSensitivity.Static or CursorSensitivity.Keyset || reqDynamic)
                && !forwardOnly && !reqFastForward);

        // Concurrency model (updatable cursors only): SCROLL_LOCKS holds a
        // cursor-scoped U lock on the fetched row; OPTIMISTIC detects out-of-
        // band modification at positioned-DML time. A read-only cursor ignores
        // both (positioned DML is rejected with Msg 16929 anyway).
        var concurrency = readOnly
            ? CursorConcurrency.Default
            : optimistic
                ? CursorConcurrency.Optimistic
                : scrollLocks ? CursorConcurrency.ScrollLocks : CursorConcurrency.Default;

        // TYPE_WARNING: emit Msg 16956 (info, severity 10) at DECLARE when an
        // explicitly-requested sensitivity was silently converted to a lesser
        // one — DYNAMIC / KEYSET over a non-updatable shape forced to STATIC
        // (probe-confirmed the warning fires at DECLARE, not OPEN).
        if (typeWarning
            && ((reqKeyset && sensitivity != CursorSensitivity.Keyset)
                || (reqDynamic && sensitivity != CursorSensitivity.Dynamic)))
        {
            batch.AppendInfoError(@class: 0, state: 1, number: 16956, "The created cursor is not of the requested type.");
        }

        var cursor = new Cursor(
            cursorName,
            selection,
            sensitivity,
            scrollable,
            readOnly,
            updatable ? baseTable : null,
            concurrency,
            forUpdateColumns);

        // Scope: explicit LOCAL wins; otherwise the database's CURSOR_DEFAULT,
        // which the simulator models as GLOBAL (real SQL Server's install
        // default — is_local_cursor_default = 0 — for every system and freshly-
        // created database; the per-database option isn't separately modeled).
        return (cursor, localScope);
    }

    /// <summary>Parses and runs <c>OPEN [GLOBAL] &lt;cursor&gt;</c> /
    /// <c>OPEN @cursorvar</c>.</summary>
    private static void ParseOpenCursor(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume OPEN
        var reference = ReadCursorReference(context);
        if (batch.IsSkipping)
            return;
        ResolveCursor(batch, reference).Open(batch);
    }

    /// <summary>Parses and runs <c>CLOSE [GLOBAL] &lt;cursor&gt;</c> /
    /// <c>CLOSE @cursorvar</c>.</summary>
    private static void ParseCloseCursor(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume CLOSE
        var reference = ReadCursorReference(context);
        if (batch.IsSkipping)
            return;
        ResolveCursor(batch, reference).Close(batch);
    }

    /// <summary>
    /// Parses and runs <c>DEALLOCATE [GLOBAL] &lt;cursor&gt;</c> /
    /// <c>DEALLOCATE @cursorvar</c>. For a named cursor, unbinds the name from
    /// its scope (LOCAL preferred over GLOBAL when unqualified; GLOBAL-qualified
    /// targets the global map) and tears the cursor down if no variable still
    /// references it. For a cursor variable, drops that variable's reference
    /// (decrementing the shared cursor's refcount) and returns it to the
    /// unallocated state. Msg 16916 / 16950 on a miss.
    /// </summary>
    private static void ParseDeallocateCursor(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume DEALLOCATE
        var reference = ReadCursorReference(context);
        if (batch.IsSkipping)
            return;

        if (reference.IsVariable)
        {
            if (!batch.CursorVariables.TryGetValue(reference.Name, out var bound))
                throw SimulatedSqlException.CursorVariableNotAllocated(reference.Name);
            if (bound is not null)
                ReleaseVariableReference(batch, bound);
            batch.CursorVariables[reference.Name] = null;
            return;
        }

        // Named cursor: unqualified removes from LOCAL first, then GLOBAL;
        // GLOBAL-qualified only touches the global map.
        Cursor removed;
        if (!reference.GlobalQualified && batch.LocalCursors.TryGetValue(reference.Name, out removed!))
            _ = batch.LocalCursors.Remove(reference.Name);
        else if (batch.Connection.Cursors.TryGetValue(reference.Name, out removed!))
            _ = batch.Connection.Cursors.Remove(reference.Name);
        else
            throw SimulatedSqlException.CursorDoesNotExist(reference.Name);

        // Drop the name; only destroy the object if no cursor variable still
        // holds it (refcount keeps a variable-referenced cursor alive).
        if (removed.VariableRefCount == 0)
            DestroyCursor(batch, removed);
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
                    offset = offsetValue.IsNull ? 0 : offsetValue.CoerceTo(SqlType.BigInt).AsInt64;

                    // An offset *literal* outside int range is a grammar-level
                    // failure (Msg 1080, class 15) where the same value through
                    // a variable is accepted and simply positions past the end
                    // — probe-confirmed 2026-07-31.
                    if (offsetExpr is Value && offset is < int.MinValue or > int.MaxValue)
                        throw SimulatedSqlException.IntegerValueOutOfRange(offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
            context.MoveNextRequired();

        var reference = ReadCursorReference(context);

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

        var cursor = ResolveCursor(batch, reference);

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
    /// A parsed cursor reference at a use site (<c>OPEN</c> / <c>CLOSE</c> /
    /// <c>DEALLOCATE</c> / <c>FETCH … FROM</c> / <c>WHERE CURRENT OF</c>):
    /// either a named cursor (optionally <c>GLOBAL</c>-qualified) or a cursor
    /// variable (<c>@c</c>). Names are stored with the leading <c>@</c> stripped
    /// for variables.
    /// </summary>
    private readonly struct CursorReference(string name, bool isVariable, bool globalQualified)
    {
        public readonly string Name = name;
        public readonly bool IsVariable = isVariable;
        public readonly bool GlobalQualified = globalQualified;
    }

    /// <summary>
    /// Reads a cursor reference at the current position — <c>@c</c> (variable),
    /// <c>GLOBAL name</c>, or bare <c>name</c> — advancing past it.
    /// </summary>
    private static CursorReference ReadCursorReference(ParserContext context)
    {
        if (context.Token is AtPrefixedString variable)
        {
            context.MoveNextOptional();
            return new CursorReference(variable.Value, isVariable: true, globalQualified: false);
        }
        var globalQualified = IsWord(context.Token, "GLOBAL");
        if (globalQualified)
            context.MoveNextRequired();
        if (context.Token is not Name name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return new CursorReference(name.Value, isVariable: false, globalQualified);
    }

    /// <summary>
    /// Resolves a parsed <see cref="CursorReference"/> to its live
    /// <see cref="Cursor"/>. A variable reference reads
    /// <see cref="BatchContext.CursorVariables"/> (Msg 16950 when unallocated).
    /// A named reference resolves LOCAL-first then GLOBAL when unqualified, or
    /// GLOBAL-only when <c>GLOBAL</c>-qualified (Msg 16916 on a miss).
    /// </summary>
    private static Cursor ResolveCursor(BatchContext batch, CursorReference reference) =>
        reference.IsVariable
            ? batch.CursorVariables.TryGetValue(reference.Name, out var bound) && bound is not null
                ? bound
                : throw SimulatedSqlException.CursorVariableNotAllocated(reference.Name)
            : !reference.GlobalQualified && batch.LocalCursors.TryGetValue(reference.Name, out var local)
                ? local
                : batch.Connection.Cursors.TryGetValue(reference.Name, out var global)
                    ? global
                    : throw SimulatedSqlException.CursorDoesNotExist(reference.Name);

    /// <summary>
    /// Registers a freshly-declared named cursor in its scope — the batch/proc-
    /// local map (<paramref name="local"/> true) or the connection-global map.
    /// Msg 16915 when a cursor of that name already exists in the same scope
    /// (LOCAL and GLOBAL are independent namespaces — a name may exist in both).
    /// </summary>
    private static void DeclareCursorInScope(BatchContext batch, string name, Cursor cursor, bool local)
    {
        var scope = local ? batch.LocalCursors : batch.Connection.Cursors;
        if (scope.ContainsKey(name))
            throw SimulatedSqlException.CursorAlreadyExists(name);
        scope[name] = cursor;
    }

    /// <summary>
    /// Tears a cursor down when its last reference goes away: releases any
    /// SCROLL_LOCKS locks it holds. Closing the materialized state is implicit
    /// (the object becomes unreachable). Idempotent for a closed cursor.
    /// </summary>
    private static void DestroyCursor(BatchContext batch, Cursor cursor) =>
        cursor.ReleaseScrollLocks(batch.Connection);

    /// <summary>
    /// Drops one cursor-variable reference to <paramref name="cursor"/>: if the
    /// decrement leaves an unnamed cursor with no remaining references, the
    /// object is destroyed (SCROLL_LOCKS locks released). Named cursors persist
    /// through their name binding regardless of the count.
    /// </summary>
    private static void ReleaseVariableReference(BatchContext batch, Cursor cursor)
    {
        cursor.VariableRefCount--;
        if (cursor.VariableRefCount <= 0 && cursor.IsUnnamed)
            DestroyCursor(batch, cursor);
    }

    /// <summary>
    /// Resolves a named cursor (LOCAL-first then GLOBAL) for binding to a cursor
    /// variable via <c>SET @c = named_cursor</c>. Msg 16916 on a miss.
    /// </summary>
    private static Cursor ResolveNamedCursor(BatchContext batch, string name) =>
        batch.LocalCursors.TryGetValue(name, out var local)
            ? local
            : batch.Connection.Cursors.TryGetValue(name, out var global)
                ? global
                : throw SimulatedSqlException.CursorDoesNotExist(name);

    /// <summary>
    /// Rebinds cursor variable <paramref name="variableName"/> to
    /// <paramref name="newCursor"/>: releases the reference it previously held
    /// and increments the new cursor's refcount. Assigning the same cursor a
    /// variable already holds is a no-op net of the release/re-take.
    /// </summary>
    private static void RebindCursorVariable(BatchContext batch, string variableName, Cursor? newCursor)
    {
        if (batch.CursorVariables.TryGetValue(variableName, out var previous) && previous is not null)
            ReleaseVariableReference(batch, previous);
        if (newCursor is not null)
            newCursor.VariableRefCount++;
        batch.CursorVariables[variableName] = newCursor;
    }

    /// <summary>
    /// Frame-exit teardown of the batch / procedure / trigger cursor scope:
    /// LOCAL named cursors and cursor variables are implicitly deallocated when
    /// their frame ends (GLOBAL cursors, living on the connection, are
    /// untouched). Each cursor-variable reference is dropped; each LOCAL cursor
    /// with no surviving variable reference is destroyed (releasing SCROLL_LOCKS
    /// locks). A LOCAL cursor handed out through a cursor OUTPUT parameter has a
    /// non-zero refcount here, so it survives into the caller's variable.
    /// </summary>
    internal static void TeardownFrameCursors(BatchContext batch)
    {
        foreach (var bound in batch.CursorVariables.Values)
        {
            if (bound is not null)
                ReleaseVariableReference(batch, bound);
        }
        batch.CursorVariables.Clear();

        foreach (var cursor in batch.LocalCursors.Values)
        {
            if (cursor.VariableRefCount == 0)
                DestroyCursor(batch, cursor);
        }
        batch.LocalCursors.Clear();
    }

    /// <summary>
    /// Parses the <c>CURRENT OF &lt;cursor&gt;</c> tail of a positioned
    /// <c>WHERE CURRENT OF</c> clause (cursor on entry: the <c>CURRENT</c>
    /// keyword) and validates the cursor against the DML target
    /// <paramref name="table"/>: Msg 16929 when the cursor is read-only,
    /// Msg 16931 when it isn't positioned on a live row (or is positioned on a
    /// different table), Msg 16932 when a positioned UPDATE assigns a column
    /// outside the cursor's <c>FOR UPDATE OF</c> list
    /// (<paramref name="assignedColumns"/>, null for DELETE), and the
    /// optimistic-conflict chain (Msg 16947) when an OPTIMISTIC cursor's current
    /// row was modified out-of-band. Returns the validated cursor whose
    /// <see cref="Cursor.CurrentRid"/> identifies the row to mutate.
    /// </summary>
    internal static Cursor ParseWhereCurrentOf(ParserContext context, HeapTable table, IReadOnlyList<string>? assignedColumns = null)
    {
        context.MoveNextRequired(); // consume CURRENT
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Of })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired(); // consume OF
        var reference = ReadCursorReference(context);

        var batch = context.Batch;
        var cursor = ResolveCursor(batch, reference);
        if (cursor.ReadOnly)
            throw SimulatedSqlException.CursorIsReadOnly();
        if (cursor.CurrentRid is null || !ReferenceEquals(cursor.BaseTable, table))
            throw SimulatedSqlException.CursorNoCurrentRow();

        // FOR UPDATE OF (…): every assigned column must be in the list.
        if (assignedColumns is not null)
        {
            foreach (var column in assignedColumns)
            {
                if (!cursor.IsColumnUpdatable(column, batch))
                    throw SimulatedSqlException.CursorColumnNotInForUpdateList();
            }
        }

        // OPTIMISTIC: raise the conflict chain if the row changed since fetch.
        cursor.CheckOptimisticConflict();
        return cursor;
    }

    /// <summary>
    /// True when the heap row at <paramref name="rid"/> is the one the
    /// positioned <paramref name="cursor"/> is sitting on — i.e. the row's
    /// stable address equals <see cref="Cursor.CurrentRid"/>.
    /// </summary>
    internal static bool CursorRowMatches(Cursor cursor, (int Page, int Slot) rid) =>
        cursor.CurrentRid is { } current && current.Equals(rid);

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
